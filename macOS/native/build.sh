#!/usr/bin/env bash
# Build Mission Planner as a native macOS app (.NET for macOS, Cocoa Windows Forms).
# On Linux this only compiles (MissionPlannerMacCompileOnly); on a Mac it makes the .app.
# Usage: macOS/native/build.sh [extra dotnet build arguments]
# (bash 3.2 on macOS: empty arrays are expanded with ${a[@]+...} under set -u)
set -euo pipefail
native_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source_dir=$(cd -- "$native_dir/../.." && pwd)
extra=()
# Linux can compile the code but not bundle a macOS app
if [[ $(uname -s) == Darwin ]]; then
    # the SDK would pick osx-x64 (Rosetta) before Build.targets can choose
    extra+=(-r "osx-$(uname -m | sed 's/x86_64/x64/')")
else
    extra+=(-p:MissionPlannerMacCompileOnly=true -p:OutputType=Library -p:IsMacEnabled=false -p:BundleOriginalResources=false -p:AppManifestDetectionEnabled=false -p:StartupObject=)
fi
cd -- "$source_dir"
build_args=(dotnet build MissionPlanner.csproj -c Release
    "-p:DirectoryBuildTargetsPath=$native_dir/Build.targets" -p:MissionPlannerMacNative=true
    -p:MissionPlannerLinuxBuild=true)
build_log=$(mktemp)
if ! "${build_args[@]}" ${extra[@]+"${extra[@]}"} "$@" 2>&1 | tee "$build_log"; then
    # The .NET for macOS workload refuses an installed Xcode that is a later point release
    # than the exact one it expects ("requires Xcode 27.1. The current version of Xcode is
    # 27.2."), even within the same major version, where it is almost always compatible
    # (https://github.com/dotnet/macios troubleshooting: -p:ValidateXcodeVersion=false).
    # Retry past just that check, but only for a same-major mismatch: a real major-version
    # gap (a brand new Xcode the workload has never seen) can fail to compile or link instead.
    required=$(grep -oE 'requires Xcode [0-9]+\.[0-9]+' "$build_log" | tail -1 | awk '{print $3}')
    installed=$(grep -oE 'current version of Xcode is [0-9]+\.[0-9]+' "$build_log" | tail -1 | awk '{print $NF}')
    rm -f "$build_log"
    if [[ -n $required && -n $installed && ${required%%.*} == "${installed%%.*}" ]]; then
        echo "Xcode $installed vs. the workload's $required (same major version): retrying with -p:ValidateXcodeVersion=false" >&2
        "${build_args[@]}" ${extra[@]+"${extra[@]}"} "$@" -p:ValidateXcodeVersion=false
    else
        exit 1
    fi
else
    rm -f "$build_log"
fi

[[ $(uname -s) == Darwin ]] || exit 0

# Complete the app bundle: Mission Planner looks for its data files (Scripts, plugins,
# graphs, NoFly, XML, ...) next to its assembly, which in the bundle is Contents/MonoBundle.
# MissionPlanner.csproj's output directory (OutDir is fixed before Build.targets is read)
out_dir="$source_dir/bin/Release/net461"
# named after ApplicationTitle: "Mission Planner.app"
app=$(find "$out_dir" -maxdepth 1 -name '*.app' | head -1)
[[ -n $app && -f $app/Contents/Info.plist && -d $app/Contents/MacOS ]] || { echo "no app bundle in $out_dir" >&2; exit 1; }
bundle="$app/Contents/MonoBundle"
rsync -a --ignore-existing --exclude '*.app' --exclude 'runtimes/' --exclude '*.pdb' "$out_dir/" "$bundle/"
# The configuration file without .NET Framework-only sections: on .NET an unknown section
# (system.web, from a Windows Forms template) makes ConfigurationManager reject the whole
# file, and log4net and the update settings with it. system.diagnostics traces to /ProgramData.
perl -0pi -e 's{\s*<system\.(web|diagnostics)>.*?</system\.\1>}{}gs' "$bundle/MissionPlanner.dll.config"
# native libraries the code loads by name (see MacNative.cs)
arch=$(uname -m)
cc -dynamiclib -O2 -arch "$arch" -install_name @loader_path/libuser32stub.dylib \
    -o "$bundle/libuser32stub.dylib" "$native_dir/../user32stub.c"
# (Homebrew's sdl2 is sdl2-compat, which loads @loader_path/libSDL3.dylib)
for lib in sdl2:libSDL2-2.0.0.dylib sdl3:libSDL3.dylib libusb:libusb-1.0.0.dylib; do
    src="$(brew --prefix "${lib%%:*}" 2>/dev/null)/lib/${lib#*:}"
    if [[ -f $src ]]; then
        cp -L -- "$src" "$bundle/${lib#*:}" && chmod u+w "$bundle/${lib#*:}"
        install_name_tool -id "@loader_path/${lib#*:}" "$bundle/${lib#*:}"
    else
        echo "${lib%%:*} not found (brew install ${lib%%:*}): $src" >&2
    fi
