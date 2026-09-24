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

    public sealed class LowBatteryAssessment
    {
        public bool IsLowBattery;
        public double BatteryPct;
        public double MarginPct;
        public double TriggerMarginPct => MarginPct * 2;
        public GridCoord? SegmentTarget;
        public double SegmentRequiredPct;
        public double AfterSegmentRequiredPct;
        /// <summary>True when the after-segment estimate had to ignore Buildings (all chargers walled off from the segment end).</summary>
        public bool AfterSegmentIgnoredObstacles;
        public GridCoord? RecoveryTile;
        public double RecoveryRequiredPct;
        public double TotalRequiredPct => SegmentRequiredPct + AfterSegmentRequiredPct + TriggerMarginPct;
    }

    /// <summary>
    /// D14. LBS = finishing the current segment at current speed would leave too little battery to reach any
    /// charge tile afterward at Slow, plus 2× margin. The after-segment estimate prefers a clear straight line and
    /// falls back to the unobstructed nearest when none exists — the monitor judges battery, not geometry.
    /// The rescue target (RecoveryTile) always requires a flyable straight line.
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
            if (double.IsInfinity(a.AfterSegmentRequiredPct))
            {
                a.AfterSegmentRequiredPct = Reachability.RequiredToNearestChargeTileIgnoringObstacles(e, segmentEnd, SpeedMode.Slow, out _);
                a.AfterSegmentIgnoredObstacles = true;
            }

            a.IsLowBattery = a.TotalRequiredPct > d.BatteryPct;

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