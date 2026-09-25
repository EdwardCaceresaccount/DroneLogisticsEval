using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SimCore.Config;
using SimCore.Util;

namespace SimCore.Planning
{
    public enum InjectionKind { MalformedJson, UnknownTool, InvalidArgument, UnknownArgument, ImpossibleCommand }

    public sealed class InjectionRecord
    {
        public int TripNumber, Attempt;
        public InjectionKind Kind;
        public string Original, Corrupted;
    }

    /// <summary>
    /// D17: wraps ANY planner; corrupts its plan's canonical JSON with a seeded fault; returns the result through the
    /// real parser. Each kind targets one error category:
    ///   MalformedJson → Schema/MALFORMED_JSON        UnknownTool → Tool/UNKNOWN_TOOL
    ///   InvalidArgument → Schema/INVALID_ARGUMENT_TYPE  UnknownArgument → Schema/UNKNOWN_ARGUMENT
    ///   ImpossibleCommand → parses cleanly, then Physical/PHYSICAL_OUT_OF_BOUNDS at validation
    /// </summary>
    public sealed class ErrorInjectingPlanner : IPlanner
    {
        public string Id => $"{_inner.Id}+inject{_cfg.Rate:0.##}";
        public string Version => _inner.Version + "+inj1.0";
        public IPlanner Inner => _inner;
        public IReadOnlyList<InjectionRecord> Injections => _injections;

        private readonly IPlanner _inner;
        private readonly ErrorInjectionConfig _cfg;
        private readonly List<InjectionKind> _kinds = new();
        private readonly DeterministicRng _rng;
        private readonly List<InjectionRecord> _injections = new();

        public ErrorInjectingPlanner(IPlanner inner, ErrorInjectionConfig cfg)
        {
            _inner = inner;
            _cfg = cfg ?? new ErrorInjectionConfig();
            foreach (var k in _cfg.Kinds)
                if (Enum.TryParse<InjectionKind>(k, ignoreCase: true, out var kind)) _kinds.Add(kind);
            if (_kinds.Count == 0) _kinds.AddRange((InjectionKind[])Enum.GetValues(typeof(InjectionKind)));
            _rng = new DeterministicRng(_cfg.Seed);
        }

        public PlanResponse Plan(PlanRequest request)
        {
            var resp = _inner.Plan(request);
            if (resp.Plan == null) return resp;
            if (!_cfg.InjectOnRetries && request.Attempt > 1) return resp;
            if (_rng.NextDouble() >= _cfg.Rate) return resp;

            var kind = _kinds[_rng.NextInt(0, _kinds.Count)];
            string original = PlanSerializer.ToToolCallJson(resp.Plan);
            string corrupted = Corrupt(original, kind, _rng);
            _injections.Add(new InjectionRecord { TripNumber = request.TripNumber, Attempt = request.Attempt, Kind = kind, Original = original, Corrupted = corrupted });
            return PlanResponse.FromJson(corrupted, Id, resp.InputTokens, resp.OutputTokens);
        }

        public static string Corrupt(string json, InjectionKind kind, DeterministicRng rng)
        {
            if (kind == InjectionKind.MalformedJson)
                return json.Substring(0, Math.Max(1, json.Length - 3));   // drops the closing "}]}"

            var root = JObject.Parse(json);
            var calls = (JArray)root["tool_calls"];
            if (calls.Count == 0) return json;

            switch (kind)
            {
                case InjectionKind.UnknownTool:
                {
                    var call = (JObject)calls[rng.NextInt(0, calls.Count)];
                    call["tool"] = (string)call["tool"] + "_X";
                    break;
                }
                case InjectionKind.InvalidArgument:
                {
                    // Prefer a numeric argument (→ INVALID_ARGUMENT_TYPE); fall back to an empty package_id.
                    foreach (var t in calls)
                    {
                        var args = t["args"] as JObject;
                        if (args == null) continue;
                        foreach (var p in args.Properties())
                            if (p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Float) { p.Value = "invalid"; return root.ToString(Newtonsoft.Json.Formatting.None); }
                    }
                    foreach (var t in calls)
                    {
                        var args = t["args"] as JObject;
                        if (args?["package_id"] != null) { args["package_id"] = ""; return root.ToString(Newtonsoft.Json.Formatting.None); }
                    }
                    ((JObject)calls[0])["args"] = new JObject { ["x"] = "invalid" };  // no args anywhere: force one
                    break;
                }
                case InjectionKind.UnknownArgument:
                {
                    var call = (JObject)calls[rng.NextInt(0, calls.Count)];
                    var args = call["args"] as JObject ?? new JObject();
                    args["altitude"] = 50;
                    call["args"] = args;
                    break;
                }
                case InjectionKind.ImpossibleCommand:
                {
                    JObject target = null;
                    foreach (var t in calls) if ((string)t["tool"] == "MOVE_TO") { target = (JObject)t; break; }
                    if (target != null) { target["args"]["x"] = 999; target["args"]["y"] = 999; }
                    else calls.Add(new JObject { ["tool"] = "MOVE_TO", ["args"] = new JObject { ["x"] = 999, ["y"] = 999 } });
                    break;
                }
            }
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}