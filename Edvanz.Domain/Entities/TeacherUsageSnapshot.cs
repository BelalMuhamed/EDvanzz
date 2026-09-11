using Edvanz.Domain.Entities.ShareProp;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Entities;

/// <summary>
/// ONE row per teacher: the current answer to "is this teacher actually using Edvanz?", pre-computed
/// by the nightly rollup so the admin dashboard reads a single indexed table instead of aggregating
/// half the database on every page load.
///
/// This is a DERIVED read model. Everything here is recomputed from <see cref="TeacherUsageDay"/>
/// plus a handful of live counts; nothing else may write to it, and losing it entirely costs only one
/// job run. Treat any value here as a cache, never as a source of truth.
///
/// The three bands are three INDEPENDENT questions and the UI always shows all three — collapsing them
/// into one "health score" was considered and rejected, because "daily, attendance only, assistants
/// only" and "weekly, full product, teacher only" are different problems needing different phone calls,
/// and a single number hides exactly that.
/// </summary>
public class TeacherUsageSnapshot : BaseEntity
{
    /// <summary>The teacher. Unique — one snapshot per teacher. FK in Fluent API (CLAUDE.md §4.1).</summary>
    public long TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;

    // ── Axis 1: cadence ────────────────────────────────────────────────────────

    /// <summary>Distinct active days in the last 7.</summary>
    public int ActiveDays7 { get; set; }

    /// <summary>Distinct active days in the last 30. The band below is derived from this.</summary>
    public int ActiveDays30 { get; set; }

    /// <summary>Distinct active days in the last 90. Gives the 30-day figure a trend to sit against.</summary>
    public int ActiveDays90 { get; set; }

    /// <summary>Total writes in the last 30 days — separates "marked one student" from "ran ten classes"
    /// on days that otherwise both count as one active day.</summary>
    public int TotalWrites30 { get; set; }

    /// <summary>How often the account is worked. See <see cref="UsageCadence"/>.</summary>
    public UsageCadence Cadence { get; set; }

    // ── Axis 2: depth ──────────────────────────────────────────────────────────

    /// <summary>Modules with a real human write in the last 30 days (<see cref="UsageModules"/> mask).</summary>
    public int ModulesUsedMask { get; set; }

    /// <summary>Modules EVER used. A teacher who has stopped using payments still shows the capability
    /// here, which is the difference between "never adopted it" and "gave up on it".</summary>
    public int ModulesUsedAllTimeMask { get; set; }

    /// <summary>How much of the product is in use. See <see cref="UsageDepth"/>.</summary>
    public UsageDepth Depth { get; set; }

    // ── Axis 3: operator mix ───────────────────────────────────────────────────

    /// <summary>Who is working the account. See <see cref="OperatorMix"/>.</summary>
    public OperatorMix Operators { get; set; }

    /// <summary>Last moment the TEACHER'S OWN account did something (write or authenticated request).</summary>
    public DateTime? LastTeacherActivityAt { get; set; }

    /// <summary>Last moment any ASSISTANT did something. Compared against the field above, this is what
    /// makes <see cref="OperatorMix.AssistantsOnly"/> visible.</summary>
    public DateTime? LastAssistantActivityAt { get; set; }

    /// <summary>Assistants currently attached to this teacher (not removed).</summary>
    public int ActiveAssistantCount { get; set; }

    // ── Lifespan ───────────────────────────────────────────────────────────────

    /// <summary>First ever day of real activity. Null = never started. Also drives the "newly live" card.</summary>
    public DateTime? FirstActivityAt { get; set; }

    /// <summary>Most recent day of real activity by anyone. Null = never started.</summary>
    public DateTime? LastActivityAt { get; set; }

    // ── Setup health — the "smart has-data" check ──────────────────────────────
    // A row count is not data. A teacher with 10 students and none of them assigned to a session has
    // an inert account: sessions drive videos, online exams and attendance, so those students see an
    // empty app while every count on the old admin screen looked fine. These pairs expose that gap.

    /// <summary>Active (non-deleted) roster rows.</summary>
    public int StudentCount { get; set; }

    /// <summary>Of those, how many are actually assigned to a session. A large gap here is the single
    /// most common silent misconfiguration on the platform.</summary>
    public int StudentsAssignedToSession { get; set; }

    /// <summary>Sessions that exist.</summary>
    public int SessionCount { get; set; }

    /// <summary>Of those, how many have generated occurrences. A session with none never produces a
    /// class day, so nothing downstream of it can work.</summary>
    public int SessionsWithOccurrences { get; set; }

    /// <summary>Student app accounts connected to this teacher (Active links).</summary>
    public int LinkedAccountCount { get; set; }

    /// <summary>Of those, how many are BOUND to a roster record. An Active-but-unbound link is
    /// connected yet sees nothing, because every module joins through that FK.</summary>
    public int BoundAccountCount { get; set; }

    /// <summary>True when at least one human attendance mark has ever been made.</summary>
    public bool HasEverMarkedAttendance { get; set; }

    /// <summary>True when at least one payment has ever been collected.</summary>
    public bool HasEverCollectedPayment { get; set; }

    /// <summary>
    /// The honest answer to "does this teacher have data?" — true only when there is a roster ASSIGNED
    /// to a session that HAS occurrences. Deliberately stricter than "has rows": a session-less roster
    /// reads as not set up here, because in the app it isn't.
    /// </summary>
    public bool HasRealData { get; set; }

    // ── Provenance ─────────────────────────────────────────────────────────────

    /// <summary>When the rollup last recomputed this row (UTC). Surfaced in the UI so an admin can tell
    /// stale numbers from quiet ones.</summary>
    public DateTime ComputedAt { get; set; }
}
