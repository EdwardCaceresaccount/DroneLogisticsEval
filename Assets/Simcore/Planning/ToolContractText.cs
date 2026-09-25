namespace SimCore.Planning
{
    /// <summary>
    /// The tool contract as prose. This MUST agree with ToolCallParser and PlanValidator — when either changes,
    /// this text changes and HarnessVersions.ToolContract is bumped. Every LLM planner receives exactly this.
    /// </summary>
    public static class ToolContractText
    {
        public const string Text =
@"You control a delivery drone by returning a JSON plan for ONE trip.

OUTPUT FORMAT — return ONLY a JSON object of this exact shape. No prose, no markdown, no code fences:
{""tool_calls"":[{""tool"":""LIFT_OFF""},{""tool"":""MOVE_TO"",""args"":{""x"":9,""y"":7}}]}

TOOLS (exact uppercase names; args as shown):
- LIFT_OFF — no args. Drone must be landed. If a CHARGE is in progress, lift-off waits until the charge target is reached.
- LAND — no args. Drone must be airborne and exactly over a tile center (i.e. right after a MOVE_TO arrives). Landing on a Facility or ChargingStation ENDS THE TRIP; any later tool calls are discarded.
- MOVE_TO — args {""x"":int,""y"":int}. Drone must be airborne. Flies in a STRAIGHT LINE to the center of tile (x,y). If that straight line crosses any Building tile the drone crashes and the mission ends. Route around buildings with intermediate MOVE_TO waypoints.
- LOAD — args {""package_id"":string}. Drone must be landed on the Facility that holds the package (package status AtFacility, origin = your tile). Takes physics.load_seconds_per_package. At most physics.inventory_capacity packages on board.
- DELIVER — args {""package_id"":string}. Package must be on board and the drone must be exactly at the package's destination tile (airborne, immediately after a MOVE_TO there). Instantaneous.
- CHARGE — args {""target_pct"":number, 0 < target_pct <= 100}. Drone must be landed on a Facility or ChargingStation. Starts charging in the background at physics.charge_pct_per_sec. LOAD may run at the same time. LIFT_OFF waits for the target.
- WAIT — args {""seconds"":number > 0}. Holds position. Drains battery if airborne.
- SET_SPEED — args {""mode"":""SLOW""|""NORMAL""|""FAST""}. Persists until changed, including across trips. Affects movement speed AND battery discharge via physics.speed_multipliers / physics.discharge_multipliers.

COORDINATES: integer tile coordinates 1..grid_size on each axis; x increases to the right, y increases upward. Distances are Euclidean, in tiles.

BATTERY: battery needed for a leg = distance / (base_speed_tiles_per_sec × speed_multiplier) × (base_discharge_pct_per_sec × discharge_multiplier). If battery reaches 0 while airborne the drone is force-landed and the mission ends.";
    }
}