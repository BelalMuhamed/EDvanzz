namespace Edvanz.Application.Options;

/// <summary>
/// Configuration for the student "one device per teacher" lock. Bound from the appsettings.json
/// section "DeviceLock" so behaviour can be changed WITHOUT a deploy — on Azure App Service via
/// application settings "DeviceLock__Enabled" / "DeviceLock__BlockNotificationCooldownHours"
/// (a save restarts the app; no redeploy, no DDL). Defaults below apply when the section is absent,
/// and reproduce the pre-2026-09-08 behaviour exactly.
///
/// WHY EACH KNOB EXISTS:
/// - <see cref="Enabled"/> is the kill switch. The recovery path for a wrongly-blocked student is
///   thin (only the teacher, or a SuperAdmin, can reset a binding), so there must be a way to stand
///   the whole feature down instantly during an incident without waiting on a store release or a
///   redeploy. It overrides every teacher's IsDeviceLockEnabled toggle.
/// - <see cref="BlockNotificationCooldownHours"/> throttles the "your student was blocked" alert to
///   the teacher. A blocked app retries on every screen, so without a cooldown one student could
///   generate dozens of pushes; the stamp lives on StudentTeacherLink.DeviceBlockNotifiedAt and is
///   cleared whenever the teacher resets the device.
/// </summary>
public class DeviceLockOptions
{
    public const string Section = "DeviceLock";

    /// <summary>Platform-wide master switch for device-lock ENFORCEMENT. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimum hours between two "student blocked on a new device" notifications for the same
    /// link. Default 6. Clamped to >= 1 at use.
    /// </summary>
    public int BlockNotificationCooldownHours { get; set; } = 6;
}
