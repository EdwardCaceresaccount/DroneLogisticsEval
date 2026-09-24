using System;
using NUnit.Framework;
using SimCore.Commands;
using SimCore.Config;
using SimCore.Domain;
using SimCore.Engine;
using SimCore.Eval;
using SimCore.Events;
using SimCore.Observation;
using SimCore.Planning;
using SimCore.State;

namespace SimCore.Tests
{
    public class RecoveryTests
    {
        private static EpisodeRunner MakeRunner(IPlanner planner, Action<ScenarioConfig> tweak = null)
        {
            var cfg = TestFixtures.Config40();
            tweak?.Invoke(cfg);
            var world = ScenarioLoader.Build(cfg);
            var log = new EventLog();
            var engine = new SimulationEngine(world, cfg, log);
            var episode = new EpisodeConfig { ScenarioId = cfg.ScenarioId, PlannerId = planner.Id };
            return new EpisodeRunner(engine, planner, new NullObservationCompiler(), episode, log);
        }

        // Deliver P001 at (9,7) [5.8 tiles], then attempt (30,30) [31 tiles] — impossible on the remaining ~84%.
        private static Plan DeliverThenOverreach() => new(new[]
        {
            Command.Load("P001"), Command.LiftOff(), Command.MoveTo(9, 7), Command.Deliver("P001"),
            Command.MoveTo(30, 30), Command.Land()
        });

        // ---------------- Reachability math ----------------

        [Test]
        public void Reachability_36_Tiles_At_Normal_Costs_Exactly_Full_Battery()
        {
            var (e, _) = TestFixtures.Engine40();
            double req = Reachability.BatteryRequiredPct(e, new GridCoord(4, 4).Center, new GridCoord(4, 40).Center, SpeedMode.Normal);
            Assert.AreEqual(100.0, req, 1e-9);
        }

        [Test]
        public void Reachability_Obstructed_Direct_Path_Is_Unreachable()
        {
            var (e, _) = TestFixtures.Engine40();
            bool blocked = Reachability.CanReachDirect(e, new GridCoord(4, 20).Center, new GridCoord(16, 20), SpeedMode.Normal, 100, 5, out _);
            bool clear   = Reachability.CanReachDirect(e, new GridCoord(4, 19).Center, new GridCoord(16, 19), SpeedMode.Normal, 100, 5, out _);
            Assert.IsFalse(blocked, "Building (10,20) sits on the y=20 line.");
            Assert.IsTrue(clear, "y=19 passes a full tile below it.");
        }

        [Test]
        public void Reachability_Nearest_Charge_Tile_Prefers_Cheapest_Clear_Path()
        {
            var (e, _) = TestFixtures.Engine40();
            double req = Reachability.RequiredToNearestChargeTile(e, new GridCoord(12, 4).Center, SpeedMode.Slow, out var tile);
            Assert.AreEqual(new GridCoord(4, 4), tile, "Facility (4,4) is 8 tiles away; charger (20,4) is also 8 — Facility scans first, ties are deterministic.");
            Assert.AreEqual(8.0 / 43.2 * 100.0, req, 0.01);
        }

        // ---------------- LBS detection + recovery (D14/D15) ----------------

        [Test]
        public void LBS_AutoRecovery_Rescues_A_Delivered_But_Overreaching_Trip()
        {
            var r = MakeRunner(ScriptedPlanner.FromPlans(DeliverThenOverreach()));
            r.StartTrip();
            r.RunTripToEnd();

            var t = r.Trips[0];
            Assert.IsTrue(t.LowBatteryOccurred);
            Assert.IsTrue(t.EmergencyRecovery);
            Assert.AreEqual(1, t.PackagesDelivered);
            Assert.AreEqual(new GridCoord(4, 4), t.RecoveryTargetTile, "Facility (4,4) is the nearest reachable charge tile from (9,7).");
            Assert.AreEqual(new GridCoord(4, 4), t.EndTile);
            Assert.AreEqual(TripClassification.DeliveryWithEmergencyCharge, t.Classification);
            Assert.AreEqual(2, t.SeverityLevel);
            Assert.AreEqual(ExecutorStatus.Aborted, t.ExecutorStatus);
            Assert.AreEqual(3, t.RecoveryCommandsDispatched);
            Assert.AreEqual("(30,30)", t.LbsSegmentTarget);

            Assert.AreEqual(1, r.Log.Count(LowBatteryEventTypes.LowBattery), "LBS latches: one event per trip.");
            Assert.AreEqual(1, r.Log.Count(LowBatteryEventTypes.RecoveryStarted));
            Assert.AreEqual(1, r.Log.Count(ExecutorEventTypes.PlanAborted));
            Assert.AreEqual("LOW_BATTERY", (string)r.Log.Last(EpisodeEventTypes.DecisionMoment).Data["type"]);
            Assert.IsTrue(r.CanStartTrip, "Rescued drone is at a base; the episode continues.");
            Assert.IsFalse(r.Engine.IsTerminal);
        }

