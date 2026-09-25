using NUnit.Framework;
using SimCore.Commands;
using SimCore.Domain;
using SimCore.Eval;
using SimCore.Observation;
using SimCore.Planning;

namespace SimCore.Tests
{
    public class PromptTests
    {
        private static PlanRequest Request(GuidanceTier tier, ObservationMode mode, int attempt = 1,
                                           ValidationResult errors = null, TripOutcome prior = null, bool autoRecovery = true)
        {
            var (e, _) = TestFixtures.Engine40();
            e.Config.Recovery.AutoRecoveryEnabled = autoRecovery;
            var obs = new ObservationCompiler().Compile(e.World, e, mode);
            return new PlanRequest(obs, 1, attempt, errors, prior, tier);
        }

        [Test]
        public void Tiers_Are_Cumulative_And_Strategy_Only_In_Full()
        {
            var none = PromptBuilder.Build(Request(GuidanceTier.None, ObservationMode.Processed)).SystemPrompt;
            var partial = PromptBuilder.Build(Request(GuidanceTier.Partial, ObservationMode.Processed)).SystemPrompt;
            var full = PromptBuilder.Build(Request(GuidanceTier.Full, ObservationMode.Processed)).SystemPrompt;

            StringAssert.Contains("tool_calls", none);
            StringAssert.Contains("OBJECTIVE:", none);
            StringAssert.DoesNotContain("ENVIRONMENT RULES", none);
            StringAssert.DoesNotContain("STRATEGY GUIDANCE", none);

            StringAssert.Contains("ENVIRONMENT RULES", partial);
            StringAssert.DoesNotContain("STRATEGY GUIDANCE", partial);

            StringAssert.Contains("ENVIRONMENT RULES", full);
            StringAssert.Contains("STRATEGY GUIDANCE", full);
            

            Assert.Less(none.Length, partial.Length);
            Assert.Less(partial.Length, full.Length);
        }

        [Test]
        public void Low_Battery_Rule_Reflects_Auto_Recovery_Setting()
        {
            var withRescue = PromptBuilder.Build(Request(GuidanceTier.Partial, ObservationMode.Processed, autoRecovery: true)).SystemPrompt;
            var noRescue = PromptBuilder.Build(Request(GuidanceTier.Partial, ObservationMode.Processed, autoRecovery: false)).SystemPrompt;
            StringAssert.Contains("ABORTS your plan", withRescue);
            StringAssert.Contains("no automatic rescue", noRescue);
        }

        [Test]
        public void User_Message_Embeds_Observation_And_Header()
        {
            var pkg = PromptBuilder.Build(Request(GuidanceTier.None, ObservationMode.Raw));
            StringAssert.Contains("TRIP 1, planning attempt 1", pkg.UserMessage);
            StringAssert.Contains("OBSERVATION MODE: RAW", pkg.UserMessage);
            StringAssert.Contains("\"observation_mode\":\"Raw\"", pkg.UserMessage);
            StringAssert.DoesNotContain("REJECTED", pkg.UserMessage);
            StringAssert.DoesNotContain("PREVIOUS TRIP", pkg.UserMessage);
            Assert.Greater(pkg.EstimatedInputTokens, 500);
        }

        [Test]
        public void Rejection_Feedback_Lists_Every_Error()
        {
            var errors = new ValidationResult();
            errors.Add(ErrorCategory.State, ErrorCodes.NotAirborne, "MOVE_TO(9,7) requires the drone to be airborne.", 0);
            errors.Add(ErrorCategory.Tool, ErrorCodes.UnknownTool, "unknown tool 'FLY'.", 3);

            var pkg = PromptBuilder.Build(Request(GuidanceTier.None, ObservationMode.Processed, attempt: 2, errors: errors));
            StringAssert.Contains("attempt 1) WAS REJECTED WITH 2 ERROR(S)", pkg.UserMessage);
            StringAssert.Contains(ErrorCodes.NotAirborne, pkg.UserMessage);
            StringAssert.Contains(ErrorCodes.UnknownTool, pkg.UserMessage);
            StringAssert.Contains("@#3", pkg.UserMessage);
            StringAssert.Contains("corrected COMPLETE plan", pkg.UserMessage);
        }

        [Test]
        public void Prior_Trip_Summary_Names_Emergency_Recovery()
        {
            var prior = new TripOutcome
            {
                TripNumber = 1, Classification = TripClassification.DeliveryWithEmergencyCharge, PackagesDelivered = 1,
                EndTile = new GridCoord(4, 4), EndTileType = TileType.Facility, BatteryEnd = 70.2,
                LowBatteryOccurred = true, EmergencyRecovery = true, RecoveryTargetTile = new GridCoord(4, 4)
            };
            var pkg = PromptBuilder.Build(Request(GuidanceTier.None, ObservationMode.Processed, prior: prior));
            StringAssert.Contains("PREVIOUS TRIP RESULT", pkg.UserMessage);
            StringAssert.Contains("DeliveryWithEmergencyCharge", pkg.UserMessage);
            StringAssert.Contains("emergency recovery to (4,4)", pkg.UserMessage);
        }

        [Test]
        public void Prompt_Is_Deterministic()
        {
            var a = PromptBuilder.Build(Request(GuidanceTier.Full, ObservationMode.Processed));
            var b = PromptBuilder.Build(Request(GuidanceTier.Full, ObservationMode.Processed));
            Assert.AreEqual(a.SystemPrompt, b.SystemPrompt);
            Assert.AreEqual(a.UserMessage, b.UserMessage);
        }

        [Test]
        public void Sanitizer_Strips_Fences_And_Prose_But_Not_Content()
        {
            const string inner = @"{""tool_calls"":[{""tool"":""LIFT_OFF""}]}";

            Assert.IsTrue(PlannerOutputSanitizer.TryExtractJson("```json\n" + inner + "\n```", out var j1, out var m1));
            Assert.AreEqual(inner, j1); Assert.IsTrue(m1);

            Assert.IsTrue(PlannerOutputSanitizer.TryExtractJson("Here is the plan:\n" + inner + "\nLet me know!", out var j2, out var m2));
            Assert.AreEqual(inner, j2); Assert.IsTrue(m2);

            Assert.IsTrue(PlannerOutputSanitizer.TryExtractJson("  " + inner + "  ", out var j3, out var m3));
            Assert.AreEqual(inner, j3); Assert.IsFalse(m3, "Whitespace trimming is not a modification.");

            Assert.IsFalse(PlannerOutputSanitizer.TryExtractJson("I cannot plan this trip.", out _, out _));
            Assert.IsFalse(PlannerOutputSanitizer.TryExtractJson("", out _, out _));
        }

        [Test]
        public void Sanitized_Output_Still_Fails_Parser_When_Malformed()
        {
            Assert.IsTrue(PlannerOutputSanitizer.TryExtractJson("```json\n{\"tool_calls\": [ {\"tool\": }\n```", out var json, out _));
            Assert.IsFalse(ToolCallParser.TryParse(json, "test", out _, out var r));
            Assert.IsTrue(r.HasCode(ErrorCodes.MalformedJson), "Sanitizing is normalization, not repair.");
        }

        [Test]
        public void Token_Estimate_Is_Four_Chars_Per_Token()
        {
            Assert.AreEqual(10, TokenEstimate.Of(new string('a', 40)));
            Assert.AreEqual(11, TokenEstimate.Of(new string('a', 41)));
            Assert.AreEqual(0, TokenEstimate.Of(""));
        }
    }
}