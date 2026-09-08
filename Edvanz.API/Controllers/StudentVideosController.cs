using Edvanz.Domain.Resources;
using Microsoft.Extensions.Localization;
using System.Net;
using Edvanz.API.Attributes;
using Edvanz.Application.Dtos.VideoContentManagement;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Edvanz.API.Common;

namespace Edvanz.API.Controllers;

/// <summary>
/// Video Content Management Module (Module 14) — student-facing endpoints.
///
/// SEPARATE CONTROLLER RATIONALE:
/// Lives apart from <see cref="VideosController"/> because the auth and
/// resolution patterns differ:
/// <list type="bullet">
///   <item>Auth: <c>[ModulePermission(roles: ["Student"], roleOnly: true)]</c>
///         instead of the module/permission combo used by teacher endpoints —
///         students have no <c>module</c> JWT claim. Same separation pattern
///         used by Module 1 (StudentUserController vs TeacherStudentController).</item>
///   <item>Resolution: JWT carries <c>User.Id</c>, but the service needs
///         <c>(teacherId, teacherStudentId)</c>. The route supplies
///         <c>teacherId</c> directly (the student picks which teacher's
///         videos they're listing). The controller resolves
///         <c>teacherStudentId</c> via the active <c>StudentTeacherLink</c>
///         for that (<c>studentUserId</c>, <c>teacherId</c>) pair.</item>
///   <item>Module-active gate: runs INSIDE the service via
///         <c>IModuleTeacherRepo.IsModuleActiveAsync</c>, NOT through the
///         <c>[ModulePermission]</c> filter. BR-ADM-010 instantaneous
///         deactivation must apply to students immediately on every request,
///         not at JWT issue time.</item>
/// </list>
///
/// SECURITY:
/// The route's <c>teacherId</c> is untrusted input. Before any service call,
/// the controller verifies an ACTIVE <see cref="Domain.Entities.StudentTeacherLink"/>
/// exists for (<c>studentUserId</c>, <c>teacherId</c>). Without that link
/// the student has no relationship with the teacher and the route returns
/// 403 — they cannot list, start, or stop the teacher's videos.
/// </summary>
[Route("api/videos/student")]
[Authorize]
public sealed class StudentVideosController : ApiBaseController
{
    private readonly IVideoService _service;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly IStudentDeviceLockService _deviceLock;

