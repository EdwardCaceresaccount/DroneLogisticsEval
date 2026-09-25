using System.Collections.Generic;
using SimCore.Commands;
using SimCore.Config;

namespace SimCore.Planning.Llm
{
    /// <summary>One request/response pair, kept for the exchange log (Block 13 exports these beside the event log).</summary>
    public sealed class LlmExchange
    {
        public int TripNumber, Attempt;
        public string SystemPrompt, UserMessage, RawOutput;
        public int InputTokens, OutputTokens;
        public double LatencySeconds;
        public bool Sanitized;
        public string ProviderError;
        public List<string> ParseErrorCodes = new();
    }

    /// <summary>
    /// The one LLM planner. Provider-agnostic by construction: PromptBuilder → provider.Complete → sanitizer → parser.
    /// Version is the PROMPT version; bump it whenever ToolContractText or GuidanceTexts change.
    /// </summary>
    public sealed class LlmPlanner : IPlanner
    {
        public const string PromptVersion = "prompt-1.0";

        public string Id => $"{_provider.Name}:{_cfg.Model}";
        public string Version => PromptVersion;
        public IReadOnlyList<LlmExchange> Exchanges => _exchanges;

        private readonly ILlmProvider _provider;
        private readonly LlmPlannerConfig _cfg;
        private readonly List<LlmExchange> _exchanges = new();

        public LlmPlanner(ILlmProvider provider, LlmPlannerConfig cfg)
        {
            _provider = provider;
            _cfg = cfg ?? new LlmPlannerConfig();
        }

        public PlanResponse Plan(PlanRequest request)
        {
            var pkg = PromptBuilder.Build(request);
            var resp = _provider.Complete(new LlmRequest
            {
                SystemPrompt = pkg.SystemPrompt,
                UserMessage = pkg.UserMessage,
                Model = _cfg.Model,
                Temperature = _cfg.Temperature,
                MaxTokens = _cfg.MaxTokens,
                TimeoutSeconds = _cfg.TimeoutSeconds,
                TripNumber = request.TripNumber,
                Attempt = request.Attempt
            });

            var ex = new LlmExchange
            {
                TripNumber = request.TripNumber, Attempt = request.Attempt,
                SystemPrompt = pkg.SystemPrompt, UserMessage = pkg.UserMessage,
                RawOutput = resp.Text, InputTokens = resp.InputTokens, OutputTokens = resp.OutputTokens,
                LatencySeconds = resp.LatencySeconds, ProviderError = resp.Error
            };
            _exchanges.Add(ex);

            if (resp.IsError)
            {
                var err = new ValidationResult();
                err.Add(ErrorCategory.Schema, "PROVIDER_ERROR", resp.Error);
                ex.ParseErrorCodes.Add("PROVIDER_ERROR");
                return PlanResponse.Failed(err, null, resp.InputTokens, resp.OutputTokens);
            }

            var result = PlanResponse.FromLlmText(resp.Text, Id, resp.InputTokens, resp.OutputTokens);
            ex.Sanitized = result.OutputSanitized;
            foreach (var e in result.Errors.Errors) ex.ParseErrorCodes.Add(e.Code);
            return result;
        }
    }
}