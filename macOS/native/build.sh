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
# A newer .NET for macOS workload (matching a newer Xcode on the machine building this) can
# reject the build for two reasons that are safe to patch around automatically, rather than
# failing outright; $patches collects the -p: overrides found to be needed, one per retry.
patches=""
build_succeeded=0
for attempt in 1 2 3; do
    build_log=$(mktemp)
    if "${build_args[@]}" ${extra[@]+"${extra[@]}"} $patches "$@" 2>&1 | tee "$build_log"; then
        rm -f "$build_log"
        build_succeeded=1
        break
    fi
    new_patch=""
    # The workload refuses an installed Xcode that is a later point release than the exact
    # one it expects ("requires Xcode 27.1. The current version of Xcode is 27.2."), even
    # within the same major version, where it is almost always compatible
    # (https://github.com/dotnet/macios troubleshooting: -p:ValidateXcodeVersion=false). A
    # real major-version gap (a brand new Xcode the workload has never seen) is left to fail
    # outright instead, since that can fail to compile or link.
    if [[ $patches != *ValidateXcodeVersion* ]] \
            && grep -qE 'requires Xcode [0-9]+\.[0-9]+\. The current version of Xcode is [0-9]+\.[0-9]+' "$build_log"; then
        required=$(grep -oE 'requires Xcode [0-9]+\.[0-9]+' "$build_log" | tail -1 | awk '{print $3}')
        installed=$(grep -oE 'current version of Xcode is [0-9]+\.[0-9]+' "$build_log" | tail -1 | awk '{print $NF}')
        if [[ -n $required && -n $installed && ${required%%.*} == "${installed%%.*}" ]]; then
            echo "Xcode $installed vs. the workload's $required (same major version): past the version check (-p:ValidateXcodeVersion=false)" >&2
            new_patch="-p:ValidateXcodeVersion=false"
        fi
    fi
    # That newer workload's bundled SDK can also raise the minimum macOS it can target
    # ("SupportedOSPlatformVersion value '12.0' ... is lower than the minimum value
    # '14.0'"): an Xcode that drops the older SDK genuinely cannot produce a binary for it,
    # so raise it to what the SDK reports as its floor (only for this build: the project
    # file still asks for 12.0, which an older Xcode/workload can still build for).
    if [[ -z $new_patch && $patches != *SupportedOSPlatformVersion* ]] \
            && grep -qE "SupportedOSPlatformVersion value '[0-9.]+' in the project file is lower than the minimum value '[0-9.]+'" "$build_log"; then
        minimum=$(grep -oE "lower than the minimum value '[0-9.]+'" "$build_log" | tail -1 | grep -oE '[0-9.]+')
        if [[ -n $minimum ]]; then
            echo "the installed workload's SDK needs at least macOS $minimum: raising SupportedOSPlatformVersion to match (-p:SupportedOSPlatformVersion=$minimum)" >&2
            new_patch="-p:SupportedOSPlatformVersion=$minimum"
        fi
    fi
    rm -f "$build_log"
    [[ -n $new_patch ]] || exit 1
    patches="$patches $new_patch"
done
[[ $build_succeeded == 1 ]] || exit 1

# The DLL plugins Windows ships in plugins/ (Shortcuts, FaceMap, Dowding), built against the app
# just built: the same arguments, and the app's macOS SDK version (see Build.targets). Only
# each plugin's own DLL goes in plugins/, beside the ones Mission Planner compiles from source.
if [[ $(uname -s) == Darwin ]]; then
    property_args=("-p:RuntimeIdentifier=osx-$(uname -m | sed 's/x86_64/x64/')")
else
    property_args=(${extra[@]+"${extra[@]}"})
fi
platform_version=$(dotnet msbuild MissionPlanner.csproj -p:Configuration=Release "${build_args[@]:5}" \
    ${property_args[@]+"${property_args[@]}"} $patches -p:TargetFramework=net10.0-macos \
    -getProperty:TargetPlatformVersion 2>/dev/null | tr -d '\r' | tail -1)
