using System.Collections.Generic;
using Newtonsoft.Json;
using SimCore.Config;
using SimCore.Domain;

namespace SimCore.Observation
{
    // ============================================================================================
    //  Shared DTOs (present in BOTH modes). These are environment rules and current facts, not derivations.
    // ============================================================================================

    public sealed class CoordObs
    {
        [JsonProperty("x")] public int X;
        [JsonProperty("y")] public int Y;
        public static CoordObs From(GridCoord c) => new() { X = c.X, Y = c.Y };
        public GridCoord ToCoord() => new(X, Y);
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>The drone's physical rules. A planner needs these to reason about range under RAW; PROCESSED pre-computes them.</summary>
    public sealed class DronePhysicsObs
    {
        [JsonProperty("battery_capacity_pct")]       public double BatteryCapacityPct;
        [JsonProperty("base_speed_tiles_per_sec")]   public double BaseSpeedTilesPerSec;
        [JsonProperty("base_discharge_pct_per_sec")] public double BaseDischargePctPerSec;
        [JsonProperty("charge_pct_per_sec")]         public double ChargePctPerSec;
        [JsonProperty("speed_multipliers")]          public SpeedTriple SpeedMultipliers;
        [JsonProperty("discharge_multipliers")]      public SpeedTriple DischargeMultipliers;
        [JsonProperty("inventory_capacity")]         public int InventoryCapacity;
        [JsonProperty("load_seconds_per_package")]   public double LoadSecondsPerPackage;
        [JsonProperty("lbs_safety_margin_pct")]      public double LbsSafetyMarginPct;
        [JsonProperty("auto_recovery_enabled")]      public bool AutoRecoveryEnabled;

        public double SpeedTilesPerSec(SpeedMode m) => BaseSpeedTilesPerSec * SpeedMultipliers.For(m);
        public double DischargePctPerSec(SpeedMode m) => BaseDischargePctPerSec * DischargeMultipliers.For(m);
        public double BatteryRequiredPct(double distanceTiles, SpeedMode m) => distanceTiles / SpeedTilesPerSec(m) * DischargePctPerSec(m);
        public double RangeTiles(double batteryPct, SpeedMode m) => batteryPct / DischargePctPerSec(m) * SpeedTilesPerSec(m);

        public static DronePhysicsObs From(ScenarioConfig cfg) => new()
        {
            BatteryCapacityPct = cfg.Drone.BatteryCapacityPct,
            BaseSpeedTilesPerSec = cfg.Drone.BaseSpeedTilesPerSec,
            BaseDischargePctPerSec = cfg.Drone.BaseDischargePctPerSec,
            ChargePctPerSec = cfg.Drone.ChargePctPerSec,
            SpeedMultipliers = cfg.Drone.SpeedMultipliers,
            DischargeMultipliers = cfg.Drone.DischargeMultipliers,
            InventoryCapacity = cfg.Drone.InventoryCapacity,
            LoadSecondsPerPackage = cfg.Drone.LoadSecondsPerPackage,
            LbsSafetyMarginPct = cfg.Recovery.LbsSafetyMarginPct,
            AutoRecoveryEnabled = cfg.Recovery.AutoRecoveryEnabled
        };
    }

    public sealed class DroneObs
    {
        [JsonProperty("tile")]        public CoordObs Tile;
        [JsonProperty("tile_type")]   public TileType TileType;
        [JsonProperty("battery_pct")] public double BatteryPct;
        [JsonProperty("speed")]       public SpeedMode Speed;
        [JsonProperty("flight")]      public FlightStatus Flight;
        [JsonProperty("inventory")]   public List<string> Inventory = new();
    }

    public sealed class MissionObs
    {
        [JsonProperty("max_trips")]                      public int MaxTrips;
        [JsonProperty("max_sim_seconds")]                public double MaxSimSeconds;
        [JsonProperty("packages_total")]                 public int PackagesTotal;
        [JsonProperty("packages_delivered")]             public int PackagesDelivered;
        [JsonProperty("require_all_packages_delivered")] public bool RequireAllPackagesDelivered;
    }

    public sealed class TileObs
    {
        [JsonProperty("x")]    public int X;
        [JsonProperty("y")]    public int Y;
        [JsonProperty("type")] public TileType Type;
    }

    public sealed class PackageObs
    {
        [JsonProperty("id")]          public string Id;
        [JsonProperty("origin")]      public CoordObs Origin;
        [JsonProperty("destination")] public CoordObs Destination;
        [JsonProperty("difficulty")]  public PackageDifficulty Difficulty;
        [JsonProperty("status")]      public PackageStatus Status;
    }

