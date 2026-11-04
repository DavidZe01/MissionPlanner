# mac-playground (eM Client): Windows Forms on Cocoa

Native macOS backend for Mission Planner's user interface: a fork of Mono's
System.Windows.Forms with a Cocoa (AppKit) driver, and System.Drawing on
CoreGraphics, running on .NET for macOS (arm64 and x64).

Source: https://github.com/emclient/mac-playground at commit
aba3290a5c63c7c6c6b323da42fb7b9d6bca3733 (2024-09-12), MIT X11 license (see
below), directories System.Windows.Forms, System.Windows.Forms.Primitives,
System.Drawing.Common, MacApi, Mono.WebBrowser and FormsTest (its test app).

Changes for Mission Planner:
- target net10.0-macos instead of net8.0-macos (and no iOS/Windows targets);
- nuget.org only, and without the Microsoft.DotNet.ApiCompat build check,
  which comes from Microsoft's internal package feeds;
- assembly identities of .NET Framework's System.Windows.Forms and System.Drawing
  (public signing with their public keys, `keys/`), and a System.Drawing facade
  (`System.Drawing.Facade`) forwarding every type, so that .NET Framework
  libraries (OpenTK, ObjectListView, ...) load against these assemblies;
- ToolStrip: only drop downs are native menus (`native_rendering`); tool strips,
  menu strips and status strips on a form lay out and draw their items;
- Button: a subclass that overrides OnPaint (a themed button) paints itself instead
  of being an NSButton;
- timers: a tick wakes a modal loop (as WM_TIMER does on Windows), so a dialog
  closed from a timer returns from ShowDialog;
- `Control.Visible` read from a worker thread (XplatUICocoa.IsVisible) does not
  call AppKit off the main thread, and `Invalidate` from a worker thread is passed to
  the main thread (Windows allows both);
- `Graphics.FromImage`: each Graphics draws through a context of its own on the
  bitmap's memory (two Graphics of one bitmap no longer share transform, clip and
  saved states: the second drew upside down), and what is drawn shows in the
  bitmap's image while the Graphics lives;
- `Graphics.Clear` replaces the pixels (alpha included) as in GDI+, instead of
  drawing over them: `Clear(Color.Transparent)` empties a back buffer;
- `new Bitmap(width, height, stride, format, scan0)` (video frames for the HUD):
  a copy of the pixels in the internal layout; `LockBits(ReadOnly)` hands out a
  converted copy and leaves the bitmap as it is (OpenGL textures);
- `Control.CreateControl` does nothing for a disposed control (a form that closes
  itself while it is shown);
- a text field's formatter does not throw when AppKit calls it while its control is
  being disposed;
- small API gaps: SaveFileDialog.OverwritePrompt/CreatePrompt,
  ImageAttributes.SetRemapTable (no-op); clipper's top-level types internal.

MIT X11 License: Permission is hereby granted, free of charge, to any person
obtaining a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including without
limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions: The
above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS
IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN
ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
