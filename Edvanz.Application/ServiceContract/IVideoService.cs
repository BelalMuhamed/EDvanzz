using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.VideoContentManagement;
using Edvanz.Domain.Enums;
using Microsoft.AspNetCore.Http;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Defines the contract for Video Content Management Module (Module 14)
/// operations. Every method returns <see cref="Result{T}"/>; the
/// implementation never throws for business-rule violations — it returns
/// <c>Result.Failure(...)</c> with a localized message key.
///
/// MAPPING TO ENDPOINTS (Spec §3 contract table):
/// <list type="bullet">
///   <item>1 — POST /api/videos                        → <see cref="CreateVideoAsync"/></item>
///   <item>2 — POST /api/videos/{id}/scopes            → <see cref="AppendScopesAsync"/></item>
///   <item>3 — PUT  /api/videos/{id}/scopes            → <see cref="ReplaceScopesAsync"/></item>
///   <item>5 — DELETE /api/videos/{id}                 → <see cref="DeleteVideoAsync"/></item>
///   <item>6 — GET  /api/videos/teacher                → <see cref="GetTeacherVideosAsync"/></item>
///   <item>7 — GET  /api/videos/student                → <see cref="GetStudentVideosAsync"/></item>
///   <item>7b — GET /api/videos/parent                 → <see cref="GetParentVideosAsync"/></item>
///   <item>8 — POST /api/videos/{id}/start             → <see cref="StartWatchAsync"/></item>
///   <item>9 — POST /api/videos/{id}/stop              → <see cref="StopWatchAsync"/></item>
///   <item>10 — GET /api/videos/{id}/analytics         → <see cref="GetAnalyticsAsync"/></item>
/// </list>
///
/// TENANT SCOPE: every method that takes a teacherId / actingUserId trusts
/// those values to come from the JWT, never from the request body or route.
/// The controller resolves them from claims.
///
/// MODULE-ACTIVE GATE (BR-ADM-010):
/// Teacher and assistant endpoints rely on the existing
/// <c>[ModulePermission("Videos", ...)]</c> filter — same convention as
/// every other module — which checks the <c>module</c> JWT claim.
/// Student and parent endpoints have no such claim and call
/// <c>IModuleTeacherRepo.IsModuleActiveAsync</c> at runtime inside the
/// service before returning data.
/// </summary>
public interface IVideoService
{
    // ══════════════════════════════════════════════════════════════════════
    // TEACHER + ASSISTANT WRITE FLOWS
    // ══════════════════════════════════════════════════════════════════════

    /// REQ-VCM-FR-01. Story A. JSON create — the optional video photo (cover image) and optional
    /// PDF attachment are referenced by <c>fileId</c> (already uploaded via <c>POST /api/upload</c>,
    /// categories <c>VideoPhoto</c> / <c>VideoAttachment</c>) and attached atomically with the
    /// video row in ONE DB transaction (no multipart, no blob I/O here). Errors:
    /// <see cref="Domain.Constants.VideoConstants.Messages.InvalidUrl"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.UnsupportedSource"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.VideoUnitNotFound"/>,
    /// plus the registry attach errors: <c>FileNotFound</c> (404), <c>FileNotOwned</c> (403),
    /// <c>FileCategoryMismatch</c> (400), <c>FileAlreadyInUse</c> (409).
    /// </summary>
    /// <param name="teacherId">Tenant scope from JWT.</param>
    /// <param name="actingUserId">User who clicked Create — Teacher or
    /// Assistant. Stored on <c>VideoAsset.CreatedByUserId</c>.</param>
    /// <param name="request">Create payload (JSON body, incl. optional
    /// <c>VideoPhotoFileId</c>/<c>AttachmentFileId</c>).</param>
    Task<Result<CreateVideoResponse>> CreateVideoAsync(
        long teacherId, long actingUserId, CreateVideoRequest request);

    /// <summary>
    /// Appends scope rows to an existing video. Idempotent on duplicates —
    /// scopes that match an existing row are skipped via the unique index
    /// and reported in <see cref="AppendScopesResponse.ScopesSkipped"/>.
    ///
    /// Story A step 7. Errors:
    /// <see cref="Domain.Constants.VideoConstants.Messages.VideoNotFound"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.NotVideoOwner"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.ScopeShapeInvalid"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.ScopeTargetNotFoundOrForeign"/>.
    /// </summary>
    Task<Result<AppendScopesResponse>> AppendScopesAsync(
        long teacherId, long actingUserId, long videoAssetId, AssignScopesRequest request);

