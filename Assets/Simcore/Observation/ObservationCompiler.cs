using System;
using System.Collections.Generic;
using SimCore.Domain;
using SimCore.Engine;
using SimCore.State;

namespace SimCore.Observation
{
    /// <summary>
    /// WorldState → Observation, in either mode, from the SAME state. Read-only over the world by construction:
    /// nothing here has a setter into WorldState. Derived facts use Reachability/GridGeometry so the planner's
    /// information and the simulator's judgement can never disagree.
    /// </summary>
    public sealed class ObservationCompiler : IObservationCompiler
    {
        public Observation Compile(WorldState world, SimulationEngine engine, ObservationMode mode)
            => mode == ObservationMode.Raw ? CompileRaw(world, engine) : CompileProcessed(world, engine);

        // ---------------------------------------------------------------- RAW

        public static RawObservation CompileRaw(WorldState world, SimulationEngine engine)
        {
            var obs = new RawObservation
            {
                TripNumber = world.TripIndex + 1,
                SimTimeSeconds = world.Clock.TimeSeconds,
                GridSize = world.Map.Size,
                Physics = DronePhysicsObs.From(engine.Config),
                Mission = MissionOf(world, engine),
                Drone = DroneOf(world)
            };

            for (int x = 1; x <= world.Map.Size; x++)
                for (int y = 1; y <= world.Map.Size; y++)
                {
                    var t = world.Map.TypeAt(new GridCoord(x, y));
                    if (t != TileType.Road) obs.Tiles.Add(new TileObs { X = x, Y = y, Type = t });
                }

            foreach (var p in OrderedPackages(world))
                obs.Packages.Add(new PackageObs
                {
                    Id = p.Id, Origin = CoordObs.From(p.Origin), Destination = CoordObs.From(p.Destination),
                    Difficulty = p.Difficulty, Status = p.Status
                });

            return obs;
        }

        // ---------------------------------------------------------------- PROCESSED

        public static ProcessedObservation CompileProcessed(WorldState world, SimulationEngine engine)
        {
            var d = world.Drone;
            var map = world.Map;
            var physics = DronePhysicsObs.From(engine.Config);
            double margin2 = physics.LbsSafetyMarginPct * 2;
            var dronePos = d.Position;
            var droneTile = d.LandedTile ?? d.Position.NearestTile();

            var obs = new ProcessedObservation
            {
                TripNumber = world.TripIndex + 1,
                SimTimeSeconds = world.Clock.TimeSeconds,
                GridSize = map.Size,
                Physics = physics,
                Mission = MissionOf(world, engine),
                Drone = DroneOf(world),
                RangeNowTiles = RangeAt(engine, d.BatteryPct),
                RangeFullTiles = RangeAt(engine, physics.BatteryCapacityPct),
                ChargingRecommended = d.Flight == FlightStatus.Landed && d.LandedTile.HasValue
                                      && map.IsChargeTile(d.LandedTile.Value) && d.BatteryPct < physics.BatteryCapacityPct - 1e-9
            };

            foreach (var b in map.AllOfType(TileType.Building)) obs.Obstacles.Add(CoordObs.From(b));

            var chargeTiles = Reachability.ChargeTiles(world);
            foreach (var c in chargeTiles)
            {
                var obstructions = Obstructions(map, dronePos, c.Center);
                obs.ChargeTiles.Add(new ChargeTileObs
                {
                    Tile = CoordObs.From(c),
                    Type = map.TypeAt(c),
                    IsCurrentLocation = d.LandedTile.HasValue && d.LandedTile.Value == c,
                    Distance = dronePos.DistanceTo(c.Center),
                    DirectPathClear = obstructions.Count == 0,
                    ObstructingTiles = obstructions,
                    BatteryRequired = RequiredAt(engine, dronePos, c.Center),
                    AvailablePackages = CountAvailableAt(world, c)
                });
            }

            foreach (var p in OrderedPackages(world))
            {
                if (p.Status == PackageStatus.Delivered) continue;
                var dest = p.Destination.Center;
                var obstructions = Obstructions(map, dronePos, dest);
                var nearest = NearestChargeFrom(engine, world, dest);
                double loop = Reachability.BatteryRequiredPct(engine, dronePos, dest, SpeedMode.Normal);
                if (nearest != null)
                    loop += Reachability.BatteryRequiredPct(engine, dest, nearest.Tile.ToCoord().Center, SpeedMode.Normal);

                obs.Packages.Add(new ProcessedPackageObs
                {
                    Id = p.Id, Origin = CoordObs.From(p.Origin), Destination = CoordObs.From(p.Destination),
                    Difficulty = p.Difficulty, Status = p.Status,
                    AvailableHere = p.Status == PackageStatus.AtFacility && d.LandedTile.HasValue && p.Origin == d.LandedTile.Value,
                    OnBoard = p.Status == PackageStatus.Loaded && d.LoadedPackageIds.Contains(p.Id),
                    DistanceOriginToDestination = p.Origin.DistanceTo(p.Destination),
                    DistanceFromDroneToDestination = dronePos.DistanceTo(dest),
                    DirectPathClearFromDrone = obstructions.Count == 0,
                    ObstructingTiles = obstructions,
                    BatteryRequiredFromDrone = RequiredAt(engine, dronePos, dest),
                    NearestChargeFromDestination = nearest,
                    LoopBatteryRequiredNormal = loop,
                    FeasibleNowNormal = loop + margin2 <= d.BatteryPct,
                    FeasibleAfterFullChargeNormal = loop + margin2 <= physics.BatteryCapacityPct
                });
            }

            return obs;
        }

