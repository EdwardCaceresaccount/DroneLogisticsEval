using System;
using System.Collections.Generic;
using System.Diagnostics;
using SimCore.Commands;
using SimCore.Config;
using SimCore.Domain;
using SimCore.Eval;
using SimCore.Events;
using SimCore.Observation;
using SimCore.Planning;
using SimCore.State;
using SimCore.Util;

namespace SimCore.Engine
{
    public sealed class EpisodeRunner
    {
        public SimulationEngine Engine { get; }
        public WorldState World => Engine.World;
        public ScenarioConfig Scenario => Engine.Config;
        public EpisodeConfig Episode { get; }
        public IPlanner Planner { get; }
        public EventLog Log { get; }
        public IReadOnlyList<TripOutcome> Trips => _trips;
        public TripOutcome CurrentTrip => _current;
        public string TerminationReason { get; private set; }

        private readonly IObservationCompiler _compiler;
        private readonly PlanValidator _validator;
        private readonly List<TripOutcome> _trips = new();
        private PlanExecutor _executor;           // the planner's plan
        private PlanExecutor _recoveryExecutor;   // simulator-injected recovery plan (D15)
        private PlanExecutor Active => _recoveryExecutor ?? _executor;
        private TripOutcome _current;
        private TripOutcome _lastCompleted;
        private WorldPos _lastPos;
        private double _lastBattery;
        private int _deliveredAtStart, _loadedAtStart;

        public EpisodeRunner(SimulationEngine engine, IPlanner planner, IObservationCompiler compiler,
                             EpisodeConfig episode, EventLog log)
        {
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            Planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
            Episode = episode ?? throw new ArgumentNullException(nameof(episode));
            Log = log ?? throw new ArgumentNullException(nameof(log));
            _validator = new PlanValidator(World, Scenario);

            Log.Header = new EpisodeHeader
            {
                RunId = Episode.EffectiveRunId(Planner.Id),
                ScenarioId = Scenario.ScenarioId,
                ScenarioVersion = Scenario.ScenarioVersion,
                Seed = Scenario.Seed,
                TickSeconds = Scenario.TickSeconds,
                PlannerId = Planner.Id,
                PlannerVersion = Planner.Version,
                ObservationMode = Episode.ObservationMode.ToString(),
                GuidanceTier = Episode.Guidance.ToString(),
                SimulationVersion = HarnessVersions.Simulation,
                EvaluationVersion = HarnessVersions.Evaluation,
                ToolContract = HarnessVersions.ToolContract
            };
            Emit(EpisodeEventTypes.EpisodeStarted, ("run_id", Log.Header.RunId), ("packages_total", World.Packages.Count),
                 ("max_trips", Scenario.Mission.MaxTrips), ("max_sim_seconds", Scenario.Mission.MaxSimSeconds),
                 ("auto_recovery_enabled", Scenario.Recovery.AutoRecoveryEnabled));
        }

        public bool IsRunning => World.Status == EpisodeStatus.Running;
        public bool IsExecuting => World.Phase == EpisodePhase.Executing;
        public bool CanStartTrip => IsRunning && World.Phase == EpisodePhase.AwaitingThinking;

        // ================================ Thinking Phase ================================

