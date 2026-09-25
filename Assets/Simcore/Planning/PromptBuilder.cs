using System;
using System.Text;
using SimCore.Commands;
using SimCore.Domain;
using SimCore.Eval;
using SimCore.Observation;

namespace SimCore.Planning
{
    /// <summary>What every LLM adapter sends. Provider-specific wrapping happens in the adapter, never here.</summary>
    public sealed class PromptPackage
    {
        public string SystemPrompt { get; }
        public string UserMessage { get; }
        public GuidanceTier Guidance { get; }
        public ObservationMode Mode { get; }
        public int EstimatedInputTokens { get; }

        public PromptPackage(string system, string user, GuidanceTier guidance, ObservationMode mode)
        {
            SystemPrompt = system; UserMessage = user; Guidance = guidance; Mode = mode;
            EstimatedInputTokens = TokenEstimate.Of(system) + TokenEstimate.Of(user);
        }
    }

    /// <summary>
    /// Provider-free token estimate (≈4 chars/token for English+JSON). Used for observation-size comparisons and
    /// budgeting; real token counts come back from the provider and override this in the log.
    /// </summary>
    public static class TokenEstimate
    {
        public static int Of(string s) => string.IsNullOrEmpty(s) ? 0 : (int)Math.Ceiling(s.Length / 4.0);
    }

    /// <summary>
    /// Assembles the request for a Thinking Phase:
    ///   system = tool contract + tier guidance
    ///   user   = trip/attempt header + observation JSON + previous-trip result + previous-attempt rejection errors
    /// Deterministic: same request → same text. The observation JSON is embedded verbatim.
    /// </summary>
    public static class PromptBuilder
    {
        public static PromptPackage Build(PlanRequest request)
        {
            bool? autoRecovery = request.Observation switch
            {
                RawObservation r => r.Physics?.AutoRecoveryEnabled,
                ProcessedObservation p => p.Physics?.AutoRecoveryEnabled,
                _ => null
            };

            string system = ToolContractText.Text + "\n\n" + GuidanceTexts.For(request.Guidance, autoRecovery);

            var u = new StringBuilder();
            u.Append("TRIP ").Append(request.TripNumber).Append(", planning attempt ").Append(request.Attempt).Append(".\n");
            u.Append("OBSERVATION MODE: ").Append(request.Observation.Mode.ToString().ToUpperInvariant()).Append("\n\n");
            u.Append("OBSERVATION (JSON):\n").Append(request.Observation.ToJson()).Append("\n");

            if (request.PriorTrip != null)
            {
                u.Append("\nPREVIOUS TRIP RESULT:\n").Append(SummarizeTrip(request.PriorTrip)).Append('\n');
            }

            if (request.PriorErrors != null && !request.PriorErrors.IsValid)
            {
                u.Append("\nYOUR PREVIOUS PLAN (attempt ").Append(request.Attempt - 1).Append(") WAS REJECTED WITH ")
                 .Append(request.PriorErrors.Errors.Count).Append(" ERROR(S):\n");
                foreach (var e in request.PriorErrors.Errors) u.Append("- ").Append(e).Append('\n');
                u.Append("Return a corrected COMPLETE plan for this trip.\n");
            }

            u.Append("\nRespond with the JSON plan only.");

            return new PromptPackage(system, u.ToString(), request.Guidance, request.Observation.Mode);
        }

        public static string SummarizeTrip(TripOutcome t)
        {
            var sb = new StringBuilder();
            sb.Append("trip ").Append(t.TripNumber).Append(": ").Append(t.Classification)
              .Append(", delivered ").Append(t.PackagesDelivered)
              .Append(", ended ").Append(t.EndTile.HasValue ? $"at {t.EndTile} ({t.EndTileType})" : "airborne")
              .Append(", battery ").Append(t.BatteryEnd.ToString("F1")).Append('%');
            if (t.LowBatteryOccurred) sb.Append(", LOW BATTERY occurred");
            if (t.EmergencyRecovery) sb.Append(" — simulator flew an emergency recovery to ").Append(t.RecoveryTargetTile);
            if (t.CommandsTruncated > 0) sb.Append(", ").Append(t.CommandsTruncated).Append(" tool call(s) discarded after base landing");
            if (!string.IsNullOrEmpty(t.HaltErrorCode)) sb.Append(", execution halted: ").Append(t.HaltErrorCode);
            if (t.Failure != State.FailureKind.None) sb.Append(", FAILURE: ").Append(t.Failure);
            return sb.ToString();
        }
    }
}