plugin_dir="$source_dir/bin/Release/net461/plugins"
mkdir -p "$plugin_dir"
# what is there before (the plugins compiled from source): the plugin builds also copy the
# app's assemblies there, which Mission Planner would then load as plugins
before_plugins=$(ls -A "$plugin_dir")
for plugin in Shortcuts FaceMap Dowding; do
    if dotnet build "plugins/$plugin/$plugin.csproj" -c Release "${build_args[@]:5}" ${extra[@]+"${extra[@]}"} $patches \
        "-p:MissionPlannerMacAppPlatformVersion=$platform_version" "$@" > "${TMPDIR:-/tmp}/plugin-$plugin.log" 2>&1; then
        # FaceMap and Dowding build into bin/Release/net461/plugins themselves
        dll=$(find "plugins/$plugin/bin" "$plugin_dir" -name "$plugin.dll" -newer MissionPlanner.csproj 2>/dev/null | head -1 || true)
        if [[ -n $dll ]]; then
            [[ $dll == "$plugin_dir/$plugin.dll" ]] || cp -- "$dll" "$plugin_dir/"
            echo "Plugin $plugin: $plugin_dir/$plugin.dll"
            rm -f "${TMPDIR:-/tmp}/plugin-$plugin.log"
            continue
        fi
    fi
    # not fatal (the app works without it), but the screen tour reports a missing plugin
    echo "PLUGIN $plugin: did not build (plugin-$plugin.log)" >&2
    grep -E ': error ' "${TMPDIR:-/tmp}/plugin-$plugin.log" | sort -u | head -5 >&2 || true
done
while IFS= read -r entry; do
    case $entry in
        Shortcuts.dll|FaceMap.dll|Dowding.dll) ;;
        *) grep -qxF -- "$entry" <<< "$before_plugins" || rm -rf -- "${plugin_dir:?}/$entry" ;;
    esac
done < <(ls -A "$plugin_dir")

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
# System.IO.Ports' native helper: without it no serial port opens (USB telemetry, firmware
# upload, SiK radios). The SDK normally puts it in MonoBundle; otherwise take this RID's
# copy from the output's runtimes/ (excluded above) or from the NuGet package
rid="osx-$(uname -m | sed 's/x86_64/x64/')"
ports_lib=libSystem.IO.Ports.Native.dylib
if [[ ! -f $bundle/$ports_lib ]]; then
    # (find fails on a missing directory, which set -e/pipefail would turn into an exit)
    src=$( { find "$out_dir/runtimes/$rid/native" \
        "${NUGET_PACKAGES:-$HOME/.nuget/packages}/runtime.$rid.runtime.native.system.io.ports"/10.*/runtimes/"$rid"/native \
        -name "$ports_lib" 2>/dev/null || true; } | sort | tail -1)
    [[ -n $src ]] || { echo "$ports_lib for $rid not found: serial ports would not open" >&2; exit 1; }
    cp -- "$src" "$bundle/$ports_lib"
