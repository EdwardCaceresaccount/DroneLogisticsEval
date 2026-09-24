using System.Collections.Generic;

namespace SimCore.Events
{
    /// <summary>
    /// One recorded fact about the episode, stamped with the sim tick it occurred on.
    /// Type strings are UPPER_SNAKE constants (see SimEventTypes) so logs are greppable
    /// and stable across versions. Block 6 adds JSONL export and Decision Moments on top.
    /// </summary>
    public sealed class SimEvent
    {
        public long Tick { get; }
        public double TimeSeconds { get; }
        public string Type { get; }
        public IReadOnlyDictionary<string, object> Data { get; }

        public SimEvent(long tick, double timeSeconds, string type, Dictionary<string, object> data = null)
        {
            Tick = tick;
            TimeSeconds = timeSeconds;
            Type = type;
            Data = data ?? new Dictionary<string, object>();
        }

        public override string ToString() => $"[t={TimeSeconds:F2}s tick={Tick}] {Type}";
    }

    public static class SimEventTypes
    {
        public const string LiftOff          = "LIFT_OFF";
        public const string Land             = "LAND";
        public const string MoveStarted      = "MOVE_STARTED";
        public const string MoveArrived      = "MOVE_ARRIVED";
        public const string SpeedSet         = "SPEED_SET";
        public const string LoadStarted      = "LOAD_STARTED";
        public const string PackageLoaded    = "PACKAGE_LOADED";
        public const string PackageDelivered = "PACKAGE_DELIVERED";
        public const string ChargeStarted    = "CHARGE_STARTED";
        public const string ChargeCompleted  = "CHARGE_COMPLETED";
        public const string WaitStarted      = "WAIT_STARTED";
        public const string WaitCompleted    = "WAIT_COMPLETED";
        public const string ForcedLanding    = "FORCED_LANDING";
        public const string Crash            = "CRASH";
    }

    public interface ISimEventSink
    {
        void Emit(SimEvent e);
    }

    /// <summary>In-memory sink. Good enough for tests and Block 4; Block 6 adds the persistent EventLog.</summary>
    public sealed class ListEventSink : ISimEventSink
    {
        public List<SimEvent> Events { get; } = new();
        public void Emit(SimEvent e) => Events.Add(e);

        public bool Contains(string type)
        {
            foreach (var e in Events) if (e.Type == type) return true;
            return false;
        }
    }
}