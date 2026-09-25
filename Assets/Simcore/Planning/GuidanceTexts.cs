using System.Text;
using SimCore.Domain;

namespace SimCore.Planning
{
    /// <summary>D16: cumulative information tiers. Strategy appears ONLY in Full.</summary>
    public static class GuidanceTexts
    {
        public const string Objective =
@"OBJECTIVE: deliver packages to their destination Houses. The mission succeeds when the required deliveries are complete (see mission.require_all_packages_delivered) within mission.max_trips trips and mission.max_sim_seconds of simulated time. Do not crash and do not run out of battery while airborne.";

        public const string EnvironmentRules =
@"ENVIRONMENT RULES:
- A trip starts with the drone landed on a Facility or ChargingStation and ends when it lands on one after flying. You plan ONE trip per response; you will be asked again for the next trip.
- Tool calls after a base landing are discarded, so charging must happen at the START of a trip, before LIFT_OFF.
- Packages can only be loaded at the Facility that holds them. Loading a package that is not at your tile is rejected.
- Your plan is validated before it runs. It is rejected — and you are asked again with the errors — if any call is malformed, names an unknown tool, includes unknown arguments, or is illegal in the state it would execute in (for example MOVE_TO while landed, LAND while landed, DELIVER when not at the destination, a third LOAD when capacity is 2). Rejections are counted.
- Your plan is NOT rejected for flying through a Building or for running out of battery. Those are executed and end the mission.
- Speed persists across trips. Check drone.speed.";

        public const string LowBatteryWithAutoRecovery =
@"- LOW BATTERY: while airborne, if completing your current leg would leave the drone unable to reach any Facility or ChargingStation afterwards (with a reserve of 2 × physics.lbs_safety_margin_pct), the simulator ABORTS your plan and flies the drone to the nearest reachable charge tile. The trip is then recorded as an emergency recovery, which scores worse than a trip you planned to end at a charger yourself.";

        public const string LowBatteryWithoutAutoRecovery =
@"- LOW BATTERY: there is no automatic rescue. If your plan runs the battery down while airborne, the drone is force-landed where it is and the mission ends.";

        public const string Strategy =
@"STRATEGY GUIDANCE:
- CHARGE to 100 before departing unless you are certain the whole trip fits in the current battery.
- Load up to two packages. Prefer destinations closer to you. It can be worth carrying a farther package toward a ChargingStation that is closer to its destination and finishing it next trip.
- Always keep enough battery to reach a Facility or ChargingStation after your LAST delivery, with a reserve of at least 2 × physics.lbs_safety_margin_pct.
- Before each MOVE_TO, check whether the straight line crosses a Building (in PROCESSED observations see direct_path_clear and obstructing_tiles; in RAW observations compute it from the tiles list). If it does, add a waypoint that clears the building.
- SLOW is the most battery-efficient speed and FAST the least. Use SLOW when range is tight; use FAST only when time, not battery, is the constraint.
- When possible, end the trip at a Facility that still has packages, so the next trip does not start with an empty repositioning flight.";

        /// <summary>Assembles the system-prompt guidance for a tier. autoRecovery=null when the observation carried no physics.</summary>
        public static string For(GuidanceTier tier, bool? autoRecovery)
        {
            var sb = new StringBuilder();
            sb.Append(Objective);

            if (tier == GuidanceTier.Partial || tier == GuidanceTier.Full)
            {
                sb.Append("\n\n").Append(EnvironmentRules);
                if (autoRecovery == true) sb.Append('\n').Append(LowBatteryWithAutoRecovery);
                else if (autoRecovery == false) sb.Append('\n').Append(LowBatteryWithoutAutoRecovery);
            }

            if (tier == GuidanceTier.Full)
                sb.Append("\n\n").Append(Strategy);

            return sb.ToString();
        }
    }
}