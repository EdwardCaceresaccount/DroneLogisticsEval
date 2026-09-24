using System.Collections.Generic;
using SimCore.Commands;

namespace SimCore.Planning
{
    /// <summary>
    /// Returns pre-authored responses in order. A test double AND a regression tool: "golden plans"
    /// replayed against a scenario must always produce the same metrics, or the simulator changed.
    /// When the script runs out it returns a failed response, which the runner treats as planner failure.
    /// </summary>
    public sealed class ScriptedPlanner : IPlanner
    {
        public string Id { get; }
        public string Version => "1.0.0";

        private readonly Queue<PlanResponse> _responses = new();
        public int CallCount { get; private set; }

        private ScriptedPlanner(string id) { Id = id; }

        public static ScriptedPlanner FromPlans(params Plan[] plans)
        {
            var p = new ScriptedPlanner("scripted");
            foreach (var plan in plans) p._responses.Enqueue(PlanResponse.Ok(plan));
            return p;
        }

        /// <summary>Raw JSON strings go through the real parser — use this to exercise rejection paths.</summary>
        public static ScriptedPlanner FromJson(params string[] jsonOutputs)
        {
            var p = new ScriptedPlanner("scripted-json");
            foreach (var json in jsonOutputs) p._responses.Enqueue(PlanResponse.FromJson(json, p.Id));
            return p;
        }

        public PlanResponse Plan(PlanRequest request)
        {
            CallCount++;
            if (_responses.Count > 0) return _responses.Dequeue();

            var err = new ValidationResult();
            err.Add(ErrorCategory.Schema, "SCRIPT_EXHAUSTED", "ScriptedPlanner has no more responses.");
            return PlanResponse.Failed(err);
        }
    }
}