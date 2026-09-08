using Edvanz.Domain.Resources;
using Microsoft.Extensions.Localization;
using Edvanz.API.Attributes;
using Edvanz.Application.Dtos.VideoContentManagement;
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
/// Video Content Management Module (Module 14) — student-facing VIDEO-QUIZ endpoints.
///
/// Separate controller from <see cref="StudentVideosController"/> (which owns list/start/stop):
/// this one owns the quiz take/submit/retry/result/review flow for a video's
/// <c>VideoExam</c>. Same auth + resolution pattern as its siblings —
/// <c>[ModulePermission(roles: ["Student"], roleOnly: true)]</c>, and the route's
/// <c>teacherId</c> (untrusted) is resolved to the caller's <c>TeacherStudent.Id</c> via the
/// active <see cref="Domain.Entities.StudentTeacherLink"/> (403 if unlinked). The module-active
/// gate + scope/Published gate + quiz-exists check all run inside the service.
///
/// RETAKE: unlike online exams, a video quiz can be re-taken — <c>POST .../exam/retry</c> resets
/// the attempt.
/// </summary>
[Route("api/videos/student")]
[Authorize]
public sealed class StudentVideoExamsController : ApiBaseController
{
    private readonly IStudentVideoExamService _service;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly IStudentDeviceLockService _deviceLock;

    public StudentVideoExamsController(
        IStudentVideoExamService service, ICurrentUserService currentUser, IUnitOfWork unitOfWork, IStringLocalizer<Messages> localizer, IStudentDeviceLockService deviceLock)
    {
        _service = service;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _deviceLock = deviceLock;
    }

    /// <summary>
    /// Take-screen: the video's quiz questions + options (never the answer key) and the student's
    /// current attempt state.
    /// </summary>
    /// <response code="200">Take-screen returned.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">No active link with this teacher, module deactivated, or video not in scope.</response>
    /// <response code="404">No student account, video not found, or the video has no quiz.</response>
    [HttpGet("teachers/{teacherId:long}/videos/{videoAssetId:long}/exam")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<VideoExamTakeScreenDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTakeScreen([FromRoute] long teacherId, [FromRoute] long videoAssetId)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        return ToResponse(await _service.GetTakeScreenAsync(teacherId, resolution.TeacherStudentId!.Value, videoAssetId));
    }

    /// <summary>
    /// Submits + auto-grades the quiz (finalize), returning the student's stats. Retake first if
    /// already submitted (409).
    /// </summary>
    /// <response code="200">Attempt submitted; stats returned.</response>
    /// <response code="400">A submitted answer is invalid for its question / single-choice rule.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">No active link, module deactivated, or video not in scope.</response>
    /// <response code="404">No student account, video not found, or the video has no quiz.</response>
    /// <response code="409">The attempt is already submitted — retry before submitting again.</response>
    [HttpPost("teachers/{teacherId:long}/videos/{videoAssetId:long}/exam/submit")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<VideoExamStatsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Submit(
        [FromRoute] long teacherId, [FromRoute] long videoAssetId, [FromBody] SubmitVideoExamRequest request)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        return ToResponse(await _service.SubmitAsync(teacherId, resolution.TeacherStudentId!.Value, videoAssetId, request));
    }

    /// <summary>
    /// Retake ("Retry"): resets the attempt to a fresh in-progress one (prior answers/score wiped)
    /// and returns the fresh take-screen. Video-quiz only.
    /// </summary>
    /// <response code="200">Attempt reset; fresh take-screen returned.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">No active link, module deactivated, or video not in scope.</response>
    /// <response code="404">No student account, video not found, or the video has no quiz.</response>
    /// <response code="409">A concurrent write raced this reset.</response>
    [HttpPost("teachers/{teacherId:long}/videos/{videoAssetId:long}/exam/retry")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<VideoExamTakeScreenDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry([FromRoute] long teacherId, [FromRoute] long videoAssetId)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        return ToResponse(await _service.RetryAsync(teacherId, resolution.TeacherStudentId!.Value, videoAssetId));
    }

    /// <summary>Result: the student's stats for their attempt.</summary>
    /// <response code="200">Stats returned.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">No active link, module deactivated, or video not in scope.</response>
    /// <response code="404">No student account, video not found, no quiz, or no attempt recorded.</response>
    [HttpGet("teachers/{teacherId:long}/videos/{videoAssetId:long}/exam/result")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<VideoExamStatsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetResult([FromRoute] long teacherId, [FromRoute] long videoAssetId)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        return ToResponse(await _service.GetResultAsync(teacherId, resolution.TeacherStudentId!.Value, videoAssetId));
    }

    /// <summary>
    /// Review: every question with the student's selected options; the answer key
    /// (<c>isCorrect</c>) and awarded degree are revealed only once the attempt is finalized.
    /// </summary>
    /// <response code="200">Review returned.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">No active link, module deactivated, or video not in scope.</response>
    /// <response code="404">No student account, video not found, or the video has no quiz.</response>
    [HttpGet("teachers/{teacherId:long}/videos/{videoAssetId:long}/exam/review")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<VideoExamReviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReview([FromRoute] long teacherId, [FromRoute] long videoAssetId)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        return ToResponse(await _service.GetReviewAsync(teacherId, resolution.TeacherStudentId!.Value, videoAssetId));
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS — verbatim copy of StudentVideosController's resolution
    // pattern (studentUserId → active StudentTeacherLink(teacherId) → TeacherStudentId).
    // ══════════════════════════════════════════════════════════════════════

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

        return StudentResolution.Ok(link.TeacherStudentId.Value);
    }

    private IActionResult NotFoundError(string message) =>
        new ObjectResult(new { success = false, code = message, message = _localizer[message].Value }) { StatusCode = (int)HttpStatusCode.NotFound };

    private IActionResult ForbiddenError(string message) =>
        new ObjectResult(new { success = false, code = message, message = _localizer[message].Value }) { StatusCode = (int)HttpStatusCode.Forbidden };

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
