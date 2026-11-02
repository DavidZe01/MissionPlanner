#!/bin/bash
# Mission Planner launcher for macOS (Contents/MacOS/MissionPlanner).
#
# Runs the same MissionPlanner.exe as Windows on a native Mono runtime
# (arm64 on Apple Silicon, x86_64 on Intel). Mono's Windows Forms has no
# 64-bit Cocoa backend, so windows are drawn through X11 (XQuartz), using the
# libgdiplus with X11 support that is bundled in Contents/Frameworks.
set -euo pipefail

contents=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
app_dir="$contents/Resources/app"
frameworks="$contents/Frameworks"
log_dir="$HOME/Library/Logs/MissionPlanner"
cache_dir="$HOME/Library/Caches/MissionPlanner"
mkdir -p -- "$log_dir" "$cache_dir"

# Started from Finder there is no terminal: show problems in a dialog too.
fail() {
    echo "Mission Planner: $1" >&2
    if [[ ! -t 2 ]]; then
        /usr/bin/osascript -e 'on run argv' \
            -e 'display alert "Mission Planner cannot start" message (item 1 of argv) as critical' \
            -e 'end run' "$1" >/dev/null 2>&1 || true
    fi
    exit 1
}

# Homebrew's environment is not inherited by apps started from Finder.
for brew_prefix in /opt/homebrew /usr/local; do
    [[ -d $brew_prefix/bin ]] && PATH="$brew_prefix/bin:$PATH"
done
export PATH

mono_bin=
for candidate in "${MISSIONPLANNER_MONO:-}" /opt/homebrew/bin/mono /usr/local/bin/mono \
    /Library/Frameworks/Mono.framework/Versions/Current/Commands/mono; do
    if [[ -n $candidate && -x $candidate ]]; then
        mono_bin=$candidate
        break
    fi