    /// <summary>
    /// Replaces ALL scope rows for a video. Wrapped in a single transaction:
    /// DELETE old → validate new → INSERT new. <c>VideoAnalytics</c> rows
    /// are deliberately untouched so a previously-targeted student's watch
    /// history survives reassignment (Story D).
    ///
    /// Errors as <see cref="AppendScopesAsync"/> plus
    /// <see cref="Domain.Constants.VideoConstants.Messages.ScopeCannotBeEmpty"/>.
    /// </summary>
    Task<Result<ReplaceScopesResponse>> ReplaceScopesAsync(
        long teacherId, long actingUserId, long videoAssetId, AssignScopesRequest request);

    /// <summary>
    /// Hard-deletes a video. Inside a single transaction:
    /// <list type="number">
    ///   <item>Build the audit snapshot (asset + scopes + analytics +
    ///         aggregates).</item>
    ///   <item>INSERT the <see cref="Domain.Entities.VideoAssetAudit"/> row.</item>
    ///   <item>DELETE the <see cref="Domain.Entities.VideoAsset"/> row —
    ///         NoAction FKs remove scopes, analytics, watch events.</item>
    ///   <item>COMMIT.</item>
    /// </list>
    ///
    /// REQ-VCM-BR-03. Story E.
    /// </summary>
    Task<Result<bool>> DeleteVideoAsync(
        long teacherId, long actingUserId, long videoAssetId);

    /// <summary>
    /// Sets a video's Draft/Published status and optional scheduled
    /// <c>PublishDate</c> (Track D1). The Settings-row quick toggle —
    /// distinct from the full update endpoint (G-EDIT), which also accepts
    /// these two fields alongside title/description/etc.
    /// </summary>
    Task<Result<bool>> SetVideoStatusAsync(
        long teacherId, long videoAssetId, SetVideoStatusRequest request);
    /// Updates title/description/sourceUrl/publishDate/status/unitIds
    /// (G-EDIT), plus (Phase 4) an optional teacher-set duration override and
    /// optional folded-in scope replacement, guarded by optimistic
    /// concurrency (<c>RowVersion</c>).
    ///
    /// Confirmed rule: when <c>SourceUrl</c> differs from the stored value,
    /// this is treated as a different video — <c>VideoAnalytics</c> and
    /// <c>VideoWatchEvent</c> rows reset, and <c>DurationSeconds</c> returns
    /// to 0 to be re-learned. Every other field-only edit preserves
    /// analytics.
    ///
    /// A pre-update <c>VideoAssetAudit</c> snapshot (<c>Action = "UPDATED"</c>)
    /// is written in the same transaction, mirroring the delete-time
    /// snapshot pattern.
    ///
    /// Errors: <see cref="Domain.Constants.VideoConstants.Messages.VideoNotFound"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.InvalidUrl"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.UnsupportedSource"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.VideoUnitNotFound"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.ConcurrencyConflict"/> (409),
    /// plus any error <see cref="ReplaceScopesAsync"/> can return, propagated
    /// unchanged when <c>Scopes</c> is provided.
    /// </summary>
    /// <param name="teacherId">Tenant scope from JWT.</param>
    /// <param name="actingUserId">User performing the edit — stored on the
    /// audit snapshot and passed through to scope replacement.</param>
    /// <param name="videoAssetId">Target video.</param>
    /// <param name="request">Update payload, including the concurrency token.</param>
    Task<Result<VideoDetailDto>> UpdateVideoAsync(
         long teacherId, long actingUserId, long videoAssetId, UpdateVideoRequest request);

  

    // ══════════════════════════════════════════════════════════════════════
    // TEACHER READ FLOWS
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Paged list of the teacher's own videos. Story B teacher endpoint.
    /// </summary>
    Task<Result<PaginatedResponse<List<TeacherVideoListItemDto>>>>
        GetTeacherVideosAsync(long teacherId, TeacherVideoListRequest request);

