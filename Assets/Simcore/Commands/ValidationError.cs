using System.Collections.Generic;

namespace SimCore.Commands
{
    /// <summary>Spec §9.6 taxonomy. Category is what gets aggregated in metrics; Code is what gets grepped.</summary>
    public enum ErrorCategory
    {
        /// <summary>Malformed structure: not JSON, missing fields, wrong types, bad values.</summary>
        Schema,
        /// <summary>Well-formed call to a tool that does not exist.</summary>
        Tool,
        /// <summary>Well-formed, real tool, but illegal in the current (or projected) state.</summary>
        State,
        /// <summary>Well-formed, real tool, but references a place that does not exist in the world.</summary>
        Physical
    }

    public static class ErrorCodes
    {
        // Schema
        public const string MalformedJson         = "MALFORMED_JSON";
        public const string MissingToolCalls      = "MISSING_TOOL_CALLS";
        public const string ToolCallNotObject     = "TOOL_CALL_NOT_OBJECT";
        public const string MissingToolName       = "MISSING_TOOL_NAME";
        public const string MissingArgument       = "MISSING_ARGUMENT";
        public const string InvalidArgumentType   = "INVALID_ARGUMENT_TYPE";
        public const string InvalidArgumentValue  = "INVALID_ARGUMENT_VALUE";
        public const string UnknownArgument       = "UNKNOWN_ARGUMENT";
        public const string EmptyPlan             = "EMPTY_PLAN";
        // Tool
        public const string UnknownTool           = "UNKNOWN_TOOL";
        // State
        public const string AlreadyFlying         = "STATE_ALREADY_FLYING";
        public const string AlreadyLanded         = "STATE_ALREADY_LANDED";
        public const string NotAirborne           = "STATE_NOT_AIRBORNE";
        public const string NotLanded             = "STATE_NOT_LANDED";
        public const string NotAtFacility         = "STATE_NOT_AT_FACILITY";
        public const string NotAtCharger          = "STATE_NOT_AT_CHARGER";
        public const string NotAtDestination      = "STATE_NOT_AT_DESTINATION";
        public const string UnknownPackage        = "STATE_UNKNOWN_PACKAGE";
        public const string PackageUnavailable    = "STATE_PACKAGE_UNAVAILABLE";
        public const string PackageNotHere        = "STATE_PACKAGE_NOT_HERE";
        public const string PackageNotOnboard     = "STATE_PACKAGE_NOT_ONBOARD";
        public const string InventoryFull         = "STATE_INVENTORY_FULL";
        public const string AlreadyCharging       = "STATE_ALREADY_CHARGING";
        public const string ChargeExceedsCapacity = "STATE_CHARGE_EXCEEDS_CAPACITY";
        // Physical
        public const string OutOfBounds           = "PHYSICAL_OUT_OF_BOUNDS";
    }

    public sealed class ValidationError
    {
        public ErrorCategory Category { get; }
        public string Code { get; }
        public string Message { get; }
        /// <summary>Index into Plan.Commands, or -1 for plan-level errors.</summary>
        public int CommandIndex { get; }

        public ValidationError(ErrorCategory category, string code, string message, int commandIndex = -1)
        {
            Category = category; Code = code; Message = message; CommandIndex = commandIndex;
        }

        public override string ToString()
            => CommandIndex >= 0 ? $"[{Category}/{Code} @#{CommandIndex}] {Message}" : $"[{Category}/{Code}] {Message}";
    }

    public sealed class ValidationResult
    {
        public List<ValidationError> Errors { get; } = new();
        public bool IsValid => Errors.Count == 0;

        public void Add(ErrorCategory cat, string code, string message, int index = -1)
            => Errors.Add(new ValidationError(cat, code, message, index));

        public bool HasCode(string code)
        {
            foreach (var e in Errors) if (e.Code == code) return true;
            return false;
        }

        public override string ToString() => IsValid ? "VALID" : string.Join("\n", Errors);
    }
}