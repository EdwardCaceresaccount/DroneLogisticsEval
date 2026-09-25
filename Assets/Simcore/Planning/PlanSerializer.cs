using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimCore.Commands;

namespace SimCore.Planning
{
    /// <summary>Plan → canonical tool-call JSON. The inverse of ToolCallParser; round-trips exactly.</summary>
    public static class PlanSerializer
    {
        public static string ToToolCallJson(Plan plan)
        {
            var calls = new JArray();
            foreach (var c in plan.Commands)
            {
                var o = new JObject { ["tool"] = c.Type.ToString() };
                JObject args = c.Type switch
                {
                    CommandType.MOVE_TO   => new JObject { ["x"] = c.Target.Value.X, ["y"] = c.Target.Value.Y },
                    CommandType.LOAD      => new JObject { ["package_id"] = c.PackageId },
                    CommandType.DELIVER   => new JObject { ["package_id"] = c.PackageId },
                    CommandType.CHARGE    => new JObject { ["target_pct"] = c.Value.Value },
                    CommandType.WAIT      => new JObject { ["seconds"] = c.Value.Value },
                    CommandType.SET_SPEED => new JObject { ["mode"] = c.Speed.Value.ToString().ToUpperInvariant() },
                    _ => null
                };
                if (args != null) o["args"] = args;
                calls.Add(o);
            }
            return new JObject { ["tool_calls"] = calls }.ToString(Formatting.None);
        }
    }
}