    /// <summary>
    /// Per-video analytics report. Story F. Returns one row per student in
    /// the resolved scope, including students who have never opened the
    /// video. Aggregates appear at the top level of the response.
    /// </summary>
    Task<Result<VideoAnalyticsResponse>> GetAnalyticsAsync(
        long teacherId, long videoAssetId, VideoAnalyticsRequest request);

    /// <summary>
    /// The same audience, sliced by class: one row per session with in-scope /
    /// watched / unseen / completed counts, so a teacher can see WHICH group is
    /// behind instead of only a single total. Rows are keyed by the student's own
    /// active session and therefore sum exactly to <c>TotalStudentsInScope</c>.
    /// </summary>
    Task<Result<VideoSessionWatchBreakdownDto>> GetAnalyticsBySessionAsync(
        long teacherId, long videoAssetId);

    // ══════════════════════════════════════════════════════════════════════
    // STUDENT READ + WATCH FLOWS
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Paged list of videos visible to the calling student. Story B student
    /// endpoint. Includes the student's own watch state via the analytics
    /// LEFT JOIN.
    ///
    /// The service runs the runtime module-active gate
    /// (<c>IModuleTeacherRepo.IsModuleActiveAsync</c>) per BR-ADM-010 — the
    /// student has no <c>module</c> JWT claim that the
    /// <c>[ModulePermission]</c> filter could check.
    /// </summary>
    /// <param name="teacherId">The teacher whose videos the student is
    /// listing. Resolved by the controller from the
    /// <c>StudentTeacherLink</c> for this student.</param>
    /// <param name="teacherStudentId">The student's <c>TeacherStudent.Id</c>
    /// under that teacher, resolved the same way.</param>
    Task<Result<PaginatedResponse<List<StudentVideoListItemDto>>>>
        GetStudentVideosAsync(
            long teacherId, long teacherStudentId, StudentVideoListRequest request, string? studentLanguage);

    /// <summary>
    /// V3 — the units a student can see under a teacher (those containing at least one video
    /// visible to the student, same predicate as the video list), each with per-unit counts
    /// (videos + how many carry a quiz) and the teacher's subject. Runs the runtime
    /// module-active gate. Batched, no N+1.
    /// </summary>
    /// <summary>
    /// Module-gated scalar rollup for the student home's Videos tile — total visible videos and
    /// how many are still unopened. Two COUNTs over the same visible-video predicate the student's
    /// own list uses, so the tile can never disagree with the list; deliberately cheaper than
    /// asking for a page of rows just to read its total.
    /// </summary>
    Task<Result<StudentVideoProgressDto>> GetStudentVideoProgressAsync(
        long teacherId, long teacherStudentId);

    Task<Result<List<StudentVideoUnitDto>>> GetStudentUnitsAsync(
        long teacherId, long teacherStudentId, string? studentLanguage);

    /// <summary>
    /// V3 drill-down — the student's visible videos within one unit (same enriched shape as
    /// <see cref="GetStudentVideosAsync"/>). Runs the runtime module-active gate.
    /// </summary>
    Task<Result<PaginatedResponse<List<StudentVideoListItemDto>>>> GetStudentVideosInUnitAsync(
        long teacherId, long teacherStudentId, long unitId, StudentVideoListRequest request, string? studentLanguage);

    /// <summary>
    /// Records the student's Open event and returns the embed URL + resume
    /// position. Story B step 5–9. Validates the student is in scope
    /// (BR-VCM-01) and the module is active.
    ///
    /// Errors:
    /// <see cref="Domain.Constants.VideoConstants.Messages.VideoNotFound"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.VideoNotInScope"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.ModuleDeactivated"/>.
    /// </summary>
    Task<Result<StartWatchResponse>> StartWatchAsync(
        long teacherId, long teacherStudentId, long videoAssetId, StartWatchRequest request);

    /// <summary>
    /// Records the student's Stop event with server-validated, clamped
    /// delta. Story C. Atomically increments
    /// <c>VideoAnalytics.TotalWatchSeconds</c> via
    /// <c>ExecuteUpdateAsync</c>.
    ///
    /// Errors:
    /// <see cref="Domain.Constants.VideoConstants.Messages.NoActiveSession"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.VideoNotInScope"/>,
    /// <see cref="Domain.Constants.VideoConstants.Messages.ModuleDeactivated"/>.
    /// </summary>
    Task<Result<StopWatchResponse>> StopWatchAsync(
        long teacherId, long teacherStudentId, long videoAssetId, StopWatchRequest request);

