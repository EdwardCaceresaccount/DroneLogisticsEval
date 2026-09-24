using System;
using System.Collections.Generic;
using SimCore.Config;
using SimCore.Domain;
using SimCore.Events;
using SimCore.State;

namespace SimCore.Engine
{
    public sealed class SimulationEngine
    {
        public WorldState World { get; }
        public ScenarioConfig Config { get; }
        public DroneActivity Activity { get; } = new();

        public bool IsCharging { get; private set; }
        public double ChargeTargetPct { get; private set; }

        private readonly ISimEventSink _events;
        private const double CenterTolerance = 1e-6;

        public SimulationEngine(WorldState world, ScenarioConfig config, ISimEventSink events)
        {
            World = world ?? throw new ArgumentNullException(nameof(world));
            Config = config ?? throw new ArgumentNullException(nameof(config));
            _events = events ?? throw new ArgumentNullException(nameof(events));
        }

        public bool IsBusy => Activity.Kind != ActivityKind.Idle;
        public bool IsTerminal => World.Drone.Failure != FailureKind.None;

        // ---------------- derived physics ----------------

        public double MoveSpeedTilesPerSec(SpeedMode mode)
            => Config.Drone.BaseSpeedTilesPerSec * Config.Drone.SpeedMultipliers.For(mode);

        public double DischargePctPerSec(SpeedMode mode)
            => Config.Drone.BaseDischargePctPerSec * Config.Drone.DischargeMultipliers.For(mode);

        public double RangeTilesAtSpeed(SpeedMode mode, double batteryPct)
            => batteryPct / DischargePctPerSec(mode) * MoveSpeedTilesPerSec(mode);

        // ---------------- primitives ----------------

        public void LiftOff()
        {
            var d = World.Drone;
            RequireNotTerminal();
            Require(d.Flight == FlightStatus.Landed, "LiftOff: drone is already flying.");
            Require(!IsBusy, $"LiftOff: drone is busy ({Activity.Kind}).");
            Require(!IsCharging, "LiftOff: charge in progress — executor must wait for it (D10).");

            d.Flight = FlightStatus.Flying;
            d.LandedTile = null;
            Emit(SimEventTypes.LiftOff, ("position", d.Position.ToString()), ("battery_pct", d.BatteryPct));
        }

        public void Land()
        {
            var d = World.Drone;
            RequireNotTerminal();
            Require(d.Flight == FlightStatus.Flying, "Land: drone is not flying.");
            Require(!IsBusy, $"Land: drone is busy ({Activity.Kind}).");

            var tile = d.Position.NearestTile();
            Require(d.Position.DistanceTo(tile.Center) <= CenterTolerance,
                $"Land: drone at {d.Position} is not over a tile center.");
            Require(World.Map.TypeAt(tile) != TileType.Building, "Land: cannot land on a Building.");

            d.Flight = FlightStatus.Landed;
            d.LandedTile = tile;
            Emit(SimEventTypes.Land, ("tile", tile.ToString()), ("tile_type", World.Map.TypeAt(tile).ToString()),
                 ("battery_pct", d.BatteryPct));
        }

        public void BeginMoveTo(WorldPos target)
        {
            var d = World.Drone;
            RequireNotTerminal();
            Require(d.Flight == FlightStatus.Flying, "BeginMoveTo: drone must be airborne.");
            Require(!IsBusy, $"BeginMoveTo: drone is busy ({Activity.Kind}).");
            Require(World.Map.InBounds(target), $"BeginMoveTo: target {target} is outside the grid.");

            Activity.SetMoving(target);
            Emit(SimEventTypes.MoveStarted, ("from", d.Position.ToString()), ("to", target.ToString()),
                 ("speed", d.Speed.ToString()), ("battery_pct", d.BatteryPct));
        }

        public void SetSpeed(SpeedMode mode)
        {
            RequireNotTerminal();
            var d = World.Drone;
            var previous = d.Speed;
            d.Speed = mode;
            Emit(SimEventTypes.SpeedSet, ("from", previous.ToString()), ("to", mode.ToString()));
        }

