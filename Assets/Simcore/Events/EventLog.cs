using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace SimCore.Events
{
    /// <summary>First line of every JSONL log. Everything needed to reproduce the run (spec §19).</summary>
    public sealed class EpisodeHeader
    {
        [JsonProperty("record")]             public string Record = "header";
        [JsonProperty("run_id")]             public string RunId;
        [JsonProperty("scenario_id")]        public string ScenarioId;
        [JsonProperty("scenario_version")]   public string ScenarioVersion;
        [JsonProperty("seed")]               public int Seed;
        [JsonProperty("tick_seconds")]       public double TickSeconds;
        [JsonProperty("planner_id")]         public string PlannerId;
        [JsonProperty("planner_version")]    public string PlannerVersion;
        [JsonProperty("observation_mode")]   public string ObservationMode;
        [JsonProperty("guidance_tier")]      public string GuidanceTier;
        [JsonProperty("simulation_version")] public string SimulationVersion;
        [JsonProperty("evaluation_version")] public string EvaluationVersion;
        [JsonProperty("tool_contract")]      public string ToolContract;
        [JsonProperty("started_utc")]        public string StartedUtc = DateTime.UtcNow.ToString("o");
    }

    public static class EpisodeEventTypes
    {
        public const string EpisodeStarted         = "EPISODE_STARTED";
        public const string TripStarted            = "TRIP_STARTED";
        public const string ThinkingStarted        = "THINKING_STARTED";
        public const string DecisionMoment         = "DECISION_MOMENT";
        public const string PlanProposed           = "PLAN_PROPOSED";
        public const string PlanRejected           = "PLAN_REJECTED";
        public const string PlanAccepted           = "PLAN_ACCEPTED";
        public const string ThinkingEnded          = "THINKING_ENDED";
        public const string ExecutionStarted       = "EXECUTION_STARTED";
        public const string PlanTruncatedAtLanding = "PLAN_TRUNCATED_AT_LANDING";
        public const string TimeLimitExceeded      = "TIME_LIMIT_EXCEEDED";
        public const string TripEnded              = "TRIP_ENDED";
        public const string EpisodeEnded           = "EPISODE_ENDED";
    }

    /// <summary>
    /// The persistent, ordered record of an episode. Engine, executor, and runner all emit into one instance,
    /// so the log is the single reconstructable truth. JSONL (one JSON object per line) is the industry
    /// format for this: streamable, greppable, and trivially loaded by pandas.
    /// </summary>
    public sealed class EventLog : ISimEventSink
    {
        public EpisodeHeader Header { get; set; }
        public List<SimEvent> Events { get; } = new();

        public void Emit(SimEvent e) => Events.Add(e);

        public int Count(string type)
        {
            int n = 0;
            foreach (var e in Events) if (e.Type == type) n++;
            return n;
        }

        public SimEvent Last(string type)
        {
            for (int i = Events.Count - 1; i >= 0; i--) if (Events[i].Type == type) return Events[i];
            return null;
        }

        public string ToJsonl()
        {
            var sb = new StringBuilder();
            if (Header != null) sb.Append(JsonConvert.SerializeObject(Header)).Append('\n');
            foreach (var e in Events)
            {
                sb.Append(JsonConvert.SerializeObject(new
                {
                    record = "event",
                    tick = e.Tick,
                    time = e.TimeSeconds,
                    type = e.Type,
                    data = e.Data
                })).Append('\n');
            }
            return sb.ToString();
        }

        public void WriteJsonl(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJsonl());
        }
    }
}