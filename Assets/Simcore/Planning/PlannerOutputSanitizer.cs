namespace SimCore.Planning
{
    /// <summary>
    /// LLMs wrap JSON in fences and prose despite instructions. Extracting the outermost {...} is a normalization,
    /// not a repair: malformed JSON inside still fails the parser and is counted. 'modified' is a tool-use signal
    /// worth logging — a planner that needs sanitizing did not follow the output contract.
    /// </summary>
    public static class PlannerOutputSanitizer
    {
        public static bool TryExtractJson(string raw, out string json, out bool modified)
        {
            json = null;
            modified = false;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string s = raw.Trim();
            int first = s.IndexOf('{');
            int last = s.LastIndexOf('}');
            if (first < 0 || last < first) return false;

            json = s.Substring(first, last - first + 1);
            modified = json.Length != s.Length;
            return true;
        }
    }
}