// Mission Planner as a native macOS app (.NET for macOS with the Cocoa Windows Forms
// backend in ExtLibs/mac-playground). Compiled only into that build (MACNATIVE).
//
// - Native libraries: the code names them as on Windows or Linux (Mono mapped those
//   names with dllmap); resolve them to the copies in the app bundle or Homebrew.
// - The macOS menu bar: an application menu (About, Quit) and an Edit menu, which is
//   what makes Cmd+C / Cmd+V / Cmd+X / Cmd+A / Cmd+Z work in text fields.
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

        static readonly string[] libraryDirs =
        {
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
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveUnmanagedDll;
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
        }
    }
}
#endif
