using System.Diagnostics;

namespace TaskbarPlayer
{
    /// <summary>Small helpers for APIs that .NET Framework 4.8 lacks.</summary>
    internal static class Compat
    {
        private static readonly Stopwatch Uptime = Stopwatch.StartNew();

        /// <summary>Monotonic milliseconds (replacement for Environment.TickCount64).</summary>
        public static long NowMs => Uptime.ElapsedMilliseconds;

        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>Enables C# records and init-only setters on .NET Framework.</summary>
    internal static class IsExternalInit { }
}
