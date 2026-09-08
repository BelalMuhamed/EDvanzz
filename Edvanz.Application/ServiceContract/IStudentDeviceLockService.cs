using System.Net;
using Edvanz.Application.Common;
using Edvanz.Domain.Entities;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// The result of running the device-lock gate for one request: the raw decision plus, when the
/// caller is denied, the stable failure code and HTTP status every entry point must return.
/// </summary>
/// <param name="Decision">The evaluated decision.</param>
/// <param name="FailureCode">Localization key / stable wire code when blocked; null when allowed.</param>
/// <param name="Status">HTTP status when blocked; null when allowed.</param>
public sealed record DeviceLockOutcome(
    DeviceLockDecision Decision,
    string? FailureCode,
    HttpStatusCode? Status)
{
    /// <summary>True when the caller must be refused.</summary>
    public bool IsBlocked => FailureCode is not null;

    /// <summary>Shared "allowed" result — no code, no status.</summary>
    public static readonly DeviceLockOutcome Allowed =
        new(DeviceLockDecision.Allowed, null, null);
}

/// <summary>
/// Runs the student device-lock gate INCLUDING its side effects, so every entry point (the six
/// student controllers, the aggregated home read and the in-app QR) shares one implementation and
/// cannot drift apart.
///
/// <see cref="StudentDeviceLockPolicy"/> stays pure and owns the RULE; this owns the I/O that the
/// rule implies:
///   1. loading the teacher configuration (unless the caller already has it),
///   2. performing the one-time binding migration when the device presented its former id,
///   3. notifying the teacher — at most once per cooldown window — that a student was blocked.
///
/// The lock itself is STRICT and stays that way: nothing here ever re-binds a genuinely different
/// device. See <see cref="StudentDeviceLockPolicy"/> for why.
/// </summary>
public interface IStudentDeviceLockService
{
    /// <summary>
    /// Evaluates the gate, loading the teacher configuration itself.
    /// </summary>
    /// <param name="link">The caller's ACTIVE link for this teacher (already resolved and authorized).</param>
    /// <param name="teacherId">Teacher whose content is being opened.</param>
    /// <param name="deviceId">Raw <c>X-Device-Id</c> header value.</param>
    /// <param name="previousDeviceId">Raw <c>X-Device-Id-Previous</c> header value, when sent.</param>
    Task<DeviceLockOutcome> EvaluateAsync(
        StudentTeacherLink link, long teacherId, string? deviceId, string? previousDeviceId);

    /// <summary>
    /// Same as <see cref="EvaluateAsync(StudentTeacherLink, long, string?, string?)"/> but reuses a
    /// configuration the caller has already loaded, so the gate costs no extra query on paths that
    /// need the config anyway (home aggregate, in-app QR). A null
    /// <paramref name="config"/> means "no configuration row" and fails OPEN, exactly as before.
    /// </summary>
    Task<DeviceLockOutcome> EvaluateAsync(
        StudentTeacherLink link, long teacherId, TeacherConfiguration? config,
        string? deviceId, string? previousDeviceId);
}
