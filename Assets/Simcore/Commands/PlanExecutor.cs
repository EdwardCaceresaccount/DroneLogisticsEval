using System.Collections.Generic;
using SimCore.Engine;
using SimCore.Events;
using SimCore.State;

namespace SimCore.Commands
{
    public enum ExecutorStatus
    {
        Running,
        PlanComplete,
        LandedAtBase,
        HaltedOnError,
        HaltedTerminal,
        /// <summary>Block 7: the simulator cancelled this plan (low-battery auto-recovery).</summary>
        Aborted
    }

    public static class ExecutorEventTypes
    {
        public const string CommandDispatched      = "COMMAND_DISPATCHED";
        public const string ExecutionError         = "EXECUTION_ERROR";
        public const string PlanCompleted          = "PLAN_COMPLETED";
        public const string PlanHaltedTerminal     = "PLAN_HALTED_TERMINAL";
        public const string PlanTruncatedAtLanding = "PLAN_TRUNCATED_AT_LANDING";
        public const string PlanAborted            = "PLAN_ABORTED";
    }

    public sealed class PlanExecutor
    {
        public SimulationEngine Engine { get; }
        public Plan Plan { get; }
        public ExecutorStatus Status { get; private set; } = ExecutorStatus.Running;
        public ValidationError HaltError { get; private set; }
        public int NextCommandIndex { get; private set; }
        public int CommandsRemaining => Plan.Commands.Count - NextCommandIndex;
        public int CommandsTruncated => Status == ExecutorStatus.LandedAtBase ? CommandsRemaining : 0;

        private readonly PlanValidator _validator;
        private readonly ISimEventSink _events;
        private bool _hasLiftedOff;

        /// <param name="alreadyAirborne">True for plans injected mid-flight (recovery), so a LAND at a base still ends the trip.</param>
        public PlanExecutor(SimulationEngine engine, Plan plan, ISimEventSink events, bool alreadyAirborne = false)
        {
            Engine = engine;
            Plan = plan;
            _events = events;
            _validator = new PlanValidator(engine.World, engine.Config);
            _hasLiftedOff = alreadyAirborne;
        }

        public bool IsDone => Status != ExecutorStatus.Running;

        public void Abort(string reason)
        {
            if (IsDone) throw new SimInvariantException($"Abort called while {Status}.");
            Status = ExecutorStatus.Aborted;
            Emit(ExecutorEventTypes.PlanAborted, ("reason", reason), ("commands_dispatched", NextCommandIndex),
                 ("commands_remaining", CommandsRemaining));
        }

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
                    return;

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