fi
echo "Serial port helper: $bundle/$ports_lib"
# The configuration file without .NET Framework-only sections: on .NET an unknown section
# (system.web, from a Windows Forms template) makes ConfigurationManager reject the whole
# file, and log4net and the update settings with it. system.diagnostics traces to /ProgramData.
perl -0pi -e 's{\s*<system\.(web|diagnostics)>.*?</system\.\1>}{}gs' "$bundle/MissionPlanner.dll.config"
# the commit this app is built from, which "Check for updates" compares with the newest build
# kept on this Mac (~/MissionPlanner-native/BUILD.txt, written by the workflow)
build_commit=${GITHUB_SHA:-$(git -C "$source_dir" rev-parse HEAD 2>/dev/null || echo unknown)}
# (and when: an update is a newer build, not just another one)
echo "$build_commit $(date +%s)" > "$app/Contents/Resources/BUILD.txt"
# the Log Analyzer: ArduPilot's own (Python, 4.5.7), which Windows runs frozen as runner.exe;
# Utilities/LogAnalyzer.cs runs this copy with python3
rm -rf -- "$bundle/LogAnalyzer"
cp -R "$native_dir/../LogAnalyzer" "$bundle/LogAnalyzer"
find "$bundle/LogAnalyzer" -name __pycache__ -prune -exec rm -rf {} +
# ... and a Python for it, as Windows runs it frozen (runner.exe) with nothing to install: a
# self-contained one (python-build-standalone) with numpy (the optical flow test), kept in a
# cache between builds. Without it the analyzer uses Homebrew's or the system's python3.
py_version=3.12.8 py_build=20250115
py_arch=$(uname -m | sed 's/arm64/aarch64/')
py_cache="$HOME/Library/Caches/MissionPlanner-native/python-$py_version-$py_build-$py_arch"
if [[ ! -x $py_cache/python/bin/python3 ]]; then
    rm -rf "$py_cache.new" && mkdir -p "$py_cache.new"
    if curl -fsSL --retry 3 "https://github.com/astral-sh/python-build-standalone/releases/download/$py_build/cpython-$py_version%2B$py_build-$py_arch-apple-darwin-install_only_stripped.tar.gz" \
            | tar -xz -C "$py_cache.new" \
        && "$py_cache.new/python/bin/python3" -m pip install -q --no-cache-dir --disable-pip-version-check numpy; then
        rm -rf "$py_cache" && mv "$py_cache.new" "$py_cache"
    else
        echo "Python for the Log Analyzer could not be prepared (it will use python3 from the system)" >&2
        rm -rf "$py_cache.new"
    fi
fi
if [[ -x $py_cache/python/bin/python3 ]]; then
    rm -rf -- "$app/Contents/Resources/python"
    cp -R "$py_cache/python" "$app/Contents/Resources/python"
    # every program and library in it must carry a valid signature (Apple Silicon kills the
    # process that loads one without)
    while IFS= read -r -d '' f; do
        if file -b "$f" | grep -q Mach-O; then
            codesign --verify "$f" 2>/dev/null || codesign --force --sign - "$f"
        fi
    done < <(find "$app/Contents/Resources/python" -type f \( -name '*.so' -o -name '*.dylib' -o -perm -u+x \) -print0)
    echo "Python: $("$app/Contents/Resources/python/bin/python3" -c 'import sys, numpy; print(sys.version.split()[0], "numpy", numpy.__version__)')"
fi
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
if [[ ! -f $simpleble_build/out/libsimplecble.dylib ]] && ! command -v cmake >/dev/null; then
    echo 'cmake not found (brew install cmake): SimpleBLE not built, Bluetooth LE will be unavailable.' >&2
elif [[ ! -f $simpleble_build/out/libsimplecble.dylib ]]; then
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