done
[[ -n $mono_bin ]] || fail "Mono is required. Install it with: brew install mono"
mono_real=$(/usr/bin/python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$mono_bin" 2>/dev/null \
    || readlink -f -- "$mono_bin" 2>/dev/null || echo "$mono_bin")
mono_prefix=$(cd -- "$(dirname -- "$mono_real")/.." && pwd)

# Refuse to run translated: on Apple Silicon Mono must have an arm64 slice.
machine=$(uname -m)
if [[ $(/usr/sbin/sysctl -n hw.optional.arm64 2>/dev/null || echo 0) == 1 ]]; then
    machine=arm64
fi
mono_archs=$(/usr/bin/lipo -archs "$mono_real" 2>/dev/null || echo unknown)
if [[ $machine == arm64 && $mono_archs != *arm64* ]]; then
    fail "The installed Mono ($mono_real) is not built for Apple Silicon ($mono_archs). Install the native one with /opt/homebrew/bin/brew install mono"
fi

# X11 display: XQuartz.
if [[ -z ${DISPLAY:-} ]]; then
    if [[ -d /Applications/Utilities/XQuartz.app || -d /Applications/XQuartz.app ]]; then
        /usr/bin/open -g -a XQuartz
        export DISPLAY=:0
        for _ in $(seq 1 100); do
            [[ -S /tmp/.X11-unix/X0 ]] && break
            sleep 0.1
        done
    else
        fail "XQuartz is required. Install it with: brew install --cask xquartz (then log out and back in)"
    fi
fi

# libgdiplus and libX11 must be the same libraries Mono's Windows Forms uses,
# because they share the X11 Display connection. Point Mono's global dllmap at
# the bundled libgdiplus and at the libX11 it was linked against.
libgdiplus="$frameworks/libgdiplus.0.dylib"
[[ -f $libgdiplus ]] || fail "The application bundle is incomplete: $libgdiplus is missing."
libx11=$(cat -- "$frameworks/libX11.path" 2>/dev/null || true)
if [[ -z $libx11 || ! -f $libx11 ]]; then
    libx11=$(/usr/bin/otool -L "$libgdiplus" | awk '/libX11\.6\.dylib/ {print $1; exit}')
fi
if [[ $libx11 == @* ]]; then
    libx11="$frameworks/$(basename -- "$libx11")"
fi
[[ -f $libx11 ]] || fail "libX11 was not found ($libx11). Install it with: brew install libx11"

mono_config_src="$mono_prefix/etc/mono/config"
[[ -f $mono_config_src ]] || fail "Mono's configuration was not found at $mono_config_src"
mono_config="$cache_dir/mono-config"
# Extra mappings for native libraries the managed code names by Linux sonames.
maps=()
[[ -f $frameworks/libSDL2-2.0.0.dylib ]] && maps+=("SDL2=$frameworks/libSDL2-2.0.0.dylib")
for gst_lib in /opt/homebrew/lib /usr/local/lib /Library/Frameworks/GStreamer.framework/Versions/1.0/lib; do
    if [[ -f $gst_lib/libgstreamer-1.0.0.dylib && -f $gst_lib/libgstapp-1.0.0.dylib ]]; then
        maps+=("libgstreamer-1.0.so.0=$gst_lib/libgstreamer-1.0.0.dylib" "libgstapp-1.0.so.0=$gst_lib/libgstapp-1.0.0.dylib")
        break
    fi
done
GDIPLUS=$libgdiplus X11=$libx11 /usr/bin/perl -e '
    my ($src, @maps) = @ARGV;
    open(my $in, "<", $src) or die "$src: $!";
    local $/; my $c = <$in>;
    # Bundled libgdiplus (with X11) and the libX11 it links against.
    $c =~ s{(<dllmap\s+dll="[^"]*(?:gdiplus|gdi32)[^"]*"\s+target=")[^"]*"}{$1$ENV{GDIPLUS}"}g;
    $c =~ s{<dllmap\s+dll="libX11"[^>]*>}{}g;
    my $add = "\t<dllmap dll=\"libX11\" target=\"$ENV{X11}\"/>\n";
    for (@maps) { my ($d, $t) = split /=/, $_, 2; $add .= "\t<dllmap dll=\"$d\" target=\"$t\"/>\n"; }
    $c =~ s{</configuration>}{$add</configuration>};
    print $c;
' "$mono_config_src" "${maps[@]}" > "$mono_config.tmp" || fail "Could not write $mono_config"
mv -f -- "$mono_config.tmp" "$mono_config"

export MONO_CONFIG="$mono_config"
# Native macOS SITL binaries built from ArduPilot's stable release.
if [[ -x $contents/Resources/sitl/ArduCopter ]]; then
    export MISSIONPLANNER_SITL_DIR="$contents/Resources/sitl"
fi
export MONO_MWF_MAC_FORCE_X11=1
export MONO_PATH="$mono_prefix/lib/mono/4.5/Facades${MONO_PATH:+:$MONO_PATH}"
export DYLD_FALLBACK_LIBRARY_PATH="$app_dir:$frameworks:$mono_prefix/lib:/opt/homebrew/lib:/usr/local/lib:/opt/X11/lib:/usr/lib"
export PATH="$mono_prefix/bin:$PATH"
export FONTCONFIG_PATH=${FONTCONFIG_PATH:-/opt/X11/lib/X11/fontconfig}
[[ -d $FONTCONFIG_PATH ]] || unset FONTCONFIG_PATH

cd -- "$app_dir"
{
    echo "=== $(date) Mission Planner for macOS"
    echo "machine=$machine macOS=$(/usr/bin/sw_vers -productVersion 2>/dev/null)"
    echo "mono=$mono_real archs=$mono_archs"
    echo "libgdiplus=$libgdiplus libX11=$libx11 DISPLAY=$DISPLAY"
} >> "$log_dir/launcher.log"

case ${1:-} in
    --self-test) shift; exec "$mono_bin" LinuxSmokeTest.exe "$@" ;;
    --tour) shift; exec "$mono_bin" ScreenTour.exe "$@" ;;
esac
if [[ -t 1 ]]; then
    exec "$mono_bin" MissionPlanner.exe "$@"
fi
exec "$mono_bin" MissionPlanner.exe "$@" >> "$log_dir/MissionPlanner.log" 2>&1
