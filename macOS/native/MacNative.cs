// Mission Planner as a native macOS app (.NET for macOS with the Cocoa Windows Forms
// backend in ExtLibs/mac-playground). Compiled only into that build (MACNATIVE).
//
// - Native libraries: the code names them as on Windows or Linux (Mono mapped those
//   names with dllmap); resolve them to the copies in the app bundle or Homebrew.
// - The macOS menu bar: an application menu (About, Quit) and an Edit menu, which is
//   what makes Cmd+C / Cmd+V / Cmd+X / Cmd+A / Cmd+Z work in text fields.
// - Files opened from Finder (.tlog, .bin, .log; document types in Info.plist).
#if MACNATIVE
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using AppKit;
using ObjCRuntime;

namespace MissionPlanner
{
    internal static class MacNative
    {
        static readonly string bundleDir = AppContext.BaseDirectory;

        // GStreamer inside the app (build.sh), before a Homebrew or framework one
        static readonly string bundledGStreamer = Path.Combine(AppContext.BaseDirectory, "gstreamer");

        static readonly string[] libraryDirs =
        {
            Path.Combine(AppContext.BaseDirectory, "gstreamer", "lib"),
            AppContext.BaseDirectory,
            "/opt/homebrew/lib",
            "/usr/local/lib",
            "/Library/Frameworks/GStreamer.framework/Versions/1.0/lib",
        };

        static bool initialised;

        public static void Init()
        {
            // the screen tour starts Program.Main again
            if (initialised)
                return;
            initialised = true;
            // a sandboxed data directory (CI on a self-hosted runner that is also someone's
            // everyday Mac: a direct run, outside the screen tour, would otherwise read and
            // write the real ~/Library/Application Support/Mission Planner)
            var dataDir = Environment.GetEnvironmentVariable("MISSIONPLANNER_DATA_DIR");
            if (!string.IsNullOrEmpty(dataDir))
                MissionPlanner.Utilities.Settings.CustomUserDataDirectory = dataDir;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveUnmanagedDll;
            // before anything else uses OpenTK, so that it never sets up its own Cocoa layer
            OpenTK.GLControl.InitToolkit();
            // diagnostics: every distinct exception, also the ones Mission Planner handles
            if (Environment.GetEnvironmentVariable("MISSIONPLANNER_FIRSTCHANCE") == "1")
            {
                var seen = new HashSet<string>();
                AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
                {
                    try
                    {
                        var ex = e.Exception;
                        var frame = new System.Diagnostics.StackTrace(ex, false).GetFrame(0)?.GetMethod();
                        var key = ex.GetType().Name + " " + ex.Message + " @ " + frame?.DeclaringType + "." + frame?.Name;
                        lock (seen)
                        {
                            if (seen.Count > 300 || !seen.Add(key))
                                return;
                        }
                        // where it came from: the calling frames (a first-chance exception has
                        // only the throwing frame yet), Mission Planner's first
                        var callers = new System.Diagnostics.StackTrace(1, false).GetFrames() ?? new System.Diagnostics.StackFrame[0];
                        var where = new List<string>();
                        foreach (var f in callers)
                        {
                            var m = f.GetMethod();
                            var type = m?.DeclaringType?.FullName ?? "";
                            if (type.StartsWith("MissionPlanner.MacNative") || type.StartsWith("System.Runtime") || type.StartsWith("System.Diagnostics"))
                                continue;
                            where.Add(type + "." + m?.Name);
                            if (where.Count == 8)
                                break;
                        }
                        Console.Error.WriteLine("FIRSTCHANCE " + key + " | " + string.Join(" < ", where));
                    }
                    catch
                    {
                    }
                };
            }
            // log4net writes to ${ALLUSERSPROFILE}/Mission Planner/MissionPlanner.log (app.config):
            // on macOS that is ~/Library/Logs/Mission Planner/MissionPlanner.log
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALLUSERSPROFILE")))
                Environment.SetEnvironmentVariable("ALLUSERSPROFILE",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs"));
            // the GStreamer inside the app: its plugins only, a registry of its own, and plugins
            // scanned in this process (the plugin scanner helper is not bundled). Read by gst_init.
            if (Directory.Exists(Path.Combine(bundledGStreamer, "plugins")))
            {
                SetNativeEnvironment("GST_PLUGIN_SYSTEM_PATH", Path.Combine(bundledGStreamer, "plugins"));
                SetNativeEnvironment("GST_PLUGIN_PATH", null);
                SetNativeEnvironment("GST_REGISTRY_FORK", "no");
                var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "Mission Planner");
                Directory.CreateDirectory(cache);
                SetNativeEnvironment("GST_REGISTRY", Path.Combine(cache, "gstreamer-1.0-registry.bin"));
            }
            // the simulators bundled by build.sh (MP_SITL_DIR): Contents/Resources/sitl, read by
            // GCSViews.SITL.BundledPath (the X11 app's launcher.sh sets the same variable)
            var sitl = Path.GetFullPath(Path.Combine(bundleDir, "..", "Resources", "sitl"));
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MISSIONPLANNER_SITL_DIR")) && File.Exists(Path.Combine(sitl, "ArduCopter")))
                Environment.SetEnvironmentVariable("MISSIONPLANNER_SITL_DIR", sitl);
        }

