using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MissionPlanner;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;

// Starts Mission Planner, visits every main screen and opens every
// configuration/setup page, capturing the X11 display and recording which ones
// fail. Used to compare platforms (macOS, Linux) against the Windows build.
//
// Environment: TOUR_OUT (output directory), TOUR_XWD (xwd or screencapture binary, optional),
// TOUR_CONNECT (optional connection string such as tcp:127.0.0.1:5760).
class ScreenTour
{
    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void Exit(int status);

    private static string outDir;
    private static string xwd;
    private static StreamWriter report;
    private static readonly Queue<KeyValuePair<string, Action>> steps = new Queue<KeyValuePair<string, Action>>();
    private static Timer stepTimer;
    private static Timer dialogKiller;
    private static Form host;
    private static int shot;
    private static readonly List<string> asyncErrors = new List<string>();
    private static int pass, fail;
    // resuming after a hung step: items already reported are not run again
    private static readonly HashSet<string> done = new HashSet<string>();
    private static volatile string current;
    private static readonly Stopwatch currentTime = new Stopwatch();

    private static void Finish(int status)
    {
        report?.Flush();
        Console.Out.Flush();
        Console.Error.Flush();
        Exit(status);
    }

    private static void Screenshot(string name)
    {
        if (string.IsNullOrEmpty(xwd))
            return;
        try
        {
            Application.DoEvents();
            // xwd on X11; macOS's screencapture for the native (Cocoa) app
            var mac = Path.GetFileName(xwd) == "screencapture";
            var file = Path.Combine(outDir, string.Format("{0:000}-{1}.{2}", ++shot, Sanitize(name), mac ? "png" : "xwd"));
            var args = mac ? "-x \"" + file + "\"" : "-root -silent -out \"" + file + "\"";
            var p = Process.Start(new ProcessStartInfo(xwd, args) { UseShellExecute = false });
            if (!p.WaitForExit(15000))
                p.Kill();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("screenshot failed: " + ex.Message);
        }
    }

    private static string Sanitize(string s)
    {
        return new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    }

    private static string CurrentStepFile => Path.Combine(outDir, "current-step.txt");

