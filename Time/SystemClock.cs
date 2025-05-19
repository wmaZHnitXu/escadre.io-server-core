// File: Core/Time/SystemClock.cs
using System;

namespace Core.Time
{
    /// <summary>
    /// A basic implementation of IClock using System.Diagnostics.Stopwatch
    /// for server-side or non-Unity client scenarios.
    /// For Unity clients, a Unity-specific clock (e.g., using Time.time) is preferred.
    /// </summary>
    public class SystemClock : IClock
    {
        private readonly System.Diagnostics.Stopwatch _stopwatch;

        public SystemClock()
        {
            _stopwatch = System.Diagnostics.Stopwatch.StartNew();
        }

        public float CurrentTime => (float)_stopwatch.Elapsed.TotalSeconds;
    }
}