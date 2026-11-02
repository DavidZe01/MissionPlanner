#!/bin/bash
# Build "Mission Planner.app" for macOS from the managed application.
#
# Usage: macOS/package.sh <managed-app-dir> [output-dir]
#
# <managed-app-dir> is the output of Linux/build.sh (the extracted
# MissionPlanner-linux-* directory): the managed code is AnyCPU IL and is the
# same on every platform. This script runs on macOS and adds what is
# macOS-specific: a native libSkiaSharp, a libgdiplus built with X11 support
# (Homebrew's is built without it) together with its dependencies, and the
# launcher. The bundle needs only Mono and XQuartz installed on the Mac.
#
# Build requirements (Homebrew): pkgconf cairo pango giflib libtiff libexif
# jpeg-turbo libpng libx11 dylibbundler sdl2
set -euo pipefail

mac_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source_dir=$(dirname -- "$mac_dir")
if [[ $# -lt 1 || ${1:-} == --help ]]; then
    sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    exit 1
fi
[[ $(uname -s) == Darwin ]] || { echo 'macOS/package.sh must run on macOS.' >&2; exit 1; }
managed_dir=$(cd -- "$1" && pwd)
output_dir=$(mkdir -p -- "${2:-$source_dir/bin/macos}" && cd -- "${2:-$source_dir/bin/macos}" && pwd)
[[ -f $managed_dir/MissionPlanner.exe ]] || { echo "MissionPlanner.exe not found in $managed_dir" >&2; exit 1; }

skia_version=3.119.4
libgdiplus_version=6.2
libgdiplus_sha256=683adb7d99d03f6ee7985173a206a2243f76632682334ced4cae2fcd20c83bc9
arch=$(uname -m)
work_dir="$output_dir/work"
app="$output_dir/Mission Planner.app"
mkdir -p -- "$work_dir"

for tool in pkg-config dylibbundler curl unzip; do
    command -v "$tool" >/dev/null || { echo "Missing $tool (brew install pkgconf dylibbundler)." >&2; exit 1; }
done

# --- libgdiplus with X11 -----------------------------------------------------
libgdiplus_build="$work_dir/libgdiplus-$libgdiplus_version-$arch"
if [[ ! -f $libgdiplus_build/prefix/lib/libgdiplus.0.dylib ]]; then
    tarball="$work_dir/libgdiplus-$libgdiplus_version.tar.gz"
    [[ -f $tarball ]] || curl -fsSL -o "$tarball" \
        "https://dl.winehq.org/mono/sources/libgdiplus/libgdiplus-$libgdiplus_version.tar.gz"
    echo "$libgdiplus_sha256  $tarball" | shasum -a 256 -c -
    rm -rf -- "$libgdiplus_build"
    mkdir -p -- "$libgdiplus_build"
    tar -xzf "$tarball" -C "$libgdiplus_build" --strip-components=1
    (
        cd -- "$libgdiplus_build"
        brew_prefix=$(brew --prefix)
        export CPPFLAGS="-I$brew_prefix/include ${CPPFLAGS:-}" LDFLAGS="-L$brew_prefix/lib ${LDFLAGS:-}"
        export PKG_CONFIG_PATH="$brew_prefix/lib/pkgconfig:$brew_prefix/share/pkgconfig:$(brew --prefix libx11)/lib/pkgconfig${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}"
        # Do not pass --with-x11: configure treats any form of that option as "disable".
        ./configure --disable-silent-rules --prefix="$libgdiplus_build/prefix" \
            --with-pango > configure.log
        grep -E '^ *(X11|Text|Cairo|Codecs)' configure.log || true
        grep -q 'X11 support.*yes\|X11:.*yes' configure.log config.log || {
            echo 'libgdiplus was configured without X11 support.' >&2; tail -40 config.log >&2; exit 1; }
        make -j"$(sysctl -n hw.ncpu)" > make.log
        make install > install.log
    )
fi

# --- native SkiaSharp (universal) ---------------------------------------------
skia_pkg="$work_dir/skiasharp.nativeassets.macos.$skia_version.nupkg"
[[ -f $skia_pkg ]] || curl -fsSL -o "$skia_pkg" \
    "https://api.nuget.org/v3-flatcontainer/skiasharp.nativeassets.macos/$skia_version/skiasharp.nativeassets.macos.$skia_version.nupkg"
rm -rf -- "$work_dir/skia" && mkdir -p -- "$work_dir/skia"
unzip -q -o "$skia_pkg" 'runtimes/osx/native/libSkiaSharp.dylib' -d "$work_dir/skia"

# --- bundle ---------------------------------------------------------------------
rm -rf -- "$app"
mkdir -p -- "$app/Contents/MacOS" "$app/Contents/Resources/app" "$app/Contents/Frameworks"
rsync -a \
    --exclude='/x64/' --exclude='/arm64/' --exclude='/mono-facades/' --exclude='/mono-tools/' \
    --exclude='/run.sh' --exclude='/ARCH' --exclude='/README.md' --exclude='*.so' \
    --exclude='/runtimes/linux-*/' --exclude='/runtimes/win*/' \
    "$managed_dir/" "$app/Contents/Resources/app/"
cp -- "$work_dir/skia/runtimes/osx/native/libSkiaSharp.dylib" "$app/Contents/Resources/app/"
cp -- "$mac_dir/README.md" "$app/Contents/Resources/app/README-macOS.md"
cp -- "$libgdiplus_build/prefix/lib/libgdiplus.0.dylib" "$app/Contents/Frameworks/"
# Copy every non-system dependency (cairo, pango, glib, libX11, ...) next to it.
dylibbundler -of -cd -b -x "$app/Contents/Frameworks/libgdiplus.0.dylib" \
    -d "$app/Contents/Frameworks" -p @loader_path/ > "$work_dir/dylibbundler.log"
install_name_tool -id @loader_path/libgdiplus.0.dylib "$app/Contents/Frameworks/libgdiplus.0.dylib"
# SDL2 for joysticks (IOKit HID); it only links system frameworks.
sdl2=$(brew --prefix sdl2 2>/dev/null)/lib/libSDL2-2.0.0.dylib
if [[ -f $sdl2 ]]; then
    cp -- "$sdl2" "$app/Contents/Frameworks/"
    chmod u+w "$app/Contents/Frameworks/libSDL2-2.0.0.dylib"
    install_name_tool -id @loader_path/libSDL2-2.0.0.dylib "$app/Contents/Frameworks/libSDL2-2.0.0.dylib"
else
    echo 'SDL2 not found (brew install sdl2): joysticks will be unavailable.' >&2
fi
if [[ ! -f $app/Contents/Frameworks/libX11.6.dylib ]]; then
    echo 'libX11 was not bundled with libgdiplus.' >&2
    exit 1
fi

cp -- "$mac_dir/launcher.sh" "$app/Contents/MacOS/MissionPlanner"
chmod +x "$app/Contents/MacOS/MissionPlanner"
version=$(head -n 1 "$managed_dir/version.txt" 2>/dev/null | tr -cd '0-9.' || true)
[[ -n $version ]] || version=1.3
sed -e "s/@VERSION@/$version/g" "$mac_dir/Info.plist" > "$app/Contents/Info.plist"
if command -v sips >/dev/null && command -v iconutil >/dev/null; then
    iconset="$work_dir/MissionPlanner.iconset"
    rm -rf -- "$iconset" && mkdir -p -- "$iconset"
    for size in 16 32 128 256 512; do
        sips -z $size $size "$source_dir/mpdesktop150.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
        sips -z $((size * 2)) $((size * 2)) "$source_dir/mpdesktop150.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
    done
    iconutil -c icns "$iconset" -o "$app/Contents/Resources/MissionPlanner.icns"
fi

# Apple Silicon only runs signed native code; install_name_tool invalidated the
# linker signatures. Sign ad hoc (users of a downloaded copy clear quarantine).
codesign --force --sign - "$app/Contents/Frameworks/"*.dylib "$app/Contents/Resources/app/libSkiaSharp.dylib"
codesign --force --sign - "$app"

{
    cat "$managed_dir/BUILD-INFO.txt" 2>/dev/null || true
    echo "macOS bundle built on $(sw_vers -productVersion) $arch"
    echo "libgdiplus $libgdiplus_version (X11), SkiaSharp native $skia_version"
} > "$app/Contents/Resources/app/BUILD-INFO.txt"

package_name="MissionPlanner-macos-$arch"
rm -f -- "$output_dir/$package_name.zip"
(cd -- "$output_dir" && ditto -c -k --keepParent "Mission Planner.app" "$package_name.zip" \
    && shasum -a 256 "$package_name.zip" > "$package_name.zip.sha256")
echo "App: $app"
echo "Package: $output_dir/$package_name.zip"