        public void StartTrip()
        {
            if (!CanStartTrip) throw new SimInvariantException($"StartTrip: status={World.Status}, phase={World.Phase}.");
            var d = World.Drone;
            if (d.Flight != FlightStatus.Landed || !d.LandedTile.HasValue || !World.Map.IsChargeTile(d.LandedTile.Value))
                throw new SimInvariantException("StartTrip: drone must be landed on a Facility or Charging Station.");

            int tripNumber = World.TripIndex + 1;
            World.Phase = EpisodePhase.Thinking;
            d.InLowBatteryState = false;

            _current = new TripOutcome
            {
                TripNumber = tripNumber,
                StartTile = d.LandedTile.Value,
                StartTileType = World.Map.TypeAt(d.LandedTile.Value),
                BatteryStart = d.BatteryPct,
                SimTimeStart = World.Clock.TimeSeconds
            };
            _lastPos = d.Position;
            _lastBattery = d.BatteryPct;
            _deliveredAtStart = World.DeliveredCount();
            _loadedAtStart = d.LoadedPackageIds.Count;

            Emit(EpisodeEventTypes.TripStarted, ("trip_number", tripNumber), ("start_tile", _current.StartTile.ToString()),
                 ("start_tile_type", _current.StartTileType.ToString()), ("battery_pct", d.BatteryPct),
                 ("packages_onboard", _loadedAtStart), ("packages_delivered_so_far", _deliveredAtStart));
            Emit(EpisodeEventTypes.ThinkingStarted, ("trip_number", tripNumber));

            Plan plan = null;
            ValidationResult errors = null;

            for (int attempt = 1; attempt <= Math.Max(1, Episode.MaxPlanAttempts); attempt++)
            {
                var observation = _compiler.Compile(World, Engine, Episode.ObservationMode);
                var request = new PlanRequest(observation, tripNumber, attempt, errors, _lastCompleted, Episode.Guidance);

                Emit(EpisodeEventTypes.DecisionMoment, ("type", "PLAN_TRIP"), ("trip_number", tripNumber), ("attempt", attempt),
                     ("decider", "planner"), ("observation_mode", observation.Mode.ToString()), ("guidance_tier", Episode.Guidance.ToString()));

                var sw = Stopwatch.StartNew();
                PlanResponse response;
                try { response = Planner.Plan(request); }
                catch (Exception ex)
                {
                    var err = new ValidationResult();
                    err.Add(ErrorCategory.Schema, "PLANNER_EXCEPTION", ex.Message);
                    response = PlanResponse.Failed(err);
                }
                sw.Stop();

                _current.PlanAttempts = attempt;
                _current.PlannerLatencySeconds += sw.Elapsed.TotalSeconds;
                _current.InputTokens += response.InputTokens;
                _current.OutputTokens += response.OutputTokens;

                errors = response.Errors;
                if (response.Plan != null && errors.IsValid)
                    errors = _validator.ValidatePlan(response.Plan, Engine.IsCharging);

                Emit(EpisodeEventTypes.PlanProposed, ("attempt", attempt), ("command_count", response.Plan?.Commands.Count ?? 0),
                     ("latency_seconds", sw.Elapsed.TotalSeconds), ("valid", errors.IsValid));

                if (response.Plan != null && errors.IsValid) { plan = response.Plan; break; }

                _current.PlanRejections++;
                Emit(EpisodeEventTypes.PlanRejected, ("attempt", attempt), ("error_count", errors.Errors.Count),
                     ("codes", string.Join(",", ErrorCodesOf(errors))), ("categories", string.Join(",", CategoriesOf(errors))));
            }

            Emit(EpisodeEventTypes.ThinkingEnded, ("trip_number", tripNumber), ("plan_accepted", plan != null),
                 ("attempts", _current.PlanAttempts), ("planner_latency_seconds", _current.PlannerLatencySeconds));

            if (plan == null)
            {
                _current.InvalidPlanner = true;
                FinishTrip();
                return;
            }

            _current.CommandsTotal = plan.Commands.Count;
            _current.Decisions = PlanDecisionSummary.FromPlan(plan, World);
            var accepted = new Dictionary<string, object> { ["plan"] = plan.ToString(), ["command_count"] = plan.Commands.Count };
            foreach (var kv in _current.Decisions.ToLogData()) accepted[kv.Key] = kv.Value;
            Log.Emit(new SimEvent(World.Clock.Ticks, World.Clock.TimeSeconds, EpisodeEventTypes.PlanAccepted, accepted));

            _executor = new PlanExecutor(Engine, plan, Log);
            _recoveryExecutor = null;
            World.Phase = EpisodePhase.Executing;
            Emit(EpisodeEventTypes.ExecutionStarted, ("trip_number", tripNumber));
        }

        // ================================ Execution Phase ================================

        public void Tick()
        {
            if (!IsExecuting) throw new SimInvariantException($"Tick: phase is {World.Phase}, not Executing.");

            Active.Tick();

            var d = World.Drone;
            _current.DistanceTraveled += _lastPos.DistanceTo(d.Position);
            double dBattery = d.BatteryPct - _lastBattery;
            if (dBattery < 0) _current.BatteryConsumed -= dBattery;
            if (Engine.IsCharging) _current.ChargingSeconds += World.Clock.TickSeconds;
            _lastPos = d.Position;
            _lastBattery = d.BatteryPct;
            if (d.Flight == FlightStatus.Flying) _current.LiftedOff = true;

            if (Active.IsDone) { FinishTrip(); return; }

            if (World.Clock.TimeSeconds >= Scenario.Mission.MaxSimSeconds)
            {
                _current.TimeLimitExceeded = true;
                Emit(EpisodeEventTypes.TimeLimitExceeded, ("sim_time", World.Clock.TimeSeconds), ("limit", Scenario.Mission.MaxSimSeconds));
                FinishTrip();
                return;
            }

            if (_recoveryExecutor == null) CheckLowBattery();
        }