    // ============================================================================================
    //  RAW: minimally transformed world state (spec §10.2)
    // ============================================================================================

    public sealed class RawObservation : Observation
    {
        [JsonProperty("grid_size")] public int GridSize;
        [JsonProperty("physics")]   public DronePhysicsObs Physics;
        [JsonProperty("mission")]   public MissionObs Mission;
        [JsonProperty("drone")]     public DroneObs Drone;
        /// <summary>Every non-Road tile. Road is implied everywhere else.</summary>
        [JsonProperty("tiles")]     public List<TileObs> Tiles = new();
        [JsonProperty("packages")]  public List<PackageObs> Packages = new();

        public RawObservation() : base(ObservationMode.Raw) { }
    }

    // ============================================================================================
    //  PROCESSED: derived, relational facts (spec §10.3). Same state, pre-computed.
    // ============================================================================================

    public sealed class RangeObs
    {
        [JsonProperty("slow")]   public double Slow;
        [JsonProperty("normal")] public double Normal;
        [JsonProperty("fast")]   public double Fast;
    }

    public sealed class ChargeTileObs
    {
        [JsonProperty("tile")]                public CoordObs Tile;
        [JsonProperty("type")]                public TileType Type;
        [JsonProperty("is_current_location")] public bool IsCurrentLocation;
        [JsonProperty("distance")]            public double Distance;
        [JsonProperty("direct_path_clear")]   public bool DirectPathClear;
        [JsonProperty("obstructing_tiles")]   public List<CoordObs> ObstructingTiles = new();
        [JsonProperty("battery_required")]    public RangeObs BatteryRequired;
        [JsonProperty("available_packages")]  public int AvailablePackages;
    }

    public sealed class NearestChargeObs
    {
        [JsonProperty("tile")]                  public CoordObs Tile;
        [JsonProperty("type")]                  public TileType Type;
        [JsonProperty("distance")]              public double Distance;
        [JsonProperty("direct_path_clear")]     public bool DirectPathClear;
        [JsonProperty("battery_required_slow")] public double BatteryRequiredSlow;
    }

    public sealed class ProcessedPackageObs
    {
        [JsonProperty("id")]                                 public string Id;
        [JsonProperty("origin")]                             public CoordObs Origin;
        [JsonProperty("destination")]                        public CoordObs Destination;
        [JsonProperty("difficulty")]                         public PackageDifficulty Difficulty;
        [JsonProperty("status")]                             public PackageStatus Status;
        [JsonProperty("available_here")]                     public bool AvailableHere;
        [JsonProperty("on_board")]                           public bool OnBoard;
        [JsonProperty("distance_origin_to_destination")]     public double DistanceOriginToDestination;
        [JsonProperty("distance_from_drone_to_destination")] public double DistanceFromDroneToDestination;
        [JsonProperty("direct_path_clear_from_drone")]       public bool DirectPathClearFromDrone;
        [JsonProperty("obstructing_tiles")]                  public List<CoordObs> ObstructingTiles = new();
        [JsonProperty("battery_required_from_drone")]        public RangeObs BatteryRequiredFromDrone;
        [JsonProperty("nearest_charge_from_destination")]    public NearestChargeObs NearestChargeFromDestination;
        /// <summary>drone → destination → nearest charge tile, at Normal.</summary>
        [JsonProperty("loop_battery_required_normal")]       public double LoopBatteryRequiredNormal;
        [JsonProperty("feasible_now_normal")]                public bool FeasibleNowNormal;
        [JsonProperty("feasible_after_full_charge_normal")]  public bool FeasibleAfterFullChargeNormal;
    }

    public sealed class ProcessedObservation : Observation
    {
        [JsonProperty("grid_size")]            public int GridSize;
        [JsonProperty("physics")]              public DronePhysicsObs Physics;
        [JsonProperty("mission")]              public MissionObs Mission;
        [JsonProperty("drone")]                public DroneObs Drone;
        [JsonProperty("range_now_tiles")]      public RangeObs RangeNowTiles;
        [JsonProperty("range_full_tiles")]     public RangeObs RangeFullTiles;
        [JsonProperty("charging_recommended")] public bool ChargingRecommended;
        [JsonProperty("charge_tiles")]         public List<ChargeTileObs> ChargeTiles = new();
        /// <summary>Undelivered packages only.</summary>
        [JsonProperty("packages")]             public List<ProcessedPackageObs> Packages = new();
        /// <summary>Building tiles — the obstacle set, so a planner can construct detours.</summary>
        [JsonProperty("obstacles")]            public List<CoordObs> Obstacles = new();

        public ProcessedObservation() : base(ObservationMode.Processed) { }
    }
}