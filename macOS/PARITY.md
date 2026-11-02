# Mission Planner on macOS: feature parity with Windows

The macOS app runs the same `MissionPlanner.exe` as Windows, so everything
written in managed code (MAVLink, mission planning, parameters, calibration
flows, log analysis, maps, scripting, plugins, ...) is the same code. What
differs between platforms is what that code asks of the operating system.
This page lists each of those dependencies, what replaces it on macOS, and how
it was verified.

Verification: `.github/workflows/macos-arm64.yml` builds the app and runs it
on an Apple Silicon runner (`macos-15`), natively as an arm64 process
(`SMOKE_RUNTIME process=Arm64`). `macOS/ScreenTour.cs` opens every main
screen, every setup/configuration page and every window with screenshots, then
repeats with a vehicle: the bundled native ArduCopter SITL, testing parameter
read/write, mission upload/download, MAVFTP, logs, arming and takeoff.
`macOS/summarize-tour.py` produces the counts below.

## Results

@RESULTS@

## Operating-system dependencies

| Feature | Windows | macOS | Status |
| --- | --- | --- | --- |
| Windows Forms UI | .NET Framework | Mono (native arm64) + X11 via XQuartz | tested |
| GDI+ drawing | gdiplus.dll | libgdiplus built with X11, bundled | tested |
| Skia rendering | libSkiaSharp.dll | libSkiaSharp.dylib (universal), bundled | tested |
| OpenGL HUD, 3D map | OpenTK WGL | OpenTK GLX via XQuartz | tested |
| Serial ports | Win32 COM | /dev/cu.* (Mono termios) | enumeration tested |
| Board detection for firmware install | WMI / SetupAPI | IOKit registry (`ioreg`) | parser tested |
| TCP/UDP/WebSocket links | managed | same | tested with SITL |
| Bluetooth LE links | SimpleBLE (WinRT) | SimpleBLE 0.9.1 (CoreBluetooth), bundled | built; needs a Mac with Bluetooth |
| WinUSB CDC links | WinUSB | not needed: CDC devices are /dev/cu.usbmodem* | equivalent |
| USB driver install / cleanup | SetupAPI / DIFx | not needed on macOS | n/a |
| Joysticks | DirectInput | SDL2 (IOKit HID), bundled | enumeration tested |
| Speech | SAPI | `say` | tested |
| HUD video streams (RTSP/UDP) | GStreamer | GStreamer (Homebrew) | tested (videotestsrc into HUD) |
| Local camera in HUD | DirectShow | GStreamer `avfvideosrc` | implemented; no camera on CI |
| HUD recording to AVI | managed MJPEG | same | same code |
| OSD Video tool | DirectShow + AVI API | GStreamer + managed MJPEG writer | tested on Linux |
| VLC test tool | libvlc.dll | libvlc.dylib from VLC.app (dlopen) | tested on Linux |
| MAVFTP "Mount as Drive" | Dokan | localhost WebDAV + `mount_webdav` | tested with SITL |
| GDAL imagery overlays | GDAL .NET bindings | GDAL command line tools (Homebrew) | tested on Linux |
| SITL simulator | downloads Windows builds | native macOS builds, bundled | tested |
| Plugins (C# compiled at runtime) | Roslyn | Roslyn on Mono (Span ambiguity fixed) | tested |
| Python scripting | IronPython | same | same code |
| Parrot WiFi check | Windows WLAN API | check skipped, connection decides | equivalent |
| Updates | in-place file update | same mechanism | not tested |
| Explorer .tlog thumbnails | shell extension | Windows Explorer only | n/a |

## Known differences

- Windows are drawn by XQuartz (X11), so they look like the Linux build rather
  than native macOS windows. Mono has no 64-bit Cocoa backend for Windows
  Forms.
- X input methods are disabled (`MONO_WINFORMS_XIM_STYLE=disabled`) because
  Mono's XIM code crashes on macOS; plain typing works, dead-key composition
  may not.
- The bundled simulators are the ArduPilot stable release from build time;
  on Windows the Simulation screen can download other versions.
