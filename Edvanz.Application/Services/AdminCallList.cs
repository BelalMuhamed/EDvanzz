using Edvanz.Domain.Interfaces;

namespace Edvanz.Application.Services;

/// <summary>
/// The priority order of the call list, and the only place that order is defined.
///
/// THE RULE: a teacher appears under their FIRST matching reason and nowhere else. That is what
/// turns overlapping lists into one worklist — before this, a teacher who had subscribed, never
/// started and had students stranded appeared three times with no hint which mattered.
///
/// The order is by what is at stake and how perishable it is, NOT by how many teachers are in each
/// bucket. <see cref="NeverSetUp"/> is much the largest group and sits near the bottom on purpose:
/// those accounts have been stuck for weeks and will keep, while a teacher who subscribed on
/// Tuesday and cannot get going is a refund that is still preventable this week.
///
/// NAMING: every member says what is true of the teacher in words a sales rep would use out loud.
/// The old names ("PaidNotStarted") were ambiguous — "paid" reads as a student's fees, not a
/// subscription — and that ambiguity reached the screen.
/// </summary>
public enum CallReason
{
    /// <summary>Subscribed recently and still has nothing that works. A refund you can still prevent.</summary>
    SubscribedNotStarted = 1,

    /// <summary>Actively teaching, subscription expired or about to. Revenue, and an easy save.</summary>
    SubscriptionEnding = 2,

    /// <summary>Assistants carry on, the teacher has stopped. Churn before it looks like churn.</summary>
    OnlyAssistantsWorking = 3,

    /// <summary>Was working regularly, now silent. Fresher is more recoverable.</summary>
    StoppedUsingIt = 4,

    /// <summary>Has students, none in a class — every one of them opens an empty app.</summary>
    StudentsSeeNothing = 5,

    /// <summary>Fully configured and idle. Everything is ready; something is blocking them.</summary>
    SetUpNeverTaught = 6,

    /// <summary>
    /// Running fine, but paying for features they have never opened. The adoption gap — the only
    /// reason on this list that is a conversation about value rather than a rescue, and the one
    /// that needs the teacher's OWN entitlement to be meaningful.
    /// </summary>
    PayingForUnusedFeatures = 7,

    /// <summary>Subscribed long ago, nothing set up. High volume, low urgency.</summary>
    NeverSetUp = 8
}

/// <summary>
/// Maps each reason to the underlying insight query and the colour it carries.
///
/// The reasons reuse the existing, already-verified <see cref="AdminInsightKind"/> queries rather
/// than re-deriving the conditions — two definitions of "stopped using it" would eventually disagree.
/// </summary>
public static class CallReasonMap
{
    /// <summary>Reasons in priority order, with the query that finds them and their severity.</summary>
    public static readonly (CallReason Reason, AdminInsightKind Kind, string Severity)[] Order =
    {
        (CallReason.SubscribedNotStarted,   AdminInsightKind.NewlySubscribed,      "attention"),
        (CallReason.SubscriptionEnding,     AdminInsightKind.ExpiringWhileActive,  "attention"),
        (CallReason.OnlyAssistantsWorking,  AdminInsightKind.AssistantOnly,        "attention"),
        (CallReason.StoppedUsingIt,         AdminInsightKind.WentQuiet,            "warning"),
        (CallReason.StudentsSeeNothing,     AdminInsightKind.SessionLessRoster,    "warning"),
        (CallReason.SetUpNeverTaught,       AdminInsightKind.SetUpNotRunning,      "warning"),
        (CallReason.PayingForUnusedFeatures,AdminInsightKind.UnusedEntitlements,   "info"),
        (CallReason.NeverSetUp,             AdminInsightKind.NeverStarted,         "info")
    };
}
