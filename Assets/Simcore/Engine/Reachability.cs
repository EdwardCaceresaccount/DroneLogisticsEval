using System.Collections.Generic;
using SimCore.Domain;
using SimCore.State;

namespace SimCore.Engine
{
    /// <summary>
    /// Single source of truth for "can the drone get there". Used by LBS detection now and by the
    /// PROCESSED observation in Block 9 — so the planner's information and the simulator's judgement
    /// can never disagree about what "reachable" means. Straight-line only (no pathfinding).
    /// </summary>
    public static class Reachability
    {
        public static double BatteryRequiredPct(SimulationEngine e, WorldPos from, WorldPos to, SpeedMode speed)
            => from.DistanceTo(to) / e.MoveSpeedTilesPerSec(speed) * e.DischargePctPerSec(speed);

        /// <summary>Direct path clear of Buildings AND enough battery (with margin).</summary>
        public static bool CanReachDirect(SimulationEngine e, WorldPos from, GridCoord target, SpeedMode speed,
                                          double batteryPct, double marginPct, out double requiredPct)
        {
            requiredPct = BatteryRequiredPct(e, from, target.Center, speed);
            if (!GridGeometry.DirectPathClear(e.World.Map, from, target.Center)) return false;
            return requiredPct + marginPct <= batteryPct;
        }

        /// <summary>Facilities then Charging Stations, each in deterministic scan order.</summary>
        public static List<GridCoord> ChargeTiles(WorldState w)
        {
            var list = new List<GridCoord>(w.Map.AllOfType(TileType.Facility));
            list.AddRange(w.Map.AllOfType(TileType.ChargingStation));
            return list;
        }

        /// <summary>Cheapest charge tile with a clear direct path. +Infinity if every charge tile is walled off.</summary>
        public static double RequiredToNearestChargeTile(SimulationEngine e, WorldPos from, SpeedMode speed, out GridCoord? tile)
        {
            tile = null;
            double best = double.PositiveInfinity;
            foreach (var c in ChargeTiles(e.World))
            {
                if (!GridGeometry.DirectPathClear(e.World.Map, from, c.Center)) continue;
                double req = BatteryRequiredPct(e, from, c.Center, speed);
                if (req < best) { best = req; tile = c; }
            }
            return best;
        }

        public static bool TryNearestReachableChargeTile(SimulationEngine e, WorldPos from, SpeedMode speed,
                                                         double batteryPct, double marginPct,
                                                         out GridCoord tile, out double requiredPct)
        {
            requiredPct = RequiredToNearestChargeTile(e, from, speed, out var t);
            tile = t ?? default;
            return t.HasValue && requiredPct + marginPct <= batteryPct;
        }
    }
}