    private static void Record(string name, bool ok, long ms, string error)
    {
        if (done.Contains(name))
            return;
        lock (asyncErrors)
        {
            if (asyncErrors.Count > 0)
            {
                ok = false;
                error = (error + " async: " + string.Join(" | ", asyncErrors)).Trim();
                asyncErrors.Clear();
            }
        }
        if (ok) pass++; else fail++;
        error = (error ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        if (error.Length > 600) error = error.Substring(0, 600);
        report.WriteLine(name + "\t" + (ok ? "OK" : "FAIL") + "\t" + ms + "\t" + error);
        report.Flush();
        Console.WriteLine("TOUR " + (ok ? "OK   " : "FAIL ") + name + (ok ? "" : " " + error));
    }

    private static void Step(string name, Action action, bool always = false)
    {
        if (done.Contains(name) && !always)
            return;
        steps.Enqueue(new KeyValuePair<string, Action>(name, action));
    }

    // A step that never returns (a native call or a modal loop that does not end)
    // would stop the tour: record it and exit, so the caller can resume after it.
    private static void Watchdog()
    {
        var limit = int.Parse(Environment.GetEnvironmentVariable("TOUR_STEP_TIMEOUT") ?? "150") * 1000;
        while (true)
        {
            System.Threading.Thread.Sleep(1000);
            var name = current;
            if (name == null || currentTime.ElapsedMilliseconds < limit)
                continue;
            Console.WriteLine("TOUR HANG " + name);
            lock (asyncErrors)
            {
                report.WriteLine(name + "\tFAIL\t" + currentTime.ElapsedMilliseconds + "\tHANG: did not finish within " + limit / 1000 + " s");
                report.Flush();
            }
            DumpHang(name);
            Finish(3);
        }
    }

    // TOUR_HANG_CMD: a program (script) that records where the process is stuck (dotnet-stack,
    // sample), with TOUR_PID and TOUR_ITEM in its environment; a screenshot first
    private static void DumpHang(string name)
    {
        var cmd = Environment.GetEnvironmentVariable("TOUR_HANG_CMD");
        if (string.IsNullOrEmpty(cmd))
            return;
        try
        {
            if (!string.IsNullOrEmpty(xwd))
            {
                var mac = Path.GetFileName(xwd) == "screencapture";
                var file = Path.Combine(outDir, "hang-" + Sanitize(name) + (mac ? ".png" : ".xwd"));
                var shotProcess = Process.Start(new ProcessStartInfo(xwd, mac ? "-x \"" + file + "\"" : "-root -silent -out \"" + file + "\"") { UseShellExecute = false });
                if (!shotProcess.WaitForExit(15000))
                    shotProcess.Kill();
            }
            var psi = new ProcessStartInfo(cmd) { UseShellExecute = false };
            psi.EnvironmentVariables["TOUR_PID"] = Process.GetCurrentProcess().Id.ToString();
            psi.EnvironmentVariables["TOUR_ITEM"] = Sanitize(name);
            var p = Process.Start(psi);
            if (!p.WaitForExit(120000))
                p.Kill();
        }
        catch (Exception ex)
        {
            Console.WriteLine("TOUR hang dump failed: " + ex.Message);
        }
    }

    // where the main menu's buttons are (they must be laid out and visible)
    private static void DescribeMainMenu()
    {
        try
        {
            var menu = MainV2.instance.MainMenu;
            Console.WriteLine("TOUR mainmenu size=" + menu.Size + " visible=" + menu.Visible + " parentVisible=" + (menu.Parent != null && menu.Parent.Visible) +
                              " displayed=" + menu.Items.Cast<ToolStripItem>().Count(i => i.Placement == ToolStripItemPlacement.Main));
            foreach (ToolStripItem item in menu.Items)
                Console.WriteLine("TOUR mainmenu item " + item.Name + " '" + item.Text + "' bounds=" + item.Bounds + " visible=" + item.Visible +
                                  " placement=" + item.Placement + " image=" + (item.Image == null ? "none" : item.Image.Size.ToString()));
        }
        catch (Exception ex)
        {
            Console.WriteLine("TOUR mainmenu: " + ex.Message);
        }
    }

    private static void RunNext(object sender, EventArgs e)
    {
        stepTimer.Stop();
        if (steps.Count == 0)
        {
            Console.WriteLine("TOUR_DONE pass=" + pass + " fail=" + fail);
            Finish(0);
            return;
        }
        var step = steps.Dequeue();
        Console.WriteLine("TOUR START " + step.Key);
        current = step.Key;
        // a step that ends the process (a crash) is recorded when the next run resumes
        try { File.WriteAllText(CurrentStepFile, step.Key); } catch { }
        currentTime.Restart();
        var sw = Stopwatch.StartNew();
        try
        {
            step.Value();
            Record(step.Key, true, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            Record(step.Key, false, sw.ElapsedMilliseconds, inner.GetType().Name + ": " + inner.Message + " @ " + FirstFrame(inner));
        }
        current = null;
        try { File.Delete(CurrentStepFile); } catch { }
        stepTimer.Start();
    }

    private static string FirstFrame(Exception ex)
    {
        var lines = (ex.StackTrace ?? "").Split('\n');
        return string.Join(" < ", lines.Take(3).Select(l => l.Trim()));
    }

    // Modal dialogs (e.g. "not connected") would stall the tour: close any
    // window that is not the main window or the page host, and note it.
    private static void KillDialogs(object sender, EventArgs e)
    {
#if MACNATIVE
        // a native file panel (NSOpenPanel or NSSavePanel run modally) is not a Form
        try
        {
            var app = AppKit.NSApplication.SharedApplication;
            if (app.ModalWindow is AppKit.NSSavePanel panel)
            {
                Console.WriteLine("TOUR dialog closed: native " + panel.GetType().Name + " '" + panel.Title + "'");
                Screenshot("dialog-" + panel.GetType().Name);
                app.AbortModal();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("TOUR native panel: " + ex.Message);
        }
#endif
        foreach (Form f in Application.OpenForms.Cast<Form>().ToArray())
        {
            if (f == MainV2.instance || f == host || f == formUnderTest || f.IsDisposed)
                continue;
            Console.WriteLine("TOUR dialog closed: " + f.GetType().Name + " '" + f.Text + "'");
            Screenshot("dialog-" + f.GetType().Name);
            // closed from the message loop rather than inside this tick, so that a dialog the
            // closing opens (an error message) is closed by a later tick: on macOS a timer does
            // not fire again while its handler runs
            var form = f;
            try
            {
                form.BeginInvoke((Action)(() =>
                {
                    try { if (!form.IsDisposed) form.Close(); } catch { }
                }));
            }
            catch
            {
                try { form.Close(); } catch { }
            }
        }
    }

    // the form OpenForm shows: the step closes it, not KillDialogs
    private static Form formUnderTest;

    private static void ShowMain(string name, ToolStripButton button, int waitMs)
    {
        Step("screen:" + name, () =>
        {
            button.PerformClick();
            Wait(waitMs);
            Screenshot("screen-" + name);
        });
    }

    private static void Wait(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(20);
        }
    }

    private static IEnumerable<Type> PageTypes()
    {
        var asm = typeof(MainV2).Assembly;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
        return types.Where(t => t.IsClass && !t.IsAbstract && typeof(UserControl).IsAssignableFrom(t)
                                && t.Namespace != null
                                && (t.Namespace.StartsWith("MissionPlanner.GCSViews.ConfigurationView")
                                    || t.Namespace == "MissionPlanner.Controls"
                                    || t.Namespace.StartsWith("MissionPlanner.Controls."))
                                && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.FullName);
    }

    private static IEnumerable<Type> FormTypes()
    {
        var asm = typeof(MainV2).Assembly;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
        // the main window and startup/teardown forms are exercised by the tour itself
        var skip = new[] { "MainV2", "Splash", "ScreenTour", "Program" };
        return types.Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && typeof(Form).IsAssignableFrom(t)
                                && t.Namespace != null && t.Namespace.StartsWith("MissionPlanner")
                                && !skip.Contains(t.Name)
                                && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.FullName);
    }

    private static void OpenForm(Type type)
    {
        Step("form:" + type.FullName, () =>
        {
            var form = (Form)Activator.CreateInstance(type);
            formUnderTest = form;
            try
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Left = 60;
                form.Top = 60;
                form.Show();
                Wait(1000);
                Screenshot("form-" + type.Name);
            }
            finally
            {
                formUnderTest = null;
                if (!form.IsDisposed)
                {
                    form.Close();
                    form.Dispose();
                }
            }
        });
    }

