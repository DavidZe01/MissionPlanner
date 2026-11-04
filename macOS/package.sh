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

# --- SimpleBLE C bindings (Bluetooth LE through CoreBluetooth) ------------------
# CommsBLE uses the SimpleBLE 0.9 C API; only a Windows build ships with the app.
simpleble_version=v0.9.1
simpleble_build="$work_dir/simpleble-$simpleble_version-$arch"
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
# SimpleBLE: point its install names at the bundle.
if [[ -f $simpleble_build/out/libsimplecble.dylib ]]; then
    cp -L -- "$simpleble_build/out/"libsimple*.dylib "$app/Contents/Frameworks/"
    for lib in "$app/Contents/Frameworks/"libsimple*.dylib; do
        [[ -L $lib ]] && continue
        chmod u+w "$lib"
        install_name_tool -id "@loader_path/$(basename -- "$lib")" "$lib"
        for dep in $(otool -L "$lib" | awk 'NR > 1 {print $1}' | grep -E '(@rpath/|/build/).*libsimple' || true); do
            install_name_tool -change "$dep" "@loader_path/$(basename -- "$dep")" "$lib"
        done
    done
    otool -L "$app/Contents/Frameworks/libsimplecble.dylib" | sed 1d
fi
# No-op user32 functions for libraries that call them while painting.
cc -dynamiclib -O2 -arch "$arch" -install_name @loader_path/libuser32stub.dylib \
    -o "$app/Contents/Frameworks/libuser32stub.dylib" "$mac_dir/user32stub.c"
# Runs Mono inside a process whose executable is in this bundle, so macOS reads the
# Bluetooth/camera usage descriptions from our Info.plist (see monohost.c).
cc -O2 -arch "$arch" -o "$app/Contents/MacOS/mono-host" "$mac_dir/monohost.c"
# SDL2 for joysticks (IOKit HID); it only links system frameworks.
sdl2=$(brew --prefix sdl2 2>/dev/null)/lib/libSDL2-2.0.0.dylib
if [[ -f $sdl2 ]]; then
    cp -- "$sdl2" "$app/Contents/Frameworks/"
    chmod u+w "$app/Contents/Frameworks/libSDL2-2.0.0.dylib"
    install_name_tool -id @loader_path/libSDL2-2.0.0.dylib "$app/Contents/Frameworks/libSDL2-2.0.0.dylib"
else
    echo 'SDL2 not found (brew install sdl2): joysticks will be unavailable.' >&2
fi
# libusb for DFU flashing (LibUsbDotNet); it only links system frameworks.
libusb=$(brew --prefix libusb 2>/dev/null)/lib/libusb-1.0.0.dylib
if [[ -f $libusb ]]; then
    cp -- "$libusb" "$app/Contents/Frameworks/"
    chmod u+w "$app/Contents/Frameworks/libusb-1.0.0.dylib"
    install_name_tool -id @loader_path/libusb-1.0.0.dylib "$app/Contents/Frameworks/libusb-1.0.0.dylib"
else
    echo 'libusb not found (brew install libusb): DFU flashing will be unavailable.' >&2
fi
# Use XQuartz's X client libraries instead of Homebrew's copies. Mono's Windows
# Forms, cairo/libgdiplus and XQuartz's libGL (used by the OpenGL HUD) then
# share one libX11, so they can share one X display connection.
x11_lib=/opt/X11/lib
[[ -f $x11_lib/libX11.6.dylib ]] || { echo "XQuartz is required to build ($x11_lib missing)." >&2; exit 1; }
for lib in "$app/Contents/Frameworks/"*.dylib; do
    chmod u+w "$lib"
    for dep in $(otool -L "$lib" | awk 'NR > 1 {print $1}' | grep '^@loader_path/' || true); do
        name=$(basename -- "$dep")
        if [[ $name =~ ^lib(X|xcb) && -f $x11_lib/$name ]]; then
            install_name_tool -change "$dep" "$x11_lib/$name" "$lib"
        fi
    done
done
for lib in "$app/Contents/Frameworks/"lib{X,xcb}*.dylib; do
    [[ -f $lib && -f $x11_lib/$(basename -- "$lib") ]] && rm -f -- "$lib"
done
if otool -L "$app/Contents/Frameworks/"*.dylib | grep -q '@loader_path/lib\(X\|xcb\)'; then
    echo 'Some X11 libraries could not be mapped to XQuartz:' >&2
    otool -L "$app/Contents/Frameworks/"*.dylib | grep '@loader_path/lib\(X\|xcb\)' >&2
fi

# Optional native SITL simulators (directory with ArduCopter, ArduPlane, ...).
if [[ -n ${MP_SITL_DIR:-} && -d $MP_SITL_DIR ]]; then
    mkdir -p -- "$app/Contents/Resources/sitl"
    cp -- "$MP_SITL_DIR"/Ardu* "$app/Contents/Resources/sitl/"
    cp -- "$MP_SITL_DIR"/VERSION.txt "$MP_SITL_DIR"/*.parm "$app/Contents/Resources/sitl/" 2>/dev/null || true
    chmod +x "$app/Contents/Resources/sitl"/Ardu*
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

{
    cat "$managed_dir/BUILD-INFO.txt" 2>/dev/null || true
    echo "macOS bundle built on $(sw_vers -productVersion) $arch"
    echo "libgdiplus $libgdiplus_version (X11), SkiaSharp native $skia_version"
} > "$app/Contents/Resources/app/BUILD-INFO.txt.new"
mv -- "$app/Contents/Resources/app/BUILD-INFO.txt.new" "$app/Contents/Resources/app/BUILD-INFO.txt"

# Apple Silicon only runs signed native code; install_name_tool invalidated the
# linker signatures. Sign ad hoc (users of a downloaded copy clear quarantine).
# Nothing in the bundle may change after this: macOS identifies the app (e.g. for
# its Bluetooth and camera permissions) by this signature.
codesign --force --sign - "$app/Contents/Frameworks/"*.dylib "$app/Contents/Resources/app/libSkiaSharp.dylib"
if [[ -d $app/Contents/Resources/sitl ]]; then
    codesign --force --sign - "$app/Contents/Resources/sitl"/Ardu*
fi
codesign --force --sign - "$app/Contents/MacOS/mono-host"
codesign --force --sign - "$app"
codesign --verify --strict "$app" && echo "Signature: valid"

package_name="MissionPlanner-macos-$arch"
rm -f -- "$output_dir/$package_name.zip"
(cd -- "$output_dir" && ditto -c -k --keepParent "Mission Planner.app" "$package_name.zip" \
    && shasum -a 256 "$package_name.zip" > "$package_name.zip.sha256")
echo "App: $app"
echo "Package: $output_dir/$package_name.zip"
