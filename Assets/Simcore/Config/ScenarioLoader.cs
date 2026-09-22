using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SimCore.Domain;
using SimCore.State;
using SimCore.Util;

namespace SimCore.Config
{
    public sealed class SimConfigException : Exception
    {
        public SimConfigException(string message) : base(message) { }
    }

    /// <summary>
    /// JSON → validated ScenarioConfig → constructed WorldState.
    /// Philosophy: a malformed scenario fails HERE with a precise message, never as a
    /// mystery mid-episode. Scenario validity is a precondition of every experiment.
    /// </summary>
    public static class ScenarioLoader
    {
        public static ScenarioConfig Parse(string json)
        {
            ScenarioConfig cfg;
            try
            {
                cfg = JsonConvert.DeserializeObject<ScenarioConfig>(json);
            }
            catch (JsonException ex)
            {
                throw new SimConfigException($"Scenario JSON is malformed: {ex.Message}");
            }
            if (cfg == null) throw new SimConfigException("Scenario JSON parsed to null.");
            Validate(cfg);
            return cfg;
        }

        public static WorldState Build(ScenarioConfig cfg)
        {
            var map = new GridMap(cfg.GridSize);

            foreach (var t in cfg.Tiles)
            {
                var c = new GridCoord(t.X, t.Y);
                if (!map.InBounds(c))
                    throw new SimConfigException($"Tile {c} is outside the {cfg.GridSize}x{cfg.GridSize} grid.");
                if (!Enum.TryParse<TileType>(t.Type, ignoreCase: true, out var type))
                    throw new SimConfigException($"Tile {c} has unknown type '{t.Type}'.");
                map.SetType(c, type);
            }

            var clock = new SimClock(cfg.TickSeconds);
            var world = new WorldState(map, clock);

            // ---- Drone start ----
            var facilities = map.AllOfType(TileType.Facility);
            if (facilities.Count == 0)
                throw new SimConfigException("Scenario has no Facility tiles; the drone has nowhere to start.");

            GridCoord start = cfg.Drone.StartFacility != null
                ? cfg.Drone.StartFacility.ToCoord()
                : facilities[0]; // deterministic scan order

            if (!map.InBounds(start) || map.TypeAt(start) != TileType.Facility)
                throw new SimConfigException($"start_facility {start} is not a Facility tile.");

            world.Drone.Position = start.Center;
            world.Drone.LandedTile = start;
            world.Drone.Flight = FlightStatus.Landed;
            world.Drone.BatteryPct = Clamp(cfg.Drone.StartingBatteryPct, 0, cfg.Drone.BatteryCapacityPct);

            // ---- Packages ----
            if (cfg.Packages.Count > 0)
                AddExplicitPackages(cfg, map, world);
            else if (cfg.PackageGeneration != null)
                GeneratePackages(cfg, map, world, new DeterministicRng(cfg.Seed));
            else
                throw new SimConfigException("Scenario defines neither explicit packages nor package_generation.");

            return world;
        }

        // ---------------------------------------------------------------

        private static void AddExplicitPackages(ScenarioConfig cfg, GridMap map, WorldState world)
        {
            var usedDestinations = new HashSet<GridCoord>();

            foreach (var spec in cfg.Packages)
            {
                if (string.IsNullOrWhiteSpace(spec.Id))
                    throw new SimConfigException("A package is missing its id.");
                if (world.Packages.ContainsKey(spec.Id))
                    throw new SimConfigException($"Duplicate package id '{spec.Id}'.");

                var origin = spec.Origin.ToCoord();
                var dest = spec.Destination.ToCoord();

                if (!map.InBounds(origin) || map.TypeAt(origin) != TileType.Facility)
                    throw new SimConfigException($"Package {spec.Id}: origin {origin} is not a Facility.");
                if (!map.InBounds(dest) || map.TypeAt(dest) != TileType.House)
                    throw new SimConfigException($"Package {spec.Id}: destination {dest} is not a House.");
                if (!usedDestinations.Add(dest))
                    throw new SimConfigException($"Package {spec.Id}: destination {dest} already used by another package (spec: no shared destinations).");
                if (!Enum.TryParse<PackageDifficulty>(spec.Difficulty, ignoreCase: true, out var diff))
                    throw new SimConfigException($"Package {spec.Id}: unknown difficulty '{spec.Difficulty}'.");

                world.Packages.Add(spec.Id, new Package(spec.Id, origin, dest, diff));
            }
        }

        private static void GeneratePackages(ScenarioConfig cfg, GridMap map, WorldState world, DeterministicRng rng)
{
    var gen = cfg.PackageGeneration;
    var houses = map.AllOfType(TileType.House);
    var usedDestinations = new HashSet<GridCoord>();
    int counter = 1;

    foreach (var facility in map.AllOfType(TileType.Facility))
    {
        foreach (var diffName in gen.PerFacility)
        {
            if (!Enum.TryParse<PackageDifficulty>(diffName, ignoreCase: true, out var diff))
                throw new SimConfigException($"package_generation: unknown difficulty '{diffName}'.");
            if (!gen.DistanceBands.TryGetValue(diffName, out var band) || band.Length != 2)
                throw new SimConfigException($"package_generation: missing/invalid distance band for '{diffName}'.");

            Console.WriteLine($"Generating {diffName} package for Facility at {facility}");
            Console.WriteLine($"Distance band: [{band[0]}, {band[1]}]");

            var candidates = new List<GridCoord>();
            foreach (var h in houses)
            {
                if (usedDestinations.Contains(h)) continue;
                double d = facility.DistanceTo(h);
                Console.WriteLine($"House at {h}, distance: {d}");
                if (d >= band[0] && d <= band[1]) candidates.Add(h);
            }

            Console.WriteLine($"Candidates: {string.Join(", ", candidates)}");
            Console.WriteLine($"Used destinations: {string.Join(", ", usedDestinations)}");

            if (candidates.Count == 0)
                throw new SimConfigException(
                    $"No available House within {band[0]}–{band[1]} tiles of Facility {facility} " +
                    $"for a {diffName} package. Add houses or adjust bands — the band is unsatisfiable (spec §12.1).");

            var dest = rng.Pick(candidates);
            usedDestinations.Add(dest);
            string id = $"P{counter:D3}";
            counter++;
            world.Packages.Add(id, new Package(id, facility, dest, diff));
        }
    }
}

        private static void Validate(ScenarioConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.ScenarioId))
                throw new SimConfigException("scenario_id is required.");
            if (cfg.GridSize < 5 || cfg.GridSize > 200)
                throw new SimConfigException($"grid_size must be 5..200, got {cfg.GridSize}.");
            if (cfg.Drone.InventoryCapacity < 1)
                throw new SimConfigException("inventory_capacity must be >= 1.");
            if (cfg.Drone.BaseSpeedTilesPerSec <= 0 || cfg.Drone.BaseDischargePctPerSec <= 0 || cfg.Drone.ChargePctPerSec <= 0)
                throw new SimConfigException("Speed, discharge, and charge rates must all be positive.");
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
    }
}