# Mission Planner as a native macOS app

`macOS/` (one level up) packages the Mono build, whose windows XQuartz draws through
X11. This directory builds the same Mission Planner code as a native macOS
application instead:

| | X11 build (`macOS/package.sh`) | Native build (`macOS/native/build.sh`) |
| --- | --- | --- |
| Runtime | Mono (Homebrew), JIT to arm64 | .NET 10 for macOS (CoreCLR arm64), inside the app |
| Windows Forms | Mono's, X11 driver | eM Client's fork with a Cocoa driver (`ExtLibs/mac-playground`) |
| System.Drawing | libgdiplus (cairo) | CoreGraphics |
| Windows | X11 windows in XQuartz | AppKit windows (`NSWindow`/`NSView`), Retina |
| Needs installed | Mono, XQuartz | nothing |

## How the build works

`build.sh` runs `dotnet build MissionPlanner.csproj` with
`DirectoryBuildTargetsPath=macOS/native/Build.targets`, which applies to every
project:

- The projects that use Windows Forms or System.Drawing, and the ones that reference
  them (list in `MissionPlannerMacProjects`), build for `net10.0-macos` and reference
  mac-playground's System.Windows.Forms, System.Drawing.Common, Primitives and MacApi.
  Their .NET Framework / NuGet Windows Forms and System.Drawing references are removed.
  The other libraries stay `netstandard2.0`, which .NET runs unchanged.
- `MissionPlanner` itself becomes the app bundle (`org.ardupilot.missionplanner`,
  `Info.plist` here, output `bin/macos-native/`).
- On Linux the build compiles everything but cannot bundle (`MissionPlannerMacCompileOnly`):
  it is the quick way to check the code compiles.

## Identities of mac-playground's assemblies

Binaries built for .NET Framework (OpenTK, ObjectListView, SkiaSharp.Views, ...)
reference `System.Windows.Forms` and `System.Drawing` with Microsoft's public key
tokens, and .NET refuses an assembly whose token differs. So mac-playground's
assemblies are public-signed with those public keys (`ExtLibs/mac-playground/keys`,
public keys only) and have version 10.0.0.0:

- System.Windows.Forms: ECMA key, token b77a5c561934e089
- System.Drawing.Common: Open key, token cc7b13ffcd2ddd51
- System.Drawing (`System.Drawing.Facade`, 4.0.1.0): Microsoft key, token
  b03f5f7f11d50a3a, forwarding every System.Drawing type to System.Drawing.Common and
  System.Drawing.Primitives; it replaces .NET's own facade (4.0.0.0), which forwards only
  the primitive types.

## Run-time differences handled in code

- `MONO` (and `GMaps.IsRunningOnMono`, `PollDelay`, ...) means "not Windows' .NET
  Framework": the code takes the same paths as under Mono on macOS (say, IOKit, ...).
- `MacNative.cs`: native libraries the code names as on Windows/Linux (Mono had dllmap):
  `libdl.so`, `SDL2`, `simpleble-c.dll`, `user32.dll` (no-op stub), `libusb-1.0`,
  `lib*.so.N` (GStreamer from Homebrew). Also the macOS menu bar (application and Edit
  menus: Cmd+C/V/X/A/Z in text fields, Cmd+Q).
- `GLControl.cs`: OpenTK's GLControl drives Carbon AGL on macOS; this one has its
  members without an OpenGL context yet (GDI+ HUD, as in the X11 build).
- Windows Forms resources are BinaryFormatter data: the compatibility implementation
  (`System.Runtime.Serialization.Formatters`) is enabled for the app.
- Name clashes: macOS framework namespaces (`Speech`, `Symbols`) and newer .NET types
  (`System.Reflection.MethodInvoker`, `System.IO.UnixFileMode`) get aliases.

## CI

`.github/workflows/macos-native.yml` builds and starts the app on an Apple Silicon
runner, only when `.github/macos-native-request` changes in a push (or from the
Actions "Run workflow" button), and publishes logs and screenshots to the `ci-native`
branch.