    private static void OpenPage(Type type)
    {
        Step("page:" + type.FullName, () =>
        {
            if (host == null || host.IsDisposed)
            {
                host = new Form { Width = 1100, Height = 750, Text = "Page host", StartPosition = FormStartPosition.Manual, Left = 40, Top = 40 };
                host.Show();
            }
            var control = (UserControl)Activator.CreateInstance(type);
            try
            {
                control.Dock = DockStyle.Fill;
                host.Text = type.Name;
                host.Controls.Add(control);
                ThemeManager.ApplyThemeTo(control);
                (control as IActivate)?.Activate();
                // in front for the screenshot (a closed dialog may have activated the main window)
                host.Activate();
                Wait(1200);
                Screenshot("page-" + type.Name);
                (control as IDeactivate)?.Deactivate();
            }
            finally
            {
                host.Controls.Remove(control);
                control.Dispose();
            }
        });
    }

    [STAThread]
    internal static void Main()
    {
        outDir = Environment.GetEnvironmentVariable("TOUR_OUT") ?? Path.Combine(Path.GetTempPath(), "mp-tour");
        Directory.CreateDirectory(outDir);
        xwd = Environment.GetEnvironmentVariable("TOUR_XWD");
        var reportPath = Path.Combine(outDir, "tour-report.tsv");
        if (File.Exists(reportPath))
            foreach (var line in File.ReadAllLines(reportPath).Skip(1))
                done.Add(line.Split('\t')[0]);
        report = new StreamWriter(reportPath, true);
        if (done.Count == 0)
            report.WriteLine("item\tresult\tms\terror");
        else
            Console.WriteLine("TOUR resuming after " + done.Count + " items");
        if (File.Exists(CurrentStepFile))
        {
            var crashed = File.ReadAllText(CurrentStepFile).Trim();
            File.Delete(CurrentStepFile);
            if (crashed.Length > 0 && !done.Contains(crashed))
            {
                report.WriteLine(crashed + "\tFAIL\t0\tCRASH: the process ended during this step");
                report.Flush();
                done.Add(crashed);
                Console.WriteLine("TOUR CRASH " + crashed);
            }
        }
        new System.Threading.Thread(Watchdog) { IsBackground = true, Name = "tour watchdog" }.Start();
        Console.WriteLine("TOUR_RUNTIME process=" + RuntimeInformation.ProcessArchitecture +
            " os=" + RuntimeInformation.OSDescription.Trim());

        Settings.CustomUserDataDirectory = Path.Combine(Path.GetTempPath(), "missionplanner-tour-" + Guid.NewGuid());
        Directory.CreateDirectory(Settings.GetUserDataDirectory());
        Settings.Instance["update_check"] = DateTime.Now.ToShortDateString();
        Settings.Instance["MainWidth"] = "1280";
        Settings.Instance["MainHeight"] = "900";
        Settings.Instance["AutoConnect"] = "[]";
        Settings.Instance["SHOW_TIPS"] = "False";
        Settings.Instance.Save();

        Application.ThreadException += (s, e) => { lock (asyncErrors) asyncErrors.Add(e.Exception.GetType().Name + ": " + e.Exception.Message); };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Console.Error.WriteLine("UNHANDLED " + e.ExceptionObject);

        var start = new Timer { Interval = 15000 };
        var waits = 0;
        start.Tick += (s, e) =>
        {
            start.Stop();
            if ((MainV2.instance == null || MainV2.instance.FlightData == null || !MainV2.instance.Visible) && ++waits < 60)
            {
                start.Interval = 5000;
                start.Start();
                return;
            }
            Screenshot("startup");
            DescribeMainMenu();
            var m = MainV2.instance;
            var connect = Environment.GetEnvironmentVariable("TOUR_CONNECT");
            if (!string.IsNullOrEmpty(connect))
            {
                Step("connect:" + connect, () => Connect(connect), true);
                FeatureChecks();
            }
            PlatformChecks();
            ShowMain("FlightData", m.MenuFlightData, 3000);
            // the HUD's back buffer itself (GDI+ drawing), to tell it from what the window shows
            Step("feature:hud-back-buffer", () =>
            {
                var hud = MissionPlanner.GCSViews.FlightData.myhud;
                var bitmap = hud.objBitmap;
                Console.WriteLine("TOUR hud opengl=" + hud.opengl + " bgimage=" + (hud.bgimage != null) + " bitmap=" + bitmap.Size + " control=" + hud.Size);
                foreach (var f in new[] { 0.3f, 0.5f, 0.7f, 0.9f })
                    Console.WriteLine("TOUR hud pixel y=" + f + " " + bitmap.GetPixel(bitmap.Width / 2, (int)(bitmap.Height * f)));
                bitmap.Save(Path.Combine(outDir, "hud-back-buffer.png"), System.Drawing.Imaging.ImageFormat.Png);
            });
            // GMapControl sets SourceOver before drawing its tiles; the Cocoa drawing library
            // once mapped it to CoreGraphics' Overlay blend, which over the map's WhiteSmoke
            // background left everything nearly white (Navy came out as (235,235,245))
            Step("feature:map-compositing", () =>
            {
                using (var bitmap = new System.Drawing.Bitmap(8, 8))
                using (var g = System.Drawing.Graphics.FromImage(bitmap))
                {
                    g.Clear(System.Drawing.Color.WhiteSmoke);
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                    g.FillRectangle(System.Drawing.Brushes.Navy, 0, 0, 8, 8);
                    var c = bitmap.GetPixel(4, 4);
                    Console.WriteLine("TOUR map compositing navy over whitesmoke: " + c);
                    if (c.R > 40 || c.G > 40 || c.B < 96 || c.B > 160)
                        throw new Exception("SourceOver drew " + c + " instead of Navy (0,0,128)");
                }
                // the Flight Data map drawn by its own code into a bitmap (tiles depend on the
                // network, so this only reports): a washed-out map is almost all near-white
                var map = MissionPlanner.GCSViews.FlightData.mymap;
                using (var bitmap = new System.Drawing.Bitmap(map.Width, map.Height))
                {
                    int samples = 0, nearWhite = 0;
                    long r = 0, gr = 0, b = 0;
                    using (var g = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        map.doPaint(new SvgNet.SvgGdi.GdiGraphics(g));
                        for (var y = 1; y < 9; y++)
                            for (var x = 1; x < 13; x++)
                            {
                                var c = bitmap.GetPixel(bitmap.Width * x / 13, bitmap.Height * y / 9);
                                samples++;
                                r += c.R; gr += c.G; b += c.B;
                                if (c.R > 225 && c.G > 225 && c.B > 225)
                                    nearWhite++;
                            }
                    }
                    Console.WriteLine("TOUR map render zoom=" + map.Zoom + " provider=" + map.MapProvider + " size=" + bitmap.Size +
                                      " near-white=" + nearWhite + "/" + samples +
                                      " mean=(" + r / samples + "," + gr / samples + "," + b / samples + ")");
                    bitmap.Save(Path.Combine(outDir, "map-render.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            });
            // the controls over the map are anchored to its panel's right and bottom edges; the
            // Cocoa SplitContainer once froze them at their design positions as it grew
            Step("feature:map-overlay-anchors", () =>
            {
                var zoom = m.FlightData.Controls.Find("TRK_zoom", true).Single();
                var panel = zoom.Parent;
                Control legend = panel.Controls["label6"], distance = panel.Controls["distanceBar1"];
                Console.WriteLine("TOUR map anchors panel=" + panel.Size + " zoom=" + zoom.Bounds + " legend=" + legend.Bounds + " distance=" + distance.Bounds);
                // the designer's panel is 595x197: only a bigger one tells anchoring from none
                if (panel.Width < 700 || panel.Height < 300)
                    return;
                // designed 0 px from the right, 17 px from the bottom (an AutoSize label: its
                // height follows the font), 51 px from the right
                if (Math.Abs(panel.Width - zoom.Right) > 4 || Math.Abs(panel.Height - legend.Bottom - 17) > 8 ||
                    Math.Abs(panel.Width - distance.Right - 51) > 4)
                    throw new Exception("map overlay not anchored: panel " + panel.Size + ", zoom bar right " + zoom.Right +
                                        ", legend bottom " + legend.Bottom + ", distance bar right " + distance.Right);
            });
            // every tab the Flight Data screen shows (Quick, Actions, Messages, PreFlight, Gauges,
            // Status, Servo/Relay, Aux Function, Scripts, Payload, Telemetry Logs, DataFlash Logs, ...)
            var tabControl = (TabControl)m.FlightData.Controls.Find("tabControlactions", true).Single();
            var tabNames = tabControl.TabPages.Cast<TabPage>().Select(p => p.Name).ToList();
            foreach (var tabName in tabNames)
            {
                var name = tabName;
                Step("screen:FlightData-" + name, () =>
                {
                    var tab = tabControl.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Name == name);
                    if (tab == null)
                        return;
                    tabControl.SelectedTab = tab;
                    Wait(1500);
                    Screenshot("flightdata-" + name);
                });
            }
            ShowMain("FlightPlanner", m.MenuFlightPlanner, 4000);
            ShowMain("InitialSetup", m.MenuInitConfig, 4000);
            ShowMain("ConfigTune", m.MenuConfigTune, 4000);
            ShowMain("Simulation", m.MenuSimulation, 3000);
            ShowMain("Help", m.MenuHelp, 2000);
            ShowMain("FlightData-again", m.MenuFlightData, 2000);
            var skip = (Environment.GetEnvironmentVariable("TOUR_SKIP") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var only = Environment.GetEnvironmentVariable("TOUR_ONLY");
            foreach (var t in PageTypes())
            {
                if (skip.Any(x => t.FullName.EndsWith(x.Trim())))
                {
                    Record("page:" + t.FullName, false, 0, "SKIPPED (hangs the tour)");
                    continue;
                }
                if (!string.IsNullOrEmpty(only) && !t.FullName.Contains(only))
                    continue;
                OpenPage(t);
            }
            if (string.IsNullOrEmpty(only) && Environment.GetEnvironmentVariable("TOUR_FORMS") != "0")
                foreach (var t in FormTypes())
                {
                    if (skip.Any(x => t.FullName.EndsWith(x.Trim())))
                    {
                        Record("form:" + t.FullName, false, 0, "SKIPPED (hangs the tour)");
                        continue;
                    }
                    OpenForm(t);
                }

            dialogKiller = new Timer { Interval = 3000 };
            dialogKiller.Tick += KillDialogs;
            dialogKiller.Start();
            stepTimer = new Timer { Interval = 300 };
            stepTimer.Tick += RunNext;
            stepTimer.Start();
        };
        start.Start();
        Program.Main(new string[0]);
    }

    private static bool IsMac
    {
        get { return RuntimeInformation.IsOSPlatform(OSPlatform.OSX); }
    }

    // Platform services that work without a vehicle.
    private static void PlatformChecks()
    {
        Step("feature:usb-device-enumeration", () =>
        {
            var ports = MissionPlanner.Utilities.Linux.GetAllCOMPorts();
            Console.WriteLine("TOUR usb devices: " + ports.Count);
        });
        Step("feature:serial-port-list", () =>
        {
            var ports = MissionPlanner.Comms.SerialPort.GetPortNames();
            Console.WriteLine("TOUR serial ports: " + string.Join(",", ports));
        });
        Step("feature:joystick-enumeration", () =>
        {
            var devices = MissionPlanner.Joystick.JoystickBase.getDevices();
            Console.WriteLine("TOUR joysticks: " + devices.Count);
            if (IsMac && !MissionPlanner.Joystick.JoystickSDL.IsSupported)
                throw new Exception("SDL2 joystick backend not available");
        });
        Step("feature:speech", () =>
        {
            var speech = new MissionPlanner.Utilities.Speech { speechEnable = true };
            var engine = MainV2.speechEngine;
            MainV2.speechEngine = speech;
            try
            {
                speech.SpeakAsync("Mission Planner test");
                var sw = Stopwatch.StartNew();
                while (!speech.IsReady && sw.ElapsedMilliseconds < 20000)
                    Wait(100);
                if (!speech.IsReady)
                    throw new Exception("speech did not finish");
            }
            finally
            {
                MainV2.speechEngine = engine;
            }
        });
        Step("feature:local-camera-enumeration", () =>
        {
            var cameras = MissionPlanner.Utilities.GStreamer.ListLocalCameras();
            Console.WriteLine("TOUR cameras: " + cameras.Count);
        });
        Step("feature:gstreamer-discovery", () =>
        {
            var gst = MissionPlanner.Utilities.GStreamer.LookForGstreamer();
            Console.WriteLine("TOUR gstreamer: " + (string.IsNullOrEmpty(gst) ? "not installed" : gst));
        });
        Step("feature:gstreamer-hud-video", () =>
        {
            MissionPlanner.Utilities.GStreamer.GstLaunch = MissionPlanner.Utilities.GStreamer.LookForGstreamer();
            if (!MissionPlanner.Utilities.GStreamer.GstLaunchExists)
                throw new Exception("GStreamer not installed");
            var gst = MissionPlanner.GCSViews.FlightData.hudGStreamer;
            var hud = MissionPlanner.GCSViews.FlightData.myhud;
            try
            {
                MainV2.instance.MenuFlightData.PerformClick();
                hud.bgimage = null;
                gst.Start("videotestsrc pattern=ball ! video/x-raw,width=640,height=480 ! videoconvert ! video/x-raw,format=BGRx ! appsink name=outsink");
                // MainV2 copies every decoded frame into the HUD background
                var sw = Stopwatch.StartNew();
                while (hud.bgimage == null && sw.ElapsedMilliseconds < 20000)
                    Wait(200);
                Wait(1500);
                Screenshot("gstreamer-hud");
                if (hud.bgimage == null)
                    throw new Exception("no video frame reached the HUD");
                // each frame replaces (and disposes) the previous one from the video thread:
                // read the size of one frame, retrying if it went in between
                string size = null;
                for (int i = 0; i < 20 && size == null; i++)
                {
                    try
                    {
                        var frame = hud.bgimage;
                        if (frame != null)
                            size = frame.Width + "x" + frame.Height;
                    }
                    catch (Exception)
                    {
                        Wait(50);
                    }
                }
                Console.WriteLine("TOUR gstreamer frame: " + (size ?? "?"));
            }
            finally
            {
                gst.Stop();
            }
        });
        if (IsMac)
            Step("feature:sitl-bundled", () =>
            {
                var dir = MissionPlanner.GCSViews.SITL.BundledPath;
                foreach (var vehicle in new[] { "ArduCopter", "ArduPlane", "ArduRover", "ArduHeli" })
                    if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, vehicle)))
                        throw new Exception("missing bundled SITL " + vehicle + " in '" + dir + "'");
            });
    }

    // Vehicle operations against the connected simulator.
    private static void FeatureChecks()
    {
        Step("feature:parameters", () =>
        {
            var count = MainV2.comPort.MAV.param.Count;
            if (count < 500)
                throw new Exception("only " + count + " parameters");
            Console.WriteLine("TOUR parameters: " + count);
        });
        Step("feature:parameter-write", () =>
        {
            var name = new[] { "RTL_ALT", "WPNAV_SPEED", "WP_SPD" }.First(n => MainV2.comPort.MAV.param.ContainsKey(n));
            var old = MainV2.comPort.GetParam(name);
            var value = old + 100;
            if (!MainV2.comPort.setParam(name, value))
                throw new Exception("setParam " + name + " failed");
            if (Math.Abs(MainV2.comPort.GetParam(name) - value) > 0.1)
                throw new Exception("read back " + MainV2.comPort.GetParam(name));
            MainV2.comPort.setParam(name, old);
            Console.WriteLine("TOUR parameter " + name + ": " + old + " -> " + value + " -> " + old);
        });
        Step("feature:mission-upload-download", () =>
        {
            var home = MainV2.comPort.MAV.cs.PlannedHomeLocation;
            double lat = MainV2.comPort.MAV.cs.lat, lng = MainV2.comPort.MAV.cs.lng;
            var mission = new List<Locationwp>();
            mission.Add(new Locationwp().Set(lat, lng, 0, (ushort) MAVLink.MAV_CMD.WAYPOINT));
            mission.Add(new Locationwp().Set(lat, lng, 20, (ushort) MAVLink.MAV_CMD.TAKEOFF));
            mission.Add(new Locationwp().Set(lat + 0.0005, lng, 30, (ushort) MAVLink.MAV_CMD.WAYPOINT));
            mission.Add(new Locationwp().Set(lat + 0.0005, lng + 0.0005, 30, (ushort) MAVLink.MAV_CMD.WAYPOINT));
            mission.Add(new Locationwp().Set(0, 0, 0, (ushort) MAVLink.MAV_CMD.RETURN_TO_LAUNCH));
            for (int i = 0; i < mission.Count; i++)
            {
                var wp = mission[i];
                wp.frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT;
                mission[i] = wp;
            }
            var sysid = (byte) MainV2.comPort.sysidcurrent;
            var compid = (byte) MainV2.comPort.compidcurrent;
            MissionPlanner.ArduPilot.mav_mission.upload(MainV2.comPort, sysid, compid, MAVLink.MAV_MISSION_TYPE.MISSION, mission).GetAwaiter().GetResult();
            var back = MissionPlanner.ArduPilot.mav_mission.download(MainV2.comPort, sysid, compid, MAVLink.MAV_MISSION_TYPE.MISSION).GetAwaiter().GetResult();
            if (back.Count != mission.Count)
                throw new Exception("downloaded " + back.Count + " items, uploaded " + mission.Count);
            Console.WriteLine("TOUR mission items: " + back.Count);
        });
        Step("feature:mavftp-list", () =>
        {
            var ftp = new MissionPlanner.ArduPilot.Mavlink.MAVFtp(MainV2.comPort, (byte) MainV2.comPort.sysidcurrent, (byte) MainV2.comPort.compidcurrent);
            var entries = ftp.kCmdListDirectory("/", new System.Threading.CancellationTokenSource(30000));
            Console.WriteLine("TOUR mavftp /: " + string.Join(", ", entries.Select(a => a.Name)));
            if (entries.Count == 0)
                throw new Exception("empty listing");
        });
        // The mount itself is tested by Probe.exe in a separate process: here the
        // server and the reader would share one process, which can block it.
        if (IsMac && Environment.GetEnvironmentVariable("TOUR_MOUNT") == "1")
            Step("feature:mavftp-mount", () =>
            {
                var ftp = new MissionPlanner.ArduPilot.Mavlink.MAVFtp(MainV2.comPort, (byte) MainV2.comPort.sysidcurrent, (byte) MainV2.comPort.compidcurrent);
                var dir = Path.Combine(Path.GetTempPath(), "mavftp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                MissionPlanner.ArduPilot.Mavlink.MavFtpWebDav.Mount(ftp, dir);
                // Use the mounted folder from another thread, as Finder would from another
                // process: the WebDAV server shares this process with the test.
                string result = null;
                Exception error = null;
                var worker = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var entries = Directory.GetFileSystemEntries(dir);
                        result = "mounted: " + string.Join(", ", entries.Select(Path.GetFileName));
                        if (entries.Length == 0)
                            throw new Exception("mounted folder is empty");
                        File.WriteAllText(Path.Combine(dir, "mp-test.txt"), "written through WebDAV");
                        var back = File.ReadAllText(Path.Combine(dir, "mp-test.txt"));
                        if (back != "written through WebDAV")
                            throw new Exception("write/read back mismatch: '" + back + "'");
                        File.Delete(Path.Combine(dir, "mp-test.txt"));
                        result += "; write/read/delete OK";
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                }) { IsBackground = true };
                worker.Start();
                var finished = false;
                var sw = Stopwatch.StartNew();
                while (!(finished = !worker.IsAlive) && sw.ElapsedMilliseconds < 90000)
                    Wait(200);
                Console.WriteLine("TOUR " + (result ?? "mount: no result"));
                try { MissionPlanner.ArduPilot.Mavlink.MavFtpWebDav.Unmount(); } catch (Exception ex) { Console.WriteLine("TOUR unmount: " + ex.Message); }
                if (!finished)
                    throw new Exception("file operations on the mount did not finish within 90 s");
                if (error != null)
                    throw error;
            });
        Step("feature:log-list", () =>
        {
            var logs = MainV2.comPort.GetLogList();
            Console.WriteLine("TOUR onboard logs: " + logs.Count);
        });
        Step("feature:arm-takeoff", () =>
        {
            MainV2.comPort.setMode("GUIDED");
            var sw = Stopwatch.StartNew();
            bool armed = false;
            while (!armed && sw.ElapsedMilliseconds < 120000)
            {
                armed = MainV2.comPort.doARM(true);
                if (!armed)
                    Wait(3000);
            }
            if (!armed)
                throw new Exception("could not arm: " + MainV2.comPort.MAV.cs.messageHigh);
            if (!MainV2.comPort.doCommand(MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, 10))
                throw new Exception("takeoff rejected");
            sw.Restart();
            while (MainV2.comPort.MAV.cs.alt < 5 && sw.ElapsedMilliseconds < 60000)
                Wait(500);
            Console.WriteLine("TOUR altitude: " + MainV2.comPort.MAV.cs.alt);
            if (MainV2.comPort.MAV.cs.alt < 5)
                throw new Exception("did not climb");
            MainV2.instance.MenuFlightData.PerformClick();
            Wait(3000);
            Screenshot("flying");
            MainV2.comPort.setMode("LAND");
        });
    }

    private static void Connect(string connect)
    {
        // tcp:host:port
        var parts = connect.Split(':');
        if (parts.Length != 3 || parts[0] != "tcp")
            throw new ArgumentException("TOUR_CONNECT must be tcp:host:port");
        MainV2.comPort.BaseStream = new MissionPlanner.Comms.TcpSerial { Host = parts[1], Port = parts[2] };
        MainV2.instance.doConnect(MainV2.comPort, "preset", "115200", true, false);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 120000 && (!MainV2.comPort.BaseStream.IsOpen || MainV2.comPort.MAV.param.Count < 100))
            Wait(500);
        Wait(3000);
        if (!MainV2.comPort.BaseStream.IsOpen || MainV2.comPort.MAV.param.Count < 100)
            throw new Exception("not connected (params=" + MainV2.comPort.MAV.param.Count + ")");
        Console.WriteLine("TOUR connected params=" + MainV2.comPort.MAV.param.Count + " fw=" + MainV2.comPort.MAV.cs.firmware);
        Screenshot("connected");
    }
}
