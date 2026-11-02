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

    static void OnFrame(object sender, object image)
    {
        if (image != null)
            Interlocked.Increment(ref frames);
    }

    static bool Run(string name, Action action, int timeoutMs = 60000)
    {
        var sw = Stopwatch.StartNew();
        var task = Task.Run(action);
        try
        {
            if (!task.Wait(timeoutMs))
            {
                Console.WriteLine("PROBE HANG " + name + " after " + timeoutMs / 1000 + " s");
                return false;
            }
            Console.WriteLine("PROBE OK   " + name + " (" + sw.ElapsedMilliseconds + " ms)");
            return true;
        }
        catch (AggregateException ex)
        {
            Console.WriteLine("PROBE FAIL " + name + ": " + ex.InnerException);
            return false;
        }
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
                mav.Open(false, true, false);
                Console.WriteLine("sysid " + mav.MAV.sysid + " fw " + mav.MAV.cs.firmware);
            }, 90000);
            MAVFtp ftp = null;
            ok &= Run("mavftp-list", () =>
            {
                ftp = new MAVFtp(mav, mav.MAV.sysid, mav.MAV.compid);
                var list = ftp.kCmdListDirectory("/", new CancellationTokenSource(30000));
                Console.WriteLine("/: " + string.Join(", ", list.Select(a => a.Name)));
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
        Environment.Exit(ok ? 0 : 1);
        return 0;
    }
}
