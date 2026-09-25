using System;
using System.Linq;
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
using SimCore.Util;

namespace SimCore.Tests
{
    public class ErrorInjectionTests
    {
        private static Plan Sample() => new(new[]
        {
            Command.Charge(100), Command.Load("P001"), Command.LiftOff(), Command.SetSpeed(SpeedMode.Slow),
            Command.MoveTo(9, 7), Command.Deliver("P001"), Command.Wait(0.5), Command.MoveTo(4, 4), Command.Land()
        });

        private static EpisodeRunner MakeRunner(IPlanner planner)
        {
            var cfg = TestFixtures.Config40();
            var world = ScenarioLoader.Build(cfg);
            var log = new EventLog();
            var engine = new SimulationEngine(world, cfg, log);
            var episode = new EpisodeConfig { ScenarioId = cfg.ScenarioId, PlannerId = planner.Id };
            return new EpisodeRunner(engine, planner, new ObservationCompiler(), episode, log);
        }

        private static PlanRequest DummyRequest(int attempt = 1)
            => new(new EmptyObservation(ObservationMode.Processed), 1, attempt, null, null, GuidanceTier.None);

        [Test]
        public void Serializer_Round_Trips_Every_Command_Type()
        {
            var plan = Sample();
            string json = PlanSerializer.ToToolCallJson(plan);
            Assert.IsTrue(ToolCallParser.TryParse(json, "rt", out var back, out var errors), errors.ToString());
            Assert.AreEqual(plan.ToString(), back.ToString());
        }

        [TestCase(InjectionKind.MalformedJson, ErrorCodes.MalformedJson)]
        [TestCase(InjectionKind.UnknownTool, ErrorCodes.UnknownTool)]
        [TestCase(InjectionKind.InvalidArgument, ErrorCodes.InvalidArgumentType)]
        [TestCase(InjectionKind.UnknownArgument, ErrorCodes.UnknownArgument)]
        public void Each_Kind_Produces_Its_Target_Parse_Error(InjectionKind kind, string expectedCode)
        {
            var cfg = new ErrorInjectionConfig { Rate = 1.0, Kinds = new() { kind.ToString() }, Seed = 7 };
            var wrapped = new ErrorInjectingPlanner(ScriptedPlanner.FromPlans(Sample()), cfg);
            var resp = wrapped.Plan(DummyRequest());

            Assert.IsNull(resp.Plan);
            Assert.IsTrue(resp.Errors.HasCode(expectedCode), resp.Errors.ToString());
            Assert.AreEqual(1, wrapped.Injections.Count);
            Assert.AreEqual(kind, wrapped.Injections[0].Kind);
        }

        [Test]
        public void Impossible_Command_Parses_But_Fails_Validation()
        {
            var cfg = new ErrorInjectionConfig { Rate = 1.0, Kinds = new() { "ImpossibleCommand" }, Seed = 7 };
            var wrapped = new ErrorInjectingPlanner(ScriptedPlanner.FromPlans(Sample()), cfg);
            var resp = wrapped.Plan(DummyRequest());

            Assert.IsNotNull(resp.Plan, "Out-of-bounds MOVE_TO is well-formed; the parser must accept it.");
            var (e, _) = TestFixtures.Engine40();
            var v = new PlanValidator(e.World, e.Config).ValidatePlan(resp.Plan, false);
            Assert.IsTrue(v.HasCode(ErrorCodes.OutOfBounds));
        }

        [Test]
        public void Retries_Are_Not_Injected_By_Default()
        {
            var cfg = new ErrorInjectionConfig { Rate = 1.0, Seed = 1 };
            var wrapped = new ErrorInjectingPlanner(ScriptedPlanner.FromPlans(Sample(), Sample()), cfg);
            Assert.IsNull(wrapped.Plan(DummyRequest(attempt: 1)).Plan);
            Assert.IsNotNull(wrapped.Plan(DummyRequest(attempt: 2)).Plan, "Attempt 2 must pass through untouched.");
            Assert.AreEqual(1, wrapped.Injections.Count);
        }

        [Test]
        public void Injection_Sequence_Is_Deterministic_Given_Seed()
        {
            var a = new ErrorInjectingPlanner(new GreedyPlanner(), new ErrorInjectionConfig { Rate = 1.0, Seed = 42 });
            var b = new ErrorInjectingPlanner(new GreedyPlanner(), new ErrorInjectionConfig { Rate = 1.0, Seed = 42 });
            MakeRunner(a).RunEpisode();
            MakeRunner(b).RunEpisode();

            CollectionAssert.AreEqual(a.Injections.Select(i => i.Kind).ToList(), b.Injections.Select(i => i.Kind).ToList());
            CollectionAssert.AreEqual(a.Injections.Select(i => i.Corrupted).ToList(), b.Injections.Select(i => i.Corrupted).ToList());
        }

        [Test]
        public void Greedy_Under_Full_Injection_Recovers_On_Every_Trip()
        {
            var wrapped = new ErrorInjectingPlanner(new GreedyPlanner(), new ErrorInjectionConfig { Rate = 1.0, Seed = 3 });
            var r = MakeRunner(wrapped);
            r.RunEpisode();

            Assert.AreEqual(EpisodeStatus.CompletedSuccess, r.World.Status, r.TerminationReason);
            Assert.AreEqual(3, r.Trips.Count);
            foreach (var t in r.Trips)
            {
                Assert.AreEqual(2, t.PlanAttempts, $"trip {t.TripNumber}");
                Assert.AreEqual(1, t.PlanRejections, $"trip {t.TripNumber}");
            }
            Assert.AreEqual(3, wrapped.Injections.Count);
            Assert.AreEqual(3, r.Log.Count(EpisodeEventTypes.PlanRejected));
            StringAssert.StartsWith("greedy+inject1", r.Log.Header.PlannerId);
        }

        [Test]
        public void Zero_Rate_Is_Transparent()
        {
            var plain = MakeRunner(new GreedyPlanner()); plain.RunEpisode();
            var wrapped = new ErrorInjectingPlanner(new GreedyPlanner(), new ErrorInjectionConfig { Rate = 0.0, Seed = 3 });
            var r = MakeRunner(wrapped); r.RunEpisode();

            Assert.AreEqual(0, wrapped.Injections.Count);
            Assert.AreEqual(plain.World.Clock.Ticks, r.World.Clock.Ticks);
            CollectionAssert.AreEqual(plain.Trips.Select(t => t.Classification).ToList(), r.Trips.Select(t => t.Classification).ToList());
            Assert.IsTrue(r.Trips.All(t => t.PlanRejections == 0));
        }
    }
}