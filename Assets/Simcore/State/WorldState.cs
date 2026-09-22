using System.Collections.Generic;
using SimCore.Domain;

namespace SimCore.State
{
    public enum EpisodePhase { AwaitingThinking, Thinking, Executing, Complete }

    public enum EpisodeStatus { Running, CompletedSuccess, CompletedFailure, Terminated }

    /// <summary>
    /// The single authoritative world (spec §6). One instance per episode.
    /// Everything downstream — observations, validation, evaluation, rendering —
    /// is a read-only view or a controlled transition of this object.
    /// </summary>
    public sealed class WorldState
    {
        public GridMap Map { get; }
        public SimClock Clock { get; }
        public DroneState Drone { get; } = new();
        public Dictionary<string, Package> Packages { get; } = new();

        /// <summary>0 before the first trip; increments when a trip ENDS (landing at Facility/Charger).</summary>
        public int TripIndex { get; set; }

        public EpisodePhase Phase { get; set; } = EpisodePhase.AwaitingThinking;
        public EpisodeStatus Status { get; set; } = EpisodeStatus.Running;

        public WorldState(GridMap map, SimClock clock)
        {
            Map = map;
            Clock = clock;
        }

        public int DeliveredCount()
        {
            int n = 0;
            foreach (var p in Packages.Values)
                if (p.Status == PackageStatus.Delivered) n++;
            return n;
        }
    }
}