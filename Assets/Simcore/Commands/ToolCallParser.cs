using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimCore.Domain;

namespace SimCore.Commands
{
    /// <summary>
    /// THE LLM CONTRACT. Every planner must emit:
    ///   { "tool_calls": [ { "tool": "MOVE_TO", "args": { "x": 9, "y": 7 } }, { "tool": "LAND" }, ... ] }
    /// Arguments by tool:
    ///   LIFT_OFF, LAND           — none
    ///   MOVE_TO                  — x:int, y:int
    ///   LOAD, DELIVER            — package_id:string
    ///   CHARGE                   — target_pct:number in (0,100]
    ///   WAIT                     — seconds:number > 0
    ///   SET_SPEED                — mode:"SLOW"|"NORMAL"|"FAST"
    /// Tool and mode names are matched case-insensitively (a lenient wire format, strict semantics).
    /// Unknown extra arguments are Schema errors: an LLM inventing "altitude": 50 is exactly the
    /// kind of hallucinated interface use this harness exists to measure.
    /// </summary>
    public static class ToolCallParser
    {
        private static readonly Dictionary<CommandType, string[]> AllowedArgs = new()
        {
            { CommandType.LIFT_OFF,  Array.Empty<string>() },
            { CommandType.LAND,      Array.Empty<string>() },
            { CommandType.MOVE_TO,   new[] { "x", "y" } },
            { CommandType.LOAD,      new[] { "package_id" } },
            { CommandType.DELIVER,   new[] { "package_id" } },
            { CommandType.CHARGE,    new[] { "target_pct" } },
            { CommandType.WAIT,      new[] { "seconds" } },
            { CommandType.SET_SPEED, new[] { "mode" } },
        };

        /// <summary>Returns true and a Plan if the text parses cleanly; otherwise false with every error found.</summary>
        public static bool TryParse(string json, string plannerId, out Plan plan, out ValidationResult result)
        {
            plan = null;
            result = new ValidationResult();

            JObject root;
            try
            {
                root = JObject.Parse(json ?? "");
            }
            catch (JsonException ex)
            {
                result.Add(ErrorCategory.Schema, ErrorCodes.MalformedJson, $"Planner output is not valid JSON: {ex.Message}");
                return false;
            }

            if (root["tool_calls"] is not JArray calls)
            {
                result.Add(ErrorCategory.Schema, ErrorCodes.MissingToolCalls, "Top-level 'tool_calls' array is missing.");
                return false;
            }

            var commands = new List<Command>();
            for (int i = 0; i < calls.Count; i++)
            {
                if (calls[i] is not JObject call)
                {
                    result.Add(ErrorCategory.Schema, ErrorCodes.ToolCallNotObject, $"tool_calls[{i}] is not an object.", i);
                    continue;
                }

                string toolName = call["tool"]?.Type == JTokenType.String ? call["tool"].Value<string>() : null;
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    result.Add(ErrorCategory.Schema, ErrorCodes.MissingToolName, $"tool_calls[{i}] has no 'tool' name.", i);
                    continue;
                }

                if (!Enum.TryParse<CommandType>(toolName, ignoreCase: true, out var type))
                {
                    result.Add(ErrorCategory.Tool, ErrorCodes.UnknownTool, $"tool_calls[{i}]: unknown tool '{toolName}'.", i);
                    continue;
                }

                var args = call["args"] as JObject ?? new JObject();
                foreach (var prop in args.Properties())
                    if (Array.IndexOf(AllowedArgs[type], prop.Name) < 0)
                        result.Add(ErrorCategory.Schema, ErrorCodes.UnknownArgument,
                            $"tool_calls[{i}] {type}: unknown argument '{prop.Name}'.", i);

                int before = result.Errors.Count;
                var cmd = BuildCommand(type, args, i, result);
                if (cmd != null && result.Errors.Count == before) commands.Add(cmd);
            }

            if (!result.IsValid) return false;
            plan = new Plan(commands, plannerId, json);
            return true;
        }

