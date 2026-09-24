using System.Collections.Generic;
using SimCore.Commands;
using SimCore.Domain;
using SimCore.State;

namespace SimCore.Eval
{
    public enum TripClassification
    {
        StrongSuccess,
        DeliveryWithEmergencyCharge,
        NoDeliverySafeReturn,
        SameLocationNoDelivery,
        ForcedLbsRecovery,
        ForcedLanding,
        Crash,
        EndedAirborne,
        EndedOffBase,
        InvalidPlannerBehavior,
        TimeLimitExceeded
    }

    public static class Severity
    {
        public static int Of(TripClassification c) => c switch
        {
            TripClassification.StrongSuccess               => 0,
            TripClassification.NoDeliverySafeReturn        => 1,
            TripClassification.SameLocationNoDelivery      => 1,
            TripClassification.DeliveryWithEmergencyCharge => 2,
            TripClassification.ForcedLbsRecovery           => 2,
            TripClassification.ForcedLanding               => 3,
            TripClassification.EndedAirborne               => 3,
            TripClassification.EndedOffBase                => 3,
            TripClassification.InvalidPlannerBehavior      => 3,
            TripClassification.TimeLimitExceeded           => 3,
            TripClassification.Crash                       => 4,
            _ => 3
        };
    }

    public sealed class PlanDecisionSummary
    {
        public List<string> PackagesLoaded { get; } = new();
        public List<string> PackagesToDeliver { get; } = new();
        public List<string> SpeedModes { get; } = new();
        public List<double> ChargeTargets { get; } = new();
        public int WaypointCount { get; set; }
        public GridCoord? PlannedEndTile { get; set; }
        public string PlannedEndTileType { get; set; }
        public bool EndsWithLand { get; set; }

        public static PlanDecisionSummary FromPlan(Plan plan, WorldState world)
        {
            var s = new PlanDecisionSummary();
            GridCoord? lastMove = null;
            foreach (var c in plan.Commands)
            {
                switch (c.Type)
                {
                    case CommandType.LOAD:      s.PackagesLoaded.Add(c.PackageId); break;
                    case CommandType.DELIVER:   s.PackagesToDeliver.Add(c.PackageId); break;
                    case CommandType.SET_SPEED: s.SpeedModes.Add(c.Speed.ToString()); break;
                    case CommandType.CHARGE:    s.ChargeTargets.Add(c.Value.Value); break;
                    case CommandType.MOVE_TO:   s.WaypointCount++; lastMove = c.Target; break;
                }
            }
            s.PlannedEndTile = lastMove;
            s.PlannedEndTileType = lastMove.HasValue && world.Map.InBounds(lastMove.Value)
                ? world.Map.TypeAt(lastMove.Value).ToString() : null;
            s.EndsWithLand = plan.Commands.Count > 0 && plan.Commands[plan.Commands.Count - 1].Type == CommandType.LAND;
            return s;
        }

        public Dictionary<string, object> ToLogData() => new()
        {
            ["packages_loaded"]       = string.Join(",", PackagesLoaded),
            ["packages_to_deliver"]   = string.Join(",", PackagesToDeliver),
            ["speed_modes"]           = string.Join(",", SpeedModes),
            ["charge_targets"]        = string.Join(",", ChargeTargets),
            ["waypoint_count"]        = WaypointCount,
            ["planned_end_tile"]      = PlannedEndTile?.ToString() ?? "",
            ["planned_end_tile_type"] = PlannedEndTileType ?? "",
            ["ends_with_land"]        = EndsWithLand
        };
    }

    public sealed class TripOutcome
    {
        public int TripNumber;
        public GridCoord StartTile;
        public TileType StartTileType;
        public GridCoord? EndTile;
        public TileType? EndTileType;
        public bool EndedAtBase;
        public bool EndedAirborne;
        public bool LiftedOff;

        public int PackagesLoaded;
        public int PackagesDelivered;

        public double BatteryStart, BatteryEnd, BatteryConsumed, ChargingSeconds, DistanceTraveled;
        public double SimTimeStart, SimTimeEnd;
        public double ExecutionSeconds => SimTimeEnd - SimTimeStart;

        public int PlanAttempts, PlanRejections;
        public double PlannerLatencySeconds;
        public int InputTokens, OutputTokens;

        public int CommandsTotal, CommandsDispatched, CommandsTruncated;

        public FailureKind Failure;
        public ExecutorStatus? ExecutorStatus;
        public string HaltErrorCode;
        public bool InvalidPlanner;
        public bool TimeLimitExceeded;

        // Block 7
        public bool LowBatteryOccurred;
        public bool EmergencyRecovery;
        public double LbsTriggerTimeSeconds;
        public double LbsBatteryPct;
        public string LbsSegmentTarget;
        public GridCoord? RecoveryTargetTile;
        public int CommandsRemainingAtAbort;
        public int RecoveryCommandsDispatched;

        public PlanDecisionSummary Decisions;
        public TripClassification Classification;
        public int SeverityLevel;

        public Dictionary<string, object> ToLogData()
        {
            var d = new Dictionary<string, object>
            {
                ["trip_number"] = TripNumber,
                ["classification"] = Classification.ToString(),
                ["severity"] = SeverityLevel,
                ["start_tile"] = StartTile.ToString(),
                ["start_tile_type"] = StartTileType.ToString(),
                ["end_tile"] = EndTile?.ToString() ?? "",
                ["end_tile_type"] = EndTileType?.ToString() ?? "",
                ["ended_at_base"] = EndedAtBase,
                ["ended_airborne"] = EndedAirborne,
                ["lifted_off"] = LiftedOff,
                ["packages_loaded"] = PackagesLoaded,
                ["packages_delivered"] = PackagesDelivered,
                ["battery_start"] = BatteryStart,
                ["battery_end"] = BatteryEnd,
                ["battery_consumed"] = BatteryConsumed,
                ["charging_seconds"] = ChargingSeconds,
                ["distance_traveled"] = DistanceTraveled,
                ["execution_seconds"] = ExecutionSeconds,
                ["plan_attempts"] = PlanAttempts,
                ["plan_rejections"] = PlanRejections,
                ["planner_latency_seconds"] = PlannerLatencySeconds,
                ["input_tokens"] = InputTokens,
                ["output_tokens"] = OutputTokens,
                ["commands_total"] = CommandsTotal,
                ["commands_dispatched"] = CommandsDispatched,
                ["commands_truncated"] = CommandsTruncated,
                ["failure"] = Failure.ToString(),
                ["executor_status"] = ExecutorStatus?.ToString() ?? "",
                ["halt_error_code"] = HaltErrorCode ?? "",
                ["invalid_planner"] = InvalidPlanner,
                ["time_limit_exceeded"] = TimeLimitExceeded,
                ["low_battery_occurred"] = LowBatteryOccurred,
                ["emergency_recovery"] = EmergencyRecovery,
                ["lbs_trigger_time"] = LbsTriggerTimeSeconds,
                ["lbs_battery_pct"] = LbsBatteryPct,
                ["lbs_segment_target"] = LbsSegmentTarget ?? "",
                ["recovery_target_tile"] = RecoveryTargetTile?.ToString() ?? "",
                ["commands_remaining_at_abort"] = CommandsRemainingAtAbort,
                ["recovery_commands_dispatched"] = RecoveryCommandsDispatched
            };
            if (Decisions != null)
                foreach (var kv in Decisions.ToLogData()) d["decision_" + kv.Key] = kv.Value;
            return d;
        }
    }
}