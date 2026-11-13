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
// TOUR_CONNECT (optional connection string such as tcp:127.0.0.1:5760 or serial:/dev/ttys003:115200),
// TOUR_SERIAL_LOOPBACK (optional serial port that echoes what it receives).
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
            // Mission Planner shows the "loading parameters" page only while a vehicle's
            // parameters are still arriving; once they all have (0 of 0 without a vehicle) its
            // timer reloads the main screen every 100 ms. Open it in its real state: one to go.
            var param = MainV2.comPort.MAV.param;
            var reported = param.TotalReported;
            var loading = type.Name == "ConfigParamLoading";
            if (loading)
                param.TotalReported = param.TotalReceived + 1;
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
                if (loading)
                    param.TotalReported = reported;
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
            // GraphicsPath.AddString takes emSize in pixels (GDI+): the HUD builds its text from
            // these paths, and as points (96/72 bigger) it overlapped and ran off the edges
            Step("feature:hud-text-size", () =>
            {
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    path.AddString("H", System.Drawing.FontFamily.GenericSansSerif, 0, 100, new System.Drawing.Point(0, 0),
                        System.Drawing.StringFormat.GenericTypographic);
                    var bounds = path.GetBounds();
                    Console.WriteLine("TOUR hud text: \"H\" at emSize 100 is " + bounds.Height + " px high");
                    // a sans-serif capital is about 0.72 em: 72 px, or 96 px if emSize were points
                    if (bounds.Height < 60 || bounds.Height > 84)
                        throw new Exception("AddString emSize 100: capital H " + bounds.Height + " px high (about 72 expected)");
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
#if MACNATIVE
            // the tab panel under the HUD in its macOS style (macOS/native/FlightDataMac.cs): a pop-up
            // menu in place of the tab strip, kept in step with the pages; Actions, Servo/Relay and Aux
            // Function inside the pane at its own width and at Windows' default 300 px; the same
            // controls with their handlers; native controls in the theme's dark or light appearance
            Step("feature:mac-sidebar", () => MacSidebar(m.FlightData));
#endif
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
                // (a comma-separated list: the connected tour opens the pages that need a vehicle)
                if (!string.IsNullOrEmpty(only) && !only.Split(',').Any(o => o.Trim().Length > 0 && t.FullName.Contains(o.Trim())))
                    continue;
                // pages Mission Planner only opens with a vehicle's parameters (it reads them on
                // Activate, as on Windows): the connected tour opens them instead
                if (string.IsNullOrEmpty(connect) && NeedsVehicle.Contains(t.Name))
                {
                    Console.WriteLine("TOUR NEEDS VEHICLE page:" + t.FullName + " (opened by the connected tour)");
                    continue;
                }
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

#if MACNATIVE
    private static void MacSidebar(MissionPlanner.GCSViews.FlightData flightData)
    {
        var mainH = (SplitContainer)flightData.Controls.Find("MainH", true).Single();
        var tabs = (TabControl)flightData.Controls.Find("tabControlactions", true).Single();
        var picker = flightData.MacTabPicker;
        var problems = new List<string>();
        if (picker == null)
            throw new Exception("no page pop-up menu");
        var selected = tabs.SelectedIndex;
        var distance = mainH.SplitterDistance;
        try
        {
            // the tab strip hidden, the pop-up menu listing the pages in order and following them
            if (tabs.TabStripVisible || tabs.DisplayRectangle.Top > 1)
                problems.Add("tab strip shown (display rectangle " + tabs.DisplayRectangle + ")");
            Func<string> listing = () => string.Join(",", picker.Items.Cast<object>());
            Console.WriteLine("TOUR mac sidebar pages: " + listing());
            if (picker.Items.Count != tabs.TabCount)
                problems.Add("menu has " + picker.Items.Count + " pages, the tab control " + tabs.TabCount);
            if (tabs.TabCount > 1)
            {
                int other = (selected + 1) % tabs.TabCount;
                picker.SelectedIndex = other;
                Wait(200);
                if (tabs.SelectedIndex != other)
                    problems.Add("menu item " + other + " selected page " + tabs.SelectedIndex);
                tabs.SelectedIndex = selected; // as Ctrl+1..0 do
                Wait(200);
                if (picker.SelectedIndex != selected)
                    problems.Add("page " + selected + " left the menu at " + picker.SelectedIndex);
                // a page removed and added again (as Customize and plugins do)
                var last = tabs.TabPages[tabs.TabCount - 1];
                tabs.TabPages.Remove(last);
                if (picker.Items.Count != tabs.TabCount)
                    problems.Add("menu not rebuilt after a page was removed");
                tabs.TabPages.Add(last);
                if (picker.Items.Count != tabs.TabCount || !picker.Items[picker.Items.Count - 1].ToString().StartsWith(last.Text))
                    problems.Add("menu not rebuilt after a page was added: " + listing());
                tabs.SelectedIndex = selected;
            }

            // inside the pane, and nothing to scroll sideways
            var checkedPages = new[] { "tabActions", "tabServo", "tabAuxFunction" };
            foreach (var width in new[] { distance, 300 })
            {
                mainH.SplitterDistance = width;
                foreach (TabPage page in tabs.TabPages)
                {
                    tabs.SelectedTab = page;
                    Wait(checkedPages.Contains(page.Name) ? 500 : 150);
                    var past = new List<string>();
                    foreach (var c in Descendants(page))
                    {
                        if (!c.Visible || c.Width <= 0)
                            continue;
                        var right = page.PointToClient(c.PointToScreen(System.Drawing.Point.Empty)).X + c.Width;
                        if (right > page.ClientSize.Width + 1)
                            past.Add(c.Name + " to " + right);
                    }
                    var sideways = Descendants(page).OfType<ScrollableControl>().Where(c => c.Visible && c.HorizontalScroll.Visible)
                        .Select(c => c.Name).ToList();
                    if (checkedPages.Contains(page.Name))
                    {
                        Console.WriteLine("TOUR mac sidebar pane " + width + " " + page.Name + ": page " + page.ClientSize +
                                          (past.Count > 0 ? ", past the edge: " + string.Join(", ", past) : ", inside") +
                                          (sideways.Count > 0 ? ", sideways bar: " + string.Join(", ", sideways) : ""));
                        if (past.Count > 0)
                            problems.Add(page.Name + " at " + width + " px: " + string.Join(", ", past.Take(6)));
                        if (sideways.Count > 0)
                            problems.Add(page.Name + " at " + width + " px scrolls sideways: " + string.Join(", ", sideways));
                    }
                    else if (past.Count > 0 || sideways.Count > 0)
                        // the pages the second phase lays out: reported only
                        Console.WriteLine("TOUR mac sidebar pane " + width + " " + page.Name + " (not yet laid out): " +
                                          past.Count + " past the edge" + (sideways.Count > 0 ? ", sideways bar" : ""));
                }
            }

            // the same controls, with their handlers
            var actions = flightData.MacActions;
            var clickKey = typeof(Control).GetField("ClickEvent", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var events = typeof(System.ComponentModel.Component).GetProperty("Events", BindingFlags.NonPublic | BindingFlags.Instance);
            Func<Control, bool> clickable = c => ((System.ComponentModel.EventHandlerList)events.GetValue(c))[clickKey] != null;
            var table = flightData.Controls.Find("tableLayoutPanel1", true).Single();
            var moved = actions.Controls.Cast<Control>().ToList();
            Console.WriteLine("TOUR mac sidebar actions: " + moved.Count + " controls (" + moved.OfType<MyButton>().Count() + " buttons, " +
                              moved.OfType<ComboBox>().Count() + " choices, " + moved.OfType<ModifyandSet>().Count() + " values), " +
                              table.Controls.Count + " left in the grid");
            if (moved.Count != 22 || table.Controls.Count != 0 || moved.OfType<ModifyandSet>().Count() != 3)
                problems.Add("Actions has " + moved.Count + " controls (22 expected), the grid " + table.Controls.Count);
            var deaf = moved.OfType<MyButton>().Where(b => !clickable(b)).Select(b => b.Name).ToList();
            var servoLow = flightData.Controls.Find("BUT_Low", true).FirstOrDefault();
            if (servoLow == null || !clickable(servoLow))
                deaf.Add("servo BUT_Low");
            if (deaf.Count > 0)
                problems.Add("buttons without their click handler: " + string.Join(", ", deaf));
            var rounded = moved.OfType<MyButton>().Count(b => b.CornerRadius > 0);
            Console.WriteLine("TOUR mac sidebar buttons rounded: " + rounded + "/" + moved.OfType<MyButton>().Count());

            // native controls in the theme's appearance
            var panel = tabs.Parent;
            var view = ObjCRuntime.Runtime.GetNSObject(panel.Handle) as AppKit.NSView;
            var appearance = view?.Appearance?.Name ?? "inherited";
            var background = tabs.TabCount > 0 ? tabs.TabPages[0].BackColor : panel.BackColor;
            Console.WriteLine("TOUR mac sidebar appearance: " + appearance + " on " + background);
            if (background.A == 255 && (background.GetBrightness() < 0.5f) != appearance.Contains("Dark"))
                problems.Add("appearance " + appearance + " on a " + (background.GetBrightness() < 0.5f ? "dark" : "light") + " theme");

            // a sideways scroll still moves a page that is wider than the pane (Status draws its columns)
            var status = tabs.TabPages["tabStatus"];
            if (status != null)
            {
                mainH.SplitterDistance = 300;
                tabs.SelectedTab = status;
                Wait(300);
                if (status.HorizontalScroll.Visible)
                {
                    int before = status.HorizontalScroll.Value;
                    typeof(Control).GetMethod("OnMouseHWheel", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(status, new object[] { new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, -120) });
                    Console.WriteLine("TOUR mac sidebar status sideways scroll " + before + " -> " + status.HorizontalScroll.Value);
                    if (status.HorizontalScroll.Value <= before)
                        problems.Add("a sideways scroll did not move Status");
                    status.HorizontalScroll.Value = 0;
                }
            }
        }
        finally
        {
            mainH.SplitterDistance = distance;
            if (selected >= 0 && selected < tabs.TabCount)
                tabs.SelectedIndex = selected;
        }
        if (problems.Count > 0)
            throw new Exception(string.Join("; ", problems));
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var grandchild in Descendants(child))
                yield return grandchild;
        }
    }
#endif

    private static readonly HashSet<string> NeedsVehicle = new HashSet<string> { "ConfigHWCompass2" };

    private static bool IsMac
    {
        get { return RuntimeInformation.IsOSPlatform(OSPlatform.OSX); }
    }

    // Platform services that work without a vehicle.
    private static void PlatformChecks()
    {
        // the Log Analyzer (ArduPilot's Python one, bundled and run with python3) on one of its
        // own example logs, through the calls Flight Data's Log Analyzer button makes
        var sampleLog = Environment.GetEnvironmentVariable("TOUR_ANALYZE_LOG");
        if (!string.IsNullOrEmpty(sampleLog) && File.Exists(sampleLog))
            Step("feature:log-analyzer", () =>
            {
                var xml = MissionPlanner.Utilities.LogAnalyzer.CheckLogFile(sampleLog);
                if (string.IsNullOrEmpty(xml) || !File.Exists(xml))
                    throw new Exception("no report from the Log Analyzer");
                var analysis = MissionPlanner.Utilities.LogAnalyzer.Results(xml);
                var findings = analysis.results.Where(r => r.status == "FAIL" || r.status == "WARN").Select(r => r.name + "=" + r.status);
                Console.WriteLine("TOUR log analyzer: " + analysis.results.Count + " tests, " + string.Join("; ", findings));
                // underpowered.log: the analyzer's own example of a copter short of thrust
                if (analysis.results.Count < 10 || !analysis.results.Any(r => r.name == "Thrust" && r.status == "FAIL"))
                    throw new Exception("unexpected Log Analyzer results: " + analysis.results.Count + " tests, " + string.Join("; ", findings));
            });
        Step("feature:usb-device-enumeration", () =>
        {
            var ports = MissionPlanner.Utilities.Linux.GetAllCOMPorts();
            Console.WriteLine("TOUR usb devices: " + ports.Count);
        });
        Step("feature:serial-port-list", () =>
        {
            var ports = MissionPlanner.Comms.SerialPort.GetPortNames();
            Console.WriteLine("TOUR serial ports: " + string.Join(",", ports));
            // one entry per device, as Windows lists one COMx: the cu.X device, not tty.X too
            if (IsMac && ports.Any(p => p.StartsWith("/dev/tty")))
                throw new Exception("tty devices listed: " + string.Join(",", ports.Where(p => p.StartsWith("/dev/tty"))));
        });
#if MACNATIVE
        // ⌘ reaches Windows Forms as Control, so the Ctrl+key shortcuts work with ⌘ too
        Step("feature:command-key", () =>
        {
            var keys = System.Windows.Forms.Mac.Extensions.ToKeys(AppKit.NSEventModifierMask.CommandKeyMask);
            Console.WriteLine("TOUR command key: " + keys);
            if (keys != Keys.Control)
                throw new Exception("command key arrives as " + keys);
        });
        // wheel messages in whole notches of 120, as a Windows mouse sends: a mouse wheel line
        // is one notch, a trackpad's points add up (120 per 40)
        Step("feature:wheel-delta", () =>
        {
            int Wheel(CoreGraphics.CGScrollEventUnit unit, int amount)
            {
                using (var cg = new CoreGraphics.CGEvent(null, unit, amount))
                {
                    var ev = AppKit.NSEvent.EventWithCGEvent(cg.Handle);
                    System.Windows.Forms.Mac.Extensions.WheelDeltas(ev, out _, out var dy);
                    return dy;
                }
            }
            var line = Wheel(CoreGraphics.CGScrollEventUnit.Line, 1);
            var first = Wheel(CoreGraphics.CGScrollEventUnit.Pixel, 20);
            var second = Wheel(CoreGraphics.CGScrollEventUnit.Pixel, 20);
            Console.WriteLine("TOUR wheel: one line " + line + ", trackpad 20 + 20 points " + first + ", " + second);
            if (line != 120 || first != 0 || second != 120)
                throw new Exception("wheel deltas: line " + line + ", points " + first + ", " + second + " (120, 0, 120 expected)");
        });
        // the JPEG encoder found by MIME type, as the HUD and map images of the web server look
        // it up (HUD.GetEncoder("image/jpeg")), honouring the quality parameter
        Step("feature:image-encoders", () =>
        {
            var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");
            if (jpeg == null)
                throw new Exception("no image/jpeg encoder");
            long Size(long quality)
            {
                using (var bmp = new System.Drawing.Bitmap(200, 200))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                using (var ms = new MemoryStream())
                {
                    for (int i = 0; i < 200; i += 4)
                        g.DrawLine(new System.Drawing.Pen(System.Drawing.Color.FromArgb(i, 255 - i, (i * 7) % 255)), 0, i, 200, 200 - i);
                    var parameters = new System.Drawing.Imaging.EncoderParameters(1);
                    parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                    bmp.Save(ms, jpeg, parameters);
                    var bytes = ms.ToArray();
                    if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
                        throw new Exception("not a JPEG at quality " + quality);
                    return bytes.Length;
                }
            }
            long low = Size(20), high = Size(95);
            Console.WriteLine("TOUR image encoders: " + string.Join(",", System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().Select(c => c.MimeType)) +
                              "; JPEG quality 20: " + low + " bytes, 95: " + high + " bytes");
            if (low >= high)
                throw new Exception("JPEG quality ignored (" + low + " >= " + high + " bytes)");
        });
        // text measured with the font's own unit: 12 pt is 96/72 of 12 px (a cache that ignored
        // the unit gave the second the first one's size)
        Step("feature:text-units", () =>
        {
            using (var bmp = new System.Drawing.Bitmap(10, 10))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            using (var px = new System.Drawing.Font("Arial", 12, System.Drawing.GraphicsUnit.Pixel))
            using (var pt = new System.Drawing.Font("Arial", 12, System.Drawing.GraphicsUnit.Point))
            {
                var wpx = g.MeasureString("Mission Planner", px).Width;
                var wpt = g.MeasureString("Mission Planner", pt).Width;
                Console.WriteLine("TOUR text units: 12 px " + wpx + ", 12 pt " + wpt + " wide");
                if (wpt / wpx < 1.25 || wpt / wpx > 1.42)
                    throw new Exception("12 pt is " + (wpt / wpx) + " times 12 px (96/72 = 1.33 expected)");
            }
        });
        // system metrics that threw NotImplementedException (ObjectListView's tooltips read them),
        // and the wait cursor of long operations
        Step("feature:system-metrics", () =>
        {
            Console.WriteLine("TOUR system metrics: cursor " + SystemInformation.CursorSize + ", icon " + SystemInformation.IconSize +
                              ", max track " + SystemInformation.MaxWindowTrackSize + ", hover " + SystemInformation.MouseHoverSize +
                              " " + SystemInformation.MouseHoverTime + " ms");
            var previous = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            Cursor.Current = Cursors.AppStarting;
            Cursor.Current = previous;
        });
        // ImageAttributes: a colour key makes white transparent (BSE panels' magenta icons), and
        // an alpha matrix through the overloads that threw (inactive vehicles are drawn at 39%)
        Step("feature:image-attributes", () =>
        {
            using (var src = new System.Drawing.Bitmap(4, 4))
            using (var dst = new System.Drawing.Bitmap(4, 4))
            using (var g = System.Drawing.Graphics.FromImage(dst))
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                using (var gs = System.Drawing.Graphics.FromImage(src))
                {
                    gs.Clear(System.Drawing.Color.White);
                    gs.FillRectangle(System.Drawing.Brushes.Black, 0, 0, 2, 4);
                }
                g.Clear(System.Drawing.Color.Red);
                ia.SetColorKey(System.Drawing.Color.White, System.Drawing.Color.White);
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, 4, 4), 0, 0, 4, 4, System.Drawing.GraphicsUnit.Pixel, ia);
                var keyed = dst.GetPixel(3, 1);
                var kept = dst.GetPixel(0, 1);
                Console.WriteLine("TOUR image attributes: colour key white -> " + keyed + ", black -> " + kept);
                if (keyed.R < 200 || keyed.G > 60 || kept.R > 60)
                    throw new Exception("colour key: white drew " + keyed + ", black " + kept);
                // the parallelogram overload with a 39% alpha matrix, as GMapMarker*.IsTransparent
                g.Clear(System.Drawing.Color.White);
                var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.39f };
                using (var alpha = new System.Drawing.Imaging.ImageAttributes())
                {
                    alpha.SetColorMatrix(cm, System.Drawing.Imaging.ColorMatrixFlag.Default, System.Drawing.Imaging.ColorAdjustType.Bitmap);
                    g.DrawImage(src, new[] { new System.Drawing.PointF(0, 0), new System.Drawing.PointF(4, 0), new System.Drawing.PointF(0, 4) },
                        new System.Drawing.RectangleF(0, 0, 4, 4), System.Drawing.GraphicsUnit.Pixel, alpha);
                }
                var faded = dst.GetPixel(0, 1);
                Console.WriteLine("TOUR image attributes: black at 39% over white -> " + faded);
                if (faded.R < 120 || faded.R > 190)
                    throw new Exception("alpha matrix: black at 39% over white drew " + faded);
            }
        });
        // an animated GIF (two 4x4 frames: red for 10/100 s, blue for 20/100 s): its frames and
        // delays, which ImageAnimator, PictureBox and ObjectListView step through
        Step("feature:animated-gif", () =>
        {
            var gif = Convert.FromBase64String("R0lGODlhBAAEAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAABAAEAAAICQABCBxIsCCAgAAh+QQBFAABACwAAAAABAAEAIEAAP8AAAAAAAAAAAAICQABCBxIsCCAgAA7");
            using (var bmp = new System.Drawing.Bitmap(new MemoryStream(gif)))
            {
                var frames = bmp.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time);
                var delays = bmp.GetPropertyItem(0x5100).Value;
                var first = bmp.GetPixel(1, 1);
                bmp.SelectActiveFrame(System.Drawing.Imaging.FrameDimension.Time, 1);
                var second = bmp.GetPixel(1, 1);
                int d0 = BitConverter.ToInt32(delays, 0), d1 = BitConverter.ToInt32(delays, 4);
                Console.WriteLine("TOUR animated gif: " + frames + " frames, delays " + d0 + "," + d1 + ", colours " + first + " then " + second);
                if (frames != 2 || d0 != 10 || d1 != 20 || first.R < 200 || second.B < 200 || second.R > 60)
                    throw new Exception("animated GIF: " + frames + " frames, delays " + d0 + "," + d1 + ", colours " + first + ", " + second);
            }
        });
        // lines of a text box as Windows numbers them (the SSH terminal selects lines with these)
        Step("feature:textbox-lines", () =>
        {
            using (var box = new TextBox { Multiline = true, Text = "ab\r\ncd\nef" })
            {
                var l0 = box.GetFirstCharIndexFromLine(0);
                var l1 = box.GetFirstCharIndexFromLine(1);
                var l2 = box.GetFirstCharIndexFromLine(2);
                var l3 = box.GetFirstCharIndexFromLine(3);
                var line = box.GetLineFromCharIndex(l2 + 1);
                Console.WriteLine("TOUR textbox lines: starts " + l0 + "," + l1 + "," + l2 + "," + l3 + "; char " + (l2 + 1) + " on line " + line +
                                  " of \"" + box.Text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"");
                var text = box.Text;
                if (l0 != 0 || l1 < 0 || l2 < 0 || l3 != -1 || line != 2 || text.Substring(l1, 2) != "cd" || text.Substring(l2, 2) != "ef")
                    throw new Exception("text box lines: " + l0 + "," + l1 + "," + l2 + "," + l3 + ", line " + line);
            }
        });
        // GDAL in the app: a GeoTIFF made by its gdal_translate, loaded as the "GDAL Custom" map
        // overlay loads one (gdalinfo, gdalwarp to WGS84, gdal_translate to PNG)
        Step("feature:gdal", () =>
        {
            var translate = MissionPlanner.Utilities.GdalCli.Tool("gdal_translate");
            var bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "gdal"));
            if (translate == null || !translate.StartsWith(bundled))
                throw new Exception("GDAL not in the app (found " + (translate ?? "none") + ")");
            var dir = Path.Combine(Path.GetTempPath(), "mp-gdal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var png = Path.Combine(dir, "source.png");
                using (var bmp = new System.Drawing.Bitmap(64, 64))
                {
                    var random = new Random(1);
                    for (int y = 0; y < 64; y++)
                        for (int x = 0; x < 64; x++)
                            bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(255, random.Next(256), x * 4, y * 4));
                    bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                }
                var images = Path.Combine(dir, "images");
                Directory.CreateDirectory(images);
                MissionPlanner.Utilities.GdalCli.Run("gdal_translate", "-of", "GTiff", "-a_srs", "EPSG:4326",
                    "-a_ullr", "149.16", "-35.36", "149.17", "-35.37", png, Path.Combine(images, "test.tif"));
                File.Delete(png);
                new MissionPlanner.Utilities.GdalCli().ScanDirectory(images);
                using (var tile = MissionPlanner.Utilities.GdalCli.GetBitmap(149.16, -35.36, 149.17, -35.37, 64, 64))
                {
                    var centre = tile?.GetPixel(32, 32);
                    Console.WriteLine("TOUR gdal: " + MissionPlanner.Utilities.GdalCli.Run("gdalinfo", "--version").Trim() +
                                      ", overlay " + (tile == null ? "none" : tile.Width + "x" + tile.Height + ", centre " + centre));
                    if (tile == null || centre.Value.A == 0)
                        throw new Exception("the GeoTIFF was not drawn on the map");
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });
        // the Simulation screen's download of a simulator for macOS (Plane, beta), as Windows
        // downloads ArduPilot's: from the sitl-macos release (sitl-macos.yml), sha256 checked
        Step("feature:sitl-download", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "mp-sitl-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                var index = Path.Combine(dir, "index.json");
                if (!System.Threading.Tasks.Task.Run(() => MissionPlanner.Utilities.Download.getFilefromNetAsync(MissionPlanner.GCSViews.SITL.MacSITLIndexUrl, index)).GetAwaiter().GetResult() ||
                    !File.Exists(index))
                {
                    Console.WriteLine("TOUR sitl download: no sitl-macos release yet (run sitl-macos.yml)");
                    return;
                }
                var sw = Stopwatch.StartNew();
                var path = // (off the UI thread: the download awaits on it otherwise)
                    System.Threading.Tasks.Task.Run(() => MissionPlanner.GCSViews.SITL.GetMacSITL(dir, "ArduPlane", "beta")).GetAwaiter().GetResult();
                if (path == null)
                    throw new Exception("ArduPlane beta not downloaded (or its sha256 differs)");
                var psi = new ProcessStartInfo(path, "--help") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.Environment.Remove("DYLD_PRINT_LIBRARIES");
                using (var proc = Process.Start(psi))
                {
                    var errors = proc.StandardError.ReadToEndAsync();
                    var output = proc.StandardOutput.ReadToEnd() + errors.Result;
                    proc.WaitForExit(20000);
                    Console.WriteLine("TOUR sitl download: ArduPlane beta in " + sw.ElapsedMilliseconds + " ms, " + new FileInfo(path).Length +
                                      " bytes; --help: " + output.Split('\n')[0].Trim());
                    if (output.Trim().Length == 0)
                        throw new Exception("the downloaded simulator does not start");
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });
        // the Log Analyzer's own Python in the app (with numpy), as Windows runs it frozen
        Step("feature:python", () =>
        {
            var python = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "python", "bin", "python3"));
            if (!File.Exists(python))
                throw new Exception("no Python in the app: " + python);
            var psi = new ProcessStartInfo(python, "-B -E -s -c \"import sys, numpy; print(sys.version.split()[0], 'numpy', numpy.__version__)\"")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            // (not the tour's DYLD_PRINT_LIBRARIES, and stderr read alongside stdout: a full pipe
            // would block the process)
            psi.Environment.Remove("DYLD_PRINT_LIBRARIES");
            using (var proc = Process.Start(psi))
            {
                var errors = proc.StandardError.ReadToEndAsync();
                var output = proc.StandardOutput.ReadToEnd() + errors.Result;
                proc.WaitForExit(30000);
                Console.WriteLine("TOUR python: " + output.Trim());
                if (proc.ExitCode != 0)
                    throw new Exception("the app's Python failed: " + output.Trim());
            }
        });
        // adb in the app (Parrot flashing), as adb.exe ships with Windows
        Step("feature:adb", () =>
        {
            var adb = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "platform-tools", "adb"));
            if (!File.Exists(adb))
                throw new Exception("no adb in the app: " + adb);
            var psi = new ProcessStartInfo(adb, "version") { UseShellExecute = false, RedirectStandardOutput = true };
            using (var proc = Process.Start(psi))
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10000);
                Console.WriteLine("TOUR adb: " + output.Split('\n')[0].Trim());
                if (proc.ExitCode != 0)
                    throw new Exception("adb version exited with " + proc.ExitCode);
            }
        });
        // "Check for updates": the commit of this build, recorded in the bundle by build.sh, and
        // the newest build kept on this Mac
        Step("feature:updater", () =>
        {
            var installed = MissionPlanner.Utilities.Update.MacInstalledBuild;
            Console.WriteLine("TOUR updater: installed build " + installed + ", on this Mac " + MissionPlanner.Utilities.Update.MacAvailableBuild +
                              ", update available " + MissionPlanner.Utilities.Update.MacUpdateAvailable);
            if (string.IsNullOrEmpty(installed))
                throw new Exception("no Contents/Resources/BUILD.txt in the app");
        });
        // the plugins Windows ships as DLLs (Shortcuts, FaceMap, Dowding: build.sh builds them)
        // loaded, beside the ones compiled from source
        Step("feature:plugins", () =>
        {
            var expected = new[] { "Shortcuts", "FaceMap", "Dowding" };
            var sw = Stopwatch.StartNew();
            string[] loaded;
            while (true)
            {
                lock (MissionPlanner.Plugin.PluginLoader.Plugins)
                    loaded = MissionPlanner.Plugin.PluginLoader.Plugins.Select(p => p.GetType().Assembly.GetName().Name).Distinct().ToArray();
                if (expected.All(loaded.Contains) || sw.ElapsedMilliseconds > 60000)
                    break;
                Wait(1000);
            }
            Console.WriteLine("TOUR plugins: " + loaded.Length + " loaded (" + string.Join(",", expected.Where(loaded.Contains)) +
                              "), errors: " + string.Join("; ", MissionPlanner.Plugin.PluginLoader.ErrorInfo.Keys.Select(Path.GetFileName)));
            var missing = expected.Where(e => !loaded.Contains(e)).ToArray();
            if (missing.Length > 0)
                throw new Exception("plugins not loaded: " + string.Join(", ", missing));
        });
        // the data directory's one-time move to ~/Documents/Mission Planner (as Windows'
        // Documents\Mission Planner), tried on temporary directories
        Step("feature:data-migration", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "mp-migration-" + Guid.NewGuid().ToString("N"));
            try
            {
                var target = Path.Combine(root, "Documents", "Mission Planner");
                var appSupport = Path.Combine(root, "Library", "Application Support", "Mission Planner");
                var mono = Path.Combine(root, ".local", "share", "Mission Planner");
                Directory.CreateDirectory(appSupport);
                File.WriteAllText(Path.Combine(appSupport, "config.xml"), "<Config />");
                Directory.CreateDirectory(mono);
                var used = MissionPlanner.Utilities.Settings.MigrateMacDataDirectory(target, appSupport, mono);
                var moved = used == target && File.Exists(Path.Combine(target, "config.xml")) && !Directory.Exists(appSupport);
                // once there, later builds' directories are left alone
                Directory.CreateDirectory(appSupport);
                var again = MissionPlanner.Utilities.Settings.MigrateMacDataDirectory(target, appSupport, mono);
                Console.WriteLine("TOUR data migration: moved " + moved + ", second run uses " + (again == target ? "Documents" : again) +
                                  "; this run: " + MissionPlanner.Utilities.Settings.GetUserDataDirectory());
                if (!moved || again != target || !Directory.Exists(appSupport))
                    throw new Exception("data migration: moved " + moved + ", second run " + again);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });
        // Console.Beep's tone (the vario) and alert sound, through the sound output on a Mac
        Step("feature:beep", () =>
        {
            var sw = Stopwatch.StartNew();
            MissionPlanner.Utilities.Beeper.Beep(880, 150);
            var ms = sw.ElapsedMilliseconds;
            MissionPlanner.Utilities.Beeper.Beep();
            Console.WriteLine("TOUR beep: 880 Hz for 150 ms took " + ms + " ms");
            if (ms < 140)
                throw new Exception("tone returned after " + ms + " ms (150 expected)");
        });
        // an owner-drawn ComboBox like the connection bar's port list, whose DrawItem adds the
        // device name: the native list shows that drawing, while Text stays the bare port
        Step("feature:owner-drawn-combo", () =>
            {
                using (var form = new Form { Width = 300, Height = 100 })
                {
                    var combo = new ComboBox { DrawMode = DrawMode.OwnerDrawFixed, Width = 250 };
                    combo.DrawItem += (s, e) =>
                    {
                        if (e.Index < 0) return;
                        e.DrawBackground();
                        e.Graphics.DrawString(combo.Items[e.Index] + " CubeOrange", e.Font, System.Drawing.Brushes.Black, e.Bounds.X, e.Bounds.Y);
                    };
                    combo.Items.Add("/dev/cu.usbmodem1");
                    form.Controls.Add(combo);
                    form.Show();
                    combo.SelectedIndex = 0;
                    Wait(300);
                    var shown = typeof(ComboBox).GetMethod("OwnerDrawnText", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.Invoke(combo, new object[] { 0 }) as string;
                    Console.WriteLine("TOUR owner-drawn combo: list \"" + shown + "\", text \"" + combo.Text + "\"");
                    if (shown != "/dev/cu.usbmodem1 CubeOrange")
                        throw new Exception("owner-drawn combo list shows \"" + shown + "\"");
                    if (combo.Text != "/dev/cu.usbmodem1")
                        throw new Exception("owner-drawn combo Text is \"" + combo.Text + "\"");
                    form.Close();
                }
            });
        // (the checks above are of the native app's own code: ExtLibs/mac-playground, the
        // updater, build.sh's plugins; the Mono build of this tour, macOS/build-tests.sh, has none)
#endif
        // TOUR_SERIAL_LOOPBACK: a serial port that sends back what it receives (a pty from
        // macOS/native/pty-bridge.py), opened the way Mission Planner opens a USB board or radio
        var loopback = Environment.GetEnvironmentVariable("TOUR_SERIAL_LOOPBACK");
        if (!string.IsNullOrEmpty(loopback))
            Step("feature:serial-open", () =>
            {
                // a SiK radio's speed and a fast USB-serial adapter's (not a POSIX B* constant)
                foreach (var baud in new[] { 57600, 921600 })
                    SerialRoundTrip(loopback, baud);
            });
        // joystick axes where DirectInput puts them, from their HID usages: a stick with X, Y,
        // Rz and a slider, and one with X, Y, a rudder, a throttle and a dial
        Step("feature:joystick-axis-usages", () =>
        {
            var stick = MissionPlanner.Joystick.JoystickSDL.HidAxes.Slots(new uint[] { 0x10030, 0x10031, 0x10035, 0x10036 });
            var pedals = MissionPlanner.Joystick.JoystickSDL.HidAxes.Slots(new uint[] { 0x10030, 0x10031, 0x10037, 0x200BA, 0x200BB });
            Console.WriteLine("TOUR joystick axes: X,Y,Rz,Slider -> " + string.Join(",", stick) + "; X,Y,Dial,Rudder,Throttle -> " + string.Join(",", pedals));
            if (string.Join(",", stick) != "0,1,5,6" || string.Join(",", pedals) != "0,1,6,5,10")
                throw new Exception("joystick axes placed " + string.Join(",", stick) + " and " + string.Join(",", pedals));
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
                // two messages queued, as Windows' SpeakAsync queues them (spoken one after the
                // other, not over each other), and busy until both are spoken
                speech.SpeakAsync("Mission Planner test");
                speech.SpeakAsync("Second message");
                var busy = !speech.IsReady;
                var sw = Stopwatch.StartNew();
                while (!speech.IsReady && sw.ElapsedMilliseconds < 20000)
                    Wait(100);
                Console.WriteLine("TOUR speech: busy " + busy + ", both spoken in " + sw.ElapsedMilliseconds + " ms");
                if (!speech.IsReady)
                    throw new Exception("speech did not finish");
                if (!busy)
                    throw new Exception("speech was ready straight after two messages");
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
#if MACNATIVE
            // Config > Planner's camera list and each camera's modes (AVFoundation, in
            // avfvideosrc's order), and the capture pipeline for a chosen mode
            var names = MacNative.CameraNames();
            for (int i = 0; i < names.Count; i++)
            {
                var modes = MacNative.CameraModes(i);
                Console.WriteLine("TOUR camera " + i + " " + names[i] + ": " + modes.Count + " modes (" +
                                  string.Join(", ", modes.Take(6).Select(m => m.Item1 + "x" + m.Item2 + "@" + m.Item3)) + ")");
                if (modes.Count == 0)
                    throw new Exception("no modes for camera " + names[i]);
            }
            var pipeline = MissionPlanner.Utilities.GStreamer.LocalCameraPipeline(0, 1280, 720, 29.97);
            if (!pipeline.Contains("video/x-raw,width=1280,height=720,framerate=29970/1000"))
                throw new Exception("camera pipeline without the mode: " + pipeline);
#endif
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

    private static void SerialRoundTrip(string path, int baud)
    {
        var port = new MissionPlanner.Comms.SerialPort(path, baud) { ReadTimeout = 2000 };
        port.Open();
        try
        {
            // every byte value, as MAVLink is binary
            var sent = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
            port.Write(sent, 0, sent.Length);
            var got = new byte[sent.Length];
            var n = 0;
            var sw = Stopwatch.StartNew();
            while (n < got.Length && sw.ElapsedMilliseconds < 5000)
            {
                if (port.BytesToRead > 0)
                    n += port.Read(got, n, Math.Min(port.BytesToRead, got.Length - n));
                else
                    System.Threading.Thread.Sleep(10);
            }
            Console.WriteLine("TOUR serial " + path + " at " + baud + ": " + n + "/" + sent.Length + " bytes back");
            if (n != sent.Length || !got.SequenceEqual(sent))
                throw new Exception("serial " + path + " at " + baud + ": " + n + "/" + sent.Length + " bytes back" +
                                    (n == sent.Length ? ", different" : ""));
        }
        finally
        {
            port.Close();
        }
    }

    private static void Connect(string connect)
    {
        // tcp:host:port, or serial:/dev/port:baud (through the same call as choosing the port
        // and baud rate in the connection bar and pressing Connect)
        var parts = connect.Split(':');
        if (parts.Length == 3 && parts[0] == "serial")
        {
            MainV2.instance.doConnect(MainV2.comPort, parts[1], parts[2], true, false);
        }
        else if (parts.Length == 3 && parts[0] == "tcp")
        {
            MainV2.comPort.BaseStream = new MissionPlanner.Comms.TcpSerial { Host = parts[1], Port = parts[2] };
            MainV2.instance.doConnect(MainV2.comPort, "preset", "115200", true, false);
        }
        else
        {
            throw new ArgumentException("TOUR_CONNECT must be tcp:host:port or serial:/dev/port:baud");
        }
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
