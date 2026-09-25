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
using SimCore.Planning.Llm;
using SimCore.State;

namespace SimCore.Tests
{
    public class LlmPipelineTests
    {
        private static (EpisodeRunner runner, LlmPlanner planner) MakeRunner(ScriptedProvider provider,
            GuidanceTier tier = GuidanceTier.Full, ObservationMode mode = ObservationMode.Processed)
        {
            var cfg = TestFixtures.Config40();
            var world = ScenarioLoader.Build(cfg);
            var log = new EventLog();
            var engine = new SimulationEngine(world, cfg, log);
            var planner = new LlmPlanner(provider, new LlmPlannerConfig { Provider = "mock", Model = "scripted" });
            var episode = new EpisodeConfig { ScenarioId = cfg.ScenarioId, PlannerId = "llm", ObservationMode = mode, Guidance = tier };
            return (new EpisodeRunner(engine, planner, new ObservationCompiler(), episode, log), planner);
        }

        private static string Trip1Json() => PlanSerializer.ToToolCallJson(new Plan(new[]
        {
            Command.Load("P001"), Command.Load("P003"), Command.LiftOff(), Command.MoveTo(9, 7), Command.Deliver("P001"),
            Command.MoveTo(15, 15), Command.Deliver("P003"), Command.MoveTo(4, 4), Command.Land()
        }));

        [Test]
        public void Fenced_Model_Output_Is_Sanitized_Parsed_And_Executed()
        {
            var provider = new ScriptedProvider().Then("Sure! Here is my plan:\n```json\n" + Trip1Json() + "\n```\nGood luck.");
            var (r, planner) = MakeRunner(provider);
            r.StartTrip(); r.RunTripToEnd();

            var t = r.Trips[0];
            Assert.AreEqual(TripClassification.StrongSuccess, t.Classification);
            Assert.AreEqual(2, t.PackagesDelivered);
            Assert.AreEqual(1, t.PlanAttempts);
            Assert.Greater(t.InputTokens, 500, "Token counts from the provider must land in the trip outcome.");
            Assert.IsTrue(planner.Exchanges[0].Sanitized, "Fences + prose → sanitized flag on.");
            StringAssert.Contains("STRATEGY GUIDANCE", planner.Exchanges[0].SystemPrompt);
            Assert.AreEqual("mock:scripted", r.Log.Header.PlannerId);
            Assert.AreEqual(LlmPlanner.PromptVersion, r.Log.Header.PlannerVersion);
        }

        [Test]
        public void Provider_Error_Is_Rejected_Then_Retry_Succeeds()
        {
            var provider = new ScriptedProvider().ThenError("HTTP 529: overloaded").Then(Trip1Json());
            var (r, planner) = MakeRunner(provider);
            r.StartTrip(); r.RunTripToEnd();

            Assert.AreEqual(2, r.Trips[0].PlanAttempts);
            Assert.AreEqual(1, r.Trips[0].PlanRejections);
            StringAssert.Contains("PROVIDER_ERROR", (string)r.Log.Last(EpisodeEventTypes.PlanRejected).Data["codes"]);
            Assert.AreEqual("HTTP 529: overloaded", planner.Exchanges[0].ProviderError);
            Assert.AreEqual(TripClassification.StrongSuccess, r.Trips[0].Classification);
        }

        [Test]
        public void Retry_Prompt_Carries_The_Rejection_Back_To_The_Model()
        {
            string bad = Trip1Json().Replace("\"MOVE_TO\"", "\"FLY_TO\"");
            var provider = new ScriptedProvider().Then(bad).Then(Trip1Json());
            var (r, planner) = MakeRunner(provider);
            r.StartTrip(); r.RunTripToEnd();

            Assert.AreEqual(2, planner.Exchanges.Count);
            StringAssert.DoesNotContain("REJECTED", planner.Exchanges[0].UserMessage);
            StringAssert.Contains("WAS REJECTED WITH", planner.Exchanges[1].UserMessage);
            StringAssert.Contains(ErrorCodes.UnknownTool, planner.Exchanges[1].UserMessage);
            StringAssert.Contains("FLY_TO", planner.Exchanges[1].UserMessage);
            CollectionAssert.Contains(planner.Exchanges[0].ParseErrorCodes, ErrorCodes.UnknownTool);
        }

        [Test]
        public void Prose_Only_Output_Exhausts_Attempts_And_Terminates()
        {
            var provider = new ScriptedProvider().Then("I cannot plan this trip.").Then("Still no.").Then("No JSON for you.");
            var (r, _) = MakeRunner(provider);
            r.RunEpisode();

            Assert.AreEqual(TripClassification.InvalidPlannerBehavior, r.Trips[0].Classification);
            Assert.AreEqual(EpisodeStatus.Terminated, r.World.Status);
            StringAssert.Contains(ErrorCodes.MalformedJson, (string)r.Log.Last(EpisodeEventTypes.PlanRejected).Data["codes"]);
        }

        [Test]
        public void Guidance_Tier_And_Mode_Reach_The_Prompt()
        {
            var provider = new ScriptedProvider().Then(Trip1Json());
            var (r, planner) = MakeRunner(provider, GuidanceTier.None, ObservationMode.Raw);
            r.StartTrip();
            StringAssert.DoesNotContain("STRATEGY GUIDANCE", planner.Exchanges[0].SystemPrompt);
            StringAssert.Contains("OBSERVATION MODE: RAW", planner.Exchanges[0].UserMessage);
            StringAssert.Contains("\"tiles\":[", planner.Exchanges[0].UserMessage);
            Assert.AreEqual("Raw", r.Log.Header.ObservationMode);
            Assert.AreEqual("None", r.Log.Header.GuidanceTier);
        }

        [Test]
        public void Http_Providers_Fail_Cleanly_Without_Keys_And_Never_Touch_The_Network()
        {
            var req = new LlmRequest { SystemPrompt = "s", UserMessage = "u", Model = "m", MaxTokens = 10, TimeoutSeconds = 5 };
            foreach (ILlmProvider p in new ILlmProvider[] { new ClaudeProvider(""), new OpenAiProvider(""), new GeminiProvider("") })
            {
                var resp = p.Complete(req);
                Assert.IsTrue(resp.IsError, p.Name);
                StringAssert.Contains(LlmKeys.EnvVarFor(p.Name), resp.Error);
            }
            Assert.Throws<ArgumentException>(() => LlmProviders.Create(new LlmPlannerConfig { Provider = "mock" }));
            Assert.Throws<ArgumentException>(() => LlmProviders.Create(new LlmPlannerConfig { Provider = "nope" }));
        }
    }
}