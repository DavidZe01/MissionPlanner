using System;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Console.Beep, replaceable per platform. A Mac has no PC speaker: there
    /// Console.Beep(frequency, duration) throws PlatformNotSupportedException and Console.Beep()
    /// only writes a BEL to the console, so the native macOS app sets these to its own sounds.
    /// </summary>
    public static class Beeper
    {
        /// <summary>The system alert sound (Console.Beep()).</summary>
        public static Action Simple = () => Console.Beep();

        /// <summary>A tone of a frequency (Hz) for a duration (ms), returning when it ends.</summary>
        public static Action<int, int> Tone = (frequency, duration) => Console.Beep(frequency, duration);

        public static void Beep()
        {
            Simple();
        }

        public static void Beep(int frequency, int duration)
        {
            Tone(frequency, duration);
        }
    }
}
