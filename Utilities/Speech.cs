using log4net;
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;

namespace MissionPlanner.Utilities
{
    public class Speech : IDisposable, ISpeech
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public bool speechEnable { get; set; } = false;

        SpeechSynthesizer _speechwindows;
        System.Diagnostics.Process _speechlinux;

        System.Speech.Synthesis.SynthesizerState _state = SynthesizerState.Ready;

        bool MONO = false;

        // macOS has a built-in synthesizer (say); festival is not available there.
        static readonly bool MacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && File.Exists("/usr/bin/say");
        System.Diagnostics.Process _speechmac;

        public bool IsReady
        {
            get
            {
#if MACNATIVE
                if (_speechav != null)
                    return _avPending <= 0;
#endif
                if (MONO)
                {
                    return _state == SynthesizerState.Ready;
                }
                else
                {
                    try
                    {
                        if (_speechwindows != null)
                            return _speechwindows.State == SynthesizerState.Ready;
                    }
                    catch
                    {
                        return false;
                    }
                    return false;
                }
            }
        }

        public Speech()
        {
            var t = Type.GetType("Mono.Runtime");
            // .NET on macOS too: not Windows' .NET Framework
            MONO = (t != null || !System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows));

            log.Info("TTS: init, mono = " + MONO);

            if (MONO)
            {
                _state = SynthesizerState.Ready;
            }
            else
            {
                _speechwindows = new SpeechSynthesizer();
            }
        }

        public void SpeakAsync(string text)
        {
            if (!MainV2.speechEnabled())
                return;

            if (text == null || String.IsNullOrWhiteSpace(text))
                return;

            text = Regex.Replace(text, @"\bPreArm\b", "Pre Arm", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bdist\b", "distance", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bNAV\b", "Navigation", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b([0-9]+)m\b", "$1 meters", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b([0-9]+)ft\b", "$1 feet", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b([0-9]+)\bbaud\b", "$1 baudrate", RegexOptions.IgnoreCase);
            // Stop, for example, QHover from being pronounced "kohver"
            text = Regex.Replace(text, @"\bq((?!u)[a-z]+)", "q $1", RegexOptions.IgnoreCase);

            if (MONO && MacOS)
            {
#if MACNATIVE
                if (!SpeakAV(text))
#endif
                SpeakMac(text);
            }
            else if (MONO)
            {
                try
                {
                    //if (_speechlinux == null)
                    {
                        _state = SynthesizerState.Speaking;
                        _speechlinux = new System.Diagnostics.Process();
                        _speechlinux.StartInfo.RedirectStandardInput = true;
                        _speechlinux.StartInfo.UseShellExecute = false;
                        _speechlinux.StartInfo.FileName = "/bin/bash";
                        _speechlinux.StartInfo.Arguments = "-c festival";
                        _speechlinux.StartInfo.WorkingDirectory = "/bin";
                        _speechlinux.Start();
                        _speechlinux.Exited += new EventHandler(_speechlinux_Exited);

                        log.Info("TTS: start " + _speechlinux.StartTime);

                    }

                    _state = SynthesizerState.Speaking;
                    _speechlinux.StandardInput.WriteLine("(SayText \"" + text + "\")");
                    _speechlinux.StandardInput.WriteLine("(quit)");

                    _speechlinux.Close();
                }
                catch (Exception ex) { log.Error(ex); } // ignore errors


                _state = SynthesizerState.Ready;
            }
            else
            {
                try
                {
                    if (_speechwindows != null)
                    {
                        PromptBuilder pb = new PromptBuilder(CultureInfo.CurrentUICulture);
                        pb.AppendText(text);
                        _speechwindows.SpeakAsync(pb);
                    }
                }
                catch (COMException)
                {

                }
            }

            log.Info("TTS: say " + text);
        }

#if MACNATIVE
        // The native macOS app: AVSpeechSynthesizer queues what it is given, as Windows'
        // SpeakAsync does (one `say` per message spoke them over each other), in the system
        // voice of the interface's language (PromptBuilder(CurrentUICulture) on Windows)
        AVFoundation.AVSpeechSynthesizer _speechav;
        int _avPending;

        bool SpeakAV(string text)
        {
            try
            {
                if (_speechav == null)
                {
                    var synth = new AVFoundation.AVSpeechSynthesizer();
                    synth.DidFinishSpeechUtterance += (s, e) => System.Threading.Interlocked.Decrement(ref _avPending);
                    synth.DidCancelSpeechUtterance += (s, e) => System.Threading.Interlocked.Decrement(ref _avPending);
                    _speechav = synth;
                }
                var utterance = new AVFoundation.AVSpeechUtterance(text);
                var language = CultureInfo.CurrentUICulture.Name;
                var voice = string.IsNullOrEmpty(language) ? null : AVFoundation.AVSpeechSynthesisVoice.FromLanguage(language);
                if (voice != null)
                    utterance.Voice = voice;
                System.Threading.Interlocked.Increment(ref _avPending);
                try
                {
                    _speechav.SpeakUtterance(utterance);
                }
                catch
                {
                    System.Threading.Interlocked.Decrement(ref _avPending);
                    throw;
                }
                return true;
            }
            catch (Exception ex)
            {
                log.Error(ex);
                return false;
            }
        }
#endif

        void SpeakMac(string text)
        {
            try
            {
                var proc = new System.Diagnostics.Process();
                proc.StartInfo.FileName = "/usr/bin/say";
                proc.StartInfo.Arguments = "-f -";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardInput = true;
                proc.EnableRaisingEvents = true;
                proc.Exited += (sender, e) =>
                {
                    if (_speechmac == sender)
                        _state = SynthesizerState.Ready;
                };
                _state = SynthesizerState.Speaking;
                _speechmac = proc;
                proc.Start();
                proc.StandardInput.Write(text);
                proc.StandardInput.Close();
            }
            catch (Exception ex)
            {
                log.Error(ex);
                _state = SynthesizerState.Ready;
            }
        }

        void _speechlinux_Exited(object sender, EventArgs e)
        {
            log.Info("TTS: exit " + _speechlinux.ExitTime);
            _state = SynthesizerState.Ready;
        }

        public void SpeakAsyncCancelAll()
        {
#if MACNATIVE
            if (_speechav != null)
            {
                try
                {
                    _speechav.StopSpeaking(AVFoundation.AVSpeechBoundary.Immediate);
                }
                catch { }
                System.Threading.Interlocked.Exchange(ref _avPending, 0);
            }
#endif
            if (MONO)
            {
                try
                {
                    if (_speechmac != null && !_speechmac.HasExited)
                        _speechmac.Kill();
                }
                catch { }
                try
                {
                    if (_speechlinux != null)
                        _speechlinux.Close();
                }
                catch { }
                _state = SynthesizerState.Ready;
            }
            else
            {
                try
                {
                    if (_speechwindows != null)
                        _speechwindows.SpeakAsyncCancelAll();
                }
                catch (System.PlatformNotSupportedException)
                {
                    _speechwindows = null;
                }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_speechwindows != null)
                _speechwindows.Dispose();
            if (_speechlinux != null)
                _speechlinux.Dispose();
            if (_speechmac != null)
                _speechmac.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}