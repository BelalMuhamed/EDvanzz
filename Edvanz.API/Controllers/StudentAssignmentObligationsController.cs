using Edvanz.Domain.Resources;
using Microsoft.Extensions.Localization;
using Edvanz.API.Attributes;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net;

using Edvanz.API.Common;

namespace Edvanz.API.Controllers;

/// <summary>
/// Module 6 (Exams &amp; Homework) — student-facing endpoints. Separate controller from
/// <see cref="AssignmentObligationsController"/> — same rationale as
/// <see cref="StudentOnlineExamsController"/> vs <c>OnlineExamsController</c>: role-only auth
/// (no module claim for students), teacherId comes from the route (a student may link to
/// multiple teachers), resolved to teacherStudentId via the active StudentTeacherLink.
/// </summary>
[Route("api/assignmentobligations/student")]
[Authorize]
public sealed class StudentAssignmentObligationsController : ApiBaseController
{
    private readonly IExamHomeworkService _service;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly IStudentDeviceLockService _deviceLock;

    public StudentAssignmentObligationsController(
        IExamHomeworkService service, ICurrentUserService currentUser, IUnitOfWork unitOfWork, IStringLocalizer<Messages> localizer, IStudentDeviceLockService deviceLock)
    {
        _service = service;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _deviceLock = deviceLock;
    }

    /// <summary>
    /// Lists every offline exam (AssignmentType.Exam) assigned to the calling student under
    /// the given teacher, paginated, sorted by date descending.
    /// </summary>
    /// <param name="teacherId">The teacher whose exams are requested.</param>
    /// <param name="page">1-based page number. Defaults to 1.</param>
    /// <param name="pageSize">Records per page. Defaults to 20, max 100.</param>
    /// <response code="200">Paginated exam list returned.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller has no active link with this teacher, or their enrollment was removed.</response>
    /// <response code="404">Caller has no student account.</response>
    [HttpGet("teachers/{teacherId:long}/exams")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<StudentOfflineExamListItemDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyExams(
        [FromRoute] long teacherId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        return ToResponse(await _service.GetMyOfflineExamsAsync(
            teacherId, resolution.TeacherStudentId!.Value, resolution.LanguagePreference, page, pageSize));
    }
    // ──────────────────────────────────────────────────────────────────
    // PRIVATE HELPERS — verbatim copy of StudentOnlineExamsController's resolution pattern
    // ──────────────────────────────────────────────────────────────────

    private async Task<StudentResolution> ResolveStudentForTeacherAsync(long teacherId)
    {
        long? userId = _currentUser.UserId;
        if (userId is null)
            return StudentResolution.Error(Unauthorized());

        var studentUser = await _unitOfWork.Users.GetActiveStudentUserByUserIdAsync(userId.Value);
        if (studentUser is null)
            return StudentResolution.Error(NotFoundError("StudentUserNotFound"));

        var link = await _unitOfWork.Users.GetActiveStudentTeacherLinkAsync(studentUser.Id, teacherId);
        if (link is null || link.LinkStatus != LinkStatus.Active)
            return StudentResolution.Error(ForbiddenError("TeacherLinkNotFound"));

        if (link.TeacherStudentId is null)
            return StudentResolution.Error(ForbiddenError("StudentEnrollmentRemoved"));

        // Device lock (per teacher): reject a wrong/unregistered device before any data access.
        var deviceError = await this.CheckDeviceLockAsync(_deviceLock, _localizer, teacherId, link);
        if (deviceError is not null)
            return StudentResolution.Error(deviceError);

        return StudentResolution.Ok(link.TeacherStudentId.Value, studentUser.LanguagePreference);
    }

    private IActionResult NotFoundError(string message) =>
        new ObjectResult(new { success = false, code = message, message = _localizer[message].Value }) { StatusCode = (int)HttpStatusCode.NotFound };

    private IActionResult ForbiddenError(string message) =>
        new ObjectResult(new { success = false, code = message, message = _localizer[message].Value }) { StatusCode = (int)HttpStatusCode.Forbidden };

    private readonly struct StudentResolution
    {
        public long? TeacherStudentId { get; }

        /// <summary>The resolved student's language preference ("ar"/"en"), for language-aware content.</summary>
        public string? LanguagePreference { get; }
        public IActionResult? ErrorResponse { get; }

        private StudentResolution(long? id, string? languagePreference, IActionResult? error)
        {
            TeacherStudentId = id;
            LanguagePreference = languagePreference;
            ErrorResponse = error;
        }

        public static StudentResolution Ok(long teacherStudentId, string? languagePreference) =>
            new(teacherStudentId, languagePreference, null);
        public static StudentResolution Error(IActionResult response) => new(null, null, response);
    }
}