        [Test]
        public void LBS_With_AutoRecovery_Disabled_Ends_In_Forced_Landing()
        {
            var r = MakeRunner(ScriptedPlanner.FromPlans(DeliverThenOverreach()), cfg => cfg.Recovery.AutoRecoveryEnabled = false);
            r.RunEpisode();

            var t = r.Trips[0];
            Assert.IsTrue(t.LowBatteryOccurred);
            Assert.IsFalse(t.EmergencyRecovery);
            Assert.AreEqual(FailureKind.ForcedLanding, t.Failure);
            Assert.AreEqual(TripClassification.ForcedLanding, t.Classification);
            Assert.AreEqual(3, t.SeverityLevel);
            Assert.AreEqual(1, r.Log.Count(LowBatteryEventTypes.LowBatteryNoAutoRecovery));
            Assert.AreEqual(0, r.Log.Count(LowBatteryEventTypes.RecoveryStarted));
            Assert.AreEqual(EpisodeStatus.CompletedFailure, r.World.Status);
            Assert.AreEqual("FORCED_LANDING", r.TerminationReason);
        }

        [Test]
        public void Planned_Charger_Hop_Is_Not_An_Emergency()
        {
            var hop = new Plan(new[]
            {
                Command.Load("P002"), Command.LiftOff(), Command.SetSpeed(SpeedMode.Slow), Command.MoveTo(20, 4), Command.Land()
            });
            var r = MakeRunner(ScriptedPlanner.FromPlans(hop));
            r.StartTrip(); r.RunTripToEnd();

            var t = r.Trips[0];
            Assert.IsFalse(t.LowBatteryOccurred);
            Assert.IsFalse(t.EmergencyRecovery);
            Assert.AreEqual(TileType.ChargingStation, t.EndTileType);
            Assert.AreEqual(TripClassification.NoDeliverySafeReturn, t.Classification);
            Assert.AreEqual(1, t.SeverityLevel);
            Assert.AreEqual(0, r.Log.Count(LowBatteryEventTypes.LowBattery));
        }

        [Test]
        public void Unrecoverable_LBS_Is_Logged_And_Plan_Continues_To_Consequence()
        {
            // Hover at the charger-adjacent tile (30,21) — one tile from charger (30,20), so the flight out is
            // feasible and no LBS fires on the way. A guard tile ensures we actually reach the WAIT.
            // Then inject a battery too low to reach ANY charge tile: recovery is judged impossible,
            // the plan is left alone, and physics finishes the story.
            var plan = new Plan(new[]
            {
                Command.LiftOff(), Command.SetSpeed(SpeedMode.Slow), Command.MoveTo(30, 21),
                Command.Wait(1.0), Command.MoveTo(30, 20), Command.Land()
            });
            var r = MakeRunner(ScriptedPlanner.FromPlans(plan));
            r.StartTrip();

            int guard = 20000;
            while (r.IsExecuting && r.Engine.Activity.Kind != ActivityKind.Waiting && guard-- > 0) r.Tick();
            Assert.IsTrue(r.IsExecuting, "Trip ended before reaching the hover point — the outbound flight must be feasible.");
            Assert.AreEqual(ActivityKind.Waiting, r.Engine.Activity.Kind);

            r.Engine.World.Drone.BatteryPct = 0.5;      // state injection: below the reserve for even a 1-tile hop
            r.RunTripToEnd();

            var t = r.Trips[0];
            Assert.IsTrue(t.LowBatteryOccurred);
            Assert.IsFalse(t.EmergencyRecovery);
            Assert.AreEqual(1, r.Log.Count(LowBatteryEventTypes.LowBatteryUnrecoverable));
            Assert.AreEqual(0, r.Log.Count(LowBatteryEventTypes.RecoveryStarted));
            Assert.AreEqual(FailureKind.ForcedLanding, t.Failure);
            Assert.AreEqual("simulator_no_reachable_charger", (string)r.Log.Last(EpisodeEventTypes.DecisionMoment).Data["decider"]);
        }

        [Test]
        public void Recovered_Drone_Can_Charge_And_Continue_Next_Trip()
        {
            var next = new Plan(new[]
            {
                Command.Charge(100), Command.Load("P003"), Command.LiftOff(), Command.MoveTo(15, 15),
                Command.Deliver("P003"), Command.MoveTo(4, 4), Command.Land()
            });
            var r = MakeRunner(ScriptedPlanner.FromPlans(DeliverThenOverreach(), next));
            r.StartTrip(); r.RunTripToEnd();
            r.StartTrip(); r.RunTripToEnd();

            Assert.AreEqual(2, r.Trips.Count);
            Assert.AreEqual(TripClassification.DeliveryWithEmergencyCharge, r.Trips[0].Classification);
            Assert.AreEqual(TripClassification.StrongSuccess, r.Trips[1].Classification);
            Assert.IsFalse(r.Trips[1].LowBatteryOccurred, "LBS state must reset between trips.");
            Assert.AreEqual(2, r.World.DeliveredCount());
        }
    }
}