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

Serial ports appear as `/dev/cu.usbmodem*` / `/dev/cu.usbserial*`. Logs are in
`~/Library/Logs/MissionPlanner`. Settings, logs and maps are kept in
`~/.local/share/Mission Planner` (shared with the Linux build's layout).

## Build

On any machine with the .NET 10 SDK and Mono, build the managed application
(`Linux/build.sh`). Then, on a Mac with Homebrew:

```sh
brew install pkgconf cairo pango giflib libtiff libexif jpeg-turbo libpng libx11 dylibbundler
macOS/package.sh bin/linux/MissionPlanner-linux-x86_64
```

The result is `bin/macos/Mission Planner.app` and `MissionPlanner-macos-<arch>.zip`.
`.github/workflows/macos-arm64.yml` does both steps and tests the app on an
Apple Silicon runner (`--self-test` and the `--tour` screen tour).
