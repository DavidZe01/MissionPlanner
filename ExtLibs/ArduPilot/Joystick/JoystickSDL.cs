using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace MissionPlanner.Joystick
{
    /// <summary>
    /// Joystick backend using SDL2 (IOKit HID on macOS). DirectInput and the
    /// Linux joystick device are not available on macOS. SDL's joystick API is
    /// not thread safe, so every SDL call runs on one dedicated thread.
    /// </summary>
    public class JoystickSDL : JoystickBase
    {
        private string name;
        private volatile bool acquired;

        public JoystickSDL(Func<MAVLinkInterface> currentInterface) : base(currentInterface)
        {
        }

        public static bool IsSupported
        {
            get { return Service.Available; }
        }

        public override bool AcquireJoystick(string name)
        {
            log.Info("AcquireJoystick " + name);
            if (!Service.Open(name))
                return false;
            this.name = name;
            acquired = true;
            return true;
        }

        public override void UnAcquireJoyStick()
        {
            log.Info("UnAcquireJoyStick " + name);
            acquired = false;
            if (name != null)
                Service.Close(name);
        }

        public override bool IsJoystickValid()
        {
            return acquired && Service.IsAttached(name);
        }

        public override IMyJoystickState GetCurrentState()
        {
            var current = Service.GetState(name);
            if (current != null)
                state = current;
            return state;
        }

        public override int getNumButtons()
        {
            return Service.GetCounts(name).Item2;
        }

        public override int getNumberPOV()
        {
            return Service.GetCounts(name).Item3;
        }

        public new static IList<string> getDevices()
        {
            return Service.Devices();
        }

        public new static JoystickBase getJoyStickByName(string name)
        {
            if (!getDevices().Contains(name))
                return null;
            var js = new JoystickSDL(() => null);
            js.AcquireJoystick(name);
            return js;
        }

        public override void Dispose()
        {
            UnAcquireJoyStick();
            base.Dispose();
        }

        /// <summary>Axis/button/hat state in the same layout as the Linux backend.</summary>
        public class SDLJoystickState : LinuxJoystickState, IMyJoystickState
        {
            private readonly int[] pov;

            public SDLJoystickState(Dictionary<byte, ushort> axis, Dictionary<byte, bool> buttons, int[] pov)
                : base(axis, buttons)
            {
                this.pov = pov;
            }

            public new int[] GetPointOfView()
            {
                return pov;
            }
        }

        private static class Service
        {
            private static readonly object sync = new object();
            private static Thread thread;
            private static readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
            private static bool initFailed;
            // Opened joysticks by name, and their latest state. Only the SDL thread
            // touches the handles; the states are swapped atomically.
            private static readonly Dictionary<string, IntPtr> handles = new Dictionary<string, IntPtr>();
            private static readonly ConcurrentDictionary<string, SDLJoystickState> states = new ConcurrentDictionary<string, SDLJoystickState>();
            private static readonly ConcurrentDictionary<string, Tuple<int, int, int>> counts = new ConcurrentDictionary<string, Tuple<int, int, int>>();

            public static bool Available
            {
                get { return Invoke(() => true, false); }
            }

            private static T Invoke<T>(Func<T> func, T fallback)
            {
                if (!EnsureThread())
                    return fallback;
                if (Thread.CurrentThread == thread)
                    return func();
                var done = new ManualResetEventSlim();
                T result = fallback;
                work.Add(() =>
                {
                    try { result = func(); }
                    catch (Exception ex) { log.Error(ex); }
                    finally { done.Set(); }
                });
                return done.Wait(3000) ? result : fallback;
            }

            private static bool EnsureThread()
            {
                lock (sync)
                {
                    if (initFailed)
                        return false;
                    if (thread != null)
                        return true;
                    var ready = new ManualResetEventSlim();
                    var ok = false;
                    thread = new Thread(() => Run(ref ok, ready)) { IsBackground = true, Name = "SDL joystick" };
                    thread.Start();
                    ready.Wait(5000);
                    if (!ok)
                    {
                        initFailed = true;
                        thread = null;
                    }
                    return ok;
                }
            }

            // MISSIONPLANNER_SDL_TRACE=1: a marker before/after each native SDL call on this
            // thread, so a hang (none of these calls has a timeout; EnsureThread's and
            // Invoke's waits only give up on THIS thread, which stays stuck in whichever call
            // it was in) shows exactly where in the next run's log - the last "> " with no
            // matching "< " right after it.
            private static readonly bool trace = Environment.GetEnvironmentVariable("MISSIONPLANNER_SDL_TRACE") == "1";

            private static void Trace(string where)
            {
                if (!trace)
                    return;
                Console.WriteLine("SDL " + where + " " + DateTime.Now.ToString("HH:mm:ss.fff"));
                Console.Out.Flush();
            }

            private static void Run(ref bool ok, ManualResetEventSlim ready)
            {
                try
                {
                    Trace("> SDL_SetHint");
                    Native.SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
                    Trace("< SDL_SetHint");
                    Trace("> SDL_Init");
                    var initResult = Native.SDL_Init(Native.SDL_INIT_JOYSTICK);
                    Trace("< SDL_Init");
                    if (initResult != 0)
                    {
                        log.Error("SDL_Init failed: " + Native.GetError());
                        ready.Set();
                        return;
                    }
                    Trace("> SDL_JoystickEventState");
                    Native.SDL_JoystickEventState(0); // polled with SDL_JoystickUpdate
                    Trace("< SDL_JoystickEventState");
                    Trace("> SDL_NumJoysticks (startup)");
                    var count = Native.SDL_NumJoysticks();
                    Trace("< SDL_NumJoysticks (startup)");
                    log.Info("SDL joystick started, " + count + " devices");
                    ok = true;
                }
                catch (Exception ex)
                {
                    // SDL2 is not installed: joysticks are unavailable, everything else works.
                    log.Error("SDL2 not available: " + ex.Message);
                    ready.Set();
                    return;
                }
                ready.Set();

                while (true)
                {
                    Action action;
                    while (work.TryTake(out action))
                        action();
                    try
                    {
                        Poll();
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex);
                    }
                    if (work.TryTake(out action, 15))
                        action();
                }
            }

            private static void Poll()
            {
                Native.SDL_JoystickUpdate();
                foreach (var kv in handles.ToArray())
                {
                    var js = kv.Value;
                    if (Native.SDL_JoystickGetAttached(js) == 0)
                    {
                        Native.SDL_JoystickClose(js);
                        handles.Remove(kv.Key);
                        SDLJoystickState removed;
                        states.TryRemove(kv.Key, out removed);
                        continue;
                    }
                    int axes = Native.SDL_JoystickNumAxes(js), buttons = Native.SDL_JoystickNumButtons(js), hats = Native.SDL_JoystickNumHats(js);
                    counts[kv.Key] = Tuple.Create(axes, buttons, hats);
                    var axis = new Dictionary<byte, ushort>();
                    for (int a = 0; a < axes && a < 128; a++)
                        axis[(byte)a] = (ushort)(Native.SDL_JoystickGetAxis(js, a) + 32768);
                    var button = new Dictionary<byte, bool>();
                    for (int b = 0; b < buttons && b < 128; b++)
                        button[(byte)b] = Native.SDL_JoystickGetButton(js, b) != 0;
                    var pov = new int[Math.Max(1, hats)];
                    for (int h = 0; h < pov.Length; h++)
                        pov[h] = h < hats ? HatToPov(Native.SDL_JoystickGetHat(js, h)) : -1;
                    states[kv.Key] = new SDLJoystickState(axis, button, pov);
                }
            }

            // DirectInput convention: hundredths of a degree clockwise from up, -1 centred.
            private static int HatToPov(byte hat)
            {
                switch (hat)
                {
                    case 0x01: return 0;
                    case 0x03: return 4500;
                    case 0x02: return 9000;
                    case 0x06: return 13500;
                    case 0x04: return 18000;
                    case 0x0c: return 22500;
                    case 0x08: return 27000;
                    case 0x09: return 31500;
                    default: return -1;
                }
            }

            private static List<string> ListNames()
            {
                Trace("> SDL_JoystickUpdate");
                Native.SDL_JoystickUpdate();
                Trace("< SDL_JoystickUpdate");
                var names = new List<string>();
                Trace("> SDL_NumJoysticks");
                int n = Native.SDL_NumJoysticks();
                Trace("< SDL_NumJoysticks = " + n);
                for (int i = 0; i < n; i++)
                {
                    Trace("> NameForIndex " + i);
                    var baseName = Native.NameForIndex(i);
                    Trace("< NameForIndex " + i + " = " + baseName);
                    var unique = baseName;
                    for (int dup = 2; names.Contains(unique); dup++)
                        unique = baseName + " #" + dup;
                    names.Add(unique);
                }
                return names;
            }

            public static IList<string> Devices()
            {
                return Invoke(() => (IList<string>)ListNames(), new List<string>());
            }

            public static bool Open(string name)
            {
                return Invoke(() =>
                {
                    if (handles.ContainsKey(name))
                        return true;
                    var index = ListNames().IndexOf(name);
                    if (index < 0)
                        return false;
                    var js = Native.SDL_JoystickOpen(index);
                    if (js == IntPtr.Zero)
                    {
                        log.Error("SDL_JoystickOpen failed: " + Native.GetError());
                        return false;
                    }
                    handles[name] = js;
                    Poll();
                    return true;
                }, false);
            }

            public static void Close(string name)
            {
                Invoke(() =>
                {
                    IntPtr js;
                    if (handles.TryGetValue(name, out js))
                    {
                        Native.SDL_JoystickClose(js);
                        handles.Remove(name);
                    }
                    SDLJoystickState removed;
                    states.TryRemove(name, out removed);
                    return true;
                }, false);
            }

            public static bool IsAttached(string name)
            {
                return name != null && states.ContainsKey(name);
            }

            public static SDLJoystickState GetState(string name)
            {
                SDLJoystickState s;
                return name != null && states.TryGetValue(name, out s) ? s : null;
            }

            public static Tuple<int, int, int> GetCounts(string name)
            {
                Tuple<int, int, int> c;
                return name != null && counts.TryGetValue(name, out c) ? c : Tuple.Create(0, 0, 0);
            }
        }

        private static class Native
        {
            // Resolved through the platform loader: libSDL2.dylib on macOS (the
            // app bundle maps it to its bundled copy), libSDL2.so elsewhere.
            private const string lib = "SDL2";
            public const uint SDL_INIT_JOYSTICK = 0x00000200;

            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SDL_Init(uint flags);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr SDL_GetError();
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SDL_NumJoysticks();
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr SDL_JoystickNameForIndex(int index);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr SDL_JoystickOpen(int index);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void SDL_JoystickClose(IntPtr joystick);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SDL_JoystickGetAttached(IntPtr joystick);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void SDL_JoystickUpdate();
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SDL_JoystickEventState(int state);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SDL_JoystickNumAxes(IntPtr joystick);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SDL_JoystickNumButtons(IntPtr joystick);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SDL_JoystickNumHats(IntPtr joystick);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern short SDL_JoystickGetAxis(IntPtr joystick, int axis);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern byte SDL_JoystickGetButton(IntPtr joystick, int button);
            [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern byte SDL_JoystickGetHat(IntPtr joystick, int hat);

            public static string GetError()
            {
                return Marshal.PtrToStringAnsi(SDL_GetError());
            }

            public static string NameForIndex(int index)
            {
                var name = Marshal.PtrToStringAnsi(SDL_JoystickNameForIndex(index));
                return string.IsNullOrWhiteSpace(name) ? "Joystick " + index : name.Trim();
            }
        }
    }
}
