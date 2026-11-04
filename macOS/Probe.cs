using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.ArduPilot.Mavlink;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

// Headless checks of the platform services the UI relies on, quick to run in CI:
//   Probe.exe gstreamer            frames from a GStreamer pipeline
//   Probe.exe vehicle host:port    MAVLink, MAVFTP, the WebDAV server and the OS mount
//   Probe.exe boards               firmware board detection from USB ids
//   Probe.exe ble                  a Bluetooth LE scan (CoreBluetooth on macOS)
class Probe
{
    static int frames;

    // Environment.Exit waits for threads blocked in native code (e.g. GStreamer)
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "_exit")]
    static extern void Exit(int status);

    static void OnFrame(object sender, object image)
    {
        if (image != null)
            Interlocked.Increment(ref frames);
    }

    static bool Run(string name, Action action, int timeoutMs = 60000)
    {
        var sw = Stopwatch.StartNew();
        Exception error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true, Name = "probe " + name };
        thread.Start();
        string result;
        if (!thread.Join(timeoutMs))
            result = "PROBE HANG " + name + " after " + timeoutMs / 1000 + " s";
        else if (error != null)
            result = "PROBE FAIL " + name + ": " + error;
        else
            result = "PROBE OK   " + name + " (" + sw.ElapsedMilliseconds + " ms)";
        Console.WriteLine(result);
        Console.Out.Flush();
        return result.StartsWith("PROBE OK");
    }

    // writes to the console and to a log file: opened as an app (open -a) there is no console
    class Tee : TextWriter
    {
        readonly TextWriter a, b;
        public Tee(TextWriter a, TextWriter b) { this.a = a; this.b = b; }
        public override System.Text.Encoding Encoding => a.Encoding;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string value) { a.Write(value); b.Write(value); }
        public override void WriteLine(string value) { a.WriteLine(value); b.WriteLine(value); }
        public override void Flush() { a.Flush(); b.Flush(); }
    }

    static int Main(string[] args)
    {
        var logDir = Environment.GetEnvironmentVariable("PROBE_LOG_DIR") ??
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "MissionPlanner");
        try
        {
            Directory.CreateDirectory(logDir);
            var file = new StreamWriter(Path.Combine(logDir, "probe-" + (args.Length > 0 ? args[0] : "none") + ".log"), false) { AutoFlush = true };
            Console.SetOut(new Tee(Console.Out, file));
            Console.WriteLine("probe " + string.Join(" ", args) + " pid " + Process.GetCurrentProcess().Id +
                              " runner " + (Environment.GetEnvironmentVariable("MP_LIBMONO") != null ? "mono-host" : "mono"));
        }
        catch
        {
        }

        Settings.CustomUserDataDirectory = Path.Combine(Path.GetTempPath(), "mp-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Settings.GetUserDataDirectory());
        var ok = true;

        if (args.Length > 0 && args[0] == "gstreamer")
        {
            ok &= Run("gstreamer-frames", () =>
            {
                GStreamer.GstLaunch = GStreamer.LookForGstreamer();
                Console.WriteLine("gstreamer at " + GStreamer.GstLaunch);
                var gst = new GStreamer();
                var ev = typeof(GStreamer).GetEvent("OnNewImage");
                ev.AddEventHandler(gst, Delegate.CreateDelegate(ev.EventHandlerType,
                    typeof(Probe).GetMethod("OnFrame", BindingFlags.Static | BindingFlags.NonPublic)));
                gst.Start(args.Length > 1 ? args[1] :
                    "videotestsrc ! video/x-raw,width=320,height=240 ! videoconvert ! video/x-raw,format=BGRx ! appsink name=outsink");
                var sw = Stopwatch.StartNew();
                while (frames < 30 && sw.ElapsedMilliseconds < 20000)
                    Thread.Sleep(100);
                Console.WriteLine("frames: " + frames);
                gst.Stop();
                if (frames < 30)
                    throw new Exception("only " + frames + " frames");
            }, 45000);
        }

        if (args.Length > 0 && args[0] == "boards")
        {
            // board detection for firmware upload from USB ids and product names, in the format
            // Linux.GetAllCOMPorts reads them from the IOKit registry (macOS) or sysfs (Linux)
            var cases = new[]
            {
                new { board = "fmuv2", id = @"USB\VID_0483&PID_5740&REV_0000", expect = "px4v2", bl = "" },
                new { board = "fmuv3-BL", id = @"USB\VID_1209&PID_5740&REV_0000", expect = "px4v3", bl = "" },
                new { board = "CubeOrange-BL", id = @"USB\VID_2DAE&PID_1016&REV_0000", expect = "chbootloader", bl = "CubeOrange" },
                new { board = "MatekH743", id = @"USB\VID_1209&PID_5740&REV_0000", expect = "chbootloader", bl = "MatekH743" },
                new { board = "PX4 FMU v4.x", id = @"USB\VID_26AC&PID_0012&REV_0000", expect = "px4v4", bl = "" },
            };
            foreach (var c in cases)
            {
                ok &= Run("board " + c.board, () =>
                {
                    var ports = new System.Collections.Generic.List<MissionPlanner.ArduPilot.DeviceInfo>
                    {
                        // another USB serial device first: it must be skipped
                        new MissionPlanner.ArduPilot.DeviceInfo { name = "/dev/cu.usbserial-A1", board = "FT232R USB UART", description = "", hardwareid = @"USB\VID_0403&PID_6001&REV_0000" },
                        new MissionPlanner.ArduPilot.DeviceInfo { name = "/dev/cu.usbmodem1101", board = c.board, description = "", hardwareid = c.id },
                    };
                    var ans = BoardDetect.DetectBoard("/dev/cu.usbmodem1101", ports);
                    Console.WriteLine(c.board + " -> " + ans + (ans == BoardDetect.boards.chbootloader ? " " + BoardDetect.chbootloader : ""));
                    if (ans.ToString() != c.expect || (c.bl != "" && BoardDetect.chbootloader != c.bl))
                        throw new Exception("expected " + c.expect + " " + c.bl);
                }, 20000);
            }
        }

        if (args.Length > 0 && args[0] == "ble")
        {
            // Bluetooth LE through the bundled SimpleBLE (CoreBluetooth), as the connection list
            // does. macOS ends a process that uses Bluetooth without a usage description in the
            // Info.plist of the app it belongs to: reaching "PROBE OK" means it did not.
            ok &= Run("ble-scan", () =>
            {
                var ports = CommsBLE.SerialPort_GetCustomPorts();
                Console.WriteLine("ble: adapter check done, " + ports.Count + " ports");
                Console.Out.Flush();
                // the scan runs in the background for 5 s
                Thread.Sleep(8000);
                ports = CommsBLE.SerialPort_GetCustomPorts();
                Console.WriteLine("ble ports: " + string.Join(", ", ports));
            }, 30000);
        }

        if (args.Length > 1 && args[0] == "vehicle")
        {
            var parts = args[1].Split(':');
            var mav = new MAVLinkInterface();
            ok &= Run("mavlink-connect", () =>
            {
                mav.BaseStream = new TcpSerial { Host = parts[0], Port = parts[1] };
                mav.Open(false, false, false);
                Console.WriteLine("sysid " + mav.MAV.sysid + " compid " + mav.MAV.compid + " fw " + mav.MAV.cs.firmware);
                // the application reads the link on its own thread (MainV2.SerialReader)
                new Thread(() =>
                {
                    while (true)
                    {
                        try { mav.readPacketAsync().Wait(); }
                        catch { Thread.Sleep(10); }
                    }
                }) { IsBackground = true, Name = "reader" }.Start();
            }, 90000);
            MAVFtp ftp = null;
            ok &= Run("mavftp-list", () =>
            {
                ftp = new MAVFtp(mav, mav.MAV.sysid, mav.MAV.compid);
                var list = ftp.kCmdListDirectory("/", new CancellationTokenSource(30000));
                Console.WriteLine("/: " + string.Join(", ", list.Select(a => a.Name)));
                if (!list.Any(a => a.Name != "." && a.Name != ".."))
                    throw new Exception("empty listing");
            });
            MavFtpWebDav server = null;
            ok &= Run("webdav-server", () =>
            {
                server = MavFtpWebDav.Serve(ftp);
                Console.WriteLine("serving " + server.Url);
                foreach (var method in new[] { "OPTIONS", "PROPFIND" })
                {
                    var req = (HttpWebRequest)WebRequest.Create(server.Url);
                    req.Method = method;
                    req.Headers["Depth"] = "1";
                    req.Timeout = 30000;
                    using (var res = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(res.GetResponseStream()))
                    {
                        var body = sr.ReadToEnd();
                        Console.WriteLine(method + " " + (int)res.StatusCode + " " + body.Length + " bytes, " +
                                          (body.Split(new[] { "<D:href>" }, StringSplitOptions.None).Length - 1) + " entries");
                    }
                }
            });
            server?.Close();
            ok &= Run("webdav-mount", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "mavftp-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                MavFtpWebDav.Mount(ftp, dir);
                try
                {
                    Console.WriteLine("mounted: " + string.Join(", ", Directory.GetFileSystemEntries(dir).Select(Path.GetFileName)));
                }
                finally
                {
                    MavFtpWebDav.Unmount();
                }
            }, 90000);
        }

        Console.WriteLine(ok ? "PROBE_PASS" : "PROBE_FAIL");
        Console.Out.Flush();
        Exit(ok ? 0 : 1);
        return 0;
    }
}
