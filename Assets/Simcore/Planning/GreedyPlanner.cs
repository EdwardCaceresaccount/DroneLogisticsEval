using System;
using System.Collections.Generic;
using System.Linq;
using SimCore.Commands;
using SimCore.Domain;
using SimCore.Observation;
using SimCore.State;

namespace SimCore.Planning
{
    /// <summary>
    /// GreedyPlanner v1.0.0 — the deterministic comparison baseline (spec §13). Explicit rule list:
    ///   R1  Always depart fully charged: CHARGE(capacity) first if battery is below capacity.
    ///   R2  Always fly at NORMAL speed.
    ///   R3  At a Facility, load the available packages with the SHORTEST origin→destination distance
    ///       (ties by id), up to remaining inventory capacity.
    ///   R4  Deliver by nearest-neighbour from the current position.
    ///   R5  Direct straight-line legs; when a leg crosses a Building, insert ONE detour waypoint chosen from a
    ///       fixed candidate ring around the first obstructing tile (cheapest clear candidate wins).
    ///   R6  End the trip at the cheapest charge tile to reach — preferring a Facility that still has packages.
    ///   R7  Budget = capacity − 2×LBS margin (so the simulator never has to rescue it). If the plan does not fit,
    ///       drop the LAST delivery and retry; keep dropped-but-carried packages on board.
    ///   R8  If nothing can be delivered this trip: load the cheapest available package (if any) and fly to the
    ///       reachable charge tile closest to its destination — the charger-as-stepping-stone move. If nothing is
    ///       carried and nothing is here, fly to the cheapest Facility that has packages. If nothing is reachable,
    ///       WAIT(1) — an explicit no-op trip that counts against max_trips.
    /// Consumes ONLY facts present in both observation modes (tiles/obstacles, packages, drone, physics); never a
    /// derived field. Never sees WorldState. Never uses GuidanceTier. Ties broken by ordinal id / scan order.
    /// </summary>
    public sealed class GreedyPlanner : IPlanner
    {
        public string Id => "greedy";
        public string Version => "1.0.0";

