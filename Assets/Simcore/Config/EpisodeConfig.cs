using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using SimCore.Domain;
using SimCore.Observation;

namespace SimCore.Config
{
    /// <summary>Which model, and how to call it. provider = "claude" | "openai" | "gemini" | "mock".</summary>
    public sealed class LlmPlannerConfig
    {
        [JsonProperty("provider")]        public string Provider = "mock";
        [JsonProperty("model")]           public string Model = "scripted";
        [JsonProperty("temperature")]     public double Temperature = 0.0;
        [JsonProperty("max_tokens")]      public int MaxTokens = 2048;
        [JsonProperty("timeout_seconds")] public int TimeoutSeconds = 60;
    }

    /// <summary>Experiment 2 condition (D17). rate 0 = clean run.</summary>
    public sealed class ErrorInjectionConfig
    {
        [JsonProperty("rate")]              public double Rate = 0.0;
        [JsonProperty("kinds")]             public List<string> Kinds = new()
            { "MalformedJson", "UnknownTool", "InvalidArgument", "UnknownArgument", "ImpossibleCommand" };
        [JsonProperty("inject_on_retries")] public bool InjectOnRetries = false;
        [JsonProperty("seed")]              public int Seed = 0;
    }

    public sealed class EpisodeConfig
    {
        [JsonProperty("run_id")]            public string RunId;
        [JsonProperty("scenario_id")]       public string ScenarioId = "";
        /// <summary>"greedy", or "llm" (then see Llm), optionally wrapped by ErrorInjection.</summary>
        [JsonProperty("planner_id")]        public string PlannerId = "";

        [JsonProperty("observation_mode")]
        [JsonConverter(typeof(StringEnumConverter))]
        public ObservationMode ObservationMode = ObservationMode.Processed;

        [JsonProperty("guidance_tier")]
        [JsonConverter(typeof(StringEnumConverter))]
        public GuidanceTier Guidance = GuidanceTier.None;

        [JsonProperty("max_plan_attempts")] public int MaxPlanAttempts = 3;

        [JsonProperty("llm")]               public LlmPlannerConfig Llm;
        [JsonProperty("error_injection")]   public ErrorInjectionConfig ErrorInjection;

        public string EffectiveRunId(string plannerId)
            => string.IsNullOrWhiteSpace(RunId) ? $"{ScenarioId}__{plannerId}__{ObservationMode}__{Guidance}" : RunId;
    }
}