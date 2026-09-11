using Edvanz.Domain.Interfaces;

namespace Edvanz.Application.Services;

/// <summary>
/// The priority order of the call list, and the only place that order is defined.
///
/// THE RULE: a teacher appears under their FIRST matching reason and nowhere else. That is what
/// turns nine overlapping lists into one worklist — before this, a teacher who had paid, not
/// started, and had students stranded appeared three times with no hint which mattered.
///
/// The order is by what is at stake and how perishable it is, NOT by how many teachers are in each
/// bucket. "Never started" is the biggest group by far and sits near the bottom on purpose: those
/// accounts have been stuck for weeks and will keep, while a teacher who paid on Tuesday and cannot
/// get going is a refund that is still preventable this week.
/// </summary>
public enum CallReason
{
    /// <summary>Paid recently and still has nothing that works. A refund you can still prevent.</summary>
    PaidNotStarted = 1,

    /// <summary>Actively working, subscription expired or about to. Revenue, and an easy save.</summary>
    ExpiringWhileWorking = 2,

    /// <summary>Assistants carry on, the teacher has stopped. Churn before it looks like churn.</summary>
    OwnerStopped = 3,

    /// <summary>Was working regularly, now silent. Fresher is more recoverable.</summary>
    WentQuiet = 4,

    /// <summary>Has students, none in a session — every one of them opens an empty app.</summary>
    StudentsStranded = 5,

    /// <summary>Fully configured and idle. Everything is ready; something is blocking them.</summary>
    SetUpNotRunning = 6,

    /// <summary>Registered long ago, nothing set up. High volume, low urgency.</summary>
    NeverStarted = 7,

    /// <summary>Using one module only. An upsell, not a rescue.</summary>
    OneModuleOnly = 8
}

/// <summary>
/// Maps each reason to the underlying insight query and the colour it carries.
///
/// The reasons reuse the existing, already-verified <see cref="AdminInsightKind"/> queries rather
/// than re-deriving the conditions — two definitions of "went quiet" would eventually disagree.
/// </summary>
public static class CallReasonMap
{
    /// <summary>Reasons in priority order, with the query that finds them and their severity.</summary>
    public static readonly (CallReason Reason, AdminInsightKind Kind, string Severity)[] Order =
    {
        (CallReason.PaidNotStarted,       AdminInsightKind.NewlySubscribed,      "attention"),
        (CallReason.ExpiringWhileWorking, AdminInsightKind.ExpiringWhileActive,  "attention"),
        (CallReason.OwnerStopped,         AdminInsightKind.AssistantOnly,        "attention"),
        (CallReason.WentQuiet,            AdminInsightKind.WentQuiet,            "warning"),
        (CallReason.StudentsStranded,     AdminInsightKind.SessionLessRoster,    "warning"),
        (CallReason.SetUpNotRunning,      AdminInsightKind.SetUpNotRunning,      "warning"),
        (CallReason.NeverStarted,         AdminInsightKind.NeverStarted,         "info"),
        (CallReason.OneModuleOnly,        AdminInsightKind.SingleModule,         "info")
    };
}
