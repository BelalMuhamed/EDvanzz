namespace Edvanz.Application.Options;

/// <summary>
/// Configuration for the PUBLIC parent portal (parent.edvanz.io — a PHP page that calls this API
/// server-to-server). Bound from the "ParentPortal" section, so every value can be changed
/// WITHOUT a redeploy: locally in appsettings.json, in production via App Service settings
/// (<c>ParentPortal__Enabled</c>, <c>ParentPortal__PortalKey</c>, …).
/// </summary>
public class ParentPortalOptions
{
    public const string Section = "ParentPortal";

    /// <summary>
    /// Platform-wide kill switch. When false EVERY parent-portal route short-circuits to
    /// <c>ParentPortalUnavailable</c> before any handler runs — no lookups, no writes. Default
    /// true; flip it in App Service settings to take the whole public surface down instantly.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Shared secret the PHP portal sends in the <c>X-Portal-Key</c> header on every call.
    /// NEVER committed: the real value is the App Service setting
    /// <c>ParentPortal__PortalKey</c>; appsettings.json only carries an empty placeholder.
    ///
    /// SECURITY: when this is empty the key filter rejects EVERY request (fail-closed). An
    /// unconfigured deployment therefore serves nothing rather than serving everything.
    /// </summary>
    public string PortalKey { get; set; } = string.Empty;

    /// <summary>
    /// When true the parent's NAME is required on an access request (400
    /// <c>ParentPortalNameRequired</c> when blank). Default FALSE so the API can be deployed
    /// BEFORE the portal build that collects the name: an older portal that omits it keeps
    /// working instead of 400-ing every parent at sign-in.
    ///
    /// Flip <c>ParentPortal__RequireParentName</c> to true in App Service settings once the
    /// portal drop that asks for the name is confirmed live — no redeploy needed. The length
    /// check below still applies whenever a name IS supplied, in either mode.
    /// </summary>
    public bool RequireParentName { get; set; } = false;

    /// <summary>
    /// When true the parent's PHONE is required on an access request (400
    /// <c>ParentPortalPhoneRequired</c> when blank). Default FALSE for the same deploy-ordering
    /// reason as <see cref="RequireParentName"/>: the API ships first, and a portal build that
    /// still treats the field as optional must keep working rather than 400-ing every parent.
    ///
    /// Flip <c>ParentPortal__RequirePhone</c> in App Service settings once the portal drop that
    /// marks the field required is confirmed live — no redeploy needed.
    ///
    /// WHY IT MATTERS (2026-09-11): the phone is the ONLY thing that makes access survive a lost
    /// device cookie, and it is what lets the roster-phone rule admit a parent with no teacher
    /// involvement at all. A blank phone means the parent's grant is pinned to one browser
    /// forever, and an iOS in-app browser that drops the cookie strands them even after the
    /// teacher approved.
    /// </summary>
    public bool RequirePhone { get; set; } = false;

    /// <summary>
    /// Abuse cap: access requests one DEVICE may create per rolling hour. Above it the endpoint
    /// returns <c>ParentPortalTooManyRequests</c> (429) instead of writing another row.
    /// </summary>
    public int RequestsPerDevicePerHour { get; set; } = 10;

    /// <summary>
    /// Abuse cap: access requests aimed at one TEACHER per rolling hour — stops a single teacher's
    /// inbox from being flooded even when the attacker rotates device ids.
    /// </summary>
    public int RequestsPerTeacherPerHour { get; set; } = 50;

    /// <summary>
    /// SCANNER BUDGET, per DEVICE, per rolling hour — see
    /// <c>ParentPortalService.RequestAccessAsync</c> for the full rationale.
    ///
    /// A request naming a student code that does not exist is answered HONESTLY ("we could not
    /// find this code"), because a real parent mistyping a code is far commoner than an attacker
    /// and the silent alternative stranded them forever. That honesty is only safe because it runs
    /// out: once a device has been told "not found" this many times inside an hour, the endpoint
    /// reverts to the neutral pending payload that reveals nothing, so walking a teacher's roster
    /// (codes are sequential A1..Z999) stops paying after the first handful of probes.
    ///
    /// Sized well above any real parent: the portal already caps a browser at 10 DISTINCT student
    /// codes per 30 minutes, so a parent correcting a typo never approaches this. Non-positive
    /// disables the budget, meaning always answer honestly.
    /// </summary>
    public int UnknownStudentCodeRepliesPerDevicePerHour { get; set; } = 8;

    /// <summary>
    /// The same scanner budget aimed at one TEACHER, so rotating device ids does not buy an
    /// attacker an unlimited supply of honest answers about that teacher's student codes.
    /// Non-positive disables it.
    /// </summary>
    public int UnknownStudentCodeRepliesPerTeacherPerHour { get; set; } = 40;
}
