using SimCore.Domain;
using SimCore.State;

namespace SimCore.Engine
{
    public static class LowBatteryEventTypes
    {
        public const string LowBattery               = "LOW_BATTERY";
        public const string LowBatteryUnrecoverable  = "LOW_BATTERY_UNRECOVERABLE";
        public const string LowBatteryNoAutoRecovery = "LOW_BATTERY_NO_AUTO_RECOVERY";
        public const string RecoveryStarted          = "RECOVERY_STARTED";
    }

    /// <summary>Everything the simulator knew at the moment it judged the battery — logged verbatim.</summary>
    public sealed class LowBatteryAssessment
    {
        public bool IsLowBattery;
        public double BatteryPct;
        public double MarginPct;                       // configured safety margin (1x)
        public double TriggerMarginPct => MarginPct * 2; // D14: trigger reserve (2x)
        public GridCoord? SegmentTarget;               // null when hovering
        public double SegmentRequiredPct;              // at the drone's current speed
        public double AfterSegmentRequiredPct;         // segment end → nearest clear charge tile, at Slow (+inf if walled off)
        public GridCoord? RecoveryTile;                // nearest charge tile reachable from CURRENT position, at Slow, with 1x margin
        public double RecoveryRequiredPct;
        public double TotalRequiredPct => SegmentRequiredPct + AfterSegmentRequiredPct + TriggerMarginPct;
    }

    /// <summary>
    /// D14. Pure function of engine state — no side effects, no events. The EpisodeRunner decides what to do
    /// with the assessment (D15), which keeps "detect" and "act" separately testable.
    /// </summary>
    public static class LowBatteryMonitor
    {
        public static LowBatteryAssessment Assess(SimulationEngine e)
        {
            var d = e.World.Drone;
            var a = new LowBatteryAssessment { BatteryPct = d.BatteryPct, MarginPct = e.Config.Recovery.LbsSafetyMarginPct };
            if (d.Flight != FlightStatus.Flying) return a;

            WorldPos segmentEnd = d.Position;
            if (e.Activity.Kind == ActivityKind.Moving)
            {
                segmentEnd = e.Activity.MoveTarget;
                a.SegmentTarget = segmentEnd.NearestTile();
                a.SegmentRequiredPct = Reachability.BatteryRequiredPct(e, d.Position, segmentEnd, d.Speed);
            }

            a.AfterSegmentRequiredPct = Reachability.RequiredToNearestChargeTile(e, segmentEnd, SpeedMode.Slow, out _);
            a.IsLowBattery = a.TotalRequiredPct > d.BatteryPct;   // +inf > anything: a walled-off segment end is LBS regardless of charge

            if (a.IsLowBattery &&
                Reachability.TryNearestReachableChargeTile(e, d.Position, SpeedMode.Slow, d.BatteryPct, a.MarginPct, out var tile, out var req))
            {
                a.RecoveryTile = tile;
                a.RecoveryRequiredPct = req;
            }
            return a;
        }
    }
}