        // ---------------------------------------------------------------- helpers

        private static MissionObs MissionOf(WorldState world, SimulationEngine engine) => new()
        {
            MaxTrips = engine.Config.Mission.MaxTrips,
            MaxSimSeconds = engine.Config.Mission.MaxSimSeconds,
            PackagesTotal = world.Packages.Count,
            PackagesDelivered = world.DeliveredCount(),
            RequireAllPackagesDelivered = engine.Config.Mission.RequireAllPackagesDelivered
        };

        private static DroneObs DroneOf(WorldState world)
        {
            var d = world.Drone;
            var tile = d.LandedTile ?? d.Position.NearestTile();
            var obs = new DroneObs
            {
                Tile = CoordObs.From(tile),
                TileType = world.Map.TypeAt(tile),
                BatteryPct = d.BatteryPct,
                Speed = d.Speed,
                Flight = d.Flight
            };
            obs.Inventory.AddRange(d.LoadedPackageIds);
            return obs;
        }

        private static RangeObs RangeAt(SimulationEngine e, double battery) => new()
        {
            Slow = e.RangeTilesAtSpeed(SpeedMode.Slow, battery),
            Normal = e.RangeTilesAtSpeed(SpeedMode.Normal, battery),
            Fast = e.RangeTilesAtSpeed(SpeedMode.Fast, battery)
        };

        private static RangeObs RequiredAt(SimulationEngine e, WorldPos from, WorldPos to) => new()
        {
            Slow = Reachability.BatteryRequiredPct(e, from, to, SpeedMode.Slow),
            Normal = Reachability.BatteryRequiredPct(e, from, to, SpeedMode.Normal),
            Fast = Reachability.BatteryRequiredPct(e, from, to, SpeedMode.Fast)
        };

        private static int CountAvailableAt(WorldState world, GridCoord facility)
        {
            int n = 0;
            foreach (var p in world.Packages.Values)
                if (p.Status == PackageStatus.AtFacility && p.Origin == facility) n++;
            return n;
        }

        private static NearestChargeObs NearestChargeFrom(SimulationEngine e, WorldState world, WorldPos from)
        {
            double req = Reachability.RequiredToNearestChargeTile(e, from, SpeedMode.Slow, out var tile);
            bool clear = true;
            if (!tile.HasValue)
            {
                req = Reachability.RequiredToNearestChargeTileIgnoringObstacles(e, from, SpeedMode.Slow, out tile);
                clear = false;
            }
            if (!tile.HasValue) return null;
            return new NearestChargeObs
            {
                Tile = CoordObs.From(tile.Value),
                Type = world.Map.TypeAt(tile.Value),
                Distance = from.DistanceTo(tile.Value.Center),
                DirectPathClear = clear,
                BatteryRequiredSlow = req
            };
        }

        /// <summary>Every Building tile the segment crosses, ordered by entry along the segment.</summary>
        private static List<CoordObs> Obstructions(GridMap map, WorldPos a, WorldPos b)
        {
            var hits = new List<(double t, GridCoord c)>();
            int xStart = Math.Max(1, (int)Math.Ceiling(Math.Min(a.X, b.X) - 0.5));
            int xEnd   = Math.Min(map.Size, (int)Math.Floor(Math.Max(a.X, b.X) + 0.5));
            int yStart = Math.Max(1, (int)Math.Ceiling(Math.Min(a.Y, b.Y) - 0.5));
            int yEnd   = Math.Min(map.Size, (int)Math.Floor(Math.Max(a.Y, b.Y) + 0.5));
            for (int x = xStart; x <= xEnd; x++)
                for (int y = yStart; y <= yEnd; y++)
                {
                    var c = new GridCoord(x, y);
                    if (map.TypeAt(c) != TileType.Building) continue;
                    if (GridGeometry.SegmentIntersectsTile(a, b, c, out double t)) hits.Add((t, c));
                }
            hits.Sort((p, q) => p.t.CompareTo(q.t));
            var result = new List<CoordObs>(hits.Count);
            foreach (var h in hits) result.Add(CoordObs.From(h.c));
            return result;
        }

        private static List<Package> OrderedPackages(WorldState world)
        {
            var list = new List<Package>(world.Packages.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }
    }
}