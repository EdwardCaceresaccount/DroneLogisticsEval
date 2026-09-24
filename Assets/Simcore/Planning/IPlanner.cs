using SimCore.Commands;
using SimCore.Domain;
using SimCore.Eval;
using SimCore.Observation;

namespace SimCore.Planning
{
    /// <summary>What a planner receives in a Thinking Phase. Never the WorldState — only the Observation.</summary>
    public sealed class PlanRequest
    {
        public Observation.Observation Observation { get; }
        public int TripNumber { get; }
        /// <summary>1-based. Greater than 1 means a prior attempt was rejected; see PriorErrors.</summary>
        public int Attempt { get; }
        /// <summary>D12: why the previous attempt was rejected. Null on attempt 1.</summary>
        public ValidationResult PriorErrors { get; }
        /// <summary>Outcome of the previous trip. Null on trip 1. (Reflection-loop hook.)</summary>
        public TripOutcome PriorTrip { get; }
        public GuidanceTier Guidance { get; }

        public PlanRequest(Observation.Observation observation, int tripNumber, int attempt,
                           ValidationResult priorErrors, TripOutcome priorTrip, GuidanceTier guidance)
        {
            Observation = observation; TripNumber = tripNumber; Attempt = attempt;
            PriorErrors = priorErrors; PriorTrip = priorTrip; Guidance = guidance;
        }
    }

    /// <summary>What a planner returns. Either a Plan, or the parse errors that prevented one.</summary>
    public sealed class PlanResponse
    {
        public Plan Plan { get; }
        public ValidationResult Errors { get; }
        public string RawOutput { get; }
        public int InputTokens { get; }
        public int OutputTokens { get; }

        private PlanResponse(Plan plan, ValidationResult errors, string raw, int inTok, int outTok)
        {
            Plan = plan; Errors = errors ?? new ValidationResult(); RawOutput = raw;
            InputTokens = inTok; OutputTokens = outTok;
        }

        public static PlanResponse Ok(Plan plan, string raw = null, int inTok = 0, int outTok = 0)
            => new(plan, null, raw, inTok, outTok);

        public static PlanResponse Failed(ValidationResult errors, string raw = null, int inTok = 0, int outTok = 0)
            => new(null, errors, raw, inTok, outTok);

        /// <summary>Convenience for text-producing planners: parse under the tool contract.</summary>
        public static PlanResponse FromJson(string json, string plannerId, int inTok = 0, int outTok = 0)
            => ToolCallParser.TryParse(json, plannerId, out var plan, out var errors)
                ? Ok(plan, json, inTok, outTok)
                : Failed(errors, json, inTok, outTok);
    }

    /// <summary>The contract every planner implements (spec §12). The simulation has zero knowledge of who is behind it.</summary>
    public interface IPlanner
    {
        string Id { get; }
        string Version { get; }
        PlanResponse Plan(PlanRequest request);
    }
}