# GDAL (georeferenced images on the map: Utilities/GdalCli runs gdalinfo, gdalwarp and
# gdal_translate), which Windows ships with Mission Planner. Homebrew's gdal brings 175
# libraries (940 MB); this one is built from source once (cached) with the drivers the map
# overlay reads and writes (GeoTIFF, VRT, PNG, JPEG, GIF) and GDAL's own copies of their
# libraries, only PROJ from Homebrew (brew install proj). In Contents/Resources/gdal, with
# every library it loads (dylibbundler, @executable_path) and GDAL's and PROJ's data.
gdal_version=3.10.1
gdal_cache="$HOME/Library/Caches/MissionPlanner-native/gdal-$gdal_version"
proj_prefix=$(brew --prefix proj 2>/dev/null || true)
if [[ ! -x $gdal_cache/bin/gdalinfo && -n $proj_prefix && -d $proj_prefix/lib/cmake ]] && command -v cmake >/dev/null; then
    gdal_src="$source_dir/bin/macos-native-work/gdal-src"
    rm -rf -- "$gdal_src" "$gdal_cache.new" && mkdir -p -- "$gdal_src"
    if curl -fsSL --retry 3 "https://github.com/OSGeo/gdal/releases/download/v$gdal_version/gdal-$gdal_version.tar.gz" \
            | tar -xz -C "$gdal_src" \
        && cmake -S "$gdal_src/gdal-$gdal_version" -B "$gdal_src/build" -DCMAKE_BUILD_TYPE=Release \
            -DCMAKE_INSTALL_PREFIX="$gdal_cache.new" -DCMAKE_PREFIX_PATH="$proj_prefix" \
            -DBUILD_SHARED_LIBS=ON -DBUILD_APPS=ON -DBUILD_TESTING=OFF \
            -DBUILD_PYTHON_BINDINGS=OFF -DBUILD_JAVA_BINDINGS=OFF -DBUILD_CSHARP_BINDINGS=OFF \
            -DGDAL_USE_EXTERNAL_LIBS=OFF -DGDAL_USE_TIFF_INTERNAL=ON -DGDAL_USE_GEOTIFF_INTERNAL=ON \
            -DGDAL_USE_PNG_INTERNAL=ON -DGDAL_USE_JPEG_INTERNAL=ON -DGDAL_USE_GIF_INTERNAL=ON \
            -DGDAL_USE_ZLIB_INTERNAL=ON -DGDAL_USE_JSONC_INTERNAL=ON \
            -DGDAL_BUILD_OPTIONAL_DRIVERS=OFF -DOGR_BUILD_OPTIONAL_DRIVERS=OFF \
            -DGDAL_ENABLE_DRIVER_PNG=ON -DGDAL_ENABLE_DRIVER_JPEG=ON -DGDAL_ENABLE_DRIVER_GIF=ON \
            > "$source_dir/bin/macos-native-work/gdal-cmake.log" 2>&1 \
        && cmake --build "$gdal_src/build" -j "$(sysctl -n hw.ncpu)" > "$source_dir/bin/macos-native-work/gdal-build.log" 2>&1 \
        && cmake --install "$gdal_src/build" > /dev/null 2>&1; then
        rm -rf -- "$gdal_cache" && mv -- "$gdal_cache.new" "$gdal_cache"
    else
        echo "GDAL: could not be built (bin/macos-native-work/gdal-*.log)" >&2
        tail -30 "$source_dir/bin/macos-native-work/gdal-cmake.log" "$source_dir/bin/macos-native-work/gdal-build.log" 2>/dev/null >&2 || true
        rm -rf -- "$gdal_cache.new"
    fi
    rm -rf -- "$gdal_src"
