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
[[ $(uname -s) == Darwin ]] || extra+=(-p:MissionPlannerMacCompileOnly=true -p:OutputType=Library -p:IsMacEnabled=false -p:BundleOriginalResources=false -p:AppManifestDetectionEnabled=false -p:StartupObject=)
cd -- "$source_dir"
dotnet build MissionPlanner.csproj -c Release \
    "-p:DirectoryBuildTargetsPath=$native_dir/Build.targets" -p:MissionPlannerMacNative=true \
    -p:MissionPlannerLinuxBuild=true \
    ${extra[@]+"${extra[@]}"} "$@"

[[ $(uname -s) == Darwin ]] || exit 0

# Complete the app bundle: Mission Planner looks for its data files (Scripts, plugins,
# graphs, NoFly, XML, ...) next to its assembly, which in the bundle is Contents/MonoBundle.
out_dir="$source_dir/bin/macos-native"
# named after ApplicationTitle: "Mission Planner.app"
app=$(find "$out_dir" -maxdepth 1 -name '*.app' | head -1)
[[ -n $app && -f $app/Contents/Info.plist && -d $app/Contents/MacOS ]] || { echo "no app bundle in $out_dir" >&2; exit 1; }
bundle="$app/Contents/MonoBundle"
rsync -a --ignore-existing --exclude '*.app' --exclude 'runtimes/' --exclude '*.pdb' "$out_dir/" "$bundle/"
# native libraries the code loads by name (see MacNative.cs)
arch=$(uname -m)
cc -dynamiclib -O2 -arch "$arch" -install_name @loader_path/libuser32stub.dylib \
    -o "$bundle/libuser32stub.dylib" "$native_dir/../user32stub.c"
for lib in sdl2:libSDL2-2.0.0.dylib libusb:libusb-1.0.0.dylib; do
    src="$(brew --prefix "${lib%%:*}" 2>/dev/null)/lib/${lib#*:}"
    if [[ -f $src ]]; then
        cp -- "$src" "$bundle/" && chmod u+w "$bundle/${lib#*:}"
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

# the bundle changed after the SDK signed it
codesign --force --deep --sign - "$app"
codesign --verify --strict "$app" && echo "Signature: valid"
echo "App: $app"