        private static Command BuildCommand(CommandType type, JObject args, int i, ValidationResult r)
        {
            switch (type)
            {
                case CommandType.LIFT_OFF: return Command.LiftOff();
                case CommandType.LAND:     return Command.Land();

                case CommandType.MOVE_TO:
                {
                    bool okX = TryInt(args, "x", i, r, out int x);
                    bool okY = TryInt(args, "y", i, r, out int y);
                    return okX && okY ? Command.MoveTo(x, y) : null;
                }
                case CommandType.LOAD:
                    return TryString(args, "package_id", i, r, out var lid) ? Command.Load(lid) : null;
                case CommandType.DELIVER:
                    return TryString(args, "package_id", i, r, out var did) ? Command.Deliver(did) : null;

                case CommandType.CHARGE:
                {
                    if (!TryNumber(args, "target_pct", i, r, out double pct)) return null;
                    if (pct <= 0 || pct > 100)
                    {
                        r.Add(ErrorCategory.Schema, ErrorCodes.InvalidArgumentValue, $"tool_calls[{i}] CHARGE: target_pct must be in (0,100], got {pct}.", i);
                        return null;
                    }
                    return Command.Charge(pct);
                }
                case CommandType.WAIT:
                {
                    if (!TryNumber(args, "seconds", i, r, out double s)) return null;
                    if (s <= 0)
                    {
                        r.Add(ErrorCategory.Schema, ErrorCodes.InvalidArgumentValue, $"tool_calls[{i}] WAIT: seconds must be > 0, got {s}.", i);
                        return null;
                    }
                    return Command.Wait(s);
                }
                case CommandType.SET_SPEED:
                {
                    if (!TryString(args, "mode", i, r, out var mode)) return null;
                    if (!Enum.TryParse<SpeedMode>(mode, ignoreCase: true, out var speed))
                    {
                        r.Add(ErrorCategory.Schema, ErrorCodes.InvalidArgumentValue, $"tool_calls[{i}] SET_SPEED: mode must be SLOW|NORMAL|FAST, got '{mode}'.", i);
                        return null;
                    }
                    return Command.SetSpeed(speed);
                }
                default:
                    throw new InvalidOperationException($"Unhandled command type {type}");
            }
        }

        private static bool TryInt(JObject args, string key, int i, ValidationResult r, out int value)
        {
            value = 0;
            var tok = args[key];
            if (tok == null) { r.Add(ErrorCategory.Schema, ErrorCodes.MissingArgument, $"tool_calls[{i}]: missing argument '{key}'.", i); return false; }
            if (tok.Type == JTokenType.Integer) { value = tok.Value<int>(); return true; }
            if (tok.Type == JTokenType.Float)
            {
                double d = tok.Value<double>();
                if (Math.Abs(d - Math.Round(d)) < 1e-9) { value = (int)Math.Round(d); return true; }
            }
            r.Add(ErrorCategory.Schema, ErrorCodes.InvalidArgumentType, $"tool_calls[{i}]: '{key}' must be an integer, got {tok.Type}.", i);
            return false;
        }

        private static bool TryNumber(JObject args, string key, int i, ValidationResult r, out double value)
        {
            value = 0;
            var tok = args[key];
            if (tok == null) { r.Add(ErrorCategory.Schema, ErrorCodes.MissingArgument, $"tool_calls[{i}]: missing argument '{key}'.", i); return false; }
            if (tok.Type == JTokenType.Integer || tok.Type == JTokenType.Float) { value = tok.Value<double>(); return true; }
            r.Add(ErrorCategory.Schema, ErrorCodes.InvalidArgumentType, $"tool_calls[{i}]: '{key}' must be a number, got {tok.Type}.", i);
            return false;
        }

        private static bool TryString(JObject args, string key, int i, ValidationResult r, out string value)
        {
            value = null;
            var tok = args[key];
            if (tok == null) { r.Add(ErrorCategory.Schema, ErrorCodes.MissingArgument, $"tool_calls[{i}]: missing argument '{key}'.", i); return false; }
            if (tok.Type == JTokenType.String && !string.IsNullOrWhiteSpace(tok.Value<string>())) { value = tok.Value<string>(); return true; }
            r.Add(ErrorCategory.Schema, ErrorCodes.InvalidArgumentType, $"tool_calls[{i}]: '{key}' must be a non-empty string.", i);
            return false;
        }
    }
}