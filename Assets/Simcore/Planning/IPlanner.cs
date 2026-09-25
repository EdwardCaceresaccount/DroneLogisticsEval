using SimCore.Commands;
using SimCore.Domain;
using SimCore.Eval;
using SimCore.Observation;

namespace SimCore.Planning
{
    public sealed class PlanRequest
    {
        public Observation.Observation Observation { get; }
        public int TripNumber { get; }
        public int Attempt { get; }
        public ValidationResult PriorErrors { get; }
        public TripOutcome PriorTrip { get; }
        public GuidanceTier Guidance { get; }

        public PlanRequest(Observation.Observation observation, int tripNumber, int attempt,
                           ValidationResult priorErrors, TripOutcome priorTrip, GuidanceTier guidance)
        {
            Observation = observation; TripNumber = tripNumber; Attempt = attempt;
            PriorErrors = priorErrors; PriorTrip = priorTrip; Guidance = guidance;
        }
    }

    public sealed class PlanResponse
    {
        public Plan Plan { get; }
        public ValidationResult Errors { get; }
        public string RawOutput { get; }
        public int InputTokens { get; }
        public int OutputTokens { get; }
        /// <summary>True when the planner's text needed fences/prose stripped to reach JSON — a contract-compliance signal.</summary>
        public bool OutputSanitized { get; }

        private PlanResponse(Plan plan, ValidationResult errors, string raw, int inTok, int outTok, bool sanitized)
        {
            Plan = plan; Errors = errors ?? new ValidationResult(); RawOutput = raw;
            InputTokens = inTok; OutputTokens = outTok; OutputSanitized = sanitized;
        }

        public static PlanResponse Ok(Plan plan, string raw = null, int inTok = 0, int outTok = 0, bool sanitized = false)
            => new(plan, null, raw, inTok, outTok, sanitized);

        public static PlanResponse Failed(ValidationResult errors, string raw = null, int inTok = 0, int outTok = 0, bool sanitized = false)
            => new(null, errors, raw, inTok, outTok, sanitized);

        /// <summary>Strict: the text must already be the JSON object.</summary>
        public static PlanResponse FromJson(string json, string plannerId, int inTok = 0, int outTok = 0)
            => ToolCallParser.TryParse(json, plannerId, out var plan, out var errors)
                ? Ok(plan, json, inTok, outTok)
                : Failed(errors, json, inTok, outTok);

        /// <summary>Lenient on wrapping, strict on content: strip fences/prose, then parse under the contract.</summary>
        public static PlanResponse FromLlmText(string text, string plannerId, int inTok = 0, int outTok = 0)
        {
            if (!PlannerOutputSanitizer.TryExtractJson(text, out var json, out bool modified))
            {
                var err = new ValidationResult();
                err.Add(ErrorCategory.Schema, ErrorCodes.MalformedJson, "Planner output contained no JSON object.");
                return Failed(err, text, inTok, outTok, sanitized: false);
            }
            return ToolCallParser.TryParse(json, plannerId, out var plan, out var errors)
                ? Ok(plan, text, inTok, outTok, modified)
                : Failed(errors, text, inTok, outTok, modified);
        }
    }

    public interface IPlanner
    {
        string Id { get; }
        string Version { get; }
        PlanResponse Plan(PlanRequest request);
    }
}