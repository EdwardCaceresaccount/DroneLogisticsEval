namespace SimCore.Util
{
    /// <summary>
    /// Bump these when behavior changes. A result file records all three, so every historical
    /// number is traceable to the exact rules that produced it (spec §19, §38).
    /// </summary>
    public static class HarnessVersions
    {
        public const string Simulation   = "0.1.0";   // engine physics, battery, movement, collision rules
        public const string Evaluation   = "0.1.0";   // trip classification, severity, metrics definitions
        public const string ToolContract = "1.0";     // the tool_calls JSON shape and the 8-tool vocabulary
    }
}