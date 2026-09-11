using Edvanz.Domain.Entities;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Every query behind the admin usage rollup. One named method per source (CLAUDE.md §3.1) — the
/// rollup service composes them and owns the maths, but never writes a predicate of its own.
///
/// DESIGN: each "buckets" method returns counts grouped to the UTC HOUR, not raw rows. SQL does the
/// counting; the service does the teacher-local day conversion through <c>ITimeZoneService</c>. That
/// split is what keeps the rollup both timezone-correct (CLAUDE.md §11b) and cheap enough to backfill
/// half a year in one pass.
///
/// Every method is scoped to ONE teacher. The rollup fans out per teacher on a Hangfire queue
/// (mirroring the auto-absent sweep), so memory stays bounded no matter how large the platform grows
/// and one teacher's bad data can never fail everyone else's run.
/// </summary>
public interface ITeacherUsageRepo
{
    // ── Activity sources ───────────────────────────────────────────────────────
    // Each returns [fromUtc, toUtc) counts for one module.

    /// <summary>
    /// Attendance marks. EXCLUDES <c>AttendanceRecord.IsAutoAbsent</c> rows — the nightly 02:30 sweep
    /// writes those with no human involved, and counting them would make every idle teacher on the
    /// platform look like a daily user. This exclusion is load-bearing; never relax it.
    /// Actor = <c>RecordedByUserId</c>.
    /// </summary>
    Task<IReadOnlyList<UsageActorHourBucket>> GetAttendanceBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>
    /// Payments collected, by <c>CollectedAt</c>. Excludes <c>IsDeleted</c> transactions — a reversed
    /// collection is a correction, not usage. Actor = <c>CollectedByUserId</c>.
    /// </summary>
    Task<IReadOnlyList<UsageActorHourBucket>> GetPaymentBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Videos uploaded. Actor = <c>CreatedByUserId</c>.</summary>
    Task<IReadOnlyList<UsageActorHourBucket>> GetVideoBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Online exams created. Actor = <c>CreatedByUserId</c>.</summary>
    Task<IReadOnlyList<UsageActorHourBucket>> GetOnlineExamBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Paper exam / homework templates created. Actor = <c>CreatedByUserId</c>.</summary>
    Task<IReadOnlyList<UsageActorHourBucket>> GetExamHomeworkBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Parent-portal follow-up grants. Actor = <c>RespondedByUserId</c> (null when
    /// auto-approved, which is correctly reported as unattributed rather than as the teacher).</summary>
    Task<IReadOnlyList<UsageActorHourBucket>> GetParentPortalBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Roster rows created. No actor column exists on TeacherStudent.</summary>
    Task<IReadOnlyList<UsageHourBucket>> GetStudentBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Sessions created. No actor column exists on Session.</summary>
    Task<IReadOnlyList<UsageHourBucket>> GetSessionBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Messages sent, by <c>SentAt</c>. No actor column exists on MessageLog.</summary>
    Task<IReadOnlyList<UsageHourBucket>> GetMessagingBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    // ── Point-in-time facts ────────────────────────────────────────────────────

    /// <summary>The live setup-health counts. See <see cref="UsageSetupHealth"/>.</summary>
    Task<UsageSetupHealth> GetSetupHealthAsync(long teacherId, CancellationToken ct = default);

    /// <summary>Whole-history facts a windowed query cannot answer. See <see cref="UsageAllTimeFacts"/>.</summary>
    Task<UsageAllTimeFacts> GetAllTimeFactsAsync(long teacherId, CancellationToken ct = default);

    /// <summary>
    /// The teacher's own user id plus the user ids of ALL their assistants (removed ones included —
    /// work done while employed is still assistant work), and each side's last-seen timestamp.
    /// </summary>
    Task<TeacherActorIdentities?> GetActorIdentitiesAsync(long teacherId, CancellationToken ct = default);

    /// <summary>Teacher ids to roll up: every non-deleted teacher. Ordered for stable fan-out.</summary>
    Task<IReadOnlyList<long>> GetTeacherIdsForRollupAsync(CancellationToken ct = default);

    /// <summary>Teacher ids that have no snapshot row yet — these need the full backfill window
    /// rather than the short nightly tail.</summary>
    Task<IReadOnlySet<long>> GetTeacherIdsWithoutSnapshotAsync(CancellationToken ct = default);

    // ── Persistence ────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces this teacher's rows across [fromDate, toDate] with <paramref name="rows"/>.
    /// Delete-then-insert is what makes the rollup idempotent (CLAUDE.md §6.4): re-running a window
    /// can never double-count, and a day whose activity was later corrected self-heals. Days with no
    /// activity are simply absent from <paramref name="rows"/> and end up with no row.
    /// </summary>
    Task ReplaceDaysAsync(
        long teacherId, DateOnly fromDate, DateOnly toDate,
        IReadOnlyCollection<TeacherUsageDay> rows, CancellationToken ct = default);

    /// <summary>Reads back stored day totals over a window, for the rolling figures and the sparkline.</summary>
    Task<IReadOnlyList<UsageDayTotals>> GetDayTotalsAsync(
        long teacherId, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default);

    /// <summary>The teacher's snapshot row, or null if the rollup has never run for them.</summary>
    Task<TeacherUsageSnapshot?> GetSnapshotAsync(long teacherId, CancellationToken ct = default);

    /// <summary>Inserts or updates the single snapshot row for a teacher.</summary>
    Task UpsertSnapshotAsync(TeacherUsageSnapshot snapshot, CancellationToken ct = default);
}
