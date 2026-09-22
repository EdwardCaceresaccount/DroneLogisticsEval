using System.Collections.Generic;
using SimCore.Domain;

namespace SimCore.State
{
    /// <summary>
    /// Authoritative drone state, owned exclusively by the simulation engine (spec §6:
    /// "the LLM must never be the authority on the state"). Planners see this only
    /// through the Observation Compiler; nothing outside SimCore.Engine mutates it.
    /// </summary>
    public sealed class DroneState
    {
        public WorldPos Position { get; set; }

        /// <summary>Tile the drone is landed on; null while flying.</summary>
        public GridCoord? LandedTile { get; set; }

        public double BatteryPct { get; set; }
        public SpeedMode Speed { get; set; } = SpeedMode.Normal;
        public FlightStatus Flight { get; set; } = FlightStatus.Landed;

        /// <summary>Package IDs currently on board (capacity enforced by the validator/engine).</summary>
        public List<string> LoadedPackageIds { get; } = new();

        /// <summary>Simulator-detected Low Battery State (spec §9.5) — set by the engine, never by a planner.</summary>
        public bool InLowBatteryState { get; set; }
    }
}