fi
if [[ -x $gdal_cache/bin/gdalinfo ]] && command -v dylibbundler >/dev/null; then
    gdal="$app/Contents/Resources/gdal"
    rm -rf -- "$gdal" && mkdir -p -- "$gdal/bin" "$gdal/lib" "$gdal/share" "$source_dir/bin/macos-native-work"
    for tool in gdalinfo gdalwarp gdal_translate; do
        cp -L "$gdal_cache/bin/$tool" "$gdal/bin/"
    done
    chmod -R u+w "$gdal"
    fix=()
    for f in "$gdal"/bin/*; do fix+=(-x "$f"); done
    # (dylibbundler asks on the terminal for a library it cannot find: no terminal here)
    dylibbundler -of -b "${fix[@]}" -d "$gdal/lib" -p '@executable_path/../lib/' -s "$gdal_cache/lib" -s "$(brew --prefix)/lib" \
        < /dev/null > "$source_dir/bin/macos-native-work/gdal-dylibbundler.log" 2>&1 \
        || { echo "GDAL bundling failed" >&2; tail -20 "$source_dir/bin/macos-native-work/gdal-dylibbundler.log" >&2; }
    # each run path once: dyld refuses a binary that lists one twice
    for f in "$gdal"/bin/* "$gdal"/lib/*.dylib; do
        for rpath in $(otool -l "$f" | awk '/cmd LC_RPATH/ {getline; getline; print $2}' | sort | uniq -d); do
            while (( $(otool -l "$f" | awk '/cmd LC_RPATH/ {getline; getline; print $2}' | grep -cxF -- "$rpath") > 1 )); do
                install_name_tool -delete_rpath "$rpath" "$f"
            done
        done
    done
    cp -RL "$gdal_cache/share/gdal" "$gdal/share/gdal"
    # PROJ's database and settings, not Homebrew's datum-shift grids (hundreds of MB of .tif
    # files; Windows' GDAL has none either)
    if [[ -n $proj_prefix && -d $proj_prefix/share/proj ]]; then
        mkdir -p "$gdal/share/proj"
        find -L "$proj_prefix/share/proj" -maxdepth 1 -type f ! -iname '*.tif' ! -iname '*.tiff' -exec cp -L {} "$gdal/share/proj/" \;
    fi
    codesign --force --sign - "$gdal"/lib/*.dylib "$gdal"/bin/*
    if otool -L "$gdal"/lib/*.dylib "$gdal"/bin/* | grep -E '^\s+/(opt|usr/local)/' >&2; then
        echo "GDAL: references outside the app remain (above)" >&2
    fi
    echo "GDAL: $(GDAL_DATA="$gdal/share/gdal" PROJ_DATA="$gdal/share/proj" GDAL_DRIVER_PATH=disable "$gdal/bin/gdalinfo" --version 2>&1 | head -1), $(ls "$gdal/lib" | wc -l | tr -d ' ') libraries, $(du -sh "$gdal" | cut -f1)"
else
    echo "GDAL not bundled (needs brew install proj cmake dylibbundler)" >&2
fi

# adb (Android platform-tools), which Windows ships as adb.exe for flashing Parrot vehicles:
# Google's own macOS build (signed by Google, universal), kept in a cache between builds;
# Firmware.AdbPath finds it in Contents/Resources/platform-tools
adb_cache="$HOME/Library/Caches/MissionPlanner-native/platform-tools"
if [[ ! -x $adb_cache/adb ]]; then
    adb_zip="$(mktemp -d)/platform-tools.zip"
    if curl -fsSL --retry 3 -o "$adb_zip" https://dl.google.com/android/repository/platform-tools-latest-darwin.zip \
        && rm -rf "$adb_cache.new" && mkdir -p "$adb_cache.new" && ditto -x -k "$adb_zip" "$adb_cache.new" \
        && [[ -x $adb_cache.new/platform-tools/adb ]]; then
        rm -rf "$adb_cache" && mv "$adb_cache.new/platform-tools" "$adb_cache" && rm -rf "$adb_cache.new"
    else
        echo "adb: platform-tools could not be downloaded (Parrot flashing will need brew install android-platform-tools)" >&2
    fi
    rm -rf "$(dirname "$adb_zip")"
fi
if [[ -x $adb_cache/adb ]]; then
    mkdir -p -- "$app/Contents/Resources/platform-tools"
    cp -- "$adb_cache/adb" "$adb_cache/NOTICE.txt" "$app/Contents/Resources/platform-tools/" 2>/dev/null \
        || cp -- "$adb_cache/adb" "$app/Contents/Resources/platform-tools/"
    codesign --verify "$app/Contents/Resources/platform-tools/adb" || codesign --force --sign - "$app/Contents/Resources/platform-tools/adb"
    echo "adb: $("$app/Contents/Resources/platform-tools/adb" version | head -1)"
fi

# Contents/MonoBundle is not a nested-code location for codesign --deep (Frameworks,
# PlugIns, MacOS, ...): the libraries there are sealed as the app's resources, never
# signed themselves. One whose signature install_name_tool broke above (SDL2, SDL3,
# libusb, SimpleBLE) stays broken under a valid app signature, and the kernel kills the
# process that loads it (SIGKILL inside dlopen: the joystick and DFU flashing). Sign each.
while IFS= read -r -d '' lib; do
    codesign --force --sign - "$lib"
done < <(find "$bundle" -type f -name '*.dylib' -print0)
# the bundle changed after the SDK signed it
codesign --force --deep --sign - "$app"
codesign --verify --strict "$app" && echo "Signature: valid"
# and the libraries one by one, which the app's own verification does not do
invalid=0
while IFS= read -r -d '' lib; do
    codesign --verify --strict "$lib" 2>/dev/null || { echo "invalid signature: $lib" >&2; invalid=$((invalid + 1)); }
done < <(find "$bundle" -type f -name '*.dylib' -print0)
[[ $invalid == 0 ]] || { echo "$invalid libraries with an invalid signature" >&2; exit 1; }
echo "Library signatures: valid"
echo "App: $app"