        [DllImport("libc", EntryPoint = "setenv")]
        static extern int setenv(string name, string value, int overwrite);

        [DllImport("libc", EntryPoint = "unsetenv")]
        static extern int unsetenv(string name);

        // .NET keeps its own copy of the environment on Unix: native libraries (GStreamer's
        // g_getenv) read the process environment, so set both
        static void SetNativeEnvironment(string name, string value)
        {
            Environment.SetEnvironmentVariable(name, value);
            if (value == null)
                unsetenv(name);
            else
                setenv(name, value, 1);
        }

        static IntPtr ResolveUnmanagedDll(Assembly assembly, string name)
        {
            foreach (var candidate in Candidates(name))
            {
                // no File.Exists: system libraries (/usr/lib) live only in the dyld shared cache
                if (NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        }

        static IEnumerable<string> Candidates(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "libdl.so":
                case "libdl.so.2":
                case "libdl":
                case "libc":
                    yield return "/usr/lib/libSystem.B.dylib";
                    yield break;
                case "sdl2":
                    // Homebrew's SDL2 is sdl2-compat: it loads @loader_path/libSDL3.dylib (bundled
                    // next to it) and, when that fails, shows an alert from whatever thread loaded
                    // it, which aborts the app
                    foreach (var dir in libraryDirs) yield return Path.Combine(dir, "libSDL2-2.0.0.dylib");
                    yield break;
                case "simpleble-c.dll":
                case "simpleble-c":
                    yield return Path.Combine(bundleDir, "libsimplecble.dylib");
                    yield break;
                case "user32.dll":
                case "user32":
                    // no-op versions of the few Win32 calls libraries make while painting
                    yield return Path.Combine(bundleDir, "libuser32stub.dylib");
                    yield break;
                case "libsystem.io.ports.native":
                    // System.IO.Ports' helper (build.sh bundles the osx-arm64 one from 10.x)
                    yield return Path.Combine(bundleDir, "libSystem.IO.Ports.Native.dylib");
                    yield break;
                case "libusb-1.0":
                case "libusb-1.0.dll":
                    foreach (var dir in libraryDirs) yield return Path.Combine(dir, "libusb-1.0.0.dylib");
                    yield break;
            }

            // Linux sonames (libgstreamer-1.0.so.0, ...): libgstreamer-1.0.0.dylib
            var so = Regex.Match(name, @"^(lib.+?)\.so(?:\.(\d+))?(?:\.\d+)*$");
            if (so.Success)
            {
                var stem = so.Groups[1].Value;
                foreach (var dir in libraryDirs)
                {
                    if (so.Groups[2].Success)
                        yield return Path.Combine(dir, stem + "." + so.Groups[2].Value + ".dylib");
                    yield return Path.Combine(dir, stem + ".dylib");
                }
            }
        }

        /// <summary>The standard macOS application and Edit menus.</summary>
        public static void CreateMenuBar()
        {
            try
            {
                var menuBar = new NSMenu("");

                var appItem = new NSMenuItem("");
                var appMenu = new NSMenu("Mission Planner");
                appMenu.AddItem(new NSMenuItem("About Mission Planner", new Selector("orderFrontStandardAboutPanel:"), ""));
                appMenu.AddItem(NSMenuItem.SeparatorItem);
                appMenu.AddItem(new NSMenuItem("Hide Mission Planner", new Selector("hide:"), "h"));
                var hideOthers = new NSMenuItem("Hide Others", new Selector("hideOtherApplications:"), "h");
                hideOthers.KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask;
                appMenu.AddItem(hideOthers);
                appMenu.AddItem(new NSMenuItem("Show All", new Selector("unhideAllApplications:"), ""));
                appMenu.AddItem(NSMenuItem.SeparatorItem);
                var quit = new NSMenuItem("Quit Mission Planner", "q", (sender, e) =>
                {
                    // close the main window like the window's close button, so settings are saved
                    var main = MainV2.instance;
                    if (main != null && !main.IsDisposed)
                        main.BeginInvoke((Action)(() => main.Close()));
                    else
                        System.Windows.Forms.Application.Exit();
                });
                appMenu.AddItem(quit);
                menuBar.AddItem(appItem);
                menuBar.SetSubmenu(appMenu, appItem);

                var editItem = new NSMenuItem("");
                var editMenu = new NSMenu("Edit");
                editMenu.AddItem(new NSMenuItem("Undo", new Selector("undo:"), "z"));
                var redo = new NSMenuItem("Redo", new Selector("redo:"), "z");
                redo.KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask;
                editMenu.AddItem(redo);
                editMenu.AddItem(NSMenuItem.SeparatorItem);
                editMenu.AddItem(new NSMenuItem("Cut", new Selector("cut:"), "x"));
                editMenu.AddItem(new NSMenuItem("Copy", new Selector("copy:"), "c"));
                editMenu.AddItem(new NSMenuItem("Paste", new Selector("paste:"), "v"));
                editMenu.AddItem(new NSMenuItem("Select All", new Selector("selectAll:"), "a"));
                menuBar.AddItem(editItem);
                menuBar.SetSubmenu(editMenu, editItem);

                var windowItem = new NSMenuItem("");
                var windowMenu = new NSMenu("Window");
                windowMenu.AddItem(new NSMenuItem("Minimize", new Selector("performMiniaturize:"), "m"));
                windowMenu.AddItem(new NSMenuItem("Zoom", new Selector("performZoom:"), ""));
                menuBar.AddItem(windowItem);
                menuBar.SetSubmenu(windowMenu, windowItem);

                NSApplication.SharedApplication.Menu = menuBar;
                NSApplication.SharedApplication.WindowsMenu = windowMenu;
            }
            catch (Exception ex)
            {
                Console.WriteLine("macOS menu bar: " + ex);
            }
            ReceiveDocuments();
        }

        static readonly Queue<string> documents = new Queue<string>();
        static System.Windows.Forms.Timer documentTimer;

        /// <summary>
        /// Files opened from Finder or dropped on the Dock icon (the document types in Info.plist).
        /// macOS sends them as an Apple event, not as arguments: open them like Program.args.
        /// </summary>
        static void ReceiveDocuments()
        {
            try
            {
                var app = NSApplication.SharedApplication;
                // AppKit calls application:openURLs: when the delegate has it (the Cocoa driver's
                // delegate does), application:openFiles: otherwise
                app.OpenUrls += (sender, e) =>
                {
                    foreach (var url in e.Urls)
                        if (url.IsFileUrl && !string.IsNullOrEmpty(url.Path))
                            QueueDocument(url.Path);
                };
                app.OpenFiles += (sender, e) =>
                {
                    foreach (var file in e.Filenames)
                        QueueDocument(file);
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine("macOS documents: " + ex);
            }
        }

        static void QueueDocument(string path)
        {
            lock (documents)
                documents.Enqueue(path);
            if (documentTimer == null)
            {
                documentTimer = new System.Windows.Forms.Timer { Interval = 500 };
                documentTimer.Tick += (sender, e) => OpenDocuments();
            }
            documentTimer.Start();
        }

        static void OpenDocuments()
        {
            // a file that starts the app arrives before the main window is up
            var main = MainV2.instance;
            if (main == null || main.IsDisposed || !main.Visible || main.FlightData == null)
                return;
            documentTimer.Stop();
            while (true)
            {
                string path;
                lock (documents)
                {
                    if (documents.Count == 0)
                        break;
                    path = documents.Dequeue();
                }
                try
                {
                    OpenDocument(main, path);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("open " + path + ": " + ex);
                }
            }
        }

        // as MainV2 does with a file given as argument
        static void OpenDocument(MainV2 main, string path)
        {
            if (!File.Exists(path))
                return;
            Console.WriteLine("MACOS_OPEN_DOCUMENT " + path);
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".tlog":
                    main.MenuFlightData.PerformClick();
                    main.FlightData.LoadLogFile(path);
                    main.FlightData.BUT_playlog_Click(null, null);
                    break;
                case ".bin":
                case ".log":
                    var browse = new MissionPlanner.Log.LogBrowse();
                    MissionPlanner.Utilities.ThemeManager.ApplyThemeTo(browse);
                    browse.logfilename = path;
                    browse.Show(main);
                    browse.BringToFront();
                    break;
            }
        }
    }
}
#endif