        public void BeginLoad(string packageId)
        {
            var d = World.Drone;
            RequireNotTerminal();
            Require(d.Flight == FlightStatus.Landed && d.LandedTile.HasValue, "BeginLoad: drone must be landed.");
            Require(!IsBusy, $"BeginLoad: drone is busy ({Activity.Kind}).");
            Require(World.Map.TypeAt(d.LandedTile.Value) == TileType.Facility, "BeginLoad: not on a Facility.");
            Require(World.Packages.TryGetValue(packageId, out var pkg), $"BeginLoad: unknown package '{packageId}'.");
            Require(pkg.Status == PackageStatus.AtFacility, $"BeginLoad: package {packageId} is {pkg.Status}.");
            Require(pkg.Origin == d.LandedTile.Value, $"BeginLoad: package {packageId} is at {pkg.Origin}, drone is at {d.LandedTile}.");
            Require(d.LoadedPackageIds.Count < Config.Drone.InventoryCapacity, "BeginLoad: inventory full.");

            Activity.SetLoading(packageId, Config.Drone.LoadSecondsPerPackage);
            Emit(SimEventTypes.LoadStarted, ("package_id", packageId), ("seconds", Config.Drone.LoadSecondsPerPackage));
        }

        public void BeginCharge(double targetPct)
        {
            var d = World.Drone;
            RequireNotTerminal();
            Require(d.Flight == FlightStatus.Landed && d.LandedTile.HasValue, "BeginCharge: drone must be landed.");
            Require(World.Map.IsChargeTile(d.LandedTile.Value), "BeginCharge: not on a Facility or Charging Station.");
            Require(!IsCharging, "BeginCharge: already charging.");

            double target = Math.Min(Math.Max(targetPct, 0), Config.Drone.BatteryCapacityPct);
            if (d.BatteryPct >= target)
            {
                Emit(SimEventTypes.ChargeCompleted, ("target_pct", target), ("battery_pct", d.BatteryPct), ("skipped", true));
                return;
            }
            IsCharging = true;
            ChargeTargetPct = target;
            Emit(SimEventTypes.ChargeStarted, ("target_pct", target), ("battery_pct", d.BatteryPct));
        }

        public void BeginWait(double seconds)
        {
            RequireNotTerminal();
            Require(!IsBusy, $"BeginWait: drone is busy ({Activity.Kind}).");
            Require(seconds > 0, "BeginWait: seconds must be positive.");
            Activity.SetWaiting(seconds);
            Emit(SimEventTypes.WaitStarted, ("seconds", seconds), ("flight", World.Drone.Flight.ToString()));
        }

        public void Deliver(string packageId)
        {
            var d = World.Drone;
            RequireNotTerminal();
            Require(!IsBusy, $"Deliver: drone is busy ({Activity.Kind}).");
            Require(World.Packages.TryGetValue(packageId, out var pkg), $"Deliver: unknown package '{packageId}'.");
            Require(pkg.Status == PackageStatus.Loaded && d.LoadedPackageIds.Contains(packageId),
                $"Deliver: package {packageId} is not on board.");
            Require(d.Position.DistanceTo(pkg.Destination.Center) <= CenterTolerance,
                $"Deliver: drone at {d.Position} is not over destination {pkg.Destination}.");

            pkg.Status = PackageStatus.Delivered;
            d.LoadedPackageIds.Remove(packageId);
            Emit(SimEventTypes.PackageDelivered, ("package_id", packageId), ("tile", pkg.Destination.ToString()),
                 ("battery_pct", d.BatteryPct), ("delivered_total", World.DeliveredCount()));
        }

        /// <summary>
        /// Block 7: simulator-initiated interruption of an in-flight activity (Moving or Waiting).
        /// The drone holds its current position (hovering, still draining). Only the recovery system calls this;
        /// it is never exposed to planners as a tool.
        /// </summary>
        public void AbortActivity(string reason)
        {
            RequireNotTerminal();
            var d = World.Drone;
            Require(d.Flight == FlightStatus.Flying, "AbortActivity: only flight activities can be aborted.");
            Require(Activity.Kind == ActivityKind.Moving || Activity.Kind == ActivityKind.Waiting || Activity.Kind == ActivityKind.Idle,
                $"AbortActivity: cannot abort {Activity.Kind}.");

            var kind = Activity.Kind;
            Activity.SetIdle();
            Emit(SimEventTypes.ActivityAborted, ("activity", kind.ToString()), ("reason", reason),
                 ("position", d.Position.ToString()), ("battery_pct", d.BatteryPct));
        }

        // ---------------- time ----------------

        public void Tick()
        {
            RequireNotTerminal();
            double dt = World.Clock.TickSeconds;

            switch (Activity.Kind)
            {
                case ActivityKind.Moving:  TickMove(dt); break;
                case ActivityKind.Loading: TickLoad(dt); break;
                case ActivityKind.Waiting: TickWait(dt); break;
                case ActivityKind.Idle:
                    if (World.Drone.Flight == FlightStatus.Flying) DrainBattery(dt, 1.0);
                    break;
            }

            if (!IsTerminal) TickCharge(dt);

            World.Clock.Advance();
        }

