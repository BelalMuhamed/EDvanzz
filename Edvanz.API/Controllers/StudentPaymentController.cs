using Edvanz.Domain.Resources;
using Microsoft.Extensions.Localization;
using System.Net;
using Edvanz.API.Attributes;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Edvanz.API.Common;

namespace Edvanz.API.Controllers;

/// <summary>
/// Payment Module (Module 4) — student-facing read endpoint.
///
/// SEPARATE CONTROLLER RATIONALE (mirrors <see cref="StudentAttendanceController"/>):
/// students carry no module claim, and the service needs (teacherId, teacherStudentId),
/// which this controller resolves from the active StudentTeacherLink.
///
/// ONE SCREEN = ONE CALL: the tracking endpoint returns the ENTIRE Payment screen —
/// header, the upcoming month, the paid section, and the overdue section — so the client
/// makes a single request (no separate summary/paid/overdue endpoints).
///
/// AUTH: [ModulePermission(roles: ["Student"], roleOnly: true)] — student role only.
///
/// SECURITY: the route's teacherId is untrusted. Before the service call the controller
/// verifies an ACTIVE StudentTeacherLink for (studentUserId, teacherId) and resolves the
/// caller's OWN teacherStudentId — a student can only ever read their own payments, only
/// under a teacher they're actually linked to. Teacher-controlled visibility
/// (StudentVisibilityPayment) is enforced in the service via PaymentViewerType.Student.
/// </summary>
[Route("api/payment/student")]
[Authorize]
public sealed class StudentPaymentController : ApiBaseController
{
    private readonly IPaymentService _paymentService;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly IStudentDeviceLockService _deviceLock;

    public StudentPaymentController(
        IPaymentService paymentService,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork, IStringLocalizer<Messages> localizer, IStudentDeviceLockService deviceLock)
    {
        _paymentService = paymentService;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _deviceLock = deviceLock;
    }

    // ──────────────────────────────────────────────────────────────────────
    // STUDENT PAYMENT TRACKING (whole screen)
    // GET /api/payment/student/teachers/{teacherId}/tracking
    // ──────────────────────────────────────────────────────────────────────
    [HttpGet("teachers/{teacherId:long}/tracking")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.StudentPaymentTrackingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyPaymentTracking([FromRoute] long teacherId)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        var result = await _paymentService.GetStudentPaymentTrackingAsync(
            teacherId, resolution.TeacherStudentId!.Value, PaymentViewerType.Student);
        return ToResponse(result);
    }

    // ──────────────────────────────────────────────────────────────────────
    // PRIVATE HELPERS
    // Replicated from StudentAttendanceController / StudentVideosController. A shared
    // CallerScopedApiBaseController (holding this resolver + StudentResolution + the error
    // helpers) would remove the duplication across the student controllers — proposed as a
    // follow-up refactor.
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the calling student's TeacherStudent.Id for the named teacher:
    /// JWT User.Id → StudentUser → active StudentTeacherLink → TeacherStudentId.
    /// Returns either the resolved id or a 401/403/404 IActionResult.
    /// </summary>
    private async Task<StudentResolution> ResolveStudentForTeacherAsync(long teacherId)
    {
        long? userId = _currentUser.UserId;
        if (userId is null)
            return StudentResolution.Error(Unauthorized());

        var studentUser = await _unitOfWork.Users
            .GetActiveStudentUserByUserIdAsync(userId.Value);
        if (studentUser is null)
            return StudentResolution.Error(NotFoundError("StudentUserNotFound"));

        var link = await _unitOfWork.Users
            .GetActiveStudentTeacherLinkAsync(studentUser.Id, teacherId);
        if (link is null || link.LinkStatus != LinkStatus.Active)
            return StudentResolution.Error(ForbiddenError("TeacherLinkNotFound"));

        if (link.TeacherStudentId is null)
            return StudentResolution.Error(ForbiddenError("StudentEnrollmentRemoved"));

        // Device lock (per teacher): reject a wrong/unregistered device before any data access.
        var deviceError = await this.CheckDeviceLockAsync(_deviceLock, _localizer, teacherId, link);
        if (deviceError is not null)
            return StudentResolution.Error(deviceError);

        return StudentResolution.Ok(link.TeacherStudentId.Value);
    }

    private IActionResult NotFoundError(string message) =>
        new ObjectResult(new { success = false, code = message, message = _localizer[message].Value })
        {
            StatusCode = (int)HttpStatusCode.NotFound,
        };

    private IActionResult ForbiddenError(string message) =>
        new ObjectResult(new { success = false, code = message, message = _localizer[message].Value })
        {
            StatusCode = (int)HttpStatusCode.Forbidden,
        };

    private readonly struct StudentResolution
    {
        public long? TeacherStudentId { get; }
        public IActionResult? ErrorResponse { get; }

        private StudentResolution(long? id, IActionResult? error)
        {
            TeacherStudentId = id;
            ErrorResponse = error;
        }

        public static StudentResolution Ok(long teacherStudentId) => new(teacherStudentId, null);
        public static StudentResolution Error(IActionResult response) => new(null, response);
    }
}