        /// <summary>D14 detect → D15 act. Runs once per trip: LBS latches until landing.</summary>
        private void CheckLowBattery()
        {
            var d = World.Drone;
            if (d.Flight != FlightStatus.Flying || d.InLowBatteryState) return;

            var a = LowBatteryMonitor.Assess(Engine);
            if (!a.IsLowBattery) return;

            d.InLowBatteryState = true;
            _current.LowBatteryOccurred = true;
            _current.LbsTriggerTimeSeconds = World.Clock.TimeSeconds;
            _current.LbsBatteryPct = d.BatteryPct;
            _current.LbsSegmentTarget = a.SegmentTarget?.ToString();

            double afterLog = double.IsInfinity(a.AfterSegmentRequiredPct) ? -1 : a.AfterSegmentRequiredPct;
            Emit(LowBatteryEventTypes.LowBattery, ("battery_pct", d.BatteryPct), ("position", d.Position.ToString()),
                 ("segment_target", a.SegmentTarget?.ToString() ?? ""), ("segment_required_pct", a.SegmentRequiredPct),
                 ("after_segment_required_pct", afterLog), ("trigger_margin_pct", a.TriggerMarginPct),
                 ("recovery_tile", a.RecoveryTile?.ToString() ?? ""), ("recovery_required_pct", a.RecoveryRequiredPct));

            bool auto = Scenario.Recovery.AutoRecoveryEnabled;
            string decider;

            if (auto && a.RecoveryTile.HasValue)
            {
                _current.CommandsDispatched = _executor.NextCommandIndex;
                _current.CommandsRemainingAtAbort = _executor.CommandsRemaining;
                _executor.Abort("LOW_BATTERY_AUTO_RECOVERY");
                Engine.AbortActivity("LOW_BATTERY_AUTO_RECOVERY");

                var tile = a.RecoveryTile.Value;
                var recovery = new Plan(new[] { Command.SetSpeed(SpeedMode.Slow), Command.MoveTo(tile), Command.Land() }, "simulator-recovery");
                _recoveryExecutor = new PlanExecutor(Engine, recovery, Log, alreadyAirborne: true);
                _current.EmergencyRecovery = true;
                _current.RecoveryTargetTile = tile;
                decider = "simulator_auto_recovery";
                Emit(LowBatteryEventTypes.RecoveryStarted, ("target_tile", tile.ToString()),
                     ("target_tile_type", World.Map.TypeAt(tile).ToString()), ("required_pct", a.RecoveryRequiredPct),
                     ("battery_pct", d.BatteryPct), ("plan", recovery.ToString()));
            }
            else if (auto)
            {
                decider = "simulator_no_reachable_charger";
                Emit(LowBatteryEventTypes.LowBatteryUnrecoverable, ("battery_pct", d.BatteryPct), ("position", d.Position.ToString()));
            }
            else
            {
                decider = "auto_recovery_disabled_plan_continues";
                Emit(LowBatteryEventTypes.LowBatteryNoAutoRecovery, ("battery_pct", d.BatteryPct), ("position", d.Position.ToString()));
            }

            Emit(EpisodeEventTypes.DecisionMoment, ("type", "LOW_BATTERY"), ("trip_number", _current.TripNumber),
                 ("decider", decider), ("battery_pct", d.BatteryPct), ("recovery_tile", a.RecoveryTile?.ToString() ?? ""));
        }

        public void RunTripToEnd()
        {
            long guard = (long)(Scenario.Mission.MaxSimSeconds / Scenario.TickSeconds) + 10;
            while (IsExecuting && guard-- > 0) Tick();
            if (IsExecuting) throw new SimInvariantException("RunTripToEnd: tick guard exhausted — time limit did not fire.");
        }

        public void RunEpisode()
        {
            while (IsRunning)
            {
                StartTrip();
                if (IsExecuting) RunTripToEnd();
            }
        }

        // ================================ Trip end / termination ================================

