using Edvanz.Domain.Entities.ShareProp;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Entities;

/// <summary>
/// One row per (teacher, day): what that teacher's account actually DID on that day. The fact table
/// behind every admin usage number, and the source the 30-day sparkline is drawn from.
///
/// THE DATE IS THE TEACHER'S LOCAL CALENDAR DAY, never UTC (CLAUDE.md §11b). A 9 PM Cairo write is
/// already "tomorrow" in UTC; bucketing it there would shift roughly a quarter of all evening work
/// into the wrong day and corrupt every cadence band built on top of it.
///
/// Written ONLY by the nightly <c>teacher-usage-rollup</c> job, which recomputes a rolling tail of
/// recent days rather than just yesterday — offline attendance and payment syncs land days late
/// (CLAUDE.md §7.2c), so a past day's counts legitimately change. The job is idempotent: it deletes
/// and rewrites the window per teacher, so a re-run can never double-count.
///
/// A day with no activity has NO ROW. Absence is the zero, which keeps the table proportional to real
/// usage instead of to teachers × days.
/// </summary>
public class TeacherUsageDay : BaseEntity
{
    /// <summary>The teacher. FK configured in Fluent API (CLAUDE.md §4.1).</summary>
    public long TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;

    /// <summary>The teacher-local calendar day these counts belong to.</summary>
    public DateOnly ActivityDate { get; set; }

    // ── Per-module write counts ────────────────────────────────────────────────

    /// <summary>Roster rows created.</summary>
    public int StudentWrites { get; set; }

    /// <summary>Classes created or scheduled.</summary>
    public int SessionWrites { get; set; }

    /// <summary>Attendance marks made BY A HUMAN. Auto-absent sweep rows are excluded at the source.</summary>
    public int AttendanceWrites { get; set; }

    /// <summary>Payments collected (reversed/deleted transactions excluded).</summary>
    public int PaymentWrites { get; set; }

    /// <summary>Videos uploaded.</summary>
    public int VideoWrites { get; set; }

    /// <summary>Online exams created.</summary>
    public int OnlineExamWrites { get; set; }

    /// <summary>Paper exams / homework created.</summary>
    public int ExamHomeworkWrites { get; set; }

    /// <summary>Messages sent.</summary>
    public int MessagingWrites { get; set; }

    /// <summary>Parent-portal follow-up grants.</summary>
    public int ParentPortalWrites { get; set; }

    // ── Rollup helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Which modules were touched on this day (<see cref="UsageModules"/> bit mask). Stored per day so
    /// the 30-day module set is a cheap OR-aggregate over rows instead of nine separate SUM-and-compare
    /// passes.
    /// </summary>
    public int ModulesMask { get; set; }

    /// <summary>Sum of every per-module count above. Stored so "busiest days" sorts without a nine-column
    /// expression, and so the sparkline reads one column.</summary>
    public int TotalWrites { get; set; }

    // ── Actor split (feeds the operator-mix axis) ──────────────────────────────

    /// <summary>Writes attributable to the teacher's OWN user account.</summary>
    public int TeacherWrites { get; set; }

    /// <summary>Writes attributable to any of the teacher's assistants.</summary>
    public int AssistantWrites { get; set; }

    /// <summary>
    /// Writes whose actor could not be determined — the module carries no actor column
    /// (videos, exams, messaging all record only the tenant). Counted separately and NEVER folded into
    /// <see cref="TeacherWrites"/>: guessing "the teacher did it" would erase the AssistantsOnly signal,
    /// which is the single most valuable output of this whole model.
    /// </summary>
    public int UnattributedWrites { get; set; }
}
