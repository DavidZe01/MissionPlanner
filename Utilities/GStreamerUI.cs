using MissionPlanner.Controls;
using System;

namespace MissionPlanner.Utilities
{
    public class GStreamerUI
    {
        public static void DownloadGStreamer()
        {
            // the download is a Windows build; on macOS GStreamer comes from Homebrew
            // (or the official GStreamer.framework), which GStreamer.LookForGstreamer finds
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                GStreamer.GstLaunch = GStreamer.LookForGstreamer();
                if (GStreamer.GstLaunch == null)
                    CustomMessageBox.Show(
                        "Video needs GStreamer. Install it in Terminal with:\n\n    brew install gstreamer\n\nthen restart Mission Planner. [link;https://gstreamer.freedesktop.org/download/#macos;GStreamer for macOS]",
                        "GStreamer");
                return;
            }

            ProgressReporterDialogue prd = new ProgressReporterDialogue();
            ThemeManager.ApplyThemeTo(prd);
            prd.DoWork += sender =>
            {
                GStreamer.DownloadGStreamer(((i, s) =>
                {
                    prd.UpdateProgressAndStatus(i, s);
                    if (prd.doWorkArgs.CancelRequested) throw new Exception("User Request");
                }));
            };
            prd.RunBackgroundOperationAsync();

            GStreamer.GstLaunch = GStreamer.LookForGstreamer();
        }
    }
}