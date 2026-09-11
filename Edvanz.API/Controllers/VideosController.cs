using Edvanz.API.Attributes;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.VideoContentManagement;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;

namespace Edvanz.API.Controllers;

/// <summary>
/// Video Content Management Module (Module 14) — teacher and assistant
/// endpoints. Inherits <see cref="ModuleSixApiBaseController"/> for the
/// JWT-to-teacher resolution helper (<c>ResolveTeacherIdAsync</c>,
/// <c>GetActingUserId</c>, <c>TeacherNotResolved</c>).
///
/// Every endpoint enforces:
/// <list type="bullet">
///   <item><c>[Authorize]</c> — JWT required (class-level).</item>
///   <item><c>[ModulePermission(VideoConstants.ModuleName, ...)]</c> — module
///         must be active in the JWT, and (for assistants) the named
///         permission must be present in the assistant's claims. Teachers
///         pass with the module claim alone; SuperAdmin bypasses.</item>
/// </list>
///
/// TENANT SCOPE: <c>teacherId</c> always derived from JWT via
/// <see cref="ModuleSixApiBaseController.ResolveTeacherIdAsync"/>; never read
/// from the request body or route. Catalog §1.3.
///
/// STUDENT-FACING ENDPOINTS LIVE ELSEWHERE: see
/// <see cref="StudentVideosController"/>.
/// </summary>
[Route("api/videos")]
[Authorize]
public sealed class VideosController : ModuleSixApiBaseController
{
    private readonly IVideoService _service;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;

