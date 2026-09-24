using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using SimCore.Domain;
using SimCore.Observation;

namespace SimCore.Config
{
    /// <summary>
    /// The experimental CONDITION (spec §19): which scenario, which planner, what it's shown, what it's told.
    /// ScenarioConfig is the environment; EpisodeConfig is the treatment. Keep them separate so
    /// "same scenario, different observation mode" is a one-field change.
    /// </summary>
    public sealed class EpisodeConfig
    {
        [JsonProperty("run_id")]            public string RunId;
        [JsonProperty("scenario_id")]       public string ScenarioId = "";
        [JsonProperty("planner_id")]        public string PlannerId = "";

        [JsonProperty("observation_mode")]
        [JsonConverter(typeof(StringEnumConverter))]
        public ObservationMode ObservationMode = ObservationMode.Processed;

        [JsonProperty("guidance_tier")]
        [JsonConverter(typeof(StringEnumConverter))]
        public GuidanceTier Guidance = GuidanceTier.None;

        /// <summary>D12: bounded retries for unparseable/invalid plans.</summary>
        [JsonProperty("max_plan_attempts")] public int MaxPlanAttempts = 3;

        public string EffectiveRunId(string plannerId)
            => string.IsNullOrWhiteSpace(RunId) ? $"{ScenarioId}__{plannerId}__{ObservationMode}__{Guidance}" : RunId;
    }
}