    // ══════════════════════════════════════════════════════════════════════
    // PARENT READ FLOW (Q7(a))
    // ══════════════════════════════════════════════════════════════════════

   
    #region INTEGRATION HOOKS (called by admin teacher hard-purge flow)

    /// <summary>
    /// Hard-deletes every video owned by the given teacher, writes a
    /// <see cref="Domain.Entities.VideoAssetAudit"/> snapshot for each one, and
    /// NoActions through to scopes, analytics, and watch events. Returns the
    /// count of videos purged.
    ///
    /// CALLED BY: the admin "permanent-purge teacher" flow that owns the full
    /// account-deletion transaction (e.g., a future <c>IAdminTeacherService</c>
    /// or extension on <c>IUserService</c>). This method is NOT exposed as an
    /// HTTP endpoint; it is an integration hook.
    ///
    /// TRANSACTION HANDLING: follows the project pattern (see
    /// <c>StudentUserService.InitializeStudentUserAsync</c>): inspects
    /// <c>IUnitOfWork.HasActiveTransaction</c>. When the caller already owns a
    /// transaction (the admin purge flow always will), participates in it.
    /// When called standalone (e.g., manual ops cleanup), opens its own.
    ///
    /// Audit rows survive the NoAction by design — only when the teacher account
    /// row itself is deleted (with NoAction configured to remove audits) does
    /// audit history go.
    ///
    /// Phase 2.2 decision context: <c>Teachers → VideoAssets = NO_ACTION</c> at
    /// the DB level, so the NoAction chain does not auto-fire on teacher delete.
    /// This service-layer method substitutes for that DB NoAction.
    /// </summary>
    /// <param name="teacherId">The teacher being permanently purged.</param>
    /// <param name="actingAdminUserId">The admin user performing the purge.
    /// Stored on every emitted <see cref="Domain.Entities.VideoAssetAudit"/>
    /// row's <c>DeletedByUserId</c>.</param>
    /// <returns>Result with the number of videos purged.</returns>
    Task<Result<int>> PurgeAllVideosForTeacherAsync(long teacherId, long actingAdminUserId);

    #endregion

    // ══════════════════════════════════════════════════════════════════════
    // ATTACHMENTS (Track F / §5) — Azure Blob Storage
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Replaces the video's photo (cover image) by referencing an already-uploaded registry file
    /// (<c>fileId</c> = FileObject.PublicId, category <c>VideoPhoto</c>). Attaches the new file,
    /// detaches the old (GC reaps its blob), and returns the stable gated URL. Idempotent if the
    /// current video photo id is resent. Errors: <c>VideoNotFound</c> (404), <c>FileNotFound</c> (404),
    /// <c>FileNotOwned</c> (403), <c>FileCategoryMismatch</c> (400), <c>FileAlreadyInUse</c> (409).
    /// </summary>
    Task<Result<VideoPhotoDto>> ReplaceVideoPhotoAsync(
        long teacherId, long actingUserId, long videoAssetId, Guid videoPhotoFileId);
    /// <summary>
    /// Pre-fill payload for the Edit screen — base fields + current Exam +
    /// current Scopes, everything needed to reconstruct the edit form in one
    /// call. For the read-only overview/details page (base fields +
    /// SeenStudentCount/UnseenStudentCount/CompletedStudentCount, no
    /// Exam/Scopes), use <see cref="GetVideoOverviewAsync"/> instead.
    /// </summary>
    Task<Result<VideoDetailDto>> GetVideoDetailAsync(long teacherId, long videoAssetId);

    /// <summary>
    /// Read-only overview/details payload — base fields plus the analytics
    /// summary (seen/unseen/completed student counts), no Exam/Scopes.
    /// Distinct from <see cref="GetVideoDetailAsync"/>, which is the Edit
    /// pre-fill and carries Exam/Scopes instead of analytics counts.
    /// </summary>
    Task<Result<VideoOverviewDto>> GetVideoOverviewAsync(long teacherId, long videoAssetId);

    // Unit membership is set through CreateVideoAsync / UpdateVideoAsync (`unitIds`) —
    // a video always belongs to >=1 unit, so there is no separate assign-to-units method.
}