    public StudentVideosController(
        IVideoService service,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork, IStringLocalizer<Messages> localizer, IStudentDeviceLockService deviceLock)
    {
        _service = service;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _deviceLock = deviceLock;
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENDPOINT 7 — STUDENT VIDEO LIST  (Story B student endpoint)
    // GET /api/videos/student/teachers/{teacherId}
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Paged list of videos the calling student can access from the named
    //   teacher. Includes the student's own watch state (HasOpened,
    //   LastOpenedAt) via the analytics LEFT JOIN.
    //
    // SAMPLE REQUEST:
    //   GET /api/videos/student/teachers/42?page=1&pageSize=20
    //
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet("teachers/{teacherId:long}")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.VideoContentManagement.StudentVideoListItemDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentVideos(
        [FromRoute] long teacherId, [FromQuery] StudentVideoListRequest request)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        var result = await _service.GetStudentVideosAsync(
            teacherId, resolution.TeacherStudentId!.Value, request, resolution.LanguagePreference);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENDPOINT 7b — STUDENT UNITS  (V3)
    // GET /api/videos/student/teachers/{teacherId}/units
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   The units the calling student can see under the named teacher (those
    //   containing at least one video visible to the student), each with
    //   per-unit counts (videos + how many carry a quiz) and the subject.
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet("teachers/{teacherId:long}/units")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.List<Edvanz.Application.Dtos.VideoContentManagement.StudentVideoUnitDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentUnits([FromRoute] long teacherId)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        return ToResponse(await _service.GetStudentUnitsAsync(
            teacherId, resolution.TeacherStudentId!.Value, resolution.LanguagePreference));
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENDPOINT 7c — STUDENT VIDEOS IN A UNIT  (V3 drill-down)
    // GET /api/videos/student/teachers/{teacherId}/units/{unitId}/videos
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet("teachers/{teacherId:long}/units/{unitId:long}/videos")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.VideoContentManagement.StudentVideoListItemDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentVideosInUnit(
        [FromRoute] long teacherId, [FromRoute] long unitId, [FromQuery] StudentVideoListRequest request)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        return ToResponse(await _service.GetStudentVideosInUnitAsync(
            teacherId, resolution.TeacherStudentId!.Value, unitId, request, resolution.LanguagePreference));
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENDPOINT 8 — START WATCH  (Story B steps 5–9)
    // POST /api/videos/student/teachers/{teacherId}/{videoAssetId}/start
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Records the student's Open event. Returns the embed URL Flutter
    //   loads into its secure WebView, plus the resume position.
    //
    // TABLES WRITTEN:
    //   VideoAnalytics (UPSERT — increment OpenCount or insert first row)
    //   VideoWatchEvents (INSERT 1, EventType = Open)
    //   VideoAssets (DurationSeconds — only if first-time or within tolerance)
    //
    // SAMPLE REQUEST:
    //   POST /api/videos/student/teachers/42/501/start
    //   {
    //     "deviceId": "f8b3a02e-...",
    //     "videoDurationSeconds": 612,
    //     "clientEventId": "11111111-2222-3333-4444-555555555555"
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════
    [HttpPost("teachers/{teacherId:long}/{videoAssetId:long}/start")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.StartWatchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StartWatch(
        [FromRoute] long teacherId,
        [FromRoute] long videoAssetId,
        [FromBody] StartWatchRequest request)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        var result = await _service.StartWatchAsync(
            teacherId, resolution.TeacherStudentId!.Value, videoAssetId, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENDPOINT 9 — STOP WATCH  (Story C, REQ-VCM-FR-03)
    // POST /api/videos/student/teachers/{teacherId}/{videoAssetId}/stop
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Records the student's Stop event with server-validated, clamped
    //   delta. Increments TotalWatchSeconds atomically.
    //
    // TABLES WRITTEN:
    //   VideoAnalytics (atomic UPDATE — TotalWatchSeconds, LastResumePositionSeconds)
    //   VideoWatchEvents (INSERT 1, EventType = Stop)
    //
    // SAMPLE REQUEST:
    //   POST /api/videos/student/teachers/42/501/stop
    //   {
    //     "deviceId": "f8b3a02e-...",
    //     "positionSeconds": 245,
    //     "deltaSeconds": 38,
    //     "clientEventId": "66666666-7777-8888-9999-aaaaaaaaaaaa"
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════
    [HttpPost("teachers/{teacherId:long}/{videoAssetId:long}/stop")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.StopWatchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StopWatch(
        [FromRoute] long teacherId,
        [FromRoute] long videoAssetId,
        [FromBody] StopWatchRequest request)
    {
        var resolution = await ResolveStudentForTeacherAsync(teacherId);
        if (resolution.ErrorResponse is not null) return resolution.ErrorResponse;

        var result = await _service.StopWatchAsync(
            teacherId, resolution.TeacherStudentId!.Value, videoAssetId, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the calling student's <c>TeacherStudent.Id</c> for the named
    /// teacher. Three-step lookup:
    /// <list type="number">
    ///   <item>JWT <c>User.Id</c> → <c>StudentUser.Id</c> via
    ///         <c>GetActiveStudentUserByUserIdAsync</c>.</item>
    ///   <item>(<c>StudentUser.Id</c>, <c>teacherId</c>) → active
    ///         <c>StudentTeacherLink</c> via
    ///         <c>GetActiveStudentTeacherLinkAsync</c>.</item>
    ///   <item>Link must have non-null <c>TeacherStudentId</c>; otherwise
    ///         the teacher has removed the student record (degraded link
    ///         state — student kept the dashboard tile but lost data
    ///         access).</item>
    /// </list>
    ///
    /// Returns a discriminated result: either the resolved id or a 403/404
    /// IActionResult. Centralizing this helper avoids three copies in the
    /// three student endpoints.
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

        return StudentResolution.Ok(link.TeacherStudentId.Value, studentUser.LanguagePreference);
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

    /// <summary>
    /// Discriminated-union result for <see cref="ResolveStudentForTeacherAsync"/>.
    /// Exactly one of <see cref="TeacherStudentId"/> or
    /// <see cref="ErrorResponse"/> is non-null.
    /// </summary>
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

        public static StudentResolution Ok(long teacherStudentId, string? languagePreference)
            => new(teacherStudentId, languagePreference, null);

        public static StudentResolution Error(IActionResult response)
            => new(null, null, response);
    }
}
