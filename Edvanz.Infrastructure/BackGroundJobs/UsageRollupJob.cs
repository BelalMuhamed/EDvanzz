using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Edvanz.Infrastructure.BackGroundJobs;

// ════════════════════════════════════════════════════════════════════════════
// DISPATCHER — nightly, fans out one usage rollup worker per teacher
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Nightly admin-usage rollup dispatcher. Registered as a Hangfire recurring job in Program.cs
/// (default 03:15 Africa/Cairo — after the 02:30 auto-absent sweep, so the two never contend for the
/// connection pool).
///
/// FAN-OUT MODEL (mirrors <see cref="AutoAbsentDispatcherJob"/>):
/// 1. Ask the service which teachers to roll up and how far back each one needs.
/// 2. Enqueue ONE per-teacher worker on the usage-rollup queue.
///
/// Per-teacher fan-out is what keeps a platform-wide recompute bounded: memory never holds more than
/// one teacher's window, and one teacher's bad data fails only their own job rather than everyone's.
/// Each worker is idempotent, so re-running the dispatcher is always safe.
/// </summary>
public class UsageRollupDispatcherJob
{
    private readonly ITeacherUsageRollupService _service;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<UsageRollupDispatcherJob> _logger;

    public UsageRollupDispatcherJob(
        ITeacherUsageRollupService service,
        IBackgroundJobClient backgroundJobs,
        ILogger<UsageRollupDispatcherJob> logger)
    {
        _service = service;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    /// <summary>Hangfire entry point. Registered as a recurring job in Program.cs.</summary>
    public async Task RunAsync()
    {
        var plan = await _service.GetRollupPlanAsync();
        if (plan.Count == 0)
        {
            _logger.LogDebug("Usage rollup dispatcher: no teachers to roll up.");
            return;
        }

        foreach (var (teacherId, days) in plan)
            _backgroundJobs.Enqueue<IUsageRollupJob>(job => job.RollupTeacherAsync(teacherId, days));

        int backfills = plan.Count(p => p.Days > AdminInsightsConstants.RecomputeTailDays);
        _logger.LogInformation(
            "Usage rollup dispatcher: enqueued {Count} workers ({Backfills} full backfills).",
            plan.Count, backfills);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// WORKER INTERFACE — declared here so Hangfire's DI activator binds it
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Per-teacher usage rollup worker. Hangfire instantiates it through DI so each invocation gets a
/// fresh DbContext scope. Mirrors the <see cref="IAutoAbsentJob"/> pattern.
/// </summary>
public interface IUsageRollupJob
{
    /// <summary>
    /// Recomputes one teacher's trailing window. Hangfire retries on exception (3 attempts,
    /// exponential backoff); the underlying service deletes and rewrites its window, so retries are
    /// safe and produce identical results.
    /// </summary>
    [Queue(AdminInsightsConstants.UsageRollupQueue)]
    Task RollupTeacherAsync(long teacherId, int days);
}

// ════════════════════════════════════════════════════════════════════════════
// WORKER IMPLEMENTATION — thin Hangfire wrapper around the service
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Default <see cref="IUsageRollupJob"/>. All logic lives in <see cref="ITeacherUsageRollupService"/>;
/// this wrapper exists only so Hangfire serializes the invocation against an interface, and so the
/// Application layer never references Hangfire (CLAUDE.md §6.2).
/// </summary>
public class UsageRollupJob : IUsageRollupJob
{
    private readonly ITeacherUsageRollupService _service;
    private readonly ILogger<UsageRollupJob> _logger;

    public UsageRollupJob(
        ITeacherUsageRollupService service,
        ILogger<UsageRollupJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RollupTeacherAsync(long teacherId, int days)
    {
        var outcome = await _service.RollupTeacherAsync(teacherId, days);

        _logger.LogDebug(
            "Usage rollup done for teacher {TeacherId}: {Days} active days, {Writes} writes over {Window}d{Skipped}.",
            teacherId, outcome.DaysWritten, outcome.TotalWrites, days,
            outcome.Skipped ? " (skipped — teacher gone)" : string.Empty);
    }
}