        private void TickMove(double dt)
        {
            var d = World.Drone;
            double step = MoveSpeedTilesPerSec(d.Speed) * dt;
            var from = d.Position;
            var target = Activity.MoveTarget;
            double remaining = from.DistanceTo(target);

            double fractionOfTick = 1.0;
            WorldPos to;
            bool arrives = remaining <= step;
            if (arrives)
            {
                to = target;
                fractionOfTick = step > 0 ? remaining / step : 0;
            }
            else
            {
                to = from.Lerp(target, step / remaining);
            }

            if (GridGeometry.TryGetFirstBuildingOnSegment(World.Map, from, to, out var wall, out double tEntry))
            {
                d.Position = from.Lerp(to, tEntry);
                DrainBattery(dt, fractionOfTick * tEntry);
                Crash(wall);
                return;
            }

            d.Position = to;
            DrainBattery(dt, fractionOfTick);
            if (IsTerminal) return;

            if (arrives)
            {
                Activity.SetIdle();
                Emit(SimEventTypes.MoveArrived, ("position", d.Position.ToString()),
                     ("tile", d.Position.NearestTile().ToString()), ("battery_pct", d.BatteryPct));
            }
        }

        private void TickLoad(double dt)
        {
            Activity.Elapse(dt);
            if (Activity.SecondsRemaining > 0) return;

            string id = Activity.LoadingPackageId;
            var pkg = World.Packages[id];
            pkg.Status = PackageStatus.Loaded;
            World.Drone.LoadedPackageIds.Add(id);
            Activity.SetIdle();
            Emit(SimEventTypes.PackageLoaded, ("package_id", id), ("inventory", World.Drone.LoadedPackageIds.Count));
        }

        private void TickWait(double dt)
        {
            if (World.Drone.Flight == FlightStatus.Flying) DrainBattery(dt, 1.0);
            if (IsTerminal) return;
            Activity.Elapse(dt);
            if (Activity.SecondsRemaining > 0) return;
            Activity.SetIdle();
            Emit(SimEventTypes.WaitCompleted);
        }

        private void TickCharge(double dt)
        {
            if (!IsCharging) return;
            var d = World.Drone;
            if (d.Flight != FlightStatus.Landed || !d.LandedTile.HasValue || !World.Map.IsChargeTile(d.LandedTile.Value))
                throw new SimInvariantException("Charging while not landed on a charge tile.");

            d.BatteryPct = Math.Min(ChargeTargetPct, d.BatteryPct + Config.Drone.ChargePctPerSec * dt);
            if (d.BatteryPct >= ChargeTargetPct)
            {
                IsCharging = false;
                Emit(SimEventTypes.ChargeCompleted, ("target_pct", ChargeTargetPct), ("battery_pct", d.BatteryPct), ("skipped", false));
            }
        }

        private void DrainBattery(double dt, double fraction)
        {
            var d = World.Drone;
            if (d.Flight != FlightStatus.Flying || fraction <= 0) return;

            d.BatteryPct -= DischargePctPerSec(d.Speed) * dt * fraction;
            if (d.BatteryPct <= 0)
            {
                d.BatteryPct = 0;
                ForcedLanding();
            }
        }

        private void ForcedLanding()
        {
            var d = World.Drone;
            var tile = d.Position.NearestTile();
            d.Flight = FlightStatus.Landed;
            d.LandedTile = World.Map.TypeAt(tile) == TileType.Building ? null : tile;
            d.Failure = FailureKind.ForcedLanding;
            Activity.SetIdle();
            Emit(SimEventTypes.ForcedLanding, ("position", d.Position.ToString()), ("nearest_tile", tile.ToString()),
                 ("nearest_tile_type", World.Map.TypeAt(tile).ToString()));
        }

        private void Crash(GridCoord wall)
        {
            var d = World.Drone;
            d.Failure = FailureKind.Crashed;
            d.LandedTile = null;
            Activity.SetIdle();
            IsCharging = false;
            Emit(SimEventTypes.Crash, ("position", d.Position.ToString()), ("building_tile", wall.ToString()),
                 ("battery_pct", d.BatteryPct));
        }

        // ---------------- helpers ----------------

        private void Emit(string type, params (string key, object value)[] data)
        {
            var dict = new Dictionary<string, object>(data.Length);
            foreach (var (key, value) in data) dict[key] = value;
            _events.Emit(new SimEvent(World.Clock.Ticks, World.Clock.TimeSeconds, type, dict));
        }

        private void RequireNotTerminal()
        {
            if (IsTerminal)
                throw new SimInvariantException($"Engine is terminal ({World.Drone.Failure}); no further actions or ticks.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new SimInvariantException(message);
        }
    }
}