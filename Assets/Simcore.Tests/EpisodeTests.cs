using System;
using Newtonsoft.Json.Linq;
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
    public class EpisodeTests
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

        // Trip 1: F(4,4) → H(9,7) → H(15,15) → back to F(4,4). 31.4 tiles at Normal (range 36).
        private static Plan Trip1() => new(new[]
        {
            Command.Charge(100), Command.Load("P001"), Command.Load("P003"), Command.LiftOff(),
            Command.MoveTo(9, 7), Command.Deliver("P001"), Command.MoveTo(15, 15), Command.Deliver("P003"),
            Command.MoveTo(4, 4), Command.Land()
        });

        // Trip 2: stage P002 at charger (20,4) with nothing delivered — a strategic waypoint hop.
        private static Plan Trip2() => new(new[]
        {
            Command.Charge(100), Command.Load("P002"), Command.LiftOff(), Command.SetSpeed(SpeedMode.Slow),
            Command.MoveTo(20, 4), Command.Land()
        });

        // Trip 3: charger (20,4) → H(30,30) → charger (30,20). 37.9 tiles at Slow (range 43.2).
        private static Plan Trip3() => new(new[]
        {
            Command.Charge(100), Command.LiftOff(), Command.MoveTo(30, 30), Command.Deliver("P002"),
            Command.MoveTo(30, 20), Command.Land()
        });

        private const string ValidHopJson =
            @"{""tool_calls"":[{""tool"":""LIFT_OFF""},{""tool"":""MOVE_TO"",""args"":{""x"":20,""y"":4}},{""tool"":""LAND""}]}";

        [Test]
        public void Speed_Range_Ordering_Matches_Spec()
        {
            var (e, _) = TestFixtures.Engine40();
            double slow = e.RangeTilesAtSpeed(SpeedMode.Slow, 100), normal = e.RangeTilesAtSpeed(SpeedMode.Normal, 100),
                   fast = e.RangeTilesAtSpeed(SpeedMode.Fast, 100);
            Assert.Greater(slow, normal, "Slow must go further per battery than Normal.");
            Assert.Greater(normal, fast, "Fast must burn more per tile than Normal.");
        }

        [Test]
        public void Three_Trip_Episode_Completes_Successfully()
        {
            var r = MakeRunner(ScriptedPlanner.FromPlans(Trip1(), Trip2(), Trip3()));
            r.RunEpisode();

            Assert.AreEqual(EpisodeStatus.CompletedSuccess, r.World.Status);
            Assert.AreEqual("ALL_DELIVERED", r.TerminationReason);
            Assert.AreEqual(3, r.Trips.Count);

            Assert.AreEqual(TripClassification.StrongSuccess, r.Trips[0].Classification);
            Assert.AreEqual(2, r.Trips[0].PackagesDelivered);
            Assert.AreEqual(TripClassification.NoDeliverySafeReturn, r.Trips[1].Classification);
            Assert.AreEqual(TileType.ChargingStation, r.Trips[1].EndTileType);
            Assert.AreEqual(new GridCoord(20, 4), r.Trips[2].StartTile);
            Assert.AreEqual(TripClassification.StrongSuccess, r.Trips[2].Classification);
            Assert.AreEqual(0, r.Trips[2].SeverityLevel);

            Assert.Greater(r.Trips[0].DistanceTraveled, 31.0);
            Assert.Less(r.Trips[0].DistanceTraveled, 32.0);
            Assert.Greater(r.Trips[1].ChargingSeconds, 4.0, "Trip 2 recharged from ~13% — must record charging time.");
            Assert.AreEqual(3, r.Log.Count(EpisodeEventTypes.TripEnded));
            Assert.AreEqual(1, r.Log.Count(EpisodeEventTypes.EpisodeEnded));
        }

        [Test]
        public void Invalid_Then_Valid_Plan_Retries_And_Proceeds_D12()
        {
            var r = MakeRunner(ScriptedPlanner.FromJson("this is not json", ValidHopJson));
            r.StartTrip();
            Assert.IsTrue(r.IsExecuting, "Second attempt was valid — trip should be executing.");
            r.RunTripToEnd();

            var t = r.Trips[0];
            Assert.AreEqual(2, t.PlanAttempts);
            Assert.AreEqual(1, t.PlanRejections);
            Assert.AreEqual(1, r.Log.Count(EpisodeEventTypes.PlanRejected));
            Assert.AreEqual(TripClassification.NoDeliverySafeReturn, t.Classification);
            StringAssert.Contains(ErrorCodes.MalformedJson, (string)r.Log.Last(EpisodeEventTypes.PlanRejected).Data["codes"]);
        }

        [Test]
        public void Exhausting_Plan_Attempts_Terminates_Episode()
        {
            var r = MakeRunner(ScriptedPlanner.FromJson("{}", "{}", "{}"));
            r.RunEpisode();

            Assert.AreEqual(1, r.Trips.Count);
            Assert.AreEqual(TripClassification.InvalidPlannerBehavior, r.Trips[0].Classification);
            Assert.AreEqual(3, r.Trips[0].PlanAttempts);
            Assert.AreEqual(EpisodeStatus.Terminated, r.World.Status);
            Assert.AreEqual("INVALID_PLANNER_BEHAVIOR", r.TerminationReason);
            Assert.AreEqual(3, r.Log.Count(EpisodeEventTypes.DecisionMoment), "One PLAN_TRIP decision moment per attempt.");
        }

        [Test]
        public void Crash_Ends_Episode_As_Failure_Severity_4()
        {
            var crash = new Plan(new[] { Command.LiftOff(), Command.MoveTo(4, 20), Command.MoveTo(16, 20), Command.Land() });
            var r = MakeRunner(ScriptedPlanner.FromPlans(crash));
            r.RunEpisode();

            Assert.AreEqual(TripClassification.Crash, r.Trips[0].Classification);
            Assert.AreEqual(4, r.Trips[0].SeverityLevel);
            Assert.AreEqual(EpisodeStatus.CompletedFailure, r.World.Status);
            Assert.AreEqual("CRASH", r.TerminationReason);
        }

        [Test]
        public void Landing_At_Base_Truncates_Remaining_Commands_D11()
        {
            var plan = new Plan(new[]
            {
                Command.LiftOff(), Command.MoveTo(20, 4), Command.Land(),
                Command.Charge(100), Command.LiftOff(), Command.MoveTo(4, 4), Command.Land()   // discarded
            });
            var r = MakeRunner(ScriptedPlanner.FromPlans(plan));
            r.StartTrip();
            r.RunTripToEnd();

            var t = r.Trips[0];
            Assert.AreEqual(ExecutorStatus.LandedAtBase, t.ExecutorStatus);
            Assert.AreEqual(4, t.CommandsTruncated);
            Assert.AreEqual(new GridCoord(20, 4), t.EndTile);
            Assert.IsFalse(r.Engine.IsCharging, "The truncated CHARGE must never have started.");
            Assert.AreEqual(1, r.Log.Count(ExecutorEventTypes.PlanTruncatedAtLanding));
            Assert.IsTrue(r.CanStartTrip, "Episode continues from the charger.");
        }

        [Test]
        public void Same_Location_No_Delivery_Is_Severity_1()
        {
            var hover = new Plan(new[] { Command.LiftOff(), Command.Wait(0.5), Command.Land() });
            var r = MakeRunner(ScriptedPlanner.FromPlans(hover));
            r.StartTrip(); r.RunTripToEnd();

            Assert.AreEqual(TripClassification.SameLocationNoDelivery, r.Trips[0].Classification);
            Assert.AreEqual(1, r.Trips[0].SeverityLevel);
            Assert.IsTrue(r.Trips[0].LiftedOff);
        }

        [Test]
        public void Max_Trips_Ends_Episode_As_Failure()
        {
            var hop = new Plan(new[] { Command.LiftOff(), Command.Wait(0.2), Command.Land() });
            var r = MakeRunner(ScriptedPlanner.FromPlans(hop, hop, hop), cfg => cfg.Mission.MaxTrips = 2);
            r.RunEpisode();

            Assert.AreEqual(2, r.Trips.Count);
            Assert.AreEqual(EpisodeStatus.CompletedFailure, r.World.Status);
            Assert.AreEqual("MAX_TRIPS", r.TerminationReason);
        }

        [Test]
        public void Time_Limit_Ends_Trip_And_Episode()
        {
            var longWait = new Plan(new[] { Command.LiftOff(), Command.Wait(3.0), Command.Land() });
            var r = MakeRunner(ScriptedPlanner.FromPlans(longWait), cfg => cfg.Mission.MaxSimSeconds = 1.0);
            r.RunEpisode();

            Assert.AreEqual(TripClassification.TimeLimitExceeded, r.Trips[0].Classification);
            Assert.AreEqual("TIME_LIMIT", r.TerminationReason);
            Assert.That(r.World.Clock.TimeSeconds, Is.InRange(1.0, 1.1));
        }

        [Test]
        public void Jsonl_Export_Has_Header_And_Parseable_Events()
        {
            var r = MakeRunner(ScriptedPlanner.FromPlans(Trip1(), Trip2(), Trip3()));
            r.RunEpisode();

            var lines = r.Log.ToJsonl().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Greater(lines.Length, 30);

            var header = JObject.Parse(lines[0]);
            Assert.AreEqual("header", (string)header["record"]);
            Assert.AreEqual("Fixture40", (string)header["scenario_id"]);
            Assert.AreEqual("scripted", (string)header["planner_id"]);
            Assert.IsNotNull(header["simulation_version"]);

            for (int i = 1; i < lines.Length; i++)
            {
                var ev = JObject.Parse(lines[i]);
                Assert.AreEqual("event", (string)ev["record"], $"line {i}");
                Assert.IsNotNull(ev["type"], $"line {i}");
                Assert.IsNotNull(ev["tick"], $"line {i}");
            }

            var last = JObject.Parse(lines[lines.Length - 1]);
            Assert.AreEqual(EpisodeEventTypes.EpisodeEnded, (string)last["type"]);
            Assert.AreEqual("CompletedSuccess", (string)last["data"]["status"]);
        }
    }
}