using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.VideoContentManagement;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Net;
using System.Text.Json;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements all Video Content Management Module (Module 14) operations.
/// Returns <see cref="Result{T}"/> for every flow; never throws on
/// business-rule violations.
///
/// ARCHITECTURAL NOTES:
/// <list type="bullet">
///   <item>All data access goes through <see cref="IUnitOfWork"/>'s named
///         repo methods — no raw expression predicates here.</item>
///   <item>Scope resolution delegates to <see cref="IVideoScopeResolver"/>.</item>
///   <item>URL parsing delegates to <see cref="IVideoUrlParser"/>.</item>
/// </list>
///
/// TRANSACTION POLICY (mirrors <c>StudentUserService</c>):
/// Methods that write multiple rows inspect
/// <c>IUnitOfWork.HasActiveTransaction</c>. When the caller already owns a
/// transaction (e.g., admin teacher-purge), this service participates. When
/// called standalone (the normal HTTP path), it manages its own.
///
/// RACE HANDLING — first-time open INSERT path:
/// <see cref="StartWatchAsync"/> uses a bounded retry loop around the
/// <c>VideoAnalytics</c> INSERT. Two simultaneous Open events from one
/// student (phone + tablet) race to insert; the unique index
/// <c>UX_VideoAnalytics_Video_Student</c> rejects the second; the service
/// catches <c>DbUpdateException</c> with a SQL Server unique-violation
/// number and retries via the increment branch (the row now exists).
///
/// PARENT ENDPOINT — DEFERRED (Q1(c)):
/// v1 ships without a parent endpoint. The student endpoint plus the
/// existing ParentChild → StudentTeacherLink / ParentChildTeacherLink data
/// model is sufficient to add it later without DB changes.
/// </summary>
public sealed class VideoService : IVideoService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IVideoScopeResolver _scopeResolver;
    private readonly IVideoUrlParser _urlParser;
    private readonly IContentPublishNotificationDispatcher _publishNotifications;
    private readonly ISubscriptionGateService _subscriptionGate;
    private readonly IFileAccessService _fileAccess;
    private readonly IVideoUnitService _videoUnitService;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;

    public VideoService(
        IUnitOfWork unitOfWork,
        IVideoScopeResolver scopeResolver,
        IVideoUrlParser urlParser,
        IContentPublishNotificationDispatcher publishNotifications,
        ISubscriptionGateService subscriptionGate,
        IFileAccessService fileAccess,
        IVideoUnitService videoUnitService,
        IStringLocalizer<Domain.Resources.Messages> localizer)
    {
        _unitOfWork = unitOfWork;
        _scopeResolver = scopeResolver;
        _urlParser = urlParser;
        _publishNotifications = publishNotifications;
        _subscriptionGate = subscriptionGate;
        _fileAccess = fileAccess;
        _videoUnitService = videoUnitService;
        _localizer = localizer;
    }



    // ══════════════════════════════════════════════════════════════════════
    // APPEND SCOPES (Story A step 7, idempotent on duplicates)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<AppendScopesResponse>> AppendScopesAsync(
        long teacherId, long actingUserId, long videoAssetId, AssignScopesRequest request)
    {
        if (request.Scopes is null || request.Scopes.Count == 0)
            return Result<AppendScopesResponse>.Failure(
                _localizer, VideoConstants.Messages.ScopeCannotBeEmpty, HttpStatusCode.BadRequest);

        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<AppendScopesResponse>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        var shapeError = ValidateScopeShape(request.Scopes);
        if (shapeError is not null)
            return Result<AppendScopesResponse>.Failure(
                _localizer, shapeError, HttpStatusCode.BadRequest);

        var ownershipError = await ValidateScopeOwnershipAsync(teacherId, request.Scopes);
        if (ownershipError is not null)
            return Result<AppendScopesResponse>.Failure(
                _localizer, ownershipError, HttpStatusCode.BadRequest);

        // Containment: the appended targets must stay within the video's units' coverage.
        var appendUnitIds = await _unitOfWork.VideoAssetsRepo.GetLinkedUnitIdsAsync(videoAssetId);
        var appendContainment = await EnsureVideoScopeWithinUnitsAsync(
            teacherId, video.Title, appendUnitIds, request.Scopes);
        if (!appendContainment.IsSuccess)
            return Result<AppendScopesResponse>.Failure(appendContainment);

        // Pre-load existing scope rows so we can dedupe client-side rather
        // than catching unique-violation exceptions per row. The DB unique
        // index UX_VideoScopes_Video_Type_Target remains as a safety net.
        var existing = await _unitOfWork.VideoAssetsRepo
            .GetVideoWithScopesAsync(videoAssetId, teacherId);
        // existing != null because we already verified ownership above.

        int skipped = 0;
        var rowsToAdd = new List<VideoScope>();
        var utcNow = DateTime.UtcNow;

        foreach (var input in request.Scopes)
        {
            bool isDuplicate = existing!.Scopes.Any(s =>
                s.ScopeType == input.ScopeType
             && s.SessionId == input.SessionId
             && s.SessionGroupId == input.SessionGroupId);

            if (isDuplicate)
            {
                skipped++;
                continue;
            }

            rowsToAdd.Add(BuildScopeEntity(input, video, actingUserId, utcNow));
        }

        if (rowsToAdd.Count > 0)
        {
            await _unitOfWork.VideoAssetsRepo.AddScopesAsync(rowsToAdd);
            await _unitOfWork.SaveChangesAsync();

            // AUDIENCE CHANGE announces too (2026-09-09). Publishing to group A notified A; adding
            // group B a week later notified NOBODY, because every announce site keyed on the video
            // becoming visible and it already was. The students who just gained access were exactly
            // the ones never told. Safe to fan out over the WHOLE audience: the job re-resolves
            // recipients at run time and the per-recipient idempotency check (plus the unique index)
            // means everyone already notified is skipped, so only the new group receives anything.
            // Only when students can actually see it - a Draft or future-dated video announces on
            // publish, not here.
            bool visibleToStudents = video.Status == VideoStatus.Published
                && (video.PublishDate is null || video.PublishDate <= DateTime.UtcNow);
            if (visibleToStudents)
                AnnounceVideoPublished(teacherId, videoAssetId, video.PublishDate);
        }

        // Dedup student count across the FULL scope set (existing + new).
        var allScopes = existing!.Scopes.Concat(rowsToAdd).ToList();
        var resolved = await _scopeResolver.ResolveFromPersistedScopesAsync(teacherId, allScopes);

        return Result<AppendScopesResponse>.Success(new AppendScopesResponse
        {
            ScopesAdded = rowsToAdd.Count,
            ScopesSkipped = skipped,
            StudentsInScope = resolved.Count,
        }, _localizer, VideoConstants.Messages.ScopesAssigned);
    }

    // ══════════════════════════════════════════════════════════════════════
    // REPLACE SCOPES (Story D, transactional)
    // ══════════════════════════════════════════════════════════════════════
    /// <inheritdoc />
    public async Task<Result<ReplaceScopesResponse>> ReplaceScopesAsync(
        long teacherId, long actingUserId, long videoAssetId, AssignScopesRequest request)
    {
        if (request.Scopes is null || request.Scopes.Count == 0)
            return Result<ReplaceScopesResponse>.Failure(
                _localizer, VideoConstants.Messages.ScopeCannotBeEmpty, HttpStatusCode.BadRequest);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        return await ReplaceScopesInternalAsync(
            teacherId, actingUserId, videoAssetId, request.Scopes, ownsTransaction);
    }

    /// <summary>
    /// Transactional body of scope replacement: video lookup → shape
    /// validation → ownership validation → delete-all → insert-new →
    /// resolve StudentsInScope. Extracted from <see cref="ReplaceScopesAsync"/>
    /// so Phase 4's extended <c>UpdateVideoAsync</c> can call it inside its
    /// own already-open transaction (<paramref name="ownsTransaction"/> =
    /// <c>false</c>) when the update request carries a non-null
    /// <c>Scopes</c> list — one code path for both the dedicated
    /// PUT-scopes endpoint and a folded-in scope replace during full update.
    ///
    /// Every validation/error path is unchanged from the pre-refactor
    /// inline version (<c>ScopeCannotBeEmpty</c> stays in the public
    /// wrapper since it's a request-shape check independent of any video;
    /// everything from video lookup onward — including
    /// <c>ScopeShapeInvalid</c> and <c>ScopeTargetNotFoundOrForeign</c> —
    /// lives here so it re-runs correctly under Phase 4's shared
    /// transaction). <c>VideoAnalytics</c> remains deliberately untouched.
    /// </summary>
    private async Task<Result<ReplaceScopesResponse>> ReplaceScopesInternalAsync(
        long teacherId, long actingUserId, long videoAssetId,
        List<VideoScopeInputDto> scopes, bool ownsTransaction)
    {
        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            return Result<ReplaceScopesResponse>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);
        }

        var shapeError = ValidateScopeShape(scopes);
        if (shapeError is not null)
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            return Result<ReplaceScopesResponse>.Failure(
                _localizer, shapeError, HttpStatusCode.BadRequest);
        }

        var ownershipError = await ValidateScopeOwnershipAsync(teacherId, scopes);
        if (ownershipError is not null)
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            return Result<ReplaceScopesResponse>.Failure(
                _localizer, ownershipError, HttpStatusCode.BadRequest);
        }

        // Containment: the replacement targets must stay within the video's units'
        // coverage. Covers the standalone PUT /videos/{id}/scopes path (the folded-in
        // update path also pre-validates the final state before this runs).
        var replaceUnitIds = await _unitOfWork.VideoAssetsRepo.GetLinkedUnitIdsAsync(videoAssetId);
        var replaceContainment = await EnsureVideoScopeWithinUnitsAsync(
            teacherId, video.Title, replaceUnitIds, scopes);
        if (!replaceContainment.IsSuccess)
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            return Result<ReplaceScopesResponse>.Failure(replaceContainment);
        }

        int oldCount = await _unitOfWork.VideoAssetsRepo.CountScopesForVideoAsync(videoAssetId);

        try
        {
            await _unitOfWork.VideoAssetsRepo.DeleteAllScopesForVideoAsync(videoAssetId);

            var utcNow = DateTime.UtcNow;
            var newRows = scopes
                .Select(input => BuildScopeEntity(input, video, actingUserId, utcNow))
                .ToList();

            await _unitOfWork.VideoAssetsRepo.AddScopesAsync(newRows);
            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            // VideoAnalytics is deliberately untouched so history survives
            // reassignment (Story D).
            var resolved = await _scopeResolver.ResolveFromPersistedScopesAsync(teacherId, newRows);

            return Result<ReplaceScopesResponse>.Success(new ReplaceScopesResponse
            {
                ScopesAdded = newRows.Count,
                ScopesRemoved = oldCount,
                StudentsInScope = resolved.Count,
            }, _localizer, VideoConstants.Messages.ScopesReplaced);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // DELETE VIDEO (Story E, transactional with audit snapshot)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<bool>> DeleteVideoAsync(
        long teacherId, long actingUserId, long videoAssetId)
    {
        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoWithScopesAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<bool>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            // Build the snapshot BEFORE the NoAction fires — once we delete,
            // the rows are gone forever (REQ-VCM-BR-03).
            var analyticsRows = await _unitOfWork.VideoAssetsRepo
                .GetAnalyticsForAuditSnapshotAsync(videoAssetId);
            var aggregates = await _unitOfWork.VideoAssetsRepo
                .GetAnalyticsAggregatesAsync(teacherId, videoAssetId);

            var utcNow = DateTime.UtcNow;
            string snapshotJson = BuildAuditSnapshot(
                video, analyticsRows, aggregates, actingUserId, utcNow);

            var audit = new VideoAssetAudit
            {
                VideoAssetId = video.Id,
                TeacherId = teacherId,
                Action = VideoAuditAction.HardDelete,
                SnapshotJson = snapshotJson,
                SnapshotArchiveUrl = null,
                DeletedByUserId = actingUserId,
                DeletedAt = utcNow,
                CreateAt = utcNow,
            };

            await _unitOfWork.VideoAssetsRepo.AddAuditAsync(audit);

            // GetVideoWithScopesAsync is AsNoTracking. Re-fetch tracked for the Remove call.
            var trackedVideo = await _unitOfWork.VideoAssetsRepo
                .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
            if (trackedVideo is null)
            {
                if (ownsTransaction)
                    await _unitOfWork.RollbackAsync();
                return Result<bool>.Failure(
                    _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);
            }

            // Detach this video's registry files (video photo, attachments, exam-question images) so
            // the GC reaps their blobs — fixes the historical orphan bug (delete left blobs behind).
            await DetachVideoFilesAsync(videoAssetId, trackedVideo.VideoPhotoFileId);

            await _unitOfWork.VideoAssetsRepo.DeleteVideoAsync(trackedVideo);
            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            return Result<bool>.Success(true, _localizer, VideoConstants.Messages.VideoDeleted);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Detaches every registry file a video references — video photo, attachments, and exam-question
    /// images — so the GC reaps their blobs. Runs inside the caller's transaction (no SaveChanges).
    /// </summary>
    private async Task DetachVideoFilesAsync(long videoAssetId, long? videoPhotoFileId)
    {
        await _fileAccess.DetachAsync(videoPhotoFileId);

        foreach (var att in await _unitOfWork.FileObjectsRepo.GetVideoAttachmentsAsync(videoAssetId))
            await _fileAccess.DetachAsync(att.Id);

        foreach (long imageId in await _unitOfWork.VideoAssetsRepo.GetExamQuestionImageFileIdsAsync(videoAssetId))
            await _fileAccess.DetachAsync(imageId);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SET VIDEO STATUS (Track D1 — Settings-row quick toggle)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<bool>> SetVideoStatusAsync(
        long teacherId, long videoAssetId, SetVideoStatusRequest request)
    {
        // Read the pre-change state so only a real Draft→Published transition
        // announces anything. Re-saving an already-published video must stay silent.
        var before = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        // VISIBLE TO STUDENTS, not merely Published (2026-09-09) - the same predicate UpdateVideoAsync
        // uses and the same one the student queries and the file gate use. Reading Status alone meant a
        // video Published FOR A FUTURE DATE counted as already published, so releasing it early (status
        // Published + publishDate null, which SetVideoStatusAsync writes as NULL) announced NOTHING at
        // the moment it went live - while the delayed job queued for the original date still fired days
        // later, telling students about "new" content they had already had. Announcing here also makes
        // that stale job harmless: the per-recipient idempotency check finds everyone already notified.
        bool wasVisibleToStudents = before is not null
            && before.Status == VideoStatus.Published
            && (before.PublishDate is null || before.PublishDate <= DateTime.UtcNow);

        bool updated = await _unitOfWork.VideoAssetsRepo.SetVideoStatusAsync(
            videoAssetId, teacherId, request.Status, request.PublishDate);

        if (!updated)
            return Result<bool>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        if (!wasVisibleToStudents && request.Status == VideoStatus.Published)
            AnnounceVideoPublished(teacherId, videoAssetId, request.PublishDate);

        return Result<bool>.Success(true, _localizer, VideoConstants.Messages.VideoStatusUpdated);
    }

    public async Task<Result<VideoDetailDto>> UpdateVideoAsync(
       long teacherId, long actingUserId, long videoAssetId, UpdateVideoRequest request)
    {
        // Step 1 — fetch the tracked entity (unchanged).
        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<VideoDetailDto>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        string title = request.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
            return Result<VideoDetailDto>.Failure(
                _localizer, VideoConstants.Messages.InvalidUrl, HttpStatusCode.BadRequest);

        // Captured BEFORE any mutation: only a real transition into "visible to
        // students" announces anything, so editing an already-published video
        // stays silent (the per-recipient dedupe would catch a repeat anyway,
        // but there is no reason to queue the work).
        bool wasVisibleToStudents = video.Status == VideoStatus.Published
            && (video.PublishDate is null || video.PublishDate <= DateTime.UtcNow);

        // Step 2 — in-application concurrency check, before any mutation.
        if (!request.RowVersion.AsSpan().SequenceEqual(video.RowVersion))
            return Result<VideoDetailDto>.Failure(
                _localizer, VideoConstants.Messages.ConcurrencyConflict, HttpStatusCode.Conflict);

        // Files were already uploaded/validated via POST /api/upload; here we only reference by id.

        if (request.Exam is not null)
        {
            var examShapeError = ValidateExamShape(request.Exam);
            if (examShapeError is not null)
                return Result<VideoDetailDto>.Failure(_localizer, examShapeError, HttpStatusCode.BadRequest);
        }
        // Step 3 — pre-update audit snapshot, built from data as it stood
        // BEFORE any mutation below. GetVideoByIdAndTeacherAsync doesn't
        // load Scopes (it's the tracked-for-mutation fetch), so scopes are
        // read separately via the same AsNoTracking method DeleteVideoAsync
        // uses for its own snapshot.
        var snapshotSource = await _unitOfWork.VideoAssetsRepo
            .GetVideoWithScopesAsync(videoAssetId, teacherId);
        var linkedUnitIdsBeforeUpdate = await _unitOfWork.VideoAssetsRepo
            .GetLinkedUnitIdsAsync(videoAssetId);
        var analyticsRows = await _unitOfWork.VideoAssetsRepo
            .GetAnalyticsForAuditSnapshotAsync(videoAssetId);
        var aggregates = await _unitOfWork.VideoAssetsRepo
            .GetAnalyticsAggregatesAsync(teacherId, videoAssetId);

        var utcNow = DateTime.UtcNow;
        string snapshotJson = BuildAuditSnapshot(
            snapshotSource!, analyticsRows, aggregates, actingUserId, utcNow,
            VideoAuditAction.Updated, linkedUnitIdsBeforeUpdate);

        // Step 4 — begin transaction (reentrant, same pattern as elsewhere).
        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            await _unitOfWork.VideoAssetsRepo.AddAuditAsync(new VideoAssetAudit
            {
                VideoAssetId = video.Id,
                TeacherId = teacherId,
                Action = VideoAuditAction.Updated,
                SnapshotJson = snapshotJson,
                SnapshotArchiveUrl = null,
                DeletedByUserId = actingUserId,
                DeletedAt = utcNow,
                CreateAt = utcNow,
            });

            // Step 5 — title/description (unchanged existing logic).
            video.Title = title;
            video.Description = request.Description?.Trim();

            // Step 6 — sourceUrl change detection + analytics reset
            // (UNCHANGED existing rule — do not alter).
            string newSourceUrl = request.SourceUrl.Trim();
            bool sourceUrlChanged = !string.Equals(
                newSourceUrl, video.SourceUrl, StringComparison.Ordinal);

            if (sourceUrlChanged)
            {
                var parseOutcome = _urlParser.Parse(newSourceUrl);
                if (!parseOutcome.IsSuccess)
                {
                    string urlErrorKey = parseOutcome.Failure switch
                    {
                        VideoUrlParseFailure.UnsupportedSource => VideoConstants.Messages.UnsupportedSource,
                        _ => VideoConstants.Messages.InvalidUrl,
                    };
                    if (ownsTransaction)
                        await _unitOfWork.RollbackAsync();
                    return Result<VideoDetailDto>.Failure(_localizer, urlErrorKey, HttpStatusCode.BadRequest);
                }

                // Same rule as create. Deliberately inside the changed-URL
                // branch: a teacher editing the title of an OLD Drive-backed
                // video must still be able to save. Only pointing a video AT a
                // Drive link is refused.
                if (parseOutcome.Success!.SourceType != VideoSourceType.YouTube)
                {
                    if (ownsTransaction)
                        await _unitOfWork.RollbackAsync();
                    return Result<VideoDetailDto>.Failure(
                        _localizer, VideoConstants.Messages.VideoSourceMustBeYouTube, HttpStatusCode.BadRequest);
                }

                video.SourceUrl = newSourceUrl;
                video.SourceType = parseOutcome.Success!.SourceType;
                video.ExternalId = parseOutcome.Success.ExternalId;

                // Confirmed rule: a changed URL is a different video — watch
                // history resets and duration is re-learned from scratch.
                await _unitOfWork.VideoAssetsRepo.ResetAnalyticsForVideoAsync(videoAssetId);
                video.DurationSeconds = 0;
                video.IsDurationManuallySet = false;
            }

            // Step 7 — publishDate/status.
            //
            // An absent PublishDate means "leave the schedule alone", NOT "clear it".
            // This used to assign unconditionally, and no edit screen sends the field
            // (the simple form shows the date read-only), so renaming a video
            // scheduled for next week nulled its date and pushed it live at once —
            // and announced it to students on the spot, since the dispatcher only
            // defers while a future date exists. Removing a schedule is now an
            // explicit request; PATCH /status keeps its own "null = publish now" rule.
            if (request.ClearPublishDate)
                video.PublishDate = null;
            else if (request.PublishDate.HasValue)
                video.PublishDate = request.PublishDate;

            if (request.Status.HasValue)
                video.Status = request.Status.Value;

            // Step 8 — new: explicit duration override wins over the
            // sourceUrl-change reset above (applied after it).
            if (request.DurationSeconds.HasValue)
            {
                video.DurationSeconds = request.DurationSeconds.Value;
                video.IsDurationManuallySet = true;
            }

            // ── Containment: after this update the video must still belong to >=1
            //    unit and keep its scope within its units' coverage. Compute the
            //    FINAL units/scopes (a null request field = leave unchanged) and
            //    validate BEFORE writing. An empty UnitIds list = "unlink all",
            //    which the helper rejects as VideoMustBelongToUnit.
            var finalUnitIds = request.UnitIds is not null
                ? request.UnitIds.Distinct().ToList()
                : await _unitOfWork.VideoAssetsRepo.GetLinkedUnitIdsAsync(videoAssetId);

            List<VideoScopeInputDto> finalScopes;
            if (request.Scopes is not null)
                finalScopes = request.Scopes;
            else
            {
                var existingWithScopes = await _unitOfWork.VideoAssetsRepo
                    .GetVideoWithScopesAsync(videoAssetId, teacherId);
                finalScopes = existingWithScopes!.Scopes
                    .Select(s => new VideoScopeInputDto
                    {
                        ScopeType = s.ScopeType,
                        SessionId = s.SessionId,
                        SessionGroupId = s.SessionGroupId,
                    }).ToList();
            }

            if (request.UnitIds is not null)
            {
                var updUnitOwnershipError = await ValidateUnitIdsOwnershipAsync(teacherId, finalUnitIds);
                if (updUnitOwnershipError is not null)
                {
                    if (ownsTransaction) await _unitOfWork.RollbackAsync();
                    return Result<VideoDetailDto>.Failure(
                        _localizer, updUnitOwnershipError, HttpStatusCode.BadRequest);
                }
            }

            var updContainment = await EnsureVideoScopeWithinUnitsAsync(
                teacherId, request.Title, finalUnitIds, finalScopes);
            if (!updContainment.IsSuccess)
            {
                if (ownsTransaction) await _unitOfWork.RollbackAsync();
                return Result<VideoDetailDto>.Failure(updContainment);
            }

            // Step 9 — new: unit links. null = untouched; empty list = unlink all.
            if (request.UnitIds is not null)
            {
                var unitOwnershipError = await ValidateUnitIdsOwnershipAsync(teacherId, request.UnitIds);
                if (unitOwnershipError is not null)
                {
                    if (ownsTransaction)
                        await _unitOfWork.RollbackAsync();
                    return Result<VideoDetailDto>.Failure(
                        _localizer, unitOwnershipError, HttpStatusCode.BadRequest);
                }

                await _unitOfWork.VideoAssetsRepo.ReplaceUnitLinksAsync(videoAssetId, request.UnitIds);
            }

            // Step 10 — new: optional scope replacement, folded into this
            // transaction via the Phase 2 shared helper.
            if (request.Scopes is not null)
            {
                var scopesResult = await ReplaceScopesInternalAsync(
                    teacherId, actingUserId, videoAssetId, request.Scopes, ownsTransaction: false);

                if (!scopesResult.IsSuccess)
                {
                    if (ownsTransaction)
                        await _unitOfWork.RollbackAsync();
                    // Message is already localized by ReplaceScopesInternalAsync —
                    // propagate as-is rather than re-keying through the localizer
                    // (same convention as ExamHomeworkService.AddStudentsAsync).

                    return Result<VideoDetailDto>.Failure(
                        scopesResult.Message ?? string.Empty, scopesResult.StatusCode);
                }
            }

            // Step 10a — video photo reference. null = leave as-is; a new id repoints the FK and
            // detaches the old video photo file (GC reaps its blob). Same id = idempotent no-op.
            if (request.VideoPhotoFileId is Guid newPhotoId)
            {
                var currentPhotoPublicId = await CurrentPublicIdAsync(video.VideoPhotoFileId);
                if (currentPhotoPublicId != newPhotoId)
                {
                    var attach = await AttachFileAsync(
                        newPhotoId, FileCategory.VideoPhoto, teacherId, actingUserId);
                    if (!attach.IsSuccess)
                    {
                        if (ownsTransaction) await _unitOfWork.RollbackAsync();
                        return Result<VideoDetailDto>.Failure(attach.Message ?? string.Empty, attach.StatusCode);
                    }
                    await _fileAccess.DetachAsync(video.VideoPhotoFileId);
                    video.VideoPhotoFileId = attach.Data!.Id;
                }
            }

            // Step 10b — attachments replace-all, same null/empty/list semantics as Scopes and
            // UnitIds: null = untouched; [] = remove all; [ids] = the exact new set (kept ids
            // untouched, missing detached — GC reaps their blobs — new ones attached).
            // RemoveAttachment=true with a null list also clears all (wire-compat alias).
            if (request.AttachmentFileIds is not null)
            {
                var desired = request.AttachmentFileIds.Distinct().ToList();
                if (desired.Count > VideoConstants.MaxAttachmentsPerVideo)
                {
                    if (ownsTransaction) await _unitOfWork.RollbackAsync();
                    return Result<VideoDetailDto>.Failure(
                        _localizer, VideoConstants.Messages.AttachmentsLimitExceeded, HttpStatusCode.UnprocessableEntity);
                }

                var current = await _unitOfWork.FileObjectsRepo.GetVideoAttachmentsAsync(videoAssetId);
                var desiredSet = desired.ToHashSet();
                var currentSet = current.Select(a => a.PublicId).ToHashSet();

                foreach (var att in current.Where(a => !desiredSet.Contains(a.PublicId)))
                    await _fileAccess.DetachAsync(att.Id);

                foreach (Guid attId in desired.Where(id => !currentSet.Contains(id)))
                {
                    var attach = await AttachFileAsync(
                        attId, FileCategory.VideoAttachment, teacherId, actingUserId, videoAssetId);
                    if (!attach.IsSuccess)
                    {
                        if (ownsTransaction) await _unitOfWork.RollbackAsync();
                        return Result<VideoDetailDto>.Failure(attach.Message ?? string.Empty, attach.StatusCode);
                    }
                }
            }
            else if (request.RemoveAttachment)
            {
                foreach (var att in await _unitOfWork.FileObjectsRepo.GetVideoAttachmentsAsync(videoAssetId))
                    await _fileAccess.DetachAsync(att.Id);
            }

            // Step 10c — exam replace-all. null = untouched. Detach the old questions' images, delete
            // the old exam tree, insert fresh, then attach the new questions' images.
            if (request.Exam is not null)
            {
                foreach (long oldImageId in await _unitOfWork.VideoAssetsRepo.GetExamQuestionImageFileIdsAsync(videoAssetId))
                    await _fileAccess.DetachAsync(oldImageId);

                await _unitOfWork.VideoAssetsRepo.DeleteExamForVideoAsync(videoAssetId);

                // The quiz is being replaced — prior student attempts answered a now-different quiz,
                // so they are void. Purge them (report → answers → options) in this transaction.
                // (Video / teacher hard-delete is handled by the CASCADE FK off VideoAsset instead.)
                await _unitOfWork.StudentVideoExamReportsRepo.PurgeReportsForVideoAsync(videoAssetId);

                var newExam = BuildExamEntity(request.Exam, videoAssetId, teacherId, actingUserId, utcNow);
                await _unitOfWork.VideoAssetsRepo.AddExamAsync(newExam);
                await _unitOfWork.SaveChangesAsync(); // generate question ids

                var imgError = await AttachExamQuestionImagesAsync(request.Exam, newExam, teacherId, actingUserId);
                if (imgError is not null)
                {
                    if (ownsTransaction) await _unitOfWork.RollbackAsync();
                    return Result<VideoDetailDto>.Failure(imgError.Message ?? string.Empty, imgError.StatusCode);
                }
            }

            // Step 11 — stamp UpdatedAt.
            video.UpdatedAt = utcNow;

            // Step 12 — SaveChangesAsync is also where EF Core's RowVersion
            // token gets checked at the SQL level (WHERE RowVersion =
            // @original) — second-layer safety net on top of Step 2's
            // in-application check, covering the race where two edits pass
            // Step 2 concurrently.
            try
            {
                await _unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (ownsTransaction)
                    await _unitOfWork.RollbackAsync();
                return Result<VideoDetailDto>.Failure(
                    _localizer, VideoConstants.Messages.ConcurrencyConflict, HttpStatusCode.Conflict);
            }

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }

        // Post-commit side effect (§5.1 ordering): the edit is durable before any
        // student is told about it, so a queue failure can never announce a video
        // whose save was rolled back.
        if (!wasVisibleToStudents && video.Status == VideoStatus.Published)
            AnnounceVideoPublished(teacherId, videoAssetId, video.PublishDate);

        // All file changes are part of the committed transaction now — no post-commit blob work,
        // so no partial-failure "warning" path. Resolve the final attachment set for the response.
        var currentAttachments = await _unitOfWork.FileObjectsRepo
            .GetVideoAttachmentsAsync(videoAssetId);

        var responseDto = MapToDetailDto(video);
        responseDto.Attachments = currentAttachments.Select(ToAttachmentDto).ToList();

        // Mirror MapVideoBaseAsync — the update response is the edit screen's refresh, so it
        // carries the current photo's fileId + gated URL too.
        Domain.Entities.FileObject? currentPhoto = video.VideoPhotoFileId is null
            ? null
            : await _unitOfWork.FileObjectsRepo.GetByIdAsync(video.VideoPhotoFileId.Value);
        responseDto.VideoPhotoFileId = currentPhoto?.PublicId;
        responseDto.VideoPhotoUrl = currentPhoto is null ? null : _fileAccess.BuildGatedUrl(currentPhoto.PublicId);

        return Result<VideoDetailDto>.Success(
            responseDto, _localizer, VideoConstants.Messages.VideoUpdated);
    }

    /// <summary>Returns the PublicId of a FileObject by its internal id, or null.</summary>
    private async Task<Guid?> CurrentPublicIdAsync(long? fileObjectId)
    {
        if (fileObjectId is null)
            return null;
        var file = await _unitOfWork.FileObjectsRepo.GetByIdAsync(fileObjectId.Value);
        return file?.PublicId;
    }

    // ══════════════════════════════════════════════════════════════════════
    // GET VIDEO DETAIL (G-EDIT supporting read)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<VideoDetailDto>> GetVideoDetailAsync(long teacherId, long videoAssetId)
    {
        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<VideoDetailDto>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        var dto = await MapVideoBaseAsync<VideoDetailDto>(video);

        var withScopes = await _unitOfWork.VideoAssetsRepo
            .GetVideoWithScopesAsync(videoAssetId, teacherId);
        if (withScopes is not null && withScopes.Scopes.Count > 0)
        {
            dto.Scopes = withScopes.Scopes
                .GroupBy(s => s.ScopeType)
                .Select(g => new CreateVideoScopeDto
                {
                    ScopeType = g.Key,
                    Ids = g.Key == VideoScopeType.Session
                        ? g.Select(s => s.SessionId!.Value).ToList()
                        : g.Select(s => s.SessionGroupId!.Value).ToList(),
                })
                .ToList();
        }

        // Unit membership + the scope options this video may pick from (the union of
        // its units' coverage) — folded in so the Edit screen needs one call, not a
        // separate allowed-scope-targets request.
        var unitIds = await _unitOfWork.VideoAssetsRepo.GetLinkedUnitIdsAsync(videoAssetId);
        dto.UnitIds = unitIds;
        var allowed = await _videoUnitService.GetAllowedScopeTargetsAsync(teacherId, unitIds);
        if (allowed.IsSuccess && allowed.Data is not null)
            dto.AllowedScopeTargets = allowed.Data;

        var exam = await _unitOfWork.VideoAssetsRepo.GetExamWithQuestionsAsync(videoAssetId, teacherId);
        if (exam is not null)
        {
            // PERF: batch-resolve every question-image FileObject in ONE query. This used to be
            // an N+1 — a GetByIdAsync per question inside a foreach. BuildGatedUrl is pure string
            // work off the loaded PublicIds (same batching approach as the student/teacher lists).
            var imageFileIds = exam.Questions
                .Where(q => q.ImageFileId is not null)
                .Select(q => q.ImageFileId!.Value)
                .Distinct()
                .ToList();
            var imagesById = imageFileIds.Count == 0
                ? new Dictionary<long, Domain.Entities.FileObject>()
                : (await _unitOfWork.FileObjectsRepo.GetByIdsAsync(imageFileIds)).ToDictionary(f => f.Id);

            var questions = exam.Questions.Select(q =>
            {
                Domain.Entities.FileObject? image =
                    q.ImageFileId is long fid && imagesById.TryGetValue(fid, out var f) ? f : null;

                return new CreateExamQuestionDto
                {
                    Text = q.Text,
                    QuestionType = q.QuestionType,
                    ImageFileId = image?.PublicId,
                    ImageUrl = image is null ? null : _fileAccess.BuildGatedUrl(image.PublicId),
                    Options = q.Options.Select(o => new CreateExamQuestionOptionDto
                    {
                        Text = o.Text,
                        IsCorrect = o.IsCorrect,
                    }).ToList(),
                };
            }).ToList();

            dto.Exam = new CreateExamDto
            {
                Title = exam.Title,
                Description = exam.Description,
                Questions = questions,
            };
        }

        return Result<VideoDetailDto>.Success(dto, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<VideoOverviewDto>> GetVideoOverviewAsync(long teacherId, long videoAssetId)
    {
        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<VideoOverviewDto>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        var dto = await MapVideoBaseAsync<VideoOverviewDto>(video);

        var aggregates = await _unitOfWork.VideoAssetsRepo
            .GetAnalyticsAggregatesAsync(teacherId, videoAssetId);
        dto.SeenStudentCount = aggregates.TotalStudentsWatched;
        dto.UnseenStudentCount = aggregates.UnseenCount;
        dto.CompletedStudentCount = aggregates.CompletedCount;

        (dto.Audience, dto.AudienceStudentCount) =
            await BuildAudienceAsync(teacherId, videoAssetId);

        return Result<VideoOverviewDto>.Success(dto, _localizer);
    }

    /// <summary>
    /// Resolves a video's scope rows into named session / group targets for the
    /// read-only Overview, mirroring how the unit screen presents its own scopes
    /// (<c>VideoUnitService</c>) so the two read identically to a teacher.
    ///
    /// Names and counts are batch-resolved — one round trip per target type, and
    /// none at all when the video has no scope of that type. A target that no
    /// longer resolves (session deleted out from under the scope) falls back to
    /// its id rather than dropping the row, so the teacher still sees that an
    /// audience entry exists.
    /// </summary>
    private async Task<(List<VideoUnitScopeDto> Audience, int StudentCount)> BuildAudienceAsync(
        long teacherId, long videoAssetId)
    {
        var withScopes = await _unitOfWork.VideoAssetsRepo
            .GetVideoWithScopesAsync(videoAssetId, teacherId);
        if (withScopes is null || withScopes.Scopes.Count == 0)
            return (new List<VideoUnitScopeDto>(), 0);

        var sessionIds = withScopes.Scopes
            .Where(s => s.ScopeType == VideoScopeType.Session && s.SessionId.HasValue)
            .Select(s => s.SessionId!.Value).Distinct().ToList();
        var groupIds = withScopes.Scopes
            .Where(s => s.ScopeType == VideoScopeType.SessionGroup && s.SessionGroupId.HasValue)
            .Select(s => s.SessionGroupId!.Value).Distinct().ToList();

        var sessionById = (sessionIds.Count > 0
                ? await _unitOfWork.SessionsRepo.GetSessionTargetSummariesAsync(teacherId, sessionIds)
                : new List<ScopeTargetSummaryRow>())
            .ToDictionary(r => r.Id);
        var groupById = (groupIds.Count > 0
                ? await _unitOfWork.SessionsRepo.GetGroupTargetSummariesAsync(teacherId, groupIds)
                : new List<ScopeTargetSummaryRow>())
            .ToDictionary(r => r.Id);

        var audience = withScopes.Scopes.Select(scope =>
        {
            bool isSession = scope.ScopeType == VideoScopeType.Session && scope.SessionId.HasValue;
            long targetId = isSession ? scope.SessionId!.Value : scope.SessionGroupId ?? 0;
            var lookup = isSession ? sessionById : groupById;

            var row = lookup.TryGetValue(targetId, out var found)
                ? found
                : new ScopeTargetSummaryRow { Id = targetId, Name = $"#{targetId}", StudentCount = 0 };

            return new VideoUnitScopeDto
            {
                ScopeType = scope.ScopeType,
                Target = new VideoScopeTargetDto
                {
                    Id = row.Id,
                    Name = row.Name,
                    StudentCount = row.StudentCount,
                },
            };
        }).ToList();

        int studentCount = await _unitOfWork.SessionsRepo
            .CountStudentsInTargetsAsync(teacherId, sessionIds, groupIds);

        return (audience, studentCount);
    }

    /// <summary>
    /// Shared base mapper for <see cref="VideoDetailDto"/> and
    /// <see cref="VideoOverviewDto"/> — both need the same Id/Title/.../
    /// RowVersion/Attachment fields; this is the single place that builds
    /// them, so a future field addition to the base shape only needs one
    /// edit. Fetches the current attachment itself (one extra query) rather
    /// than requiring every caller to pass one in.
    /// </summary>
    private async Task<TDto> MapVideoBaseAsync<TDto>(VideoAsset video) where TDto : VideoBaseDto, new()
    {
        var attachments = await _unitOfWork.FileObjectsRepo
            .GetVideoAttachmentsAsync(video.Id);

        // The edit screen needs the current photo's fileId (to resend/keep it) and its
        // gated URL (to display it) — resolve the registry row to get the PublicId.
        Domain.Entities.FileObject? photo = video.VideoPhotoFileId is null
            ? null
            : await _unitOfWork.FileObjectsRepo.GetByIdAsync(video.VideoPhotoFileId.Value);

        var questionsNumber = await _unitOfWork.VideoAssetsRepo
            .GetExamQuestionCountAsync(video.Id);

        var attachmentDtos = attachments.Select(ToAttachmentDto).ToList();

        return new TDto
        {
            Id = video.Id,
            Title = video.Title,
            Description = video.Description,
            SourceType = video.SourceType,
            SourceUrl = video.SourceUrl,
            DurationSeconds = video.DurationSeconds,
            PublishDate = video.PublishDate,
            Status = video.Status,
            RowVersion = video.RowVersion,
            VideoPhotoFileId = photo?.PublicId,
            VideoPhotoUrl = photo is null ? null : _fileAccess.BuildGatedUrl(photo.PublicId),
            Attachments = attachmentDtos,
            AttachmentsNumber = attachmentDtos.Count,
            QuestionsNumber = questionsNumber,
        };
    }

    private static VideoDetailDto MapToDetailDto(VideoAsset video) => new()
    {
        Id = video.Id,
        Title = video.Title,
        Description = video.Description,
        SourceType = video.SourceType,
        SourceUrl = video.SourceUrl,
        DurationSeconds = video.DurationSeconds,
        PublishDate = video.PublishDate,
        Status = video.Status,
        RowVersion = video.RowVersion,
    };

    // ══════════════════════════════════════════════════════════════════════
    // TEACHER VIDEO LIST (Story B teacher endpoint)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<TeacherVideoListItemDto>>>>
        GetTeacherVideosAsync(long teacherId, TeacherVideoListRequest request)
    {
        var (rows, totalCount) = await _unitOfWork.VideoAssetsRepo
            .GetTeacherVideosPagedAsync(teacherId, request.Search, request.Page, request.PageSize,
                request.Status);

        // Batch-resolve the page's cover-photo file ids to opaque PublicId + gated URL in one
        // query (never a per-row lookup) — same approach as the student list. BuildGatedUrl is
        // pure string work off the loaded PublicIds.
        var photoIds = rows.Where(r => r.VideoPhotoFileId is not null)
            .Select(r => r.VideoPhotoFileId!.Value).Distinct().ToList();
        var photosById = photoIds.Count == 0
            ? new Dictionary<long, Domain.Entities.FileObject>()
            : (await _unitOfWork.FileObjectsRepo.GetByIdsAsync(photoIds)).ToDictionary(f => f.Id);

        var items = rows.Select(r => new TeacherVideoListItemDto
        {
            Id = r.Id,
            Title = r.Title,
            Description = r.Description,
            SourceUrl = r.SourceUrl,
            SourceType = r.SourceType,
            DurationSeconds = r.DurationSeconds,
            StudentsInScope = r.StudentsInScope,
            TotalOpens = r.TotalOpens,
            SeenStudentCount = r.SeenStudentCount,
            UnseenStudentCount = r.UnseenStudentCount,
            Status = r.Status,
            PublishDate = r.PublishDate,
            VideoPhotoFileId = r.VideoPhotoFileId is long pid && photosById.TryGetValue(pid, out var photo)
                ? photo.PublicId
                : null,
            VideoPhotoUrl = r.VideoPhotoFileId is long pid2 && photosById.TryGetValue(pid2, out var photo2)
                ? _fileAccess.BuildGatedUrl(photo2.PublicId)
                : null,
            QuestionsNumber = r.QuestionsNumber,
            AttachmentsNumber = r.AttachmentsNumber,
            CreatedAt = r.CreatedAt,
        }).ToList();

        var response = new PaginatedResponse<List<TeacherVideoListItemDto>>
        {
            data = items,
            page = request.Page,
            pageSize = request.PageSize,
            totalCount = totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
        };

        return Result<PaginatedResponse<List<TeacherVideoListItemDto>>>.Success(response, _localizer);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ANALYTICS (Story F)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<VideoAnalyticsResponse>> GetAnalyticsAsync(
        long teacherId, long videoAssetId, VideoAnalyticsRequest request)
    {
        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<VideoAnalyticsResponse>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        var (rows, totalCount) = await _unitOfWork.VideoAssetsRepo
            .GetAnalyticsRowsForTeacherAsync(
                teacherId, videoAssetId, request.Search,
                request.SortBy, request.SortDirection, request.StatusFilter,
                request.SessionId, request.SessionGroupId,
                request.Page, request.PageSize);

        var aggregates = await _unitOfWork.VideoAssetsRepo
            .GetAnalyticsAggregatesAsync(teacherId, videoAssetId);

        var rowDtos = rows.Select(r => new VideoAnalyticsRowDto
        {
            TeacherStudentId = r.TeacherStudentId,
            StudentName = r.StudentName,
            StudentCode = r.StudentCode,
            SessionName = r.SessionName,
            SessionId = r.SessionId,
            HasOpened = r.HasOpened,
            OpenCount = r.OpenCount,
            TotalWatchSeconds = r.TotalWatchSeconds,
            VideoDurationSeconds = r.VideoDurationSeconds,
            FirstOpenedAt = r.FirstOpenedAt,
            LastOpenedAt = r.LastOpenedAt,
            EstimatedCompletionPct = r.EstimatedCompletionPct,
            RawWatchPct = r.RawWatchPct,
        }).ToList();

        var response = new VideoAnalyticsResponse
        {
            VideoAssetId = video.Id,
            Title = video.Title,
            DurationSeconds = video.DurationSeconds,
            TotalStudentsInScope = aggregates.TotalStudentsInScope,
            TotalStudentsWatched = aggregates.TotalStudentsWatched,
            UnseenCount = aggregates.UnseenCount,
            CompletedCount = aggregates.CompletedCount,
            Rows = rowDtos,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
        };

        return Result<VideoAnalyticsResponse>.Success(response, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<VideoSessionWatchBreakdownDto>> GetAnalyticsBySessionAsync(
        long teacherId, long videoAssetId)
    {
        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<VideoSessionWatchBreakdownDto>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        var rows = await _unitOfWork.VideoAssetsRepo
            .GetAnalyticsBySessionAsync(teacherId, videoAssetId);

        // Totals are SUMMED FROM THE ROWS, not read from GetAnalyticsAggregatesAsync.
        // That method counts the resolved audience directly, while the rows (like the
        // report rows) inner-join TeacherStudents — and the legacy per-student scope
        // branch of the audience query never joins it, so an orphaned VideoScope row
        // would be counted in the header and absent from every row beneath it. This
        // screen's whole claim is "here is that total, split by class", so a header it
        // cannot reconcile with is worse than one that differs from another screen by
        // a legacy row. Also saves three round trips.
        int totalInScope = rows.Sum(r => r.StudentsInScope);
        int totalWatched = rows.Sum(r => r.WatchedCount);

        var response = new VideoSessionWatchBreakdownDto
        {
            VideoAssetId = video.Id,
            Title = video.Title,
            TotalStudentsInScope = totalInScope,
            TotalStudentsWatched = totalWatched,
            UnseenCount = Math.Max(0, totalInScope - totalWatched),
            CompletedCount = rows.Sum(r => r.CompletedCount),
            Rows = rows.Select(r => new VideoSessionWatchRowDto
            {
                SessionId = r.SessionId,
                SessionName = r.SessionName,
                SessionGroupId = r.SessionGroupId,
                SessionGroupName = r.SessionGroupName,
                StudentsInScope = r.StudentsInScope,
                WatchedCount = r.WatchedCount,
                UnseenCount = Math.Max(0, r.StudentsInScope - r.WatchedCount),
                CompletedCount = r.CompletedCount,
                // Computed here rather than on the client so every surface rounds the
                // same way; an empty session reads 0 instead of dividing by zero.
                WatchedPct = r.StudentsInScope == 0
                    ? 0
                    : (int)Math.Round(r.WatchedCount * 100d / r.StudentsInScope),
            }).ToList(),
        };

        return Result<VideoSessionWatchBreakdownDto>.Success(response, _localizer);
    }

    // ══════════════════════════════════════════════════════════════════════
    // STUDENT — VIDEO LIST (Story B student endpoint)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<StudentVideoListItemDto>>>>
        GetStudentVideosAsync(
            long teacherId, long teacherStudentId, StudentVideoListRequest request, string? studentLanguage)
    {
        // Runtime module-active gate — students have no `module` JWT claim.
        var moduleGate = await CheckModuleActiveAsync<PaginatedResponse<List<StudentVideoListItemDto>>>(teacherId);
        if (moduleGate is not null) return moduleGate;
        var visibilityGate = await CheckStudentVisibilityAsync<PaginatedResponse<List<StudentVideoListItemDto>>>(teacherId);
        if (visibilityGate is not null) return visibilityGate;

        var (rows, totalCount) = await _unitOfWork.VideoAssetsRepo
            .GetVisibleVideosForStudentAsync(
                teacherId, teacherStudentId, request.Page, request.PageSize,
                request.Search, request.ResolveWatchStatus());

        var response = await BuildStudentVideoPageAsync(
            teacherId, rows, totalCount, request.Page, request.PageSize, studentLanguage);
        return Result<PaginatedResponse<List<StudentVideoListItemDto>>>.Success(response, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<StudentVideoListItemDto>>>> GetStudentVideosInUnitAsync(
        long teacherId, long teacherStudentId, long unitId, StudentVideoListRequest request, string? studentLanguage)
    {
        var moduleGate = await CheckModuleActiveAsync<PaginatedResponse<List<StudentVideoListItemDto>>>(teacherId);
        if (moduleGate is not null) return moduleGate;
        var visibilityGate = await CheckStudentVisibilityAsync<PaginatedResponse<List<StudentVideoListItemDto>>>(teacherId);
        if (visibilityGate is not null) return visibilityGate;

        var (rows, totalCount) = await _unitOfWork.VideoAssetsRepo
            .GetVisibleVideosForStudentInUnitAsync(
                teacherId, teacherStudentId, unitId, request.Page, request.PageSize,
                request.Search, request.ResolveWatchStatus());

        var response = await BuildStudentVideoPageAsync(
            teacherId, rows, totalCount, request.Page, request.PageSize, studentLanguage);
        return Result<PaginatedResponse<List<StudentVideoListItemDto>>>.Success(response, _localizer);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<Result<StudentVideoProgressDto>> GetStudentVideoProgressAsync(
        long teacherId, long teacherStudentId)
    {
        var moduleGate = await CheckModuleActiveAsync<StudentVideoProgressDto>(teacherId);
        if (moduleGate is not null) return moduleGate;

        // One rule, three outcomes — the SAME VideoWatchRules classification behind the row
        // badge and the list filter, so the tile's "N new" can no longer count a video the
        // list is badging differently. The repo returns the buckets already summing to Total.
        var counts = await _unitOfWork.VideoAssetsRepo
            .GetStudentVideoWatchCountsAsync(teacherId, teacherStudentId);

        return Result<StudentVideoProgressDto>.Success(
            new StudentVideoProgressDto
            {
                Total = counts.Total,
                Seen = counts.InProgress + counts.Completed,
                NotStarted = counts.NotStarted,
                InProgress = counts.InProgress,
                Watched = counts.Completed,
            },
            _localizer);
    }

    public async Task<Result<List<StudentVideoUnitDto>>> GetStudentUnitsAsync(
        long teacherId, long teacherStudentId, string? studentLanguage)
    {
        var moduleGate = await CheckModuleActiveAsync<List<StudentVideoUnitDto>>(teacherId);
        if (moduleGate is not null) return moduleGate;
        var visibilityGate = await CheckStudentVisibilityAsync<List<StudentVideoUnitDto>>(teacherId);
        if (visibilityGate is not null) return visibilityGate;

        var units = await _unitOfWork.VideoAssetsRepo
            .GetStudentVisibleUnitsAsync(teacherId, teacherStudentId);

        string subject = ResolveTeacherSubject(
            await _unitOfWork.VideoAssetsRepo.GetTeacherSubjectAsync(teacherId), studentLanguage);

        var items = units.Select(u => new StudentVideoUnitDto
        {
            Id = u.Id,
            Title = u.Title,
            Description = u.Description,
            VideoCount = u.VideoCount,
            QuizCount = u.QuizVideoCount,
            WatchedCount = u.WatchedVideoCount,
            Subject = subject,
        }).ToList();

        return Result<List<StudentVideoUnitDto>>.Success(items, _localizer);
    }

    /// <summary>
    /// Shared mapper for the student video list and the V3 unit drill-down. Batch-resolves the
    /// page's cover photos + attachments (never per-row) and resolves the teacher's subject ONCE
    /// (all rows share one teacher), then maps rows to the enriched DTO (V2 quiz info, V4 watch
    /// status, subject).
    /// </summary>
    private async Task<PaginatedResponse<List<StudentVideoListItemDto>>> BuildStudentVideoPageAsync(
        long teacherId, IReadOnlyList<Domain.Interfaces.StudentVideoListRow> rows,
        int totalCount, int page, int pageSize, string? studentLanguage)
    {
        var photoIds = rows.Where(r => r.VideoPhotoFileId is not null)
            .Select(r => r.VideoPhotoFileId!.Value).Distinct().ToList();
        var photosById = photoIds.Count == 0
            ? new Dictionary<long, Domain.Entities.FileObject>()
            : (await _unitOfWork.FileObjectsRepo.GetByIdsAsync(photoIds)).ToDictionary(f => f.Id);

        var videoIds = rows.Select(r => r.Id).ToList();
        var attachmentsByVideo = videoIds.Count == 0
            ? new Dictionary<long, List<Domain.Entities.FileObject>>()
            : (await _unitOfWork.FileObjectsRepo.GetVideoAttachmentsForVideosAsync(videoIds))
                .GroupBy(f => f.VideoAssetId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList()); // all live attachments, oldest-first

        // Subject is per-teacher — resolve once for the whole page (all rows share one teacher).
        string subject = ResolveTeacherSubject(
            await _unitOfWork.VideoAssetsRepo.GetTeacherSubjectAsync(teacherId), studentLanguage);

        var items = rows.Select(r => new StudentVideoListItemDto
        {
            Id = r.Id,
            Title = r.Title,
            Description = r.Description,
            SourceType = r.SourceType,
            SourceUrl = r.SourceUrl,
            DurationSeconds = r.DurationSeconds,
            AssignedAt = r.AssignedAt,
            HasOpened = r.HasOpened,
            LastOpenedAt = r.LastOpenedAt,
            HasQuiz = r.HasQuiz,
            QuestionsCount = r.QuestionsCount,
            WatchStatus = ComputeWatchStatus(r.TotalWatchSeconds, r.DurationSeconds),
            Subject = subject,
            VideoPhotoUrl = r.VideoPhotoFileId is long pid && photosById.TryGetValue(pid, out var photo)
                ? _fileAccess.BuildGatedUrl(photo.PublicId)
                : null,
            Attachments = attachmentsByVideo.TryGetValue(r.Id, out var atts)
                ? atts.Select(ToAttachmentDto).ToList()
                : new List<VideoAttachmentDto>(),
        }).ToList();

        return new PaginatedResponse<List<StudentVideoListItemDto>>
        {
            data = items,
            page = page,
            pageSize = pageSize,
            totalCount = totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
        };
    }

    /// <summary>
    /// V4 — 3-state watch indicator. Delegates to <see cref="VideoWatchRules"/>, which the unit
    /// watched-count and the home "not started" count also use, so the badge a student sees on a
    /// video can never contradict the progress its unit reports.
    /// </summary>
    private static VideoWatchStatus ComputeWatchStatus(long totalWatchSeconds, int durationSeconds)
        => VideoWatchRules.Compute(totalWatchSeconds, durationSeconds);

    /// <summary>
    /// Resolves the teacher's subject display name, replicating the canonical
    /// <c>StudentUserService.BuildDashboardTeacherDtoFromBatch</c> pattern (AAM-FR-02.2/BUG-3): a
    /// linked ministry subject (in the STUDENT's stored <c>LanguagePreference</c>; null/empty →
    /// English) wins; otherwise the free-text <c>CustomSubject</c>; otherwise empty. Uses the
    /// student's stored language, NOT the request UI-culture, so it matches the offline-exam and
    /// dashboard modules.
    /// </summary>
    private static string ResolveTeacherSubject(Domain.Interfaces.TeacherSubjectInfo? info, string? studentLanguage)
    {
        if (info is null) return string.Empty;

        bool isArabic = string.Equals(studentLanguage?.Trim(), "ar", StringComparison.OrdinalIgnoreCase);
        string? ministry = isArabic ? info.SubjectNameAr : info.SubjectNameEn;
        if (!string.IsNullOrWhiteSpace(ministry)) return ministry!;

        return info.CustomSubject ?? string.Empty;
    }

    // ══════════════════════════════════════════════════════════════════════
    // STUDENT — START WATCH (Story B steps 5–9)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<StartWatchResponse>> StartWatchAsync(
        long teacherId, long teacherStudentId, long videoAssetId, StartWatchRequest request)
    {
        var moduleGate = await CheckModuleActiveAsync<StartWatchResponse>(teacherId);
        if (moduleGate is not null) return moduleGate;

        // Idempotency replay: if the same client event was already recorded,
        // return current state without inserting anything new.
        if (request.ClientEventId.HasValue
            && await _unitOfWork.VideoAssetsRepo.HasEventWithClientIdAsync(request.ClientEventId.Value))
        {
            return await BuildStartReplayResponseAsync(
                teacherId, teacherStudentId, videoAssetId);
        }

        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<StartWatchResponse>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        bool inScope = await _unitOfWork.VideoAssetsRepo
            .IsStudentInVideoScopeAsync(teacherStudentId, videoAssetId, teacherId);
        if (!inScope)
            return Result<StartWatchResponse>.Failure(
                _localizer, VideoConstants.Messages.VideoNotInScope, HttpStatusCode.Forbidden);

        // Trust-boundary duration update. Out-of-band reports are silently
        // ignored; the bool result is for telemetry, not control flow.
        if (request.VideoDurationSeconds > 0)
        {
            await _unitOfWork.VideoAssetsRepo.TryUpdateDurationWithinToleranceAsync(
                videoAssetId,
                request.VideoDurationSeconds,
                VideoConstants.DurationToleranceFraction);

            // Re-load to capture any duration update for the response.
            video = await _unitOfWork.VideoAssetsRepo
                .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        }

        var utcNow = DateTime.UtcNow;

        // UPSERT with bounded retry — see private helper.
        var snapshot = await UpsertAnalyticsOnOpenWithRetryAsync(
            videoAssetId, teacherStudentId, teacherId, video!.DurationSeconds, utcNow);

        // Append the Open event row.
        var openEvent = new VideoWatchEvent
        {
            VideoAssetId = videoAssetId,
            TeacherId = teacherId,
            TeacherStudentId = teacherStudentId,
            DeviceId = request.DeviceId,
            EventType = VideoEventType.Open,
            PositionSeconds = snapshot.LastResumePositionSeconds,
            DeltaSinceLastSeconds = 0,
            EventUtc = utcNow,
            ClientEventId = request.ClientEventId,
            CreateAt = utcNow,
        };
        await _unitOfWork.VideoAssetsRepo.AddWatchEventAsync(openEvent);
        await _unitOfWork.SaveChangesAsync();

        int resumeFrom = ComputeResumeFromSeconds(
            snapshot.LastResumePositionSeconds, video.DurationSeconds);

        return Result<StartWatchResponse>.Success(new StartWatchResponse
        {
            VideoAssetId = video.Id,
            EmbedUrl = _urlParser.BuildEmbedUrl(video.SourceType, video.ExternalId),
            SourceType = video.SourceType,
            ResumeFromSeconds = resumeFrom,
            DurationSeconds = video.DurationSeconds,
        }, _localizer, VideoConstants.Messages.WatchStarted);
    }

    // ══════════════════════════════════════════════════════════════════════
    // STUDENT — STOP WATCH (Story C)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<StopWatchResponse>> StopWatchAsync(
        long teacherId, long teacherStudentId, long videoAssetId, StopWatchRequest request)
    {
        var moduleGate = await CheckModuleActiveAsync<StopWatchResponse>(teacherId);
        if (moduleGate is not null) return moduleGate;

        // Idempotency replay.
        if (request.ClientEventId.HasValue
            && await _unitOfWork.VideoAssetsRepo.HasEventWithClientIdAsync(request.ClientEventId.Value))
        {
            return await BuildStopReplayResponseAsync(videoAssetId, teacherStudentId);
        }

        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<StopWatchResponse>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        bool inScope = await _unitOfWork.VideoAssetsRepo
            .IsStudentInVideoScopeAsync(teacherStudentId, videoAssetId, teacherId);
        if (!inScope)
            return Result<StopWatchResponse>.Failure(
                _localizer, VideoConstants.Messages.VideoNotInScope, HttpStatusCode.Forbidden);

        var anchorUtc = await _unitOfWork.VideoAssetsRepo
            .GetLastEventUtcForDeviceAsync(teacherStudentId, videoAssetId, request.DeviceId);
        if (anchorUtc is null)
            return Result<StopWatchResponse>.Failure(
                _localizer, VideoConstants.Messages.NoActiveSession, HttpStatusCode.Conflict);

        // Same trust-boundary duration update as start-watch. That call fires on the play
        // transition, before the player's metadata is necessarily loaded, so a first play
        // reporting 0 used to leave the video's length unknown until someone opened it again
        // with better timing — and until then every percentage was dead (no completion
        // percent, "completed" stuck at 0 for students who had watched it all). Progress
        // reports keep arriving, so a length that resolves a moment after playback lands here.
        //
        // Applied BEFORE the clamps below, which bound the delta and the resume position by
        // the video's length: learning it now makes this very report the first one they can
        // actually bound.
        // This runs on EVERY pause, and the client reports the length on every one of
        // them once it knows it — so the already-agreed case must cost nothing. Only a
        // report that DIFFERS from what is stored is worth a write, and the reload the
        // start path does is unnecessary here: ExecuteUpdate sets exactly the reported
        // value, so when it reports a row changed, that value is the stored one. Kept in
        // a local rather than written onto the tracked entity, which SaveChangesAsync
        // below would otherwise re-issue as a second UPDATE.
        int effectiveDurationSeconds = video.DurationSeconds;
        if (request.VideoDurationSeconds > 0
            && request.VideoDurationSeconds != effectiveDurationSeconds)
        {
            bool durationAccepted = await _unitOfWork.VideoAssetsRepo
                .TryUpdateDurationWithinToleranceAsync(
                    videoAssetId,
                    request.VideoDurationSeconds,
                    VideoConstants.DurationToleranceFraction);

            if (durationAccepted)
                effectiveDurationSeconds = request.VideoDurationSeconds;
        }

        var utcNow = DateTime.UtcNow;

        var (acceptedDelta, deltaWasClamped) = ClampDelta(
            request.DeltaSeconds, anchorUtc.Value, utcNow, effectiveDurationSeconds);

        int clampedPosition =
            ClampPosition(request.PositionSeconds, effectiveDurationSeconds);

        var increment = await _unitOfWork.VideoAssetsRepo.IncrementWatchAtomicAsync(
            videoAssetId, teacherStudentId, acceptedDelta, clampedPosition, utcNow);

        if (increment is null)
        {
            // Edge case: analytics row vanished between scope check and update.
            return Result<StopWatchResponse>.Failure(
                _localizer, VideoConstants.Messages.NoActiveSession, HttpStatusCode.Conflict);
        }

        var stopEvent = new VideoWatchEvent
        {
            VideoAssetId = videoAssetId,
            TeacherId = teacherId,
            TeacherStudentId = teacherStudentId,
            DeviceId = request.DeviceId,
            EventType = VideoEventType.Stop,
            PositionSeconds = clampedPosition,
            DeltaSinceLastSeconds = (int)acceptedDelta,
            EventUtc = utcNow,
            ClientEventId = request.ClientEventId,
            CreateAt = utcNow,
        };
        await _unitOfWork.VideoAssetsRepo.AddWatchEventAsync(stopEvent);
        await _unitOfWork.SaveChangesAsync();

        return Result<StopWatchResponse>.Success(new StopWatchResponse
        {
            TotalWatchSeconds = increment.TotalWatchSeconds,
            LastResumePositionSeconds = increment.LastResumePositionSeconds,
            AcceptedDelta = (int)acceptedDelta,
            DeltaWasClamped = deltaWasClamped,
        }, _localizer, VideoConstants.Messages.WatchStopped);
    }

    // ──────────────────────────────────────────────────────────────────────
    // DEFERRED — parent endpoint (was Q7(a) in Phase 3 review)
    // ──────────────────────────────────────────────────────────────────────
    //
    // Phase 5 Q1(c): the parent endpoint is dropped from v1 pending a
    // concrete Flutter screen design. The student endpoint plus the
    // existing ParentChild → StudentTeacherLink / ParentChildTeacherLink
    // data model is sufficient to add it later without DB changes.

    // ══════════════════════════════════════════════════════════════════════
    // INTEGRATION HOOK — admin teacher-purge
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<int>> PurgeAllVideosForTeacherAsync(
        long teacherId, long actingAdminUserId)
    {
        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            // Fetch every video the teacher owns. PageSize int.MaxValue is
            // safe: the largest single-teacher inventory in production is
            // bounded by the spec's no-cap-on-scope-but-realistic-usage.
            var (rows, _) = await _unitOfWork.VideoAssetsRepo
                .GetTeacherVideosPagedAsync(teacherId, search: null, page: 1, pageSize: int.MaxValue);

            var utcNow = DateTime.UtcNow;
            int audited = 0;

            foreach (var row in rows)
            {
                var fullVideo = await _unitOfWork.VideoAssetsRepo
                    .GetVideoWithScopesAsync(row.Id, teacherId);
                if (fullVideo is null) continue;

                var analyticsRows = await _unitOfWork.VideoAssetsRepo
                    .GetAnalyticsForAuditSnapshotAsync(row.Id);
                var aggregates = await _unitOfWork.VideoAssetsRepo
                    .GetAnalyticsAggregatesAsync(teacherId, row.Id);

                string snapshotJson = BuildAuditSnapshot(
                    fullVideo, analyticsRows, aggregates, actingAdminUserId, utcNow);

                await _unitOfWork.VideoAssetsRepo.AddAuditAsync(new VideoAssetAudit
                {
                    VideoAssetId = row.Id,
                    TeacherId = teacherId,
                    Action = VideoAuditAction.HardDelete,
                    SnapshotJson = snapshotJson,
                    SnapshotArchiveUrl = null,
                    DeletedByUserId = actingAdminUserId,
                    DeletedAt = utcNow,
                    CreateAt = utcNow,
                });

                // Detach this video's registry files so the GC reaps their blobs (orphan-bug fix).
                await DetachVideoFilesAsync(row.Id, fullVideo.VideoPhotoFileId);
                audited++;
            }

            // Bulk DELETE — NoAction FKs remove scopes, analytics, watch events.
            await _unitOfWork.VideoAssetsRepo.DeleteAllVideosForTeacherAsync(teacherId);
            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            return Result<int>.Success(audited, _localizer, VideoConstants.Messages.VideoDeleted);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runtime module-active gate. Returns a Failure result when the module
    /// is deactivated; otherwise null so the caller proceeds.
    ///
    /// The bang on <c>ModuleTeacherRepo!</c> reflects a pre-existing
    /// codebase quirk (the property is declared nullable in IUnitOfWork).
    /// Per Phase 5 Q2(a): keep the bang here rather than restructuring the
    /// shared interface in this PR.
    /// </summary>
    /// <summary>
    /// Tells the scoped students a video is (or will become) available.
    ///
    /// Best-effort and deliberately swallowed: publishing must never fail because a
    /// queue was unreachable. Enqueue only — the fan-out itself runs on Hangfire, so
    /// the teacher never waits on it, and the job re-checks visibility before sending.
    /// </summary>
    private void AnnounceVideoPublished(long teacherId, long videoAssetId, DateTime? publishDate)
    {
        try
        {
            _publishNotifications.DispatchVideoPublished(teacherId, videoAssetId, publishDate);
        }
        catch (Exception)
        {
            // Intentionally ignored — the video is published either way.
        }
    }

    private async Task<Result<T>?> CheckModuleActiveAsync<T>(long teacherId)
    {
        bool active = await _unitOfWork.ModuleTeacherRepo!
            .IsModuleActiveAsync(teacherId, VideoConstants.ModuleName);
        if (active) return null;

        return Result<T>.Failure(
            _localizer, VideoConstants.Messages.ModuleDeactivated, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The teacher's STUDENT-VISIBILITY switch for Videos, enforced on the student-facing reads
    /// themselves (2026-09-09).
    ///
    /// The flag used to gate only the home tile and the dashboard projection, so turning Videos off
    /// removed the button while the list route still answered 200 with the full content to anyone
    /// calling it directly - the teacher believed the material was hidden and it was not. The
    /// content-publish job started honouring the same flag, which left the system saying two different
    /// things: no announcement, still readable. One switch now governs the tile, the list and the push.
    ///
    /// FAIL OPEN on a missing configuration row, matching the student home aggregate and the
    /// notification job - a teacher with no config has never had anything hidden.
    /// </summary>
    private async Task<Result<T>?> CheckStudentVisibilityAsync<T>(long teacherId)
    {
        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);
        if (config?.StudentVisibilityVideo ?? true) return null;

        return Result<T>.Failure(
            _localizer, VideoConstants.Messages.ModuleDeactivated, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Validates each input scope row's shape against the
    /// <c>CK_VideoScopes_ExactlyOneTarget</c> and
    /// <c>CK_VideoScopes_ScopeTypeMatchesFK</c> rules. Returns a localization
    /// key on failure, null on success.
    /// </summary>
    private static string? ValidateScopeShape(IEnumerable<VideoScopeInputDto> scopes)
    {
        foreach (var s in scopes)
        {
            int populated =
              + (s.SessionId.HasValue ? 1 : 0)
              + (s.SessionGroupId.HasValue ? 1 : 0);

            if (populated != 1)
                return VideoConstants.Messages.ScopeShapeInvalid;

            bool typeMatches = s.ScopeType switch
            {
                VideoScopeType.Session => s.SessionId.HasValue,
                VideoScopeType.SessionGroup => s.SessionGroupId.HasValue,
                _ => false,
            };

            if (!typeMatches)
                return VideoConstants.Messages.ScopeShapeInvalid;
        }
        return null;
    }

    /// <summary>
    /// Validates each scope target belongs to the calling teacher. Returns a
    /// localization key on failure, null on success.
    /// </summary>
    private async Task<string?> ValidateScopeOwnershipAsync(
        long teacherId, IEnumerable<VideoScopeInputDto> scopes)
    {
        foreach (var s in scopes)
        {
            long targetId = s.ScopeType switch
            {
                VideoScopeType.Session => s.SessionId!.Value,
                VideoScopeType.SessionGroup => s.SessionGroupId!.Value,
                _ => 0,
            };

            bool owned = await _unitOfWork.VideoAssetsRepo
                .IsScopeTargetOwnedByTeacherAsync(teacherId, s.ScopeType, targetId);

            if (!owned)
                return VideoConstants.Messages.ScopeTargetNotFoundOrForeign;
        }
        return null;
    }

    /// <summary>
    /// Builds a <see cref="VideoScope"/> entity from an input DTO. The video
    /// argument supplies <c>TeacherId</c> for the composite-FK column.
    /// </summary>
    private static VideoScope BuildScopeEntity(
        VideoScopeInputDto input, VideoAsset video, long actingUserId, DateTime utcNow)
    {
        return new VideoScope
        {
            VideoAssetId = video.Id,
            TeacherId = video.TeacherId,
            ScopeType = input.ScopeType,
            SessionId = input.SessionId,
            SessionGroupId = input.SessionGroupId,
            AssignedByUserId = actingUserId,
            AssignedAt = utcNow,
            CreateAt = utcNow,
        };
    }

    /// <summary>
    /// Resolves a set of scope targets (Session / SessionGroup) to the concrete set
    /// of session ids they reach — a group expands to its member sessions. This is
    /// the "resolved audience" behind the containment rule: a video's scope is valid
    /// only when its resolved sessions are a subset of the sessions its units cover.
    /// Teacher-scoped throughout.
    /// </summary>
    private async Task<HashSet<long>> ResolveScopeSessionIdsAsync(
        long teacherId,
        IEnumerable<(VideoScopeType ScopeType, long? SessionId, long? SessionGroupId)> targets)
    {
        var sessionIds = new HashSet<long>();
        var groupIds = new List<long>();

        foreach (var t in targets)
        {
            if (t.ScopeType == VideoScopeType.Session && t.SessionId.HasValue)
                sessionIds.Add(t.SessionId.Value);
            else if (t.ScopeType == VideoScopeType.SessionGroup && t.SessionGroupId.HasValue)
                groupIds.Add(t.SessionGroupId.Value);
        }

        if (groupIds.Count > 0)
        {
            var rows = await _unitOfWork.SessionsRepo.GetSessionsByGroupIdsAsync(teacherId, groupIds);
            foreach (var r in rows)
                sessionIds.Add(r.Id);
        }

        return sessionIds;
    }

    /// <summary>
    /// Enforces the containment invariant for a video: it must belong to at least
    /// one unit, and every session its scope reaches must be covered by the union of
    /// its units' scopes (resolved-audience subset). Returns a successful
    /// <see cref="Result{T}"/> when the invariant holds, or an illustrative failure
    /// — naming the video and the offending sessions — that the caller propagates
    /// via <c>Result&lt;T&gt;.Failure(result)</c>.
    /// </summary>
    private async Task<Result<bool>> EnsureVideoScopeWithinUnitsAsync(
        long teacherId, string videoTitle,
        IReadOnlyCollection<long> unitIds,
        IReadOnlyCollection<VideoScopeInputDto> scopes)
    {
        if (unitIds.Count == 0)
            return Result<bool>.Failure(
                _localizer, VideoConstants.Messages.VideoMustBelongToUnit,
                new object?[] { videoTitle }, HttpStatusCode.UnprocessableEntity);

        var videoSessions = await ResolveScopeSessionIdsAsync(
            teacherId, scopes.Select(s => (s.ScopeType, s.SessionId, s.SessionGroupId)));

        // An empty (or all-empty-group) scope reaches nobody — trivially within
        // any unit boundary, so a unit-linked but not-yet-scoped video is allowed.
        if (videoSessions.Count == 0)
            return Result<bool>.Success(true, HttpStatusCode.OK);

        var unitTargets = await _unitOfWork.VideoUnitsRepo.GetScopeRowsForUnitsAsync(unitIds);
        var unitSessions = await ResolveScopeSessionIdsAsync(
            teacherId, unitTargets.Select(s => (s.ScopeType, s.SessionId, s.SessionGroupId)));

        var offending = videoSessions.Except(unitSessions).ToList();
        if (offending.Count == 0)
            return Result<bool>.Success(true, HttpStatusCode.OK);

        var names = await _unitOfWork.SessionsRepo.GetSessionNamesByIdsAsync(teacherId, offending);
        string offendingLabel = string.Join(", ",
            offending.Select(id => names.TryGetValue(id, out var n) ? n : $"#{id}"));

        return Result<bool>.Failure(
            _localizer, VideoConstants.Messages.VideoScopeExceedsUnitScope,
            new object?[] { videoTitle, offendingLabel }, HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// UPSERT-with-retry for the StartWatch flow's analytics row. Returns the
    /// snapshot of the analytics state after the operation.
    ///
    /// Loop body: try increment first (existing row, the common case); on
    /// miss, try INSERT and SaveChanges. If a competing transaction won the
    /// INSERT race (SQL Server unique-violation 2601/2627), loop back and
    /// the increment branch will succeed against the row they inserted.
    /// </summary>
    private async Task<VideoAnalyticsSnapshot> UpsertAnalyticsOnOpenWithRetryAsync(
        long videoAssetId, long teacherStudentId, long teacherId,
        int videoDurationSeconds, DateTime utcNow)
    {
        for (int attempt = 0; attempt <= VideoConstants.MaxOpenRetries; attempt++)
        {
            // Try the increment branch first — common case.
            var existing = await _unitOfWork.VideoAssetsRepo
                .IncrementOpenCountIfExistsAsync(videoAssetId, teacherStudentId, utcNow);
            if (existing is not null) return existing;

            // No row yet — queue an INSERT and try to save.
            var fresh = new VideoAnalytics
            {
                VideoAssetId = videoAssetId,
                TeacherId = teacherId,
                TeacherStudentId = teacherStudentId,
                OpenCount = 1,
                TotalWatchSeconds = 0,
                FirstOpenedAt = utcNow,
                LastUpdated = utcNow,
                VideoDurationAtFirstWatch = videoDurationSeconds,
                LastResumePositionSeconds = 0,
                CreateAt = utcNow,
            };

            try
            {
                await _unitOfWork.VideoAssetsRepo.AddAnalyticsRowForFirstOpenAsync(fresh);
                await _unitOfWork.SaveChangesAsync();

                return new VideoAnalyticsSnapshot
                {
                    OpenCount = 1,
                    TotalWatchSeconds = 0,
                    LastResumePositionSeconds = 0,
                };
            }
            catch (DbUpdateException ex)
                when (IsUniqueViolation(ex) && attempt < VideoConstants.MaxOpenRetries)
            {
                // Lost the insert race. The change tracker now holds a
                // detached-state entity that EF Core won't try to save again
                // because the SaveChanges call failed atomically — but the
                // entity reference is still in the change tracker as a
                // failed Added entry. We don't need to do anything: on the
                // next iteration, IncrementOpenCountIfExistsAsync runs as a
                // raw SQL UPDATE that doesn't involve the tracker, and it
                // finds the row the competing transaction inserted.
                //
                // Loop and retry.
            }
        }

        throw new InvalidOperationException(
            $"VCM analytics UPSERT exhausted retries for video {videoAssetId} student {teacherStudentId}.");
    }

    /// <summary>
    /// Detects SQL Server unique-key violation (errors 2601/2627) inside
    /// <see cref="DbUpdateException"/>. Mirrors the pattern in
    /// <c>SubscriptionService.IsUniqueViolation</c>.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var sqlException = ex.InnerException as Microsoft.Data.SqlClient.SqlException
                           ?? ex.GetBaseException() as Microsoft.Data.SqlClient.SqlException;

        return sqlException is { Number: 2601 or 2627 };
    }

    /// <summary>
    /// Two-layer clamp on the client-supplied delta:
    /// <list type="number">
    ///   <item>Clamp to <c>serverElapsed + tolerance</c>.</item>
    ///   <item>Then clamp to <c>DurationSeconds</c>.</item>
    /// </list>
    /// </summary>
    private static (long acceptedDelta, bool wasClamped) ClampDelta(
        int reportedDelta, DateTime anchorUtc, DateTime utcNow, int durationSeconds)
    {
        bool clamped = false;

        long working = Math.Max(0, reportedDelta);

        long serverElapsed = (long)Math.Ceiling((utcNow - anchorUtc).TotalSeconds);
        long elapsedCap = serverElapsed + VideoConstants.DeltaToleranceSeconds;
        if (working > elapsedCap)
        {
            working = elapsedCap;
            clamped = true;
        }

        if (durationSeconds > 0 && working > durationSeconds)
        {
            working = durationSeconds;
            clamped = true;
        }

        return (working, clamped);
    }

    /// <summary>Bounds the client-reported position to <c>[0, DurationSeconds]</c>.</summary>
    private static int ClampPosition(int reportedPosition, int durationSeconds)
    {
        if (reportedPosition < 0) return 0;
        if (durationSeconds > 0 && reportedPosition > durationSeconds) return durationSeconds;
        return reportedPosition;
    }

    /// <summary>
    /// Computes the resume seek position. Capped at
    /// <c>DurationSeconds - ResumeEndOfVideoBufferSeconds</c> so the player
    /// never resumes within the last 5 seconds (avoids "instant complete"
    /// glitches).
    /// </summary>
    private static int ComputeResumeFromSeconds(int lastPosition, int durationSeconds)
    {
        if (durationSeconds <= 0) return 0;
        int upperBound = Math.Max(0, durationSeconds - VideoConstants.ResumeEndOfVideoBufferSeconds);
        return Math.Clamp(lastPosition, 0, upperBound);
    }

    /// <summary>
    /// Idempotency replay branch for Start: returns current state without
    /// inserting anything new. Pure read — uses
    /// <see cref="IVideoAssetRepo.GetAnalyticsSnapshotAsync"/> to avoid
    /// mutating <c>LastUpdated</c>.
    /// </summary>
    private async Task<Result<StartWatchResponse>> BuildStartReplayResponseAsync(
        long teacherId, long teacherStudentId, long videoAssetId)
    {
        var video = await _unitOfWork.VideoAssetsRepo
            .GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<StartWatchResponse>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        var snapshot = await _unitOfWork.VideoAssetsRepo
            .GetAnalyticsSnapshotAsync(videoAssetId, teacherStudentId);
        int lastResume = snapshot?.LastResumePositionSeconds ?? 0;

        return Result<StartWatchResponse>.Success(new StartWatchResponse
        {
            VideoAssetId = video.Id,
            EmbedUrl = _urlParser.BuildEmbedUrl(video.SourceType, video.ExternalId),
            SourceType = video.SourceType,
            ResumeFromSeconds = ComputeResumeFromSeconds(lastResume, video.DurationSeconds),
            DurationSeconds = video.DurationSeconds,
        }, _localizer, VideoConstants.Messages.WatchStarted);
    }

    /// <summary>
    /// Idempotency replay branch for Stop: returns current totals without
    /// re-incrementing. Pure read — same rationale as
    /// <see cref="BuildStartReplayResponseAsync"/>.
    /// </summary>
    private async Task<Result<StopWatchResponse>> BuildStopReplayResponseAsync(
        long videoAssetId, long teacherStudentId)
    {
        var snapshot = await _unitOfWork.VideoAssetsRepo
            .GetAnalyticsSnapshotAsync(videoAssetId, teacherStudentId);
        if (snapshot is null)
            return Result<StopWatchResponse>.Failure(
                _localizer, VideoConstants.Messages.NoActiveSession, HttpStatusCode.Conflict);

        return Result<StopWatchResponse>.Success(new StopWatchResponse
        {
            TotalWatchSeconds = snapshot.TotalWatchSeconds,
            LastResumePositionSeconds = snapshot.LastResumePositionSeconds,
            AcceptedDelta = 0,
            DeltaWasClamped = false,
        }, _localizer, VideoConstants.Messages.WatchStopped);
    }

    /// <summary>
    /// Shared audit-snapshot serializer for both delete-time (Story E) and
    /// pre-update (Phase 4 / G-EDIT) audit rows. <paramref name="action"/> and
    /// <paramref name="linkedUnitIds"/> default so the two pre-existing delete
    /// call sites need no changes; <c>UpdateVideoAsync</c> passes both
    /// explicitly.
    ///
    /// NOTE: the payload field names <c>deletedByUserId</c>/<c>deletedAt</c>
    /// are historical — for an "Updated" snapshot they mean "actor/timestamp
    /// of this audit event," not literal deletion. The sibling <c>action</c>
    /// field disambiguates. Not renaming the JSON shape here to avoid
    /// changing the read contract for existing delete-snapshot rows.
    /// </summary>
    private static string BuildAuditSnapshot(
        VideoAsset video,
        IReadOnlyList<VideoAnalyticsAuditRow> analytics,
        VideoAnalyticsAggregates aggregates,
        long deletedByUserId,
        DateTime deletedAt,
        string action = VideoAuditAction.HardDelete,
        IReadOnlyList<long>? linkedUnitIds = null)
    {
        var payload = new
        {
            action,
            videoAsset = new
            {
                id = video.Id,
                teacherId = video.TeacherId,
                title = video.Title,
                description = video.Description,
                sourceUrl = video.SourceUrl,
                sourceType = video.SourceType.ToString(),
                externalId = video.ExternalId,
                durationSeconds = video.DurationSeconds,
                createdAt = video.CreateAt,
                createdByUserId = video.CreatedByUserId,
            },
            unitIds = linkedUnitIds ?? Array.Empty<long>(),
            scopes = video.Scopes.Select(s => new
            {
                id = s.Id,
                scopeType = s.ScopeType.ToString(),
                teacherStudentId = s.TeacherStudentId,
                sessionId = s.SessionId,
                sessionGroupId = s.SessionGroupId,
                assignedByUserId = s.AssignedByUserId,
                assignedAt = s.AssignedAt,
            }),
            analytics = analytics.Select(a => new
            {
                teacherStudentId = a.TeacherStudentId,
                studentName = a.StudentName,
                studentCode = a.StudentCode,
                openCount = a.OpenCount,
                totalWatchSeconds = a.TotalWatchSeconds,
                firstOpenedAt = a.FirstOpenedAt,
                lastUpdated = a.LastUpdated,
                lastResumePositionSeconds = a.LastResumePositionSeconds,
            }),
            aggregates = new
            {
                totalStudentsInScope = aggregates.TotalStudentsInScope,
                totalStudentsWatched = aggregates.TotalStudentsWatched,
                totalWatchSecondsAcrossAll = analytics.Sum(a => a.TotalWatchSeconds),
                avgCompletionPctAtDelete = ComputeAvgCompletion(analytics, video.DurationSeconds),
            },
            deletedByUserId,
            deletedAt,
        };

        return JsonSerializer.Serialize(payload);
    }

    private static int ComputeAvgCompletion(
        IReadOnlyList<VideoAnalyticsAuditRow> analytics, int durationSeconds)
    {
        if (durationSeconds <= 0 || analytics.Count == 0) return 0;
        long totalSeconds = analytics.Sum(a => Math.Min(a.TotalWatchSeconds, durationSeconds));
        return (int)(totalSeconds * 100 / (analytics.Count * (long)durationSeconds));
    }

    /// <inheritdoc />
    public async Task<Result<VideoPhotoDto>> ReplaceVideoPhotoAsync(
        long teacherId, long actingUserId, long videoAssetId, Guid videoPhotoFileId)
    {
        var video = await _unitOfWork.VideoAssetsRepo.GetVideoByIdAndTeacherAsync(videoAssetId, teacherId);
        if (video is null)
            return Result<VideoPhotoDto>.Failure(
                _localizer, VideoConstants.Messages.VideoNotFound, HttpStatusCode.NotFound);

        // Idempotent no-op if the same file is resent.
        var currentPublicId = await CurrentPublicIdAsync(video.VideoPhotoFileId);
        if (currentPublicId == videoPhotoFileId)
            return Result<VideoPhotoDto>.Success(
                new VideoPhotoDto { VideoPhotoFileId = videoPhotoFileId, ReadUrl = _fileAccess.BuildGatedUrl(videoPhotoFileId) },
                _localizer, VideoConstants.Messages.VideoPhotoReplaced, HttpStatusCode.OK);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            var attach = await AttachFileAsync(
                videoPhotoFileId, FileCategory.VideoPhoto, teacherId, actingUserId);
            if (!attach.IsSuccess)
            {
                if (ownsTransaction) await _unitOfWork.RollbackAsync();
                return Result<VideoPhotoDto>.Failure(attach.Message ?? string.Empty, attach.StatusCode);
            }

            await _fileAccess.DetachAsync(video.VideoPhotoFileId);
            video.VideoPhotoFileId = attach.Data!.Id;
            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            return Result<VideoPhotoDto>.Success(
                new VideoPhotoDto { VideoPhotoFileId = attach.Data.PublicId, ReadUrl = _fileAccess.BuildGatedUrl(attach.Data.PublicId) },
                _localizer, VideoConstants.Messages.VideoPhotoReplaced, HttpStatusCode.OK);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }


    
    /// <summary>
    /// Attaches an uploaded registry file (by its public id) to a video slot, in the caller's
    /// transaction: validates owner/tenant + category + not-already-in-use, flips it to Attached,
    /// optionally back-references the video, and returns the tracked FileObject. The caller sets the
    /// consuming FK from the returned <c>Id</c> and builds the gated URL from <c>PublicId</c>.
    /// </summary>
    private async Task<Result<Domain.Entities.FileObject>> AttachFileAsync(
        Guid fileId, FileCategory category, long teacherId, long actingUserId, long? videoAssetId = null)
    {
        var result = await _fileAccess.ResolveForAttachAsync(fileId, category, actingUserId, teacherId);
        if (!result.IsSuccess)
            return result;

        if (videoAssetId is not null)
            result.Data!.VideoAssetId = videoAssetId;

        return result;
    }

    /// <inheritdoc />
    public async Task<Result<CreateVideoResponse>> CreateVideoAsync(
        long teacherId, long actingUserId, CreateVideoRequest request)
    {
        // Free-tier quota: videos (see ModuleQuota table).
        if (!await _subscriptionGate.CanCreateAsync(
                teacherId, ModuleQuotaKeys.Videos,
                () => _unitOfWork.VideoAssetsRepo.CountByTeacherAsync(teacherId)))
            return Result<CreateVideoResponse>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequired, HttpStatusCode.Forbidden);

        string title = request.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
            return Result<CreateVideoResponse>.Failure(
                _localizer, VideoConstants.Messages.InvalidUrl, HttpStatusCode.BadRequest);

        // Parse URL — distinguishes "not a URL" from "unsupported provider".
        var parseOutcome = _urlParser.Parse(request.SourceUrl);
        if (!parseOutcome.IsSuccess)
        {
            string urlErrorKey = parseOutcome.Failure switch
            {
                VideoUrlParseFailure.UnsupportedSource => VideoConstants.Messages.UnsupportedSource,
                _ => VideoConstants.Messages.InvalidUrl,
            };
            return Result<CreateVideoResponse>.Failure(
                _localizer, urlErrorKey, HttpStatusCode.BadRequest);
        }

        // A Drive link parses fine and used to be stored happily — then every
        // student who opened the video got "unsupported source", because no
        // client ships a Drive player. Reject it at the door with a message
        // that says what to do instead.
        if (parseOutcome.Success!.SourceType != VideoSourceType.YouTube)
            return Result<CreateVideoResponse>.Failure(
                _localizer, VideoConstants.Messages.VideoSourceMustBeYouTube, HttpStatusCode.BadRequest);

        // Files (video photo/attachment) were already uploaded via POST /api/upload and validated
        // there (type/size). Here we only reference them by id; type/size are not re-checked.

        // ── Merged-creation refactor: scope + exam validated BEFORE any write, fail-fast.
        var scopeInputs = new List<VideoScopeInputDto>();
        if (request.Scopes is { Count: > 0 })
        {
            foreach (var group in request.Scopes)
            {
                foreach (var id in group.Ids.Distinct())
                {
                    scopeInputs.Add(group.ScopeType switch
                    {
                        VideoScopeType.Session => new VideoScopeInputDto
                        {
                            ScopeType = VideoScopeType.Session,
                            SessionId = id,
                        },
                        VideoScopeType.SessionGroup => new VideoScopeInputDto
                        {
                            ScopeType = VideoScopeType.SessionGroup,
                            SessionGroupId = id,
                        },
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(group.ScopeType), group.ScopeType, "Unrecognized scope type."),
                    });
                }
            }

            var scopeShapeError = ValidateScopeShape(scopeInputs);
            if (scopeShapeError is not null)
                return Result<CreateVideoResponse>.Failure(
                    _localizer, scopeShapeError, HttpStatusCode.BadRequest);

            var scopeOwnershipError = await ValidateScopeOwnershipAsync(teacherId, scopeInputs);
            if (scopeOwnershipError is not null)
                return Result<CreateVideoResponse>.Failure(
                    _localizer, scopeOwnershipError, HttpStatusCode.BadRequest);
        }

        // ── Containment: a video must live inside >=1 unit, and its scope must stay
        //    within the sessions/groups its units cover (resolved-audience subset).
        //    Validated fail-fast, before any write.
        var createUnitIds = request.UnitIds?.Distinct().ToList() ?? new List<long>();
        var createUnitOwnershipError = await ValidateUnitIdsOwnershipAsync(teacherId, createUnitIds);
        if (createUnitOwnershipError is not null)
            return Result<CreateVideoResponse>.Failure(
                _localizer, createUnitOwnershipError, HttpStatusCode.BadRequest);

        var createContainment = await EnsureVideoScopeWithinUnitsAsync(
            teacherId, title, createUnitIds, scopeInputs);
        if (!createContainment.IsSuccess)
            return Result<CreateVideoResponse>.Failure(createContainment);

        if (request.Exam is not null)
        {
            var examShapeError = ValidateExamShape(request.Exam);
            if (examShapeError is not null)
                return Result<CreateVideoResponse>.Failure(
                    _localizer, examShapeError, HttpStatusCode.BadRequest);
        }

        // Everything is ONE SQL transaction now — video, video photo/attachment file references,
        // scopes, and exam are all-or-nothing. The files themselves were already uploaded to blob
        // storage via POST /api/upload; here we only flip their registry rows to Attached and store
        // FK references, so there is no cross-store saga and no compensation.
        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        VideoAsset video;
        int scopesAdded = 0;
        int studentsInScope = 0;
        long? examId = null;
        Guid? videoPhotoPublicId = null;
        var attachmentDtos = new List<VideoAttachmentDto>();
        try
        {
            video = new VideoAsset
            {
                TeacherId = teacherId,
                Title = title,
                Description = request.Description?.Trim(),
                SourceUrl = request.SourceUrl.Trim(),
                SourceType = parseOutcome.Success!.SourceType,
                ExternalId = parseOutcome.Success.ExternalId,
                DurationSeconds = 0, // Story A: learned on first open (unless overridden below).
                CreatedByUserId = actingUserId,
                CreateAt = DateTime.UtcNow,
                // Optional create-time visibility controls (parity with the update endpoint).
                // Status omitted = entity default (Published) — unchanged behavior.
                PublishDate = request.PublishDate,
            };

            // Explicit duration override — same semantics as the update endpoint: stored value
            // wins and student reports can no longer overwrite it.
            if (request.DurationSeconds.HasValue)
            {
                video.DurationSeconds = request.DurationSeconds.Value;
                video.IsDurationManuallySet = true;
            }

            await _unitOfWork.VideoAssetsRepo.AddVideoAsync(video);
            await _unitOfWork.SaveChangesAsync(); // generates video.Id

            // Status CANNOT be set on the INSERT: the column has a DB default (Published) and EF
            // omits it from the INSERT whenever the CLR value equals the enum default — which is
            // exactly 'Draft' (model warning 20601). Caught live: create with status=Draft
            // persisted as Published. Applying it as an UPDATE after the row exists writes the
            // column unconditionally.
            if (request.Status.HasValue)
            {
                video.Status = request.Status.Value;
                await _unitOfWork.VideoAssetsRepo.UpdateAsync(video); // full-entity Modified — column written regardless of change-tracker state
                await _unitOfWork.SaveChangesAsync();
            }

            // Unit links (M:N), created with the video — same validation + replace helper the
            // update endpoint uses. Ownership check → 400 VideoUnitNotFound on a foreign/unknown id.
            if (request.UnitIds is { Count: > 0 })
            {
                var unitOwnershipError = await ValidateUnitIdsOwnershipAsync(teacherId, request.UnitIds);
                if (unitOwnershipError is not null)
                {
                    if (ownsTransaction) await _unitOfWork.RollbackAsync();
                    return Result<CreateVideoResponse>.Failure(
                        _localizer, unitOwnershipError, HttpStatusCode.BadRequest);
                }

                await _unitOfWork.VideoAssetsRepo.ReplaceUnitLinksAsync(video.Id, request.UnitIds);
                await _unitOfWork.SaveChangesAsync();
            }

            var utcNow = DateTime.UtcNow;

            // Video photo reference.
            if (request.VideoPhotoFileId is Guid photoId)
            {
                var attach = await AttachFileAsync(
                    photoId, FileCategory.VideoPhoto, teacherId, actingUserId);
                if (!attach.IsSuccess)
                {
                    if (ownsTransaction) await _unitOfWork.RollbackAsync();
                    return Result<CreateVideoResponse>.Failure(attach.Message ?? string.Empty, attach.StatusCode);
                }
                video.VideoPhotoFileId = attach.Data!.Id;
                videoPhotoPublicId = attach.Data.PublicId;
            }

            // Attachment references (back-referenced to this video). Duplicates ignored; the
            // whole create rolls back if any single attach fails (all-or-nothing).
            if (request.AttachmentFileIds is { Count: > 0 })
            {
                var attachmentIds = request.AttachmentFileIds.Distinct().ToList();
                if (attachmentIds.Count > VideoConstants.MaxAttachmentsPerVideo)
                {
                    if (ownsTransaction) await _unitOfWork.RollbackAsync();
                    return Result<CreateVideoResponse>.Failure(
                        _localizer, VideoConstants.Messages.AttachmentsLimitExceeded, HttpStatusCode.UnprocessableEntity);
                }

                foreach (Guid attId in attachmentIds)
                {
                    var attach = await AttachFileAsync(
                        attId, FileCategory.VideoAttachment, teacherId, actingUserId, video.Id);
                    if (!attach.IsSuccess)
                    {
                        if (ownsTransaction) await _unitOfWork.RollbackAsync();
                        return Result<CreateVideoResponse>.Failure(attach.Message ?? string.Empty, attach.StatusCode);
                    }
                    attachmentDtos.Add(ToAttachmentDto(attach.Data!));
                }
            }

            await _unitOfWork.SaveChangesAsync();

            if (scopeInputs.Count > 0)
            {
                var scopeRows = scopeInputs
                    .Select(input => BuildScopeEntity(input, video, actingUserId, utcNow))
                    .ToList();

                await _unitOfWork.VideoAssetsRepo.AddScopesAsync(scopeRows);
                await _unitOfWork.SaveChangesAsync();

                scopesAdded = scopeRows.Count;
                var resolved = await _scopeResolver.ResolveFromPersistedScopesAsync(teacherId, scopeRows);
                studentsInScope = resolved.Count;
            }

            if (request.Exam is not null)
            {
                var exam = BuildExamEntity(request.Exam, video.Id, teacherId, actingUserId, utcNow);
                await _unitOfWork.VideoAssetsRepo.AddExamAsync(exam);
                await _unitOfWork.SaveChangesAsync(); // generates exam.Id (+ cascaded question/option ids)

                var imgError = await AttachExamQuestionImagesAsync(
                    request.Exam, exam, teacherId, actingUserId);
                if (imgError is not null)
                {
                    if (ownsTransaction) await _unitOfWork.RollbackAsync();
                    return Result<CreateVideoResponse>.Failure(imgError.Message ?? string.Empty, imgError.StatusCode);
                }
                await _unitOfWork.SaveChangesAsync();

                examId = exam.Id;
            }

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }

        // Post-commit: a video created straight into Published is new content to its
        // students exactly like one toggled later, so it announces the same way.
        if (video.Status == VideoStatus.Published)
            AnnounceVideoPublished(teacherId, video.Id, video.PublishDate);

        return Result<CreateVideoResponse>.Success(
                   new CreateVideoResponse
                   {
                       VideoAssetId = video.Id,
                       VideoPhotoFileId = videoPhotoPublicId,
                       VideoPhotoReadUrl = videoPhotoPublicId is null ? null : _fileAccess.BuildGatedUrl(videoPhotoPublicId.Value),
                       Attachments = attachmentDtos,
                       ScopesAdded = scopesAdded,
                       StudentsInScope = studentsInScope,
                       ExamId = examId,
                   },
                   _localizer,
                   VideoConstants.Messages.VideoCreated,
                   HttpStatusCode.Created);
    }

    /// <summary>Maps an Attached <see cref="Domain.Entities.FileObject"/> to the wire DTO (gated URL).</summary>
    private VideoAttachmentDto ToAttachmentDto(Domain.Entities.FileObject file) => new()
    {
        Id = file.PublicId,
        FileName = file.OriginalName,
        ContentType = file.ContentType,
        FileSizeBytes = file.SizeBytes,
        ReadUrl = _fileAccess.BuildGatedUrl(file.PublicId),
        CreatedAt = file.CreateAt,
    };

    /// <summary>
    /// Resolves and attaches each exam question's optional image (by public id), setting
    /// <c>ImageFileId</c> on the corresponding built <see cref="VideoExamQuestion"/> entity. The dto
    /// and entity question lists are index-aligned (<see cref="BuildExamEntity"/> preserves order).
    /// Returns a failure Result to propagate, or null on success.
    /// </summary>
    private async Task<Result<bool>?> AttachExamQuestionImagesAsync(
        CreateExamDto dto, VideoExam exam, long teacherId, long actingUserId)
    {
        var questions = exam.Questions.ToList();
        for (int i = 0; i < dto.Questions.Count && i < questions.Count; i++)
        {
            if (dto.Questions[i].ImageFileId is not Guid imageId)
                continue;

            var attach = await AttachFileAsync(
                imageId, FileCategory.VideoExamQuestionImage, teacherId, actingUserId);
            if (!attach.IsSuccess)
                return Result<bool>.Failure(attach.Message ?? string.Empty, attach.StatusCode);

            questions[i].ImageFileId = attach.Data!.Id;
        }
        return null;
    }

    /// <summary>
    /// Validates an exam's shape before any DB write (fail-fast, same
    /// philosophy as ValidateScopeShape). Returns a localization key on
    /// failure, null on success.
    /// </summary>
    private static string? ValidateExamShape(CreateExamDto exam)
    {
        if (string.IsNullOrWhiteSpace(exam.Title))
            return VideoConstants.Messages.ExamTitleRequired;

        if (exam.Questions is null || exam.Questions.Count == 0)
            return VideoConstants.Messages.ExamMustHaveQuestions;

        foreach (var q in exam.Questions)
        {
            if (string.IsNullOrWhiteSpace(q.Text))
                return VideoConstants.Messages.ExamQuestionTextRequired;

            if (q.Options is null || q.Options.Count < 2)
                return VideoConstants.Messages.ExamQuestionNeedsOptions;

            int correctCount = q.Options.Count(o => o.IsCorrect);

            if (q.QuestionType == VideoExamQuestionType.SingleChoice && correctCount != 1)
                return VideoConstants.Messages.SingleChoiceNeedsExactlyOneCorrect;

            if (q.QuestionType == VideoExamQuestionType.MultipleChoice && correctCount < 1)
                return VideoConstants.Messages.MultipleChoiceNeedsAtLeastOneCorrect;
        }

        return null;
    }

    /// <summary>
    /// Builds the full VideoExam entity graph (exam → questions → options) from
    /// the create-time DTO. Not persisted here — caller AddExamAsync's the
    /// returned root and the change tracker cascades the whole graph on
    /// SaveChangesAsync.
    /// </summary>
    private static VideoExam BuildExamEntity(
        CreateExamDto dto, long videoAssetId, long teacherId, long actingUserId, DateTime utcNow)
    {
        var exam = new VideoExam
        {
            VideoAssetId = videoAssetId,
            TeacherId = teacherId,
            Title = dto.Title.Trim(),
            Description = dto.Description?.Trim(),
            CreatedByUserId = actingUserId,
            CreateAt = utcNow,
        };

        int questionOrder = 0;
        foreach (var qDto in dto.Questions)
        {
            var question = new VideoExamQuestion
            {
                Text = qDto.Text.Trim(),
                QuestionType = qDto.QuestionType,
                SortOrder = questionOrder++,
                CreateAt = utcNow,
            };

            int optionOrder = 0;
            foreach (var oDto in qDto.Options)
            {
                question.Options.Add(new VideoExamQuestionOption
                {
                    Text = oDto.Text.Trim(),
                    IsCorrect = oDto.IsCorrect,
                    SortOrder = optionOrder++,
                    CreateAt = utcNow,
                });
            }

            exam.Questions.Add(question);
        }

        return exam;
    }
    /// <summary>
    /// Validates every id belongs to the calling teacher. Shared by
    /// <see cref="AssignVideoToUnitsAsync"/> and <see cref="UpdateVideoAsync"/>'s
    /// UnitIds handling so both run through identical ownership checks.
    /// </summary>
    private async Task<string?> ValidateUnitIdsOwnershipAsync(long teacherId, IEnumerable<long> unitIds)
    {
        foreach (var unitId in unitIds.Distinct())
        {
            var unit = await _unitOfWork.VideoUnitsRepo.GetUnitByIdAndTeacherAsync(unitId, teacherId);
            if (unit is null)
                return VideoConstants.Messages.VideoUnitNotFound;
        }
        return null;
    }

}
