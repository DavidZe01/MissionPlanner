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

    static int Main(string[] args)
    {
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
                if (!list.Any(a => a.Name == "mp-probe.txt"))
                    throw new Exception("mp-probe.txt not listed");
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
                var get = (HttpWebRequest)WebRequest.Create(server.Url + "mp-probe.txt");
                get.Timeout = 30000;
                using (var res = (HttpWebResponse)get.GetResponse())
                using (var sr = new StreamReader(res.GetResponseStream()))
                {
                    var text = sr.ReadToEnd().Trim();
                    Console.WriteLine("GET mp-probe.txt " + (int)res.StatusCode + ": " + text);
                    if (text != "mission planner probe")
                        throw new Exception("unexpected content");
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
