namespace Edvanz.Application.Options;

/// <summary>
/// Configuration for the nightly auto-absent sweep (AutoAbsentJob / AttendanceAutoAbsentService).
/// Bound from the appsettings.json section "AutoAbsent" so behaviour can be tuned WITHOUT a code
/// change — locally via appsettings.json, on Azure App Service via application settings
/// "AutoAbsent__Enabled" / "AutoAbsent__EffectiveFrom" etc. (a save restarts the app; no redeploy,
/// no DDL). Defaults below apply when the section (or a key) is absent.
///
/// WHY EACH KNOB EXISTS:
/// - <see cref="Enabled"/> is the kill switch — flip it off to stop the sweep instantly in an
///   incident without an unschedule/redeploy.
/// - <see cref="EffectiveFrom"/> makes the feature NON-retroactive: the sweep NEVER marks absences
///   for class days before this date. On first deploy this prevents a mass back-write of every
///   unmarked historical occurrence. Set it to the go-live date.
/// - <see cref="LookbackDays"/> bounds the rolling catch-up window (from each teacher's local today
///   backwards) so a multi-day outage still fills in the missed days, while the query stays cheap.
///   Never marks older than <see cref="EffectiveFrom"/> regardless of this window.
/// </summary>
public class AutoAbsentOptions
{
    public const string Section = "AutoAbsent";

    /// <summary>Master on/off switch for the nightly sweep. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The earliest class-day date the sweep may mark absent. Occurrences dated before this
    /// (teacher-local) are ignored forever. Default 2026-07-24 (the feature go-live date) — set to
    /// your own deploy date. Parsed as a plain calendar date (time component ignored).
    /// </summary>
    public DateTime EffectiveFrom { get; set; } = new DateTime(2026, 7, 24);

    /// <summary>
    /// Rolling catch-up window in days, counted back from each teacher's local today. A day older
    /// than this is not swept even if unmarked (it stays a "scheduled/unmarked" null cell). Default
    /// 14 — comfortably survives a multi-day job/hosting outage. Clamped to >= 1 at use.
    /// </summary>
    public int LookbackDays { get; set; } = 14;

    /// <summary>
    /// Cron for the recurring dispatcher, evaluated in Africa/Cairo. Default "30 2 * * *" (02:30) —
    /// off-peak and after the day has fully closed for the Cairo-anchored tenant base.
    /// </summary>
    public string CronExpression { get; set; } = "30 2 * * *";

    /// <summary>
    /// Days of grace AFTER a class day has closed before the sweep may infer an absence on it.
    ///
    /// WHY: attendance is routinely taken with no connectivity, and the queued marks only reach the
    /// server when the app is next open and online. At 0 (the pre-2026-09-10 behaviour) an evening
    /// class was swept ~6 hours later, overnight, while the app was closed — so the sweep wrote
    /// absences for a class the tutor HAD already marked, and the tutor's real marks then had to
    /// fight those rows on the way in. 1 moves the sweep to the second night after the class, well
    /// past any realistic sync delay, while a genuinely unmarked day still becomes Absent
    /// automatically — just a day later.
    ///
    /// Clamped to >= 0 at use. Set 0 to restore the old timing (nothing else changes).
    /// </summary>
    public int GraceDays { get; set; } = 1;
}
