using System.Collections.Generic;
using SimCore.Config;
using SimCore.Domain;
using SimCore.State;

namespace SimCore.Commands
{
    /// <summary>
    /// Lightweight state used to check commands without running physics. Built either from
    /// the live WorldState (execution-time check) or advanced command-by-command (plan-time projection).
    /// Tracks only what the State/Physical categories need: flight, tile, position, inventory, charging.
    /// Battery is intentionally absent — see D5.
    /// </summary>
    public sealed class ProjectedState
    {
        public FlightStatus Flight;
        public GridCoord? LandedTile;
        public WorldPos Position;
        public bool IsCharging;
        public readonly List<string> Loaded = new();
        public readonly Dictionary<string, PackageStatus> PackageStatus = new();

        public static ProjectedState FromWorld(WorldState world, bool engineIsCharging)
        {
            var s = new ProjectedState
            {
                Flight = world.Drone.Flight,
                LandedTile = world.Drone.LandedTile,
                Position = world.Drone.Position,
                IsCharging = engineIsCharging
            };
            s.Loaded.AddRange(world.Drone.LoadedPackageIds);
            foreach (var kv in world.Packages) s.PackageStatus[kv.Key] = kv.Value.Status;
            return s;
        }
    }

    /// <summary>
    /// Checks commands for State and Physical errors (Schema/Tool errors never reach here — the parser owns those).
    /// ValidatePlan = projection walk over a whole plan. CheckCommand = one command against one state,
    /// reused by the executor at dispatch time against the live world.
    /// </summary>
    public sealed class PlanValidator
    {
        private const double CenterTolerance = 1e-6;
        private readonly WorldState _world;
        private readonly ScenarioConfig _cfg;

        public PlanValidator(WorldState world, ScenarioConfig cfg)
        {
            _world = world;
            _cfg = cfg;
        }

        public ValidationResult ValidatePlan(Plan plan, bool engineIsCharging)
        {
            var result = new ValidationResult();
            if (plan.Commands.Count == 0)
            {
                result.Add(ErrorCategory.Schema, ErrorCodes.EmptyPlan, "Plan contains no commands.");
                return result;
            }

            var s = ProjectedState.FromWorld(_world, engineIsCharging);
            for (int i = 0; i < plan.Commands.Count; i++)
            {
                var cmd = plan.Commands[i];
                var errs = CheckCommand(cmd, s, i);
                if (errs.Count == 0) Apply(cmd, s);
                else result.Errors.AddRange(errs);   // keep walking: report everything, not just the first
            }
            return result;
        }

