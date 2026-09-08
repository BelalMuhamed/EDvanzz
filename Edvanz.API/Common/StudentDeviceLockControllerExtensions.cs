using System.Threading.Tasks;
using Edvanz.Application.Common;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Edvanz.API.Common;

/// <summary>
/// Device-lock gate shared by the student teacher-scoped controllers. Each of them already
/// resolves the active <see cref="StudentTeacherLink"/> for (student, teacher); this adds the
/// one-device check on top, delegating to <see cref="IStudentDeviceLockService"/> so the rule AND
/// its side effects (binding migration, teacher alert) live in exactly one place. Returns a
/// localized error <see cref="IActionResult"/> when the caller's device is not allowed
/// (409 registration-required / 403 mismatch / 403 missing id), or null when access may proceed.
///
/// Response body matches the controllers' existing <c>{ success, code, message }</c> shape so the
/// frontend branches on the stable <c>code</c> exactly as it does for the other resolution failures.
/// </summary>
public static class StudentDeviceLockControllerExtensions
{
    /// <summary>Reads the client's stable device id from the request header (empty string when absent).</summary>
    public static string ReadDeviceId(this ControllerBase controller) =>
        controller.Request.Headers[StudentDeviceLockPolicy.HeaderName].ToString();

    /// <summary>
    /// Reads the id this device was PREVIOUSLY known by (empty string when absent — the live
    /// 4.0.0+17 build never sends it, which is exactly the pre-existing behaviour).
    /// </summary>
    public static string ReadPreviousDeviceId(this ControllerBase controller) =>
        controller.Request.Headers[StudentDeviceLockPolicy.PreviousHeaderName].ToString();

    /// <summary>
    /// Evaluates the device lock for <paramref name="link"/> and returns a localized error
    /// response when blocked, or null when allowed.
    /// </summary>
    public static async Task<IActionResult?> CheckDeviceLockAsync(
        this ControllerBase controller,
        IStudentDeviceLockService deviceLock,
        IStringLocalizer localizer,
        long teacherId,
        StudentTeacherLink link)
    {
        var outcome = await deviceLock.EvaluateAsync(
            link, teacherId, controller.ReadDeviceId(), controller.ReadPreviousDeviceId());

        if (!outcome.IsBlocked)
            return null;

        return new ObjectResult(new
        {
            success = false,
            code = outcome.FailureCode,
            message = localizer[outcome.FailureCode!].Value
        })
        {
            StatusCode = (int)outcome.Status!.Value
        };
    }
}
