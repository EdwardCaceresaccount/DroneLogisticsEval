using SimCore.Commands;
using SimCore.State;

namespace SimCore.Eval
{
    /// <summary>
    /// Maps a finished TripOutcome's raw facts to its classification + severity (spec §16).
    /// Order matters: terminal/critical conditions are checked before productive ones, so a trip
    /// that delivered a package and then crashed is a Crash, not a success.
    /// </summary>
    public static class TripClassifier
    {
        public static void Classify(TripOutcome t)
        {
            t.Classification = Decide(t);
            t.SeverityLevel = Severity.Of(t.Classification);
        }

        private static TripClassification Decide(TripOutcome t)
        {
            if (t.InvalidPlanner)                                   return TripClassification.InvalidPlannerBehavior;
            if (t.Failure == FailureKind.Crashed)                   return TripClassification.Crash;
            if (t.Failure == FailureKind.ForcedLanding)             return TripClassification.ForcedLanding;
            if (t.TimeLimitExceeded)                                return TripClassification.TimeLimitExceeded;
            if (t.ExecutorStatus == Commands.ExecutorStatus.HaltedOnError) return TripClassification.InvalidPlannerBehavior;
            if (t.EndedAirborne)                                    return TripClassification.EndedAirborne;
            if (!t.EndedAtBase)                                     return TripClassification.EndedOffBase;

            // Safe at a base from here on.
            if (t.PackagesDelivered > 0)
                return t.EmergencyRecovery ? TripClassification.DeliveryWithEmergencyCharge : TripClassification.StrongSuccess;
            if (t.EmergencyRecovery)                                return TripClassification.ForcedLbsRecovery;
            if (t.EndTile.HasValue && t.EndTile.Value == t.StartTile) return TripClassification.SameLocationNoDelivery;
            return TripClassification.NoDeliverySafeReturn;
        }
    }
}