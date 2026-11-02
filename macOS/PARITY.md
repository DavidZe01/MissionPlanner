# Mission Planner on macOS: feature parity with Windows

The macOS app runs the same `MissionPlanner.exe` as Windows, so everything
written in managed code (MAVLink, mission planning, parameters, calibration
flows, log analysis, maps, scripting, plugins, ...) is the same code. What
differs between platforms is what that code asks of the operating system.
This page lists each of those dependencies, what replaces it on macOS, and how
it was verified.

Verification: `.github/workflows/macos-arm64.yml` builds the app and runs it
on Apple Silicon runners (macOS 15 and 26), natively as an arm64 process
(`SMOKE_RUNTIME process=Arm64`). `macOS/ScreenTour.cs` opens every main
screen, every setup/configuration page and every window with screenshots, then
repeats with a vehicle: the bundled native ArduCopter SITL, testing parameter
read/write, mission upload/download, MAVFTP, logs, arming and takeoff.
`macOS/summarize-tour.py` produces the counts below.

## Results

### macOS 26, Apple Silicon: offline tour + tour connected to the native ArduCopter SITL

A page counts when it works in either tour (some pages need a vehicle).
Connected checks include parameter write, mission upload/download, MAVFTP,
onboard log list, and arming and taking off to 7 m in GUIDED mode.

```
feature      14/14  
screen        7/7   
page         78/80  
   FAIL page:MissionPlanner.Controls.OpenGLtest: NotSupportedException: OpenGL is not available on this display @ at MissionPlanner.Controls.OpenGLtest..ctor () [0x00099] in <6cf2a4caf11844e7af73c6eeb46305d1>:
   FAIL page:MissionPlanner.Controls.OpenGLtest2: NotSupportedException: OpenGL is not available on this display @ at MissionPlanner.Controls.OpenGLtest2..ctor () [0x0011f] in <6cf2a4caf11844e7af73c6eeb46305d1>
form         38/38  
connect       1/1   
total       138/140  (98.6%)
```

### Linux ARM64 (ubuntu-24.04-arm, native), without a vehicle

```
page         78/80  
   FAIL page:MissionPlanner.GCSViews.ConfigurationView.ConfigParamLoading: SKIPPED (hangs the tour)
   FAIL page:MissionPlanner.GCSViews.ConfigurationView.ConfigHWCompass2: NullReferenceException: Object reference not set to an instance of an object @ at MissionPlanner.GCSViews.ConfigurationView.ConfigHWCompass2.Activate () [0x002f
feature       7/7   
screen        7/7   
form         38/38  
total       130/132  (98.5%)
```


## Operating-system dependencies

| Feature | Windows | macOS | Status |
| --- | --- | --- | --- |
| Windows Forms UI | .NET Framework | Mono (native arm64) + X11 via XQuartz | tested |
| GDI+ drawing | gdiplus.dll | libgdiplus built with X11, bundled | tested |
| Skia rendering | libSkiaSharp.dll | libSkiaSharp.dylib (universal), bundled | tested |
| OpenGL HUD, 3D map | OpenTK WGL | OpenTK GLX via XQuartz; GDI+ HUD where GLX has no renderer | GDI+ path tested; GLX needs a Mac with a GPU (CI VMs have none) |
| Serial ports | Win32 COM | /dev/cu.* (Mono termios) | enumeration tested |
| Board detection for firmware install | WMI / SetupAPI | IOKit registry (`ioreg`) | parser tested |
| TCP/UDP/WebSocket links | managed | same | tested with SITL |
| Bluetooth LE links | SimpleBLE (WinRT) | SimpleBLE 0.9.1 (CoreBluetooth), bundled | built; needs a Mac with Bluetooth |
| WinUSB CDC links | WinUSB | not needed: CDC devices are /dev/cu.usbmodem* | equivalent |
| USB driver install / cleanup | SetupAPI / DIFx | not needed on macOS | n/a |
| Joysticks | DirectInput | SDL2 (IOKit HID), bundled | enumeration tested |
| Speech | SAPI | `say` | tested |
| HUD video streams (RTSP/UDP) | GStreamer | GStreamer (Homebrew) | tested: frames into the HUD |
| Local camera in HUD | DirectShow | GStreamer `avfvideosrc` | implemented; no camera on CI |
| HUD recording to AVI | managed MJPEG | same | same code |
| OSD Video tool | DirectShow + AVI API | GStreamer + managed MJPEG writer | tested on Linux (overlay, timing, AVI output) |
| VLC test tool | libvlc.dll | libvlc.dylib from VLC.app (dlopen) | tested on Linux |
| MAVFTP "Mount as Drive" | Dokan | localhost WebDAV + `mount_webdav` | mount and listing tested with SITL (Probe.exe); writing through the mount not tested |
| GDAL imagery overlays | GDAL .NET bindings | GDAL command line tools (Homebrew) | tested on Linux |
| SITL simulator | downloads Windows builds | native macOS builds, bundled | tested |
| Plugins (C# compiled at runtime) | Roslyn | Roslyn on Mono (Span ambiguity fixed) | tested |
| Python scripting | IronPython | same | same code |
| Parrot WiFi check | Windows WLAN API | check skipped, connection decides | equivalent |
| Updates | in-place file update | same mechanism | not tested |
| Explorer .tlog thumbnails | shell extension | Windows Explorer only | n/a |

## Fixed along the way (also affect Linux/Windows)

- Joysticks never worked under Mono: the send loop exited on Mono.
- Plugins using Span/MemoryExtensions failed to compile under Mono.
- USB VID/PID board matching never matched on Linux (missing `&` suffix).
- GPS order page crashed with current firmware (renamed CAN node parameters).
- MSBuild created only one of `Plugins/` and `plugins/` on case-sensitive filesystems.

## Known differences

- Windows are drawn by XQuartz (X11), so they look like the Linux build rather
  than native macOS windows. Mono has no 64-bit Cocoa backend for Windows
  Forms.
- X input methods are disabled (`MONO_WINFORMS_XIM_STYLE=disabled`) because
  Mono's XIM code crashes on macOS; plain typing works, dead-key composition
  may not.
- Not testable on CI and so unverified: Bluetooth LE, local cameras and
  joysticks with real hardware, firmware flashing to a board, OpenGL on a GPU.
- The bundled simulators are the ArduPilot stable release from build time;
  on Windows the Simulation screen can download other versions.
