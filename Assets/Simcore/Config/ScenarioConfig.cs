using System.Collections.Generic;
using Newtonsoft.Json;
using SimCore.Domain;

namespace SimCore.Config
{
    /// <summary>
    /// The complete, serializable definition of an evaluation environment (spec §3).
    /// A scenario JSON file + this class = everything needed to reconstruct the world.
    /// Versioned: changing a scenario means bumping scenario_version, never silently editing.
    /// NOTE (D7): there is deliberately no "difficulty" enum here. Scenario difficulty is
    /// emergent from configuration; human-facing labels live in scenario_id ("Easy-001").
    /// </summary>
    public sealed class ScenarioConfig
    {
        [JsonProperty("scenario_id")]      public string ScenarioId = "";
        [JsonProperty("scenario_version")] public string ScenarioVersion = "1.0.0";
        [JsonProperty("seed")]             public int Seed = 0;
        [JsonProperty("tick_seconds")]     public double TickSeconds = 0.05;
        [JsonProperty("grid_size")]        public int GridSize = 20;

        /// <summary>Only non-Road tiles are listed; everything unlisted defaults to Road.</summary>
        [JsonProperty("tiles")]            public List<TileSpec> Tiles = new();

        [JsonProperty("drone")]            public DroneConfig Drone = new();

        /// <summary>Explicit packages. If non-empty, package_generation is ignored.</summary>
        [JsonProperty("packages")]         public List<PackageSpec> Packages = new();

        /// <summary>Seeded generation from difficulty distance bands (used when packages is empty).</summary>
        [JsonProperty("package_generation")] public PackageGenerationConfig PackageGeneration;

        [JsonProperty("mission")]          public MissionConfig Mission = new();
        [JsonProperty("recovery")]         public RecoveryConfig Recovery = new();
    }

    public sealed class TileSpec
    {
        [JsonProperty("x")]    public int X;
        [JsonProperty("y")]    public int Y;
        [JsonProperty("type")] public string Type = "Road";
    }

    public sealed class CoordSpec
    {
        [JsonProperty("x")] public int X;
        [JsonProperty("y")] public int Y;
        public GridCoord ToCoord() => new GridCoord(X, Y);
    }

    public sealed class DroneConfig
    {
        /// <summary>null → deterministic default: first Facility in scan order.</summary>
        [JsonProperty("start_facility")]            public CoordSpec StartFacility;

        /// <summary>D2: default 100. The "cold start at 1%" idea is a scenario variant, not the default.</summary>
        [JsonProperty("starting_battery_pct")]      public double StartingBatteryPct = 100.0;
        [JsonProperty("battery_capacity_pct")]      public double BatteryCapacityPct = 100.0;

        /// <summary>D9: rates in %/sec, tick-rate independent. 100/12 ≈ 8.333 → 12 s of normal flight.</summary>
        [JsonProperty("base_discharge_pct_per_sec")] public double BaseDischargePctPerSec = 100.0 / 12.0;

        /// <summary>100/6 ≈ 16.667 → 6 s empty-to-full recharge.</summary>
        [JsonProperty("charge_pct_per_sec")]        public double ChargePctPerSec = 100.0 / 6.0;

        [JsonProperty("base_speed_tiles_per_sec")]  public double BaseSpeedTilesPerSec = 3.0;

        [JsonProperty("speed_multipliers")]         public SpeedTriple SpeedMultipliers =
            new SpeedTriple { Slow = 0.6, Normal = 1.0, Fast = 1.5 };

        [JsonProperty("discharge_multipliers")]     public SpeedTriple DischargeMultipliers =
            new SpeedTriple { Slow = 0.5, Normal = 1.0, Fast = 1.9 };
        [JsonProperty("load_seconds_per_package")]  public double LoadSecondsPerPackage = 2.0;
        [JsonProperty("inventory_capacity")]        public int InventoryCapacity = 2;
    }

    public sealed class SpeedTriple
    {
        [JsonProperty("slow")]   public double Slow;
        [JsonProperty("normal")] public double Normal;
        [JsonProperty("fast")]   public double Fast;

        public double For(SpeedMode mode) => mode switch
        {
            SpeedMode.Slow => Slow,
            SpeedMode.Fast => Fast,
            _ => Normal
        };
    }

    public sealed class PackageSpec
    {
        [JsonProperty("id")]          public string Id = "";
        [JsonProperty("origin")]      public CoordSpec Origin = new();
        [JsonProperty("destination")] public CoordSpec Destination = new();
        [JsonProperty("difficulty")]  public string Difficulty = "Normal";
    }

    public sealed class PackageGenerationConfig
    {
        /// <summary>Difficulties spawned at every Facility, in order. Default: one of each.</summary>
        [JsonProperty("per_facility")] public List<string> PerFacility = new() { "Easy", "Normal", "Hard" };

        /// <summary>Inclusive [min,max] straight-line distance bands, in tiles, per difficulty.</summary>
        [JsonProperty("distance_bands")] public Dictionary<string, double[]> DistanceBands = new()
        {
            { "Easy",   new[] {  5.0, 10.0 } },
            { "Normal", new[] { 10.0, 20.0 } },
            { "Hard",   new[] { 20.0, 30.0 } },
        };
    }

    public sealed class MissionConfig
    {
        [JsonProperty("require_all_packages_delivered")] public bool RequireAllPackagesDelivered = true;
        [JsonProperty("max_trips")]                      public int MaxTrips = 20;
        [JsonProperty("max_sim_seconds")]                public double MaxSimSeconds = 600.0;
    }

    public sealed class RecoveryConfig
    {
        /// <summary>Easy-guideline behavior: simulator force-reroutes to nearest charger on LBS.
        /// Turned off in harder conditions so battery mismanagement has real consequences.</summary>
        [JsonProperty("auto_recovery_enabled")] public bool AutoRecoveryEnabled = true;

        /// <summary>Battery % held in reserve when computing "can I safely reach X".</summary>
        [JsonProperty("lbs_safety_margin_pct")] public double LbsSafetyMarginPct = 5.0;
    }
}