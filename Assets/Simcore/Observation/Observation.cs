using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using SimCore.Engine;
using SimCore.State;

namespace SimCore.Observation
{
    public enum ObservationMode { Raw, Processed }

    /// <summary>
    /// What a planner is shown. Never the WorldState. Serialization is the LLM contract for observations,
    /// so it is defined once here: snake_case names, enums as strings, nulls omitted.
    /// </summary>
    public abstract class Observation
    {
        [JsonProperty("observation_mode")]
        [JsonConverter(typeof(StringEnumConverter))]
        public ObservationMode Mode { get; }

        [JsonProperty("trip_number")]      public int TripNumber { get; set; }
        [JsonProperty("sim_time_seconds")] public double SimTimeSeconds { get; set; }

        protected Observation(ObservationMode mode) { Mode = mode; }

        public static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new StringEnumConverter() },
            Formatting = Formatting.None
        };

        public virtual string ToJson() => JsonConvert.SerializeObject(this, JsonSettings);
    }

    public interface IObservationCompiler
    {
        Observation Compile(WorldState world, SimulationEngine engine, ObservationMode mode);
    }

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