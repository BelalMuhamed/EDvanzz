namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Recomputes the admin usage model — the per-day activity facts and the per-teacher snapshot behind
/// the three axes (cadence, module depth, operator mix) and the setup-health figures.
///
/// Run nightly by the <c>teacher-usage-rollup</c> Hangfire job, which fans out one call to
/// <see cref="RollupTeacherAsync"/> per teacher. Also callable on demand from the admin API when
/// someone needs a teacher's numbers refreshed immediately rather than tomorrow.
///
/// EVERYTHING HERE IS DERIVED. No other code writes the usage tables, and losing them entirely costs
/// one job run — so a rollup may always be re-run, and re-running it must never change the answer.
/// </summary>
public interface ITeacherUsageRollupService
{
    /// <summary>
    /// Recomputes one teacher's usage over the trailing <paramref name="days"/> and rewrites their
    /// snapshot.
    ///
    /// IDEMPOTENT (CLAUDE.md §6.4): the window is deleted and rewritten, so a retry or an overlapping
    /// run can never double-count. Pass a long window to backfill history, a short one for the nightly
    /// pass — the work is identical either way.
    /// </summary>
    /// <param name="teacherId">The teacher to recompute.</param>
    /// <param name="days">How many trailing days to recompute, counted in the teacher's local calendar.</param>
    /// <returns>What was written, for the job's log line.</returns>
    Task<UsageRollupOutcome> RollupTeacherAsync(long teacherId, int days, CancellationToken ct = default);

    /// <summary>
    /// Recomputes ONLY the entitlement mask for one teacher — cheap, no activity queries.
    ///
    /// Called the moment a module is granted or revoked, so a feature granted this morning does not
    /// read as an unused entitlement until the nightly run. Best-effort by contract: an admin's
    /// grant must never fail because a derived cache could not be refreshed.
    /// </summary>
    Task RefreshEntitlementAsync(long teacherId, CancellationToken ct = default);

    /// <summary>
    /// The teachers the nightly dispatcher should enqueue, each paired with the window it needs:
    /// the short recompute tail normally, or the full backfill window for a teacher who has no
    /// snapshot yet (a new account, or the very first run after deploy).
    /// </summary>
    Task<IReadOnlyList<(long TeacherId, int Days)>> GetRollupPlanAsync(CancellationToken ct = default);
}

/// <summary>What a single teacher's rollup produced. Logged by the job; not a wire contract.</summary>
/// <param name="TeacherId">The teacher recomputed.</param>
/// <param name="DaysWritten">Days that had activity and therefore got a row. Days with none get none.</param>
/// <param name="TotalWrites">Total writes counted across the window.</param>
/// <param name="Skipped">True when the teacher could not be resolved (purged mid-run); nothing was written.</param>
public sealed record UsageRollupOutcome(long TeacherId, int DaysWritten, int TotalWrites, bool Skipped);