    public VideosController(
        IVideoService service,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork,
        IStringLocalizer<Domain.Resources.Messages> localizer)
        : base(currentUser, unitOfWork)
    {
        _service = service;
        _localizer = localizer;
    }
    //   Creates a new video reference for the calling teacher, optionally
    //   with scopes (sessions/groups) and an exam, all in one atomic
    //   transaction. Parses the teacher-supplied URL into
    //   (provider, externalId). If Scopes is omitted, the video is created
    //   scope-less — scope it via PUT /videos/{id}/scopes afterward.
    [HttpPost]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionManageVideos)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.CreateVideoResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateVideo([FromBody] CreateVideoRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.CreateVideoAsync(teacherId.Value, GetActingUserId(), request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENDPOINT 2 — APPEND SCOPES  (Story A step 7, idempotent on duplicates)
    // POST /api/videos/{videoAssetId}/scopes
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Adds one or more scope rows to an existing video. Duplicate rows are
    //   silently skipped (counted in scopesSkipped). Service validates each
    //   scope target belongs to the calling teacher.
    //
    // TABLES WRITTEN: VideoScopes
    //
    // SAMPLE REQUEST:
    //   POST /api/videos/42/scopes
    //   {
    //     "scopes": [
    //       { "scopeType": "IndividualStudent", "teacherStudentId": 101 },
    //       { "scopeType": "Session", "sessionId": 33 }
    //     ]
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════
    [HttpPost("{videoAssetId:long}/scopes")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionManageVideos)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.AppendScopesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AppendScopes(
        [FromRoute] long videoAssetId, [FromBody] AssignScopesRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.AppendScopesAsync(
            teacherId.Value, GetActingUserId(), videoAssetId, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENDPOINT 3 — REPLACE ALL SCOPES  (Story D, transactional)
    // PUT /api/videos/{videoAssetId}/scopes
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Atomically replaces all scope rows for a video. Old scopes are
    //   deleted; new scopes are inserted; VideoAnalytics is deliberately
    //   untouched so previously-targeted students' watch history survives.
    //
    // TABLES WRITTEN: VideoScopes (DELETE all, then INSERT new)
    //
    // ══════════════════════════════════════════════════════════════════════
    [HttpPut("{videoAssetId:long}/scopes")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionManageVideos)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.ReplaceScopesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReplaceScopes(
        [FromRoute] long videoAssetId, [FromBody] AssignScopesRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.ReplaceScopesAsync(
            teacherId.Value, GetActingUserId(), videoAssetId, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENDPOINT 5 — DELETE VIDEO  (Story E, REQ-VCM-BR-03)
    // DELETE /api/videos/{videoAssetId}
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Hard-deletes the video and writes a JSON audit snapshot in the same
    //   transaction. NoAction FKs remove scopes, analytics, and watch events.
    //
    // TABLES WRITTEN:
    //   VideoAssetAudits (INSERT 1)
    //   VideoAssets (DELETE — NoAction removes children)
    //
    // ══════════════════════════════════════════════════════════════════════
    [HttpDelete("{videoAssetId:long}")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionManageVideos)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteVideo([FromRoute] long videoAssetId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.DeleteVideoAsync(
            teacherId.Value, GetActingUserId(), videoAssetId);
        return ToResponse(result);
    }

    // WHAT IT DOES:
    //   Single edit endpoint — updates title/description/sourceUrl/
    //   publishDate/status/unitIds/duration, plus optional folded-in scope
    //   replacement, exam replace-all, and attachment replace/remove, all in
    //   one call. Guarded by optimistic concurrency — a stale RowVersion
    //   returns 409. If sourceUrl changes, VideoAnalytics/VideoWatchEvent
    //   reset and DurationSeconds returns to 0 (confirmed: a changed URL is
    //   a different video). Other field-only edits preserve analytics.
    //   PUT /videos/{id}/scopes remains available for scope-only updates
    //   that don't want to resend the whole video form.
    //
    //   JSON body (UpdateVideoRequest) — NOT multipart. Files are referenced by
    //   fileId only: `videoPhotoFileId` (cover) and `attachmentFileIds`
    //   (replace-all set; or `removeAttachment: true` to clear) — both are
    //   pre-uploaded via POST /api/upload and passed here as their opaque
    //   fileId. Blob/registry work (attach-new, detach-old) happens AFTER the
    //   field/scope/exam transaction commits — a failed file swap does not roll
    //   back an otherwise-successful edit; it surfaces in this response's
    //   `Warning` field instead. The response (VideoDetailDto) echoes the
    //   current `videoPhotoFileId` and `attachments[].id` so the edit screen can
    //   resend them on the next PUT.
    //
    // ══════════════════════════════════════════════════════════════════════
   

    [HttpPut("{videoAssetId:long}")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionManageVideos)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.VideoDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateVideo(
        [FromRoute] long videoAssetId, [FromBody] UpdateVideoRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.UpdateVideoAsync(
            teacherId.Value, GetActingUserId(), videoAssetId, request);
        return ToResponse(result);
    }
    // ══════════════════════════════════════════════════════════════════════
    // G-EDIT — GET VIDEO DETAIL (supporting endpoint, Edit pre-fill + Overview)
    // GET /api/videos/{videoAssetId}
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet("{videoAssetId:long}")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.VideoDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVideoDetail([FromRoute] long videoAssetId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.GetVideoDetailAsync(teacherId.Value, videoAssetId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // GET VIDEO OVERVIEW — read-only details page (base fields + analytics
    // summary), distinct from GET /{id} which is the Edit pre-fill
    // (base fields + Exam + Scopes, no analytics counts)
    // GET /api/videos/{videoAssetId}/overview
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet("{videoAssetId:long}/overview")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.VideoOverviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVideoOverview([FromRoute] long videoAssetId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.GetVideoOverviewAsync(teacherId.Value, videoAssetId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SET VIDEO STATUS  (Track D1 — Settings-row quick toggle, §5)
    // PATCH /api/videos/{videoAssetId}/status
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Sets Draft/Published + optional scheduled PublishDate. Distinct from
    //   the full edit form (PUT /api/videos/{id}), which also accepts these
    //   two fields for the S13 form.
    //
    // ══════════════════════════════════════════════════════════════════════
    [HttpPatch("{videoAssetId:long}/status")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionManageVideos)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetVideoStatus(
        [FromRoute] long videoAssetId, [FromBody] SetVideoStatusRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.SetVideoStatusAsync(teacherId.Value, videoAssetId, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENDPOINT 6 — TEACHER VIDEO LIST  (Story B teacher endpoint)
    // GET /api/videos/teacher
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Paged list of the calling teacher's own videos with per-video
    //   aggregates (StudentsInScope, TotalOpens). Newest first.
    //
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet("teacher")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.VideoContentManagement.TeacherVideoListItemDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTeacherVideos([FromQuery] TeacherVideoListRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.GetTeacherVideosAsync(teacherId.Value, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENDPOINT 10 — ANALYTICS REPORT  (Story F, REQ-VCM-FR-04)
    // GET /api/videos/{videoAssetId}/analytics
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Per-video analytics report. One row per student in the resolved
    //   scope, including students who never opened the video. Aggregates
    //   appear at the top level.
    //
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet("{videoAssetId:long}/analytics")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.VideoAnalyticsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAnalytics(
        [FromRoute] long videoAssetId, [FromQuery] VideoAnalyticsRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.GetAnalyticsAsync(teacherId.Value, videoAssetId, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════
    // WATCH BREAKDOWN BY SESSION
    // GET /api/videos/{videoAssetId}/analytics/by-session
    // ══════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Answers "which class is behind on this video?" — one row per session
    //   with in-scope / watched / unseen / completed counts, plus the same
    //   totals the analytics header carries. Rows are keyed by the student's
    //   own active session, so they sum exactly to TotalStudentsInScope even
    //   when a session is reachable both directly and through a scoped group.
    //   Not paginated: a teacher has tens of sessions, not thousands.
    //
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet("{videoAssetId:long}/analytics/by-session")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.VideoSessionWatchBreakdownDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAnalyticsBySession([FromRoute] long videoAssetId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _service.GetAnalyticsBySessionAsync(teacherId.Value, videoAssetId));
    }
   
    // ══════════════════════════════════════════════════════════════════════
    // REPLACE VIDEO PHOTO (cover image; formerly "thumbnail") — attach-new → detach-old
    // PUT /api/videos/{videoAssetId}/video-photo
    // ══════════════════════════════════════════════════════════════════════
    [HttpPut("{videoAssetId:long}/video-photo")]
    [ModulePermission(VideoConstants.ModuleName, VideoConstants.PermissionManageVideos)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.VideoContentManagement.VideoPhotoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReplaceVideoPhoto(
        [FromRoute] long videoAssetId, [FromBody] ReplaceVideoPhotoRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _service.ReplaceVideoPhotoAsync(
            teacherId.Value, GetActingUserId(), videoAssetId, request.VideoPhotoFileId);
        return ToResponse(result);
    }

    // Attachment download is gone — the attachment's stable gated URL (/api/files/{fileId}) is
    // returned in the video read/create responses and used directly.

    // Unit membership is set via POST /api/videos (create) and PUT /api/videos/{id}
    // (update `unitIds`) — a video always belongs to >=1 unit, so there is no
    // separate assign-to-units endpoint.

    // ══════════════════════════════════════════════════════════════════════
    // G-EDIT — GET VIDEO DETAIL
}