        private void FinishTrip()
        {
            var d = World.Drone;
            var t = _current;

            t.SimTimeEnd = World.Clock.TimeSeconds;
            t.BatteryEnd = d.BatteryPct;
            t.Failure = d.Failure;
            t.EndedAirborne = d.Flight == FlightStatus.Flying;
            t.EndTile = d.LandedTile;
            t.EndTileType = d.LandedTile.HasValue ? World.Map.TypeAt(d.LandedTile.Value) : (TileType?)null;
            t.EndedAtBase = d.Flight == FlightStatus.Landed && d.LandedTile.HasValue && World.Map.IsChargeTile(d.LandedTile.Value)
                            && d.Failure == FailureKind.None;
            t.PackagesDelivered = World.DeliveredCount() - _deliveredAtStart;
            t.PackagesLoaded = Math.Max(0, d.LoadedPackageIds.Count + t.PackagesDelivered - _loadedAtStart);

            if (_executor != null)
            {
                t.ExecutorStatus = _executor.Status;
                t.CommandsDispatched = _executor.NextCommandIndex;
                t.CommandsTruncated = _executor.CommandsTruncated;
                t.HaltErrorCode = _executor.HaltError?.Code;
            }
            if (_recoveryExecutor != null)
                t.RecoveryCommandsDispatched = _recoveryExecutor.NextCommandIndex;

            TripClassifier.Classify(t);
            _trips.Add(t);
            _lastCompleted = t;
            World.TripIndex++;
            _executor = null;
            _recoveryExecutor = null;

            Log.Emit(new SimEvent(World.Clock.Ticks, World.Clock.TimeSeconds, EpisodeEventTypes.TripEnded, t.ToLogData()));

            EvaluateTermination(t);
            World.Phase = IsRunning ? EpisodePhase.AwaitingThinking : EpisodePhase.Complete;
        }

        private void EvaluateTermination(TripOutcome t)
        {
            string reason = null;
            EpisodeStatus status = EpisodeStatus.Running;

            if (t.Classification == TripClassification.InvalidPlannerBehavior) { status = EpisodeStatus.Terminated; reason = "INVALID_PLANNER_BEHAVIOR"; }
            else if (t.Failure == FailureKind.Crashed)                          { status = EpisodeStatus.CompletedFailure; reason = "CRASH"; }
            else if (t.Failure == FailureKind.ForcedLanding)                    { status = EpisodeStatus.CompletedFailure; reason = "FORCED_LANDING"; }
            else if (t.TimeLimitExceeded)                                       { status = EpisodeStatus.CompletedFailure; reason = "TIME_LIMIT"; }
            else if (t.EndedAirborne || !t.EndedAtBase)                         { status = EpisodeStatus.CompletedFailure; reason = "NOT_AT_BASE"; }
            else if (Scenario.Mission.RequireAllPackagesDelivered && World.DeliveredCount() >= World.Packages.Count)
                                                                                { status = EpisodeStatus.CompletedSuccess; reason = "ALL_DELIVERED"; }
            else if (World.TripIndex >= Scenario.Mission.MaxTrips)              { status = EpisodeStatus.CompletedFailure; reason = "MAX_TRIPS"; }

            if (status == EpisodeStatus.Running) return;

            World.Status = status;
            TerminationReason = reason;
            Emit(EpisodeEventTypes.EpisodeEnded, ("status", status.ToString()), ("reason", reason),
                 ("trips", World.TripIndex), ("packages_delivered", World.DeliveredCount()), ("packages_total", World.Packages.Count),
                 ("sim_time", World.Clock.TimeSeconds), ("battery_end", World.Drone.BatteryPct));
        }

        private static IEnumerable<string> ErrorCodesOf(ValidationResult r)
        { foreach (var e in r.Errors) yield return e.Code; }

        private static IEnumerable<string> CategoriesOf(ValidationResult r)
        {
            var seen = new HashSet<string>();
            foreach (var e in r.Errors) if (seen.Add(e.Category.ToString())) yield return e.Category.ToString();
        }

        private void Emit(string type, params (string key, object value)[] data)
        {
            var dict = new Dictionary<string, object>(data.Length);
            foreach (var (k, v) in data) dict[k] = v;
            Log.Emit(new SimEvent(World.Clock.Ticks, World.Clock.TimeSeconds, type, dict));
        }
    }
}