done
# SimpleBLE C bindings (Bluetooth LE through CoreBluetooth), built as for the X11 app
simpleble_version=v0.9.1
simpleble_build="$source_dir/bin/macos-native-work/simpleble-$simpleble_version-$arch"
if [[ ! -f $simpleble_build/out/libsimplecble.dylib ]] && command -v cmake >/dev/null; then
    rm -rf -- "$simpleble_build" && mkdir -p -- "$simpleble_build/out"
    if curl -fsSL "https://codeload.github.com/simpleble/simpleble/tar.gz/refs/tags/$simpleble_version" \
            | tar -xz -C "$simpleble_build" --strip-components=1 \
        && cmake -S "$simpleble_build/simplecble" -B "$simpleble_build/build" -DCMAKE_BUILD_TYPE=Release \
            -DBUILD_SHARED_LIBS=ON > "$simpleble_build/cmake.log" 2>&1 \
        && cmake --build "$simpleble_build/build" -j"$(sysctl -n hw.ncpu)" >> "$simpleble_build/cmake.log" 2>&1; then
        find "$simpleble_build/build" -name 'libsimple*.dylib' -exec cp -L {} "$simpleble_build/out/" \;
    else
        echo 'SimpleBLE build failed: Bluetooth LE will be unavailable.' >&2
        tail -20 "$simpleble_build/cmake.log" >&2 || true
    fi
fi
if [[ -f $simpleble_build/out/libsimplecble.dylib ]]; then
    cp -L -- "$simpleble_build/out/"libsimple*.dylib "$bundle/"
    for lib in "$bundle/"libsimple*.dylib; do
        chmod u+w "$lib"
        install_name_tool -id "@loader_path/$(basename -- "$lib")" "$lib"
        for dep in $(otool -L "$lib" | awk 'NR > 1 {print $1}' | grep -E '(@rpath/|/build/).*libsimple' || true); do
            install_name_tool -change "$dep" "@loader_path/$(basename -- "$dep")" "$lib"
        done
    done
fi

# GStreamer (video in the HUD, cameras, OSD video) inside the app, from Homebrew's gstreamer,
# with the plugins Mission Planner's pipelines use: RTP/UDP/TCP/RTSP, decodebin, H.264/H.265
# parsing, VideoToolbox decoding and AVFoundation cameras (applemedia), JPEG/MJPEG, MP4.
# dylibbundler copies their libraries next to them and points every reference at
# @loader_path, so nothing outside the app is needed. MacNative.Init sets the plugin path.
gst_prefix=$(brew --prefix gstreamer 2>/dev/null || true)
if [[ -n $gst_prefix && -d $gst_prefix/lib/gstreamer-1.0 ]] && command -v dylibbundler >/dev/null; then
    gst="$bundle/gstreamer"
    rm -rf -- "$gst" && mkdir -p -- "$gst/lib" "$gst/plugins" "$source_dir/bin/macos-native-work"
    for plugin in coreelements app videotestsrc videoconvertscale videoconvert videoscale videorate \
                  videofilter udp tcp rtp rtpmanager rtsp playback typefindfunctions videoparsersbad \
                  applemedia jpeg multipart isomp4 autodetect; do
        if [[ -f $gst_prefix/lib/gstreamer-1.0/libgst$plugin.dylib ]]; then
            cp -L "$gst_prefix/lib/gstreamer-1.0/libgst$plugin.dylib" "$gst/plugins/"
        fi
    done
    # the libraries GStreamer.cs calls (P/Invoke)
    for lib in gstreamer-1.0.0 gstapp-1.0.0 gstvideo-1.0.0 gstbase-1.0.0; do
        cp -L "$gst_prefix/lib/lib$lib.dylib" "$gst/lib/"
    done
    chmod -R u+w "$gst"
    fix=()
    for f in "$gst"/plugins/*.dylib "$gst"/lib/*.dylib; do fix+=(-x "$f"); done
    dylibbundler -of -b "${fix[@]}" -d "$gst/lib" -p '@loader_path/../lib/' -s "$(brew --prefix)/lib" \
        > "$source_dir/bin/macos-native-work/gstreamer-dylibbundler.log" 2>&1 \
        || { echo "GStreamer bundling failed" >&2; tail -20 "$source_dir/bin/macos-native-work/gstreamer-dylibbundler.log" >&2; }
    # dylibbundler fixed the plugins' references, not their own install names
    for f in "$gst"/plugins/*.dylib; do
        install_name_tool -id "@loader_path/$(basename -- "$f")" "$f" 2>/dev/null
    done
    codesign --force --sign - "$gst"/lib/*.dylib "$gst"/plugins/*.dylib
    if otool -L "$gst"/lib/*.dylib "$gst"/plugins/*.dylib | grep -E '^\s+/(opt|usr/local)/' >&2; then
        echo "GStreamer: references outside the app remain (above)" >&2
    fi
    echo "GStreamer: $(ls "$gst/plugins" | wc -l | tr -d ' ') plugins, $(ls "$gst/lib" | wc -l | tr -d ' ') libraries, $(du -sh "$gst" | cut -f1)"
else
    echo "GStreamer not bundled (brew install gstreamer dylibbundler)" >&2
fi

# Optional native SITL simulators (MP_SITL_DIR with ArduCopter, ArduPlane, ...; the
# macos-arm64.yml "sitl" job builds them), found by MacNative.Init in Contents/Resources/sitl
if [[ -n ${MP_SITL_DIR:-} && -d $MP_SITL_DIR ]]; then
    mkdir -p -- "$app/Contents/Resources/sitl"
    cp -- "$MP_SITL_DIR"/Ardu* "$app/Contents/Resources/sitl/"
    cp -- "$MP_SITL_DIR"/VERSION.txt "$MP_SITL_DIR"/*.parm "$app/Contents/Resources/sitl/" 2>/dev/null || true
    chmod +x "$app/Contents/Resources/sitl"/Ardu*
    codesign --force --sign - "$app/Contents/Resources/sitl"/Ardu*
fi

# the bundle changed after the SDK signed it
codesign --force --deep --sign - "$app"
codesign --verify --strict "$app" && echo "Signature: valid"
echo "App: $app"