        public List<ValidationError> CheckCommand(Command cmd, ProjectedState s, int index)
        {
            var errs = new List<ValidationError>();
            void Err(ErrorCategory cat, string code, string msg) => errs.Add(new ValidationError(cat, code, msg, index));

            switch (cmd.Type)
            {
                case CommandType.LIFT_OFF:
                    if (s.Flight == FlightStatus.Flying) Err(ErrorCategory.State, ErrorCodes.AlreadyFlying, "LIFT_OFF while already airborne.");
                    break;

                case CommandType.LAND:
                    if (s.Flight == FlightStatus.Landed) Err(ErrorCategory.State, ErrorCodes.AlreadyLanded, "LAND while already landed.");
                    break;

                case CommandType.MOVE_TO:
                    if (s.Flight != FlightStatus.Flying) Err(ErrorCategory.State, ErrorCodes.NotAirborne, $"MOVE_TO{cmd.Target} requires the drone to be airborne (LIFT_OFF first).");
                    if (!_world.Map.InBounds(cmd.Target.Value)) Err(ErrorCategory.Physical, ErrorCodes.OutOfBounds, $"MOVE_TO{cmd.Target} is outside the {_world.Map.Size}x{_world.Map.Size} grid.");
                    // Deliberately NOT checked: obstruction between here and Target (D5).
                    break;

                case CommandType.LOAD:
                {
                    if (s.Flight != FlightStatus.Landed || !s.LandedTile.HasValue)
                    { Err(ErrorCategory.State, ErrorCodes.NotLanded, $"LOAD({cmd.PackageId}) requires the drone to be landed."); break; }
                    if (_world.Map.TypeAt(s.LandedTile.Value) != TileType.Facility)
                    { Err(ErrorCategory.State, ErrorCodes.NotAtFacility, $"LOAD({cmd.PackageId}) requires a Facility; drone is on {_world.Map.TypeAt(s.LandedTile.Value)} {s.LandedTile}."); break; }
                    if (!_world.Packages.TryGetValue(cmd.PackageId, out var pkg))
                    { Err(ErrorCategory.State, ErrorCodes.UnknownPackage, $"LOAD: no package '{cmd.PackageId}' exists."); break; }
                    if (s.PackageStatus[cmd.PackageId] != PackageStatus.AtFacility)
                    { Err(ErrorCategory.State, ErrorCodes.PackageUnavailable, $"LOAD({cmd.PackageId}): package is {s.PackageStatus[cmd.PackageId]}."); break; }
                    if (pkg.Origin != s.LandedTile.Value)
                    { Err(ErrorCategory.State, ErrorCodes.PackageNotHere, $"LOAD({cmd.PackageId}): package is at Facility {pkg.Origin}, drone is at {s.LandedTile}."); break; }
                    if (s.Loaded.Count >= _cfg.Drone.InventoryCapacity)
                        Err(ErrorCategory.State, ErrorCodes.InventoryFull, $"LOAD({cmd.PackageId}): inventory full ({_cfg.Drone.InventoryCapacity}).");
                    break;
                }

                case CommandType.DELIVER:
                {
                    if (!_world.Packages.TryGetValue(cmd.PackageId, out var pkg))
                    { Err(ErrorCategory.State, ErrorCodes.UnknownPackage, $"DELIVER: no package '{cmd.PackageId}' exists."); break; }
                    if (!s.Loaded.Contains(cmd.PackageId))
                    { Err(ErrorCategory.State, ErrorCodes.PackageNotOnboard, $"DELIVER({cmd.PackageId}): package is not on board."); break; }
                    if (s.Position.DistanceTo(pkg.Destination.Center) > CenterTolerance)
                        Err(ErrorCategory.State, ErrorCodes.NotAtDestination, $"DELIVER({cmd.PackageId}): drone at {s.Position.NearestTile()} but destination is {pkg.Destination}.");
                    break;
                }

                case CommandType.CHARGE:
                    if (s.Flight != FlightStatus.Landed || !s.LandedTile.HasValue)
                    { Err(ErrorCategory.State, ErrorCodes.NotLanded, "CHARGE requires the drone to be landed."); break; }
                    if (!_world.Map.IsChargeTile(s.LandedTile.Value))
                    { Err(ErrorCategory.State, ErrorCodes.NotAtCharger, $"CHARGE requires a Facility or Charging Station; drone is on {_world.Map.TypeAt(s.LandedTile.Value)} {s.LandedTile}."); break; }
                    if (s.IsCharging) Err(ErrorCategory.State, ErrorCodes.AlreadyCharging, "CHARGE while a charge is already in progress.");
                    if (cmd.Value.Value > _cfg.Drone.BatteryCapacityPct)
                        Err(ErrorCategory.State, ErrorCodes.ChargeExceedsCapacity, $"CHARGE({cmd.Value:F0}) exceeds capacity {_cfg.Drone.BatteryCapacityPct}.");
                    break;

                case CommandType.WAIT:
                case CommandType.SET_SPEED:
                    break; // argument validity already enforced by the parser; legal in any state
            }
            return errs;
        }

        /// <summary>Advance the projection as if the command succeeded. Mirrors engine effects at the state level only.</summary>
        public static void Apply(Command cmd, ProjectedState s)
        {
            switch (cmd.Type)
            {
                case CommandType.LIFT_OFF:
                    s.Flight = FlightStatus.Flying; s.LandedTile = null; s.IsCharging = false; // executor waits out the charge (D10)
                    break;
                case CommandType.LAND:
                    s.Flight = FlightStatus.Landed; s.LandedTile = s.Position.NearestTile();
                    break;
                case CommandType.MOVE_TO:
                    s.Position = cmd.Target.Value.Center;
                    break;
                case CommandType.LOAD:
                    s.Loaded.Add(cmd.PackageId); s.PackageStatus[cmd.PackageId] = PackageStatus.Loaded;
                    break;
                case CommandType.DELIVER:
                    s.Loaded.Remove(cmd.PackageId); s.PackageStatus[cmd.PackageId] = PackageStatus.Delivered;
                    break;
                case CommandType.CHARGE:
                    s.IsCharging = true;
                    break;
                // WAIT, SET_SPEED: no state-level effect
            }
        }
    }
}