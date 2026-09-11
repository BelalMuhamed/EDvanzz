using Edvanz.Domain.Constants;

namespace Edvanz.Application.Options;

/// <summary>
/// Configuration for the nightly admin usage rollup (UsageRollupJob / TeacherUsageRollupService).
/// Bound from the appsettings.json section "AdminInsights" so behaviour can be tuned WITHOUT a code
/// change — locally via appsettings.json, on Azure App Service via application settings
/// "AdminInsights__Enabled" / "AdminInsights__BackfillDays" etc. (a save restarts the app; no
/// redeploy, no DDL). Defaults below apply when the section, or any single key, is absent.
///
/// WHY EACH KNOB EXISTS:
/// - <see cref="Enabled"/> is the kill switch. The rollup fans out one worker per teacher, and its
///   FIRST run backfills half a year for every teacher at once — by far the heaviest thing this job
///   ever does. Being able to stop it instantly, without unscheduling or redeploying, is the
///   difference between a five-second fix and an incident. Mirrors AutoAbsentOptions.Enabled.
/// - <see cref="BackfillDays"/> bounds that first run. Lower it if the initial pass proves too heavy
///   for the database tier; the only cost of a smaller window is a shorter history on the charts,
///   and raising it later simply backfills more on the next run.
/// - <see cref="RecomputeTailDays"/> is the nightly window. It must stay comfortably larger than the
///   longest realistic offline-sync delay: attendance and payments collected with no connectivity
///   arrive days late (CLAUDE.md §7.2c), and any day inside this tail self-heals on the next run.
///   A day that falls out of the tail before its late writes land keeps the stale count forever.
/// - <see cref="CronExpression"/> schedules the dispatcher, evaluated in Africa/Cairo.
///
/// NOTE: nothing here changes what the numbers MEAN — the band thresholds live in
/// <see cref="AdminInsightsConstants"/> and are deliberately code, not configuration, so a tuning
/// change is reviewable rather than something that silently reclassifies every teacher.
/// </summary>
public class AdminInsightsOptions
{
    public const string Section = "AdminInsights";

    /// <summary>Master on/off switch for the nightly rollup. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How far back the FIRST run reaches for a teacher who has never been rolled up. Default 180.
    /// Clamped to [1, 730] at use.
    /// </summary>
    public int BackfillDays { get; set; } = AdminInsightsConstants.BackfillDays;

    /// <summary>
    /// How many recent days each nightly run recomputes for a teacher who already has a snapshot.
    /// Default 7. Clamped to [1, 90] at use.
    /// </summary>
    public int RecomputeTailDays { get; set; } = AdminInsightsConstants.RecomputeTailDays;

    /// <summary>
    /// Cron for the recurring dispatcher, evaluated in Africa/Cairo. Default "15 3 * * *" (03:15) —
    /// off-peak, and after the 02:30 auto-absent sweep so the two never contend for the pool.
    /// </summary>
    public string CronExpression { get; set; } = AdminInsightsConstants.DefaultCronExpression;
}
