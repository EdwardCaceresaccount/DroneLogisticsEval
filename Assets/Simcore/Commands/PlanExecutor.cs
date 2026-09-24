using System.Collections.Generic;
using SimCore.Engine;
using SimCore.Events;
using SimCore.State;

namespace SimCore.Commands
{
    public enum ExecutorStatus
    {
        Running,
        /// <summary>Every command dispatched, engine idle, no charge pending.</summary>
        PlanComplete,
        /// <summary>D11: drone landed on a Facility/Charging Station after flying. Trip boundary; remaining commands discarded.</summary>
        LandedAtBase,
        /// <summary>A command was illegal against the LIVE state at dispatch time (reality diverged from the plan).</summary>
        HaltedOnError,
        /// <summary>Engine hit a terminal failure (crash / forced landing) mid-plan.</summary>
        HaltedTerminal
    }

    public static class ExecutorEventTypes
    {
        public const string CommandDispatched      = "COMMAND_DISPATCHED";
        public const string ExecutionError         = "EXECUTION_ERROR";
        public const string PlanCompleted          = "PLAN_COMPLETED";
        public const string PlanHaltedTerminal     = "PLAN_HALTED_TERMINAL";
        public const string PlanTruncatedAtLanding = "PLAN_TRUNCATED_AT_LANDING";
    }

    /// <summary>
    /// Feeds one validated Plan into engine primitives, one tick at a time.
    /// Per tick: dispatch as many commands as the state allows (instantaneous commands chain;
    /// a blocking command stops dispatch until the engine is idle again), then advance the engine.
    /// D10 sync rule lives here: LIFT_OFF is not dispatched while a charge is in progress.
    /// D11 lives here too: LAND on a base tile after flight ends the plan immediately.
    /// </summary>
    public sealed class PlanExecutor
    {
        public SimulationEngine Engine { get; }
        public Plan Plan { get; }
        public ExecutorStatus Status { get; private set; } = ExecutorStatus.Running;
        public ValidationError HaltError { get; private set; }
        public int NextCommandIndex { get; private set; }
        public int CommandsTruncated => Status == ExecutorStatus.LandedAtBase ? Plan.Commands.Count - NextCommandIndex : 0;

        private readonly PlanValidator _validator;
        private readonly ISimEventSink _events;
        private bool _hasLiftedOff;

        public PlanExecutor(SimulationEngine engine, Plan plan, ISimEventSink events)
        {
            Engine = engine;
            Plan = plan;
            _events = events;
            _validator = new PlanValidator(engine.World, engine.Config);
        }

        public bool IsDone => Status != ExecutorStatus.Running;

        public void Tick()
        {
            if (IsDone) throw new SimInvariantException($"Executor.Tick called while {Status}.");

            Dispatch();
            if (Status == ExecutorStatus.HaltedOnError || Status == ExecutorStatus.LandedAtBase) return;

            Engine.Tick();

            if (Engine.IsTerminal)
            {
                Status = ExecutorStatus.HaltedTerminal;
                Emit(ExecutorEventTypes.PlanHaltedTerminal, ("failure", Engine.World.Drone.Failure.ToString()),
                     ("commands_dispatched", NextCommandIndex), ("commands_total", Plan.Commands.Count));
                return;
            }

            if (NextCommandIndex >= Plan.Commands.Count && !Engine.IsBusy && !Engine.IsCharging)
            {
                Status = ExecutorStatus.PlanComplete;
                Emit(ExecutorEventTypes.PlanCompleted, ("commands_total", Plan.Commands.Count));
            }
        }

        private void Dispatch()
        {
            while (NextCommandIndex < Plan.Commands.Count && !Engine.IsBusy)
            {
                var cmd = Plan.Commands[NextCommandIndex];

                if (cmd.Type == CommandType.LIFT_OFF && Engine.IsCharging)
                    return; // D10: hold the plan until the charge target is reached

                var live = ProjectedState.FromWorld(Engine.World, Engine.IsCharging);
                var errs = _validator.CheckCommand(cmd, live, NextCommandIndex);
                if (errs.Count > 0)
                {
                    HaltError = errs[0];
                    Status = ExecutorStatus.HaltedOnError;
                    Emit(ExecutorEventTypes.ExecutionError, ("command_index", NextCommandIndex), ("command", cmd.ToString()),
                         ("category", HaltError.Category.ToString()), ("code", HaltError.Code), ("message", HaltError.Message));
                    return;
                }

                Execute(cmd);
                Emit(ExecutorEventTypes.CommandDispatched, ("command_index", NextCommandIndex), ("command", cmd.ToString()));
                NextCommandIndex++;

                if (cmd.Type == CommandType.LIFT_OFF) _hasLiftedOff = true;

                if (cmd.Type == CommandType.LAND && _hasLiftedOff)
                {
                    var tile = Engine.World.Drone.LandedTile;
                    if (tile.HasValue && Engine.World.Map.IsChargeTile(tile.Value))
                    {
                        Status = ExecutorStatus.LandedAtBase;
                        if (CommandsTruncated > 0)
                            Emit(ExecutorEventTypes.PlanTruncatedAtLanding, ("tile", tile.Value.ToString()),
                                 ("tile_type", Engine.World.Map.TypeAt(tile.Value).ToString()),
                                 ("commands_truncated", CommandsTruncated));
                        return;
                    }
                }
            }
        }

        private void Execute(Command cmd)
        {
            switch (cmd.Type)
            {
                case CommandType.LIFT_OFF:  Engine.LiftOff(); break;
                case CommandType.LAND:      Engine.Land(); break;
                case CommandType.MOVE_TO:   Engine.BeginMoveTo(cmd.Target.Value.Center); break;
                case CommandType.LOAD:      Engine.BeginLoad(cmd.PackageId); break;
                case CommandType.DELIVER:   Engine.Deliver(cmd.PackageId); break;
                case CommandType.CHARGE:    Engine.BeginCharge(cmd.Value.Value); break;
                case CommandType.WAIT:      Engine.BeginWait(cmd.Value.Value); break;
                case CommandType.SET_SPEED: Engine.SetSpeed(cmd.Speed.Value); break;
            }
        }

        private void Emit(string type, params (string key, object value)[] data)
        {
            var dict = new Dictionary<string, object>(data.Length);
            foreach (var (k, v) in data) dict[k] = v;
            _events.Emit(new SimEvent(Engine.World.Clock.Ticks, Engine.World.Clock.TimeSeconds, type, dict));
        }
    }
}