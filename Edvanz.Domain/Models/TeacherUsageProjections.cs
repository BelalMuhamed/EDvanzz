using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Interfaces;

// ════════════════════════════════════════════════════════════════════════════
// ADMIN INSIGHTS — REPOSITORY PROJECTION TYPES
// ════════════════════════════════════════════════════════════════════════════
//
// Query projections returned by ITeacherUsageRepo and IAdminInsightsRepo. They live in the
// Edvanz.Domain.Interfaces namespace alongside the interfaces, matching the convention set by
// VideoRepoProjections.cs. These are NOT DTOs — repos return these, services map them onward.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One hour of activity in a module, as counted by SQL.
///
/// THE HOUR IS THE REASON THIS TYPE EXISTS. The rollup needs counts bucketed by the TEACHER'S
/// LOCAL calendar day, but SQL holds UTC. Egypt sits at UTC+2 (UTC+3 under DST), so the local-day
/// boundary lands on a whole UTC hour — grouping to the hour is therefore lossless for that shift,
/// while collapsing millions of individual writes down to a handful of rows per day.
///
/// The conversion itself happens in C# through <c>ITimeZoneService</c>, never with a hardcoded
/// offset, so DST transitions stay correct and a future per-teacher timezone follows automatically.
/// </summary>
public sealed record UsageHourBucket(DateTime UtcHour, int Count);

/// <summary>
/// One hour of activity in a module that records WHO did it.
///
/// <paramref name="ActorUserId"/> is null when the source row carries no actor — an older record
/// written before the column existed, or a system-generated grant. Those counts are reported as
/// unattributed and are NEVER assumed to be the teacher's own: guessing would erase
/// <see cref="OperatorMix.AssistantsOnly"/>, the most valuable signal the model produces.
/// </summary>
public sealed record UsageActorHourBucket(DateTime UtcHour, long? ActorUserId, int Count);

/// <summary>
/// The live "is this account actually set up?" counts, read straight from the domain tables rather
/// than from activity history.
///
/// Each pair exists because the first number alone lies. Ten students mean nothing if none are
/// assigned to a session; a session means nothing if it never generated an occurrence; a linked
/// student account sees nothing until it is bound to a roster record. Those three gaps are the
/// platform's most common silent misconfigurations, and every one of them looked healthy on the
/// old admin screen.
/// </summary>
public sealed record UsageSetupHealth(
    int StudentCount,
    int StudentsAssignedToSession,
    int SessionCount,
    int SessionsWithOccurrences,
    int LinkedAccountCount,
    int BoundAccountCount,
    int ActiveAssistantCount);

/// <summary>
/// Facts spanning a teacher's WHOLE stored history, not a rolling window.
///
/// These exist because the snapshot is otherwise rebuilt from a 90-day read-back, and three fields
/// cannot survive that horizon. A teacher last active 100 days ago would come back with a null
/// <paramref name="LastActivityAt"/> — dropping them off the "went quiet" card, which is precisely
/// the list they belong on — and a module they stopped using four months ago would vanish from the
/// all-time mask, erasing the difference between "never adopted it" and "gave up on it".
///
/// All four are computed from the full <c>TeacherUsageDays</c> history in one aggregate.
/// </summary>
public sealed record UsageAllTimeFacts(
    bool HasEverMarkedAttendance,
    bool HasEverCollectedPayment,
    DateTime? FirstActivityAt,
    DateTime? LastActivityAt,
    int ModulesAllTimeMask);

/// <summary>
/// A single day's totals as stored in <c>TeacherUsageDays</c>, read back to compute the rolling
/// window figures on the snapshot and to draw the admin's 30-day sparkline.
/// </summary>
public sealed record UsageDayTotals(
    DateOnly ActivityDate,
    int TotalWrites,
    int ModulesMask,
    int TeacherWrites,
    int AssistantWrites);

/// <summary>
/// The identities that let the rollup tell "the teacher did this" from "an assistant did this".
/// Assistant ids include REMOVED assistants: work they did while employed is still assistant work,
/// and dropping it would retroactively rewrite history every time someone left.
/// </summary>
public sealed record TeacherActorIdentities(
    long TeacherUserId,
    IReadOnlySet<long> AssistantUserIds,
    int ActiveAssistantCount,
    DateTime? LastTeacherSeenAt,
    DateTime? LastAssistantSeenAt);
