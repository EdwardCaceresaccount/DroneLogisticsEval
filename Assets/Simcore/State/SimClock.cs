using System;

namespace SimCore.State
{
    /// <summary>
    /// Fixed-timestep simulation clock. Sim time = Ticks * TickSeconds, full stop.
    /// It never reads wall-clock time and never reads Unity's Time class (blocked anyway).
    /// The visualization layer may run ticks slower, faster, or infinitely fast (headless) —
    /// the simulation result is identical in all three cases. This is the determinism guarantee.
    /// </summary>
    public sealed class SimClock
    {
        public double TickSeconds { get; }
        public long Ticks { get; private set; }
        public double TimeSeconds => Ticks * TickSeconds;

        public SimClock(double tickSeconds)
        {
            if (tickSeconds <= 0 || tickSeconds > 1.0)
                throw new ArgumentOutOfRangeException(nameof(tickSeconds),
                    $"Tick must be in (0, 1] seconds, got {tickSeconds}.");
            TickSeconds = tickSeconds;
        }

        public void Advance() => Ticks++;
    }
}