# Mission Planner on macOS (Apple Silicon and Intel)

`Mission Planner.app` runs the same `MissionPlanner.exe` as the Windows
release. The managed code is AnyCPU .NET IL; on a Mac it is compiled to native
arm64 (or x86_64) machine code by the Mono runtime. Nothing is emulated or
virtualized, and Rosetta is not used: the launcher refuses to start if the
installed Mono has no arm64 code on an Apple Silicon Mac.

Mono's Windows Forms has no 64-bit Cocoa backend, so windows are drawn through
X11 by XQuartz. The app bundles the native libraries it needs (libSkiaSharp and
a libgdiplus built with X11 support, plus their dependencies).

## Install

1. Install [Homebrew](https://brew.sh), then the runtime and the X server:

   ```sh
   brew install mono
   brew install --cask xquartz
   ```

   Log out and back in once after installing XQuartz.

2. Download `MissionPlanner-macos-arm64.zip` (Apple Silicon) and unzip it.
   The app is signed ad hoc, not notarized, so clear the download quarantine:

   ```sh
   xattr -dr com.apple.quarantine "Mission Planner.app"
   ```

3. Move `Mission Planner.app` to Applications and open it. XQuartz starts
   automatically.

Optional, for the features that use them (as on Windows, where they are also
separate installs):

| Feature | Install |
| --- | --- |
| HUD video streams, local cameras, OSD Video tool | `brew install gstreamer` |
| GeoTIFF / imagery overlays (GDAL Custom map) | `brew install gdal` |
| VLC video test tool | VLC.app in /Applications |

Bundled, nothing to install: native SITL simulators (ArduCopter, ArduPlane,
ArduRover, ArduHeli), joysticks (SDL2), Bluetooth LE (SimpleBLE/CoreBluetooth),
speech (macOS `say`), MAVFTP "Mount as Drive" (WebDAV, mounted with the
built-in client), OpenGL HUD (XQuartz GLX).

The HUD uses OpenGL through XQuartz when XQuartz reports a usable renderer
(`/opt/X11/bin/glxinfo -B`), and GDI+ drawing otherwise. Set
`MISSIONPLANNER_NO_OPENGL=1` to force GDI+ (e.g. with graphics problems).

Serial ports appear as `/dev/cu.usbmodem*` / `/dev/cu.usbserial*`. Logs are in
`~/Library/Logs/MissionPlanner`. Settings, logs and maps are kept in
`~/.local/share/Mission Planner` (shared with the Linux build's layout).

## Build

On any machine with the .NET 10 SDK and Mono, build the managed application
(`Linux/build.sh`). Then, on a Mac with Homebrew:

```sh
brew install pkgconf cairo pango giflib libtiff libexif jpeg-turbo libpng libx11 dylibbundler sdl2
macOS/package.sh bin/linux/MissionPlanner-linux-x86_64
```

The result is `bin/macos/Mission Planner.app` and `MissionPlanner-macos-<arch>.zip`.
`.github/workflows/macos-arm64.yml` does both steps and tests the app on an
Apple Silicon runner (`--self-test` and the `--tour` screen tour).
