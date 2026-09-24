using SimCore.Engine;
using SimCore.State;

namespace SimCore.Observation
{
    /// <summary>The project's primary research variable (spec §10). Same world, different information.</summary>
    public enum ObservationMode { Raw, Processed }

    /// <summary>
    /// What a planner is shown. Never the WorldState itself. Block 9 delivers RawObservation and
    /// ProcessedObservation; this base class exists now so the runner's contract is final from day one.
    /// </summary>
    public abstract class Observation
    {
        public ObservationMode Mode { get; }
        public int TripNumber { get; set; }
        public double SimTimeSeconds { get; set; }

        protected Observation(ObservationMode mode) { Mode = mode; }

        /// <summary>Serialized form handed to LLM planners. Baselines may use the typed object directly.</summary>
        public abstract string ToJson();
    }

    public interface IObservationCompiler
    {
        Observation Compile(WorldState world, SimulationEngine engine, ObservationMode mode);
    }

    /// <summary>Placeholder for planners that don't read observations (scripted test doubles). Replaced in Block 9.</summary>
    public sealed class EmptyObservation : Observation
    {
        public EmptyObservation(ObservationMode mode) : base(mode) { }
        public override string ToJson() => "{}";
    }

    public sealed class NullObservationCompiler : IObservationCompiler
    {
        public Observation Compile(WorldState world, SimulationEngine engine, ObservationMode mode)
            => new EmptyObservation(mode) { TripNumber = world.TripIndex + 1, SimTimeSeconds = world.Clock.TimeSeconds };
    }
}