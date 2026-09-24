using System;

namespace SimCore.Engine
{
    /// <summary>
    /// Thrown when an engine primitive is called in a state the validator should have prevented.
    /// This is a HARNESS BUG, never a planner failure. If one of these appears in an
    /// evaluation run, the run is invalid and the bug must be fixed before results count.
    /// </summary>
    public sealed class SimInvariantException : Exception
    {
        public SimInvariantException(string message) : base(message) { }
    }
}