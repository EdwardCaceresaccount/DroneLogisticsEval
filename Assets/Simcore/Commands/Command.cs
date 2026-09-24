using System.Collections.Generic;
using SimCore.Domain;

namespace SimCore.Commands
{
    /// <summary>The eight tools (D4). Names are the wire format — UPPER_SNAKE, exactly as an LLM must emit them.</summary>
    public enum CommandType { LIFT_OFF, LAND, MOVE_TO, LOAD, DELIVER, CHARGE, WAIT, SET_SPEED }

    /// <summary>One validated-shape planner action. Immutable. Constructed only via factories or the parser.</summary>
    public sealed class Command
    {
        public CommandType Type { get; }
        public GridCoord? Target { get; }      // MOVE_TO
        public string PackageId { get; }       // LOAD, DELIVER
        public double? Value { get; }          // CHARGE target_pct, WAIT seconds
        public SpeedMode? Speed { get; }       // SET_SPEED

        private Command(CommandType type, GridCoord? target = null, string packageId = null,
                        double? value = null, SpeedMode? speed = null)
        {
            Type = type; Target = target; PackageId = packageId; Value = value; Speed = speed;
        }

        public static Command LiftOff() => new(CommandType.LIFT_OFF);
        public static Command Land() => new(CommandType.LAND);
        public static Command MoveTo(int x, int y) => new(CommandType.MOVE_TO, target: new GridCoord(x, y));
        public static Command MoveTo(GridCoord c) => new(CommandType.MOVE_TO, target: c);
        public static Command Load(string packageId) => new(CommandType.LOAD, packageId: packageId);
        public static Command Deliver(string packageId) => new(CommandType.DELIVER, packageId: packageId);
        public static Command Charge(double targetPct) => new(CommandType.CHARGE, value: targetPct);
        public static Command Wait(double seconds) => new(CommandType.WAIT, value: seconds);
        public static Command SetSpeed(SpeedMode mode) => new(CommandType.SET_SPEED, speed: mode);

        /// <summary>Blocking commands occupy the engine's activity slot; the executor waits for them to finish.</summary>
        public bool IsBlocking => Type == CommandType.MOVE_TO || Type == CommandType.LOAD || Type == CommandType.WAIT;

        public override string ToString() => Type switch
        {
            CommandType.MOVE_TO   => $"MOVE_TO{Target}",
            CommandType.LOAD      => $"LOAD({PackageId})",
            CommandType.DELIVER   => $"DELIVER({PackageId})",
            CommandType.CHARGE    => $"CHARGE({Value:F0})",
            CommandType.WAIT      => $"WAIT({Value:F2})",
            CommandType.SET_SPEED => $"SET_SPEED({Speed})",
            _ => Type.ToString()
        };
    }

    /// <summary>An ordered command sequence for one trip, plus provenance. What a planner returns from a Thinking Phase.</summary>
    public sealed class Plan
    {
        public IReadOnlyList<Command> Commands { get; }
        public string PlannerId { get; }
        /// <summary>Verbatim planner output (LLM text). Kept for the event log — never used by the engine.</summary>
        public string RawPlannerOutput { get; }

        public Plan(IReadOnlyList<Command> commands, string plannerId = "unknown", string rawPlannerOutput = null)
        {
            Commands = commands ?? new List<Command>();
            PlannerId = plannerId;
            RawPlannerOutput = rawPlannerOutput;
        }

        public override string ToString() => string.Join(" → ", Commands);
    }
}