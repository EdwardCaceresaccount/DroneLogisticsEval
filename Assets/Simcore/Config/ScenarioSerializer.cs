using System.Collections.Generic;
using Newtonsoft.Json;

namespace SimCore.Config
{
    /// <summary>ScenarioConfig → JSON. Tiles sorted for stable diffs (D8: the file is canonical).</summary>
    public static class ScenarioSerializer
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        public static string ToJson(ScenarioConfig cfg)
        {
            cfg.Tiles.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            cfg.Packages.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return JsonConvert.SerializeObject(cfg, Settings);
        }

        /// <summary>Deep copy via round-trip — the painter edits a draft, never the loaded config.</summary>
        public static ScenarioConfig Clone(ScenarioConfig cfg)
            => JsonConvert.DeserializeObject<ScenarioConfig>(JsonConvert.SerializeObject(cfg, Settings));
    }
}