using System;
using SimCore.Domain;

namespace SimCore.Engine
{
    /// <summary>Blocking physical activities. Exactly one at a time. (Charging is NOT here — see D10.)</summary>
    public enum ActivityKind { Idle, Moving, Loading, Waiting }

    /// <summary>The engine's current blocking activity and its progress.</summary>
    public sealed class DroneActivity
    {
        public ActivityKind Kind { get; private set; } = ActivityKind.Idle;
        public WorldPos MoveTarget { get; private set; }
        public string LoadingPackageId { get; private set; }
        public double SecondsRemaining { get; private set; }

        internal void SetMoving(WorldPos target)
        {
            Kind = ActivityKind.Moving;
            MoveTarget = target;
            LoadingPackageId = null;
            SecondsRemaining = 0;
        }

        internal void SetLoading(string packageId, double seconds)
        {
            Kind = ActivityKind.Loading;
            LoadingPackageId = packageId;
            SecondsRemaining = seconds;
        }

        internal void SetWaiting(double seconds)
        {
            Kind = ActivityKind.Waiting;
            LoadingPackageId = null;
            SecondsRemaining = seconds;
        }

        internal void SetIdle()
        {
            Kind = ActivityKind.Idle;
            LoadingPackageId = null;
            SecondsRemaining = 0;
        }

        internal void Elapse(double dt) => SecondsRemaining = Math.Max(0, SecondsRemaining - dt);
    }
}