        private static readonly int[] DetourOffsets = { 2, 3, 4, 6, 8 };
        private static readonly (int dx, int dy)[] Directions =
            { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

        // ------------------------------------------------------------------ view over either observation mode

        private sealed class Pkg
        {
            public string Id; public GridCoord Origin; public GridCoord Destination; public PackageStatus Status;
        }

        private sealed class View
        {
            public DronePhysicsObs Physics;
            public GridCoord DroneTile;
            public TileType DroneTileType;
            public double Battery;
            public HashSet<string> Inventory = new();
            public List<Pkg> Packages = new();
            public List<(GridCoord tile, TileType type)> ChargeTiles = new();
            public GridMap Map;

            public static View From(Observation.Observation o)
            {
                var v = new View();
                switch (o)
                {
                    case RawObservation r:
                        v.Physics = r.Physics; v.DroneTile = r.Drone.Tile.ToCoord(); v.DroneTileType = r.Drone.TileType;
                        v.Battery = r.Drone.BatteryPct; foreach (var id in r.Drone.Inventory) v.Inventory.Add(id);
                        v.Map = new GridMap(r.GridSize);
                        foreach (var t in r.Tiles)
                        {
                            var c = new GridCoord(t.X, t.Y);
                            v.Map.SetType(c, t.Type);
                            if (t.Type == TileType.Facility || t.Type == TileType.ChargingStation) v.ChargeTiles.Add((c, t.Type));
                        }
                        foreach (var p in r.Packages)
                            v.Packages.Add(new Pkg { Id = p.Id, Origin = p.Origin.ToCoord(), Destination = p.Destination.ToCoord(), Status = p.Status });
                        break;

                    case ProcessedObservation p:
                        v.Physics = p.Physics; v.DroneTile = p.Drone.Tile.ToCoord(); v.DroneTileType = p.Drone.TileType;
                        v.Battery = p.Drone.BatteryPct; foreach (var id in p.Drone.Inventory) v.Inventory.Add(id);
                        v.Map = new GridMap(p.GridSize);
                        foreach (var b in p.Obstacles) v.Map.SetType(b.ToCoord(), TileType.Building);
                        foreach (var c in p.ChargeTiles) { v.Map.SetType(c.Tile.ToCoord(), c.Type); v.ChargeTiles.Add((c.Tile.ToCoord(), c.Type)); }
                        foreach (var pk in p.Packages)
                        {
                            v.Map.SetType(pk.Destination.ToCoord(), TileType.House);
                            v.Packages.Add(new Pkg { Id = pk.Id, Origin = pk.Origin.ToCoord(), Destination = pk.Destination.ToCoord(), Status = pk.Status });
                        }
                        break;

                    default:
                        throw new NotSupportedException($"GreedyPlanner cannot read {o.GetType().Name}.");
                }
                v.Packages.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                return v;
            }

            public double Req(double distance) => Physics.BatteryRequiredPct(distance, SpeedMode.Normal);
        }

        private sealed class Delivery { public Pkg Pkg; public List<GridCoord> Route; public double Cost; }
        private sealed class EndChoice { public GridCoord Tile; public List<GridCoord> Route; public double Cost; }

        // ------------------------------------------------------------------ entry

        public PlanResponse Plan(PlanRequest request)
        {
            View v;
            try { v = View.From(request.Observation); }
            catch (Exception ex)
            {
                var err = new ValidationResult();
                err.Add(ErrorCategory.Schema, "GREEDY_UNSUPPORTED_OBSERVATION", ex.Message);
                return PlanResponse.Failed(err);
            }

            var commands = new List<Command>();
            double capacity = v.Physics.BatteryCapacityPct;
            double budget = capacity - 2 * v.Physics.LbsSafetyMarginPct;

            if (v.Battery < capacity - 1e-9) commands.Add(Command.Charge(capacity));                       // R1

            var carried = v.Packages.Where(p => p.Status == PackageStatus.Loaded && v.Inventory.Contains(p.Id)).ToList();
            var availableHere = v.DroneTileType == TileType.Facility
                ? v.Packages.Where(p => p.Status == PackageStatus.AtFacility && p.Origin == v.DroneTile)
                            .OrderBy(p => p.Origin.DistanceTo(p.Destination)).ThenBy(p => p.Id, StringComparer.Ordinal).ToList()
                : new List<Pkg>();
            int slots = Math.Max(0, v.Physics.InventoryCapacity - carried.Count);
            var toLoad = availableHere.Take(slots).ToList();                                               // R3

            List<Delivery> deliveries; EndChoice end;
            if (!TryPlanDeliveries(v, carried, toLoad, budget, out deliveries, out end))                 // R4–R7
                (toLoad, end) = Reposition(v, carried, availableHere, slots, budget);                     // R8

            foreach (var p in toLoad) commands.Add(Command.Load(p.Id));

            bool flying = deliveries.Count > 0 || end != null;
            if (flying)
            {
                commands.Add(Command.LiftOff());
                commands.Add(Command.SetSpeed(SpeedMode.Normal));                                          // R2
                foreach (var d in deliveries)
                {
                    foreach (var wp in d.Route) commands.Add(Command.MoveTo(wp));
                    commands.Add(Command.Deliver(d.Pkg.Id));
                }
                foreach (var wp in end.Route) commands.Add(Command.MoveTo(wp));
                commands.Add(Command.Land());
            }
            else if (commands.Count == 0)
            {
                commands.Add(Command.Wait(1.0));
            }

            return PlanResponse.Ok(new Plan(commands, Id));
        }

        // ------------------------------------------------------------------ R4–R7

        private static bool TryPlanDeliveries(View v, List<Pkg> carried, List<Pkg> toLoad, double budget,
                                              out List<Delivery> deliveries, out EndChoice end)
        {
            var pool = carried.Concat(toLoad).ToList();
            deliveries = new List<Delivery>();
            end = null;

            while (pool.Count > 0)
            {
                var seq = new List<Delivery>();
                var remaining = new List<Pkg>(pool);
                var pos = v.DroneTile;
                double total = 0;
                Pkg unroutable = null;

                while (remaining.Count > 0)
                {
                    Delivery best = null;
                    foreach (var p in remaining)
                    {
                        var route = Route(v, pos, p.Destination, out double dist);
                        if (route == null) continue;
                        double cost = v.Req(dist);
                        if (best == null || cost < best.Cost - 1e-9 ||
                            (Math.Abs(cost - best.Cost) <= 1e-9 && string.CompareOrdinal(p.Id, best.Pkg.Id) < 0))
                            best = new Delivery { Pkg = p, Route = route, Cost = cost };
                    }
                    if (best == null) { unroutable = remaining[0]; break; }
                    seq.Add(best); total += best.Cost; pos = best.Pkg.Destination; remaining.Remove(best.Pkg);
                }

                if (unroutable != null) { pool.Remove(unroutable); toLoad.Remove(unroutable); continue; }

                var endChoice = ChooseEnd(v, pos, budget - total, toLoad);
                if (endChoice != null) { deliveries = seq; end = endChoice; return true; }

                var last = seq[seq.Count - 1].Pkg;                                                          // R7
                pool.Remove(last); toLoad.Remove(last);
            }
            return false;
        }

        private static EndChoice ChooseEnd(View v, GridCoord from, double remainingBudget, List<Pkg> toLoad)
        {
            EndChoice bestWithPackages = null, bestAny = null;
            foreach (var (tile, type) in v.ChargeTiles)
            {
                var route = Route(v, from, tile, out double dist);
                if (route == null) continue;
                double cost = v.Req(dist);
                if (cost > remainingBudget + 1e-9) continue;

                var choice = new EndChoice { Tile = tile, Route = route, Cost = cost };
                bool hasPackages = type == TileType.Facility &&
                    v.Packages.Any(p => p.Status == PackageStatus.AtFacility && p.Origin == tile && !toLoad.Contains(p));
                if (hasPackages && (bestWithPackages == null || cost < bestWithPackages.Cost - 1e-9)) bestWithPackages = choice;
                if (bestAny == null || cost < bestAny.Cost - 1e-9) bestAny = choice;
            }
            return bestWithPackages ?? bestAny;                                                             // R6
        }

        // ------------------------------------------------------------------ R8

        private static (List<Pkg> toLoad, EndChoice end) Reposition(View v, List<Pkg> carried, List<Pkg> availableHere,
                                                                     int slots, double budget)
        {
            var toLoad = new List<Pkg>();
            Pkg focus = carried.OrderBy(p => v.DroneTile.DistanceTo(p.Destination)).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
            if (focus == null && availableHere.Count > 0 && slots > 0) { focus = availableHere[0]; toLoad.Add(focus); }

            EndChoice best = null;
            if (focus != null)
            {
                double bestGoalDist = double.PositiveInfinity;
                foreach (var (tile, _) in v.ChargeTiles)
                {
                    if (tile == v.DroneTile) continue;
                    var route = Route(v, v.DroneTile, tile, out double dist);
                    if (route == null) continue;
                    double cost = v.Req(dist);
                    if (cost > budget + 1e-9) continue;
                    double goalDist = tile.DistanceTo(focus.Destination);
                    if (goalDist < bestGoalDist - 1e-9) { bestGoalDist = goalDist; best = new EndChoice { Tile = tile, Route = route, Cost = cost }; }
                }
            }
            else
            {
                foreach (var (tile, type) in v.ChargeTiles)
                {
                    if (type != TileType.Facility || tile == v.DroneTile) continue;
                    if (!v.Packages.Any(p => p.Status == PackageStatus.AtFacility && p.Origin == tile)) continue;
                    var route = Route(v, v.DroneTile, tile, out double dist);
                    if (route == null) continue;
                    double cost = v.Req(dist);
                    if (cost > budget + 1e-9) continue;
                    if (best == null || cost < best.Cost - 1e-9) best = new EndChoice { Tile = tile, Route = route, Cost = cost };
                }
            }
            return (toLoad, best);
        }

        // ------------------------------------------------------------------ R5 routing

        /// <summary>Waypoints from 'from' to 'to' (excluding start, including end), or null if no clear route is found.</summary>
        private static List<GridCoord> Route(View v, GridCoord from, GridCoord to, out double distance)
        {
            distance = from.DistanceTo(to);
            if (GridGeometry.DirectPathClear(v.Map, from.Center, to.Center)) return new List<GridCoord> { to };

            GridGeometry.TryGetFirstBuildingOnSegment(v.Map, from.Center, to.Center, out var wall, out _);
            GridCoord? bestWp = null;
            double bestDist = double.PositiveInfinity;

            foreach (int off in DetourOffsets)
                foreach (var (dx, dy) in Directions)
                {
                    var w = new GridCoord(wall.X + dx * off, wall.Y + dy * off);
                    if (!v.Map.InBounds(w) || v.Map.TypeAt(w) == TileType.Building) continue;
                    if (!GridGeometry.DirectPathClear(v.Map, from.Center, w.Center)) continue;
                    if (!GridGeometry.DirectPathClear(v.Map, w.Center, to.Center)) continue;
                    double d = from.DistanceTo(w) + w.DistanceTo(to);
                    if (d < bestDist - 1e-9) { bestDist = d; bestWp = w; }
                }

            if (!bestWp.HasValue) return null;
            distance = bestDist;
            return new List<GridCoord> { bestWp.Value, to };
        }
    }
}