using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Extended repository interface for the Video Content Management Module
/// (Module 14). Centralizes every domain-specific query for the five VCM
/// entities: <c>VideoAsset</c>, <c>VideoScope</c>, <c>VideoAnalytics</c>,
/// <c>VideoWatchEvent</c>, and <c>VideoAssetAudit</c>.
///
/// ARCHITECTURAL NOTE (same rationale as <c>IUserRepo</c>, <c>IAttendanceRepo</c>,
/// <c>IPaymentRepo</c>, <c>IExamHomeworkRepo</c>):
/// All expression-based queries are encapsulated here in named methods. The
/// Application layer never builds raw predicates. If a query changes, you edit
/// ONE method in this repo — not every service that uses it.
///
/// Inherits <see cref="IGenericRepo{T, Tkey}"/> over <see cref="VideoAsset"/>
/// for basic CRUD on the primary entity. Other entities are accessed via the
/// named methods below.
///
/// QUERY PROJECTION TYPES — declared in the sibling file
/// <c>VideoRepoProjections.cs</c> within this same namespace:
/// <see cref="TeacherVideoListRow"/>, <see cref="StudentVideoListRow"/>,
/// <see cref="VideoAnalyticsSnapshot"/>, <see cref="VideoAnalyticsIncrementResult"/>,
/// <see cref="VideoAnalyticsReportRow"/>, <see cref="VideoAnalyticsAggregates"/>,
/// <see cref="VideoAnalyticsAuditRow"/>.
///
/// CONCURRENCY NOTE — atomic UPSERT path:
/// The Stop event flow uses <see cref="IncrementWatchAtomicAsync"/>, which
/// executes a conditional <c>ExecuteUpdateAsync</c> with SQL-side increment
/// rather than a tracked read-modify-write. This makes simultaneous Stop
/// events from multiple devices (Story C concurrency case) correctly add
/// their deltas without an in-application lock. SQL Server's row-level X
/// lock serializes the two updates with sub-millisecond contention.
///
/// IDEMPOTENCY NOTE — ClientEventId path:
/// The Open and Stop event inserts use <see cref="HasEventWithClientIdAsync"/>
/// to detect retried events before insertion. The unique filtered index
/// <c>UX_VWE_ClientEventId</c> is the safety net at the DB level.
/// </summary>
public interface IVideoAssetRepo : IGenericRepo<VideoAsset, long>
{
    // ══════════════════════════════════════════════════════════════════════
    // VIDEO ASSET — WRITE PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds a new video to the change tracker. The service layer composes
    /// the asset + initial scope rows + first analytics-row defaults inside a
    /// single Unit of Work transaction (Story A).
    /// </summary>
    Task AddVideoAsync(VideoAsset video);
    /// <summary>
    /// Hard-deletes a video. Explicitly clears every NoAction-FK child table
    /// (scopes, attachments, analytics, watch events, unit links) before
    /// removing the VideoAsset row — NoAction blocks a delete with
    /// referencing rows still present, it does not cascade. VideoExam is
    /// exempt (CASCADE FK, removed automatically). The service layer must
    /// persist the JSON snapshot to <see cref="VideoAssetAudit"/> in the SAME
    /// transaction before invoking this (REQ-VCM-BR-03).
    /// </summary>
    Task DeleteVideoAsync(VideoAsset video);

    /// <summary>
    /// Conditionally updates <see cref="VideoAsset.DurationSeconds"/> when
    /// the client-supplied duration is acceptable per the trust-boundary rule
    /// in <c>VideoConstants.DurationToleranceFraction</c>:
    /// <list type="bullet">
    ///   <item>Current value = 0 → first-watch claim is accepted as-is.</item>
    ///   <item>Current value &gt; 0 and reported value within ±5% → accepted
    ///         (last-writer-wins on the column).</item>
    ///   <item>Otherwise → silently ignored. Service layer logs and proceeds.</item>
    /// </list>
    /// Implemented as <c>ExecuteUpdateAsync</c> with the tolerance check in the
    /// WHERE clause so the update is one round trip.
    /// </summary>
    /// <returns><c>true</c> if the row was updated; <c>false</c> if the report
    /// was out of tolerance and silently ignored.</returns>
    Task<bool> TryUpdateDurationWithinToleranceAsync(
        long videoAssetId,
        int reportedDurationSeconds,
        double toleranceFraction);

    /// <summary>
    /// Sets <c>Status</c> and <c>PublishDate</c> on a video, owner-scoped.
    /// Track D1 — backs both the quick Settings-row toggle
    /// (<c>PATCH /api/videos/{id}/status</c>) and the full update endpoint
    /// (G-EDIT). Single <c>ExecuteUpdateAsync</c> round trip.
    /// </summary>
    /// <returns><c>true</c> if a row was updated; <c>false</c> if the video
    /// doesn't exist or belongs to a different teacher.</returns>
    Task<bool> SetVideoStatusAsync(
        long videoAssetId,
        long teacherId,
        VideoStatus status,
        DateTime? publishDate);

    /// <summary>
    /// Hard-deletes every <c>VideoAnalytics</c> and <c>VideoWatchEvent</c> row
    /// for a video and resets <c>DurationSeconds</c> to 0. Called by G-EDIT's
    /// update flow only when <c>SourceUrl</c> changes — confirmed rule: a
    /// changed URL is a different video, so its watch history restarts.
    /// Does NOT touch <c>VideoScope</c> rows.
    /// </summary>
    Task ResetAnalyticsForVideoAsync(long videoAssetId);

    // ══════════════════════════════════════════════════════════════════════
    // VIDEO ASSET — READ PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fetches a video by id, scoped to the teacher who owns it. Returns
    /// <c>null</c> if the row does not exist or belongs to a different
    /// teacher. Tracked: caller may mutate <c>DurationSeconds</c> indirectly
    /// or pass to <c>DeleteVideoAsync</c>.
    /// </summary>
    Task<VideoAsset?> GetVideoByIdAndTeacherAsync(long videoAssetId, long teacherId);

    /// <summary>
    /// Fetches a video together with its scope rows eagerly loaded. Used by
    /// the delete flow (snapshot-building) and the "manage access" view.
    /// AsNoTracking — caller does not mutate.
    /// </summary>
    Task<VideoAsset?> GetVideoWithScopesAsync(long videoAssetId, long teacherId);

    /// <summary>
    /// Paged list of a teacher's own videos for the Teacher Videos screen.
    /// Story B endpoint <c>GET /api/videos/teacher</c>. Backed by
    /// <c>IX_VideoAssets_TeacherId_CreatedAt</c> (newest first).
    ///
    /// The repo also returns the per-video <c>StudentsInScope</c> and
    /// <c>TotalOpens</c> aggregates so the service layer doesn't need to
    /// post-process — single round trip via projection.
    /// </summary>
    /// <param name="teacherId">Tenant scope (multi-tenant isolation).</param>
    /// <param name="search">Optional partial-match filter on
    /// <c>VideoAsset.Title</c> using <c>EF.Functions.Like</c>.</param>
    /// <param name="page">1-based page number (clamped by caller).</param>
    /// <param name="pageSize">Page size (clamped by caller).</param>
    Task<(IReadOnlyList<TeacherVideoListRow> Items, int TotalCount)>
        GetTeacherVideosPagedAsync(
            long teacherId,
            string? search,
            int page,
            int pageSize,
            VideoStatus? status = null);

    /// <summary>
    /// Paged list of videos visible to a specific student. Story B endpoint
    /// <c>GET /api/videos/student</c>. Resolves access through the union of
    /// the three scope-target indexes filtered by the student's session and
    /// session group memberships.
    ///
    /// Includes the per-(video, student) analytics state — <c>HasOpened</c>,
    /// <c>LastOpenedAt</c> — via a LEFT JOIN to <c>VideoAnalytics</c> so the
    /// student's video-list cards can show watch indicators in one round trip.
    /// </summary>
    /// <param name="teacherId">The teacher whose videos are being listed.
    /// Caller (service) supplies this from the <c>StudentTeacherLink</c>
    /// resolution; the repo trusts it.</param>
    /// <param name="teacherStudentId">The student whose access is checked.</param>
    /// <param name="search">Optional title filter, Arabic-normalized like the teacher's list.</param>
    /// <param name="watchStatus">
    /// Optional watch-state filter. Null = every visible video. Otherwise keeps only rows whose
    /// state under <see cref="Helpers.VideoWatchRules"/> equals the requested one, so the filter
    /// and the row badge can never disagree. Applied before the count, so the total describes the
    /// filtered set.
    /// </param>
    Task<(IReadOnlyList<StudentVideoListRow> Items, int TotalCount)>
        GetVisibleVideosForStudentAsync(
            long teacherId,
            long teacherStudentId,
            int page,
            int pageSize,
            string? search = null,
            VideoWatchStatus? watchStatus = null);

    /// <summary>
    /// Same visible-video resolution as <see cref="GetVisibleVideosForStudentAsync"/>, but
    /// restricted to videos linked to <paramref name="unitId"/> (V3 unit drill-down). Shares one
    /// query path with the un-filtered list so the two never disagree. Batched (no N+1).
    /// </summary>
    /// <param name="search">Optional title filter, Arabic-normalized like the teacher's list.</param>
    /// <param name="watchStatus">
    /// Optional watch-state filter — same three states and same rule
    /// (<see cref="Helpers.VideoWatchRules"/>) as the un-filtered list above.
    /// </param>
    Task<(IReadOnlyList<StudentVideoListRow> Items, int TotalCount)>
        GetVisibleVideosForStudentInUnitAsync(
            long teacherId,
            long teacherStudentId,
            long unitId,
            int page,
            int pageSize,
            string? search = null,
            VideoWatchStatus? watchStatus = null);

    /// <summary>
    /// The units (V3) a student can see under a teacher — those containing at least one video
    /// visible to the student (same "visible video" predicate as
    /// <see cref="GetVisibleVideosForStudentAsync"/>) — with per-unit counts (videos + how many
    /// carry a quiz). One query + in-memory group; batched, no N+1.
    /// </summary>
    Task<IReadOnlyList<StudentVideoUnitRow>> GetStudentVisibleUnitsAsync(
        long teacherId, long teacherStudentId);

    /// <summary>
    /// Scalar seen/unseen rollup for one student under one teacher (Parent Module requirements
    /// §9.1): total videos visible to the student, and how many of those the student has opened
    /// at least once. Uses the EXACT same visible-video scope predicate as
    /// <see cref="GetVisibleVideosForStudentAsync"/> (Published + PublishDate gate, Session /
    /// SessionGroup scope union) so the count can never disagree with what the student's own video
    /// list shows — just a COUNT instead of a paged projection, since the caller only needs totals.
    ///
    /// <para>"Seen" means STARTED (the student has watch time on it), not "an analytics row
    /// exists" — a row is written with 0 seconds on the first play report, so the old row-exists
    /// test counted a video the student never actually watched. Thin delegate to
    /// <see cref="GetStudentVideoWatchCountsAsync"/> so this and the tri-state breakdown are the
    /// same numbers.</para>
    /// </summary>
    Task<(int Total, int Seen)> GetStudentVideoSeenCountsAsync(long teacherId, long teacherStudentId);

    /// <summary>
    /// Tri-state rollup for one student under one teacher over the SAME visible-video scope
    /// predicate as <see cref="GetVisibleVideosForStudentAsync"/>: how many videos are visible,
    /// and how many fall in each <see cref="VideoWatchStatus"/> under
    /// <see cref="Helpers.VideoWatchRules"/> (SQL mirror). The three buckets always sum to
    /// <c>Total</c>, so the student home Videos tile and the list filter can never disagree.
    /// Three COUNTs, no materialized rows.
    /// </summary>
    Task<(int Total, int NotStarted, int InProgress, int Completed)> GetStudentVideoWatchCountsAsync(
        long teacherId, long teacherStudentId);

    /// <summary>
    /// The owning teacher's subject for the student video-list <c>Subject</c> column:
    /// <c>CustomSubject</c> plus the first linked ministry subject's English/Arabic names (the
    /// service resolves the display value by the reader's language, replicating the canonical
    /// <c>StudentUserService</c> pattern). One lookup per list, never per row. Null if the
    /// teacher does not exist.
    /// </summary>
    /// <summary>
    /// The roster ids a video is scoped to — the SAME resolved audience the analytics
    /// report and the teacher list's per-video counts use, so a publish notification can
    /// never reach someone the video does not actually target.
    /// </summary>
    Task<IReadOnlyList<long>> GetAudienceTeacherStudentIdsAsync(long teacherId, long videoAssetId);

    Task<TeacherSubjectInfo?> GetTeacherSubjectAsync(long teacherId);

    /// <summary>
    /// Student take-screen projection of a video's quiz questions + options, ordered by
    /// SortOrder — <c>IsCorrect</c> is absent from the projected type (security by shape,
    /// mirrors <c>IOnlineExamRepo.GetQuestionsForStudentAsync</c>). Empty if the video has no
    /// quiz. The service fills each row's gated <c>ImageUrl</c> from <c>ImageFileInternalId</c>.
    /// </summary>
    Task<IReadOnlyList<StudentVideoExamQuestionRow>> GetStudentVideoExamQuestionsAsync(long videoAssetId);

    /// <summary>
    /// Lightweight header of a video's quiz (id/title/description), tenant-scoped — also the
    /// "does this video have a quiz?" existence check (null = no quiz). Does not load the
    /// question graph or answer key.
    /// </summary>
    Task<VideoExamHeaderRow?> GetVideoExamHeaderAsync(long videoAssetId, long teacherId);

    // ══════════════════════════════════════════════════════════════════════
    // SCOPE — WRITE PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds a batch of scope rows for an existing video. Service layer is
    /// responsible for validating each scope's target belongs to the same
    /// teacher (via <see cref="IsScopeTargetOwnedByTeacherAsync"/>) before
    /// calling.
    /// </summary>
    Task AddScopesAsync(IEnumerable<VideoScope> scopes);

    /// <summary>
    /// Hard-deletes every scope row for a given video. Used by the
    /// PUT-replace-all flow (Story D), wrapped in the service layer's
    /// transaction. Implemented as <c>ExecuteDeleteAsync</c> for a single
    /// round trip — no in-memory loading.
    /// </summary>
    Task DeleteAllScopesForVideoAsync(long videoAssetId);

    /// <summary>
    /// Hard-deletes every <c>VideoScope</c> row targeting a given session
    /// (<c>ScopeType = Session</c>). Called by the session-delete cleanup in
    /// <c>SessionService.DeleteSessionAsync</c> BEFORE the session row is hard-
    /// deleted: the <c>VideoScopes.SessionId</c> FK is <c>NoAction</c>, so any
    /// row still targeting the session would otherwise block the delete with a
    /// 409 "conflicts with existing data". The scope row is a live access rule,
    /// not history — nulling the FK would violate the CHECK constraint (exactly
    /// one target FK non-null), so the row is removed. Set-based
    /// <c>ExecuteDeleteAsync</c>, one round trip.
    /// </summary>
    Task DeleteScopesBySessionAsync(long sessionId);

    /// <summary>
    /// Hard-deletes every <c>VideoScope</c> row targeting a given session group
    /// (<c>ScopeType = SessionGroup</c>). Session-group counterpart of
    /// <see cref="DeleteScopesBySessionAsync"/>, called by
    /// <c>SessionService.DeleteGroupAsync</c> so the <c>NoAction</c>
    /// <c>SessionGroupId</c> FK cannot block the group delete.
    /// </summary>
    Task DeleteScopesByGroupAsync(long sessionGroupId);

    /// <summary>
    /// Hard-deletes a single scope row, scoped to the teacher. Used by the
    /// DELETE-single-scope endpoint (Story D, endpoint #4). Returns
    /// <c>false</c> if the row does not exist or belongs to a different
    /// teacher.
    /// </summary>
    Task<bool> DeleteScopeByIdAndTeacherAsync(long scopeId, long teacherId);

    /// <summary>
    /// Returns the count of scope rows for a video. Used by the
    /// "last scope cannot be removed" check in DELETE-single-scope.
    /// </summary>
    Task<int> CountScopesForVideoAsync(long videoAssetId);

    // ══════════════════════════════════════════════════════════════════════
    // SCOPE — VALIDATION (called by IVideoScopeResolver)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a scope target row (student / session / session-group)
    /// belongs to the given teacher. Returns <c>false</c> if the target id
    /// does not exist or belongs to a different teacher.
    ///
    /// Used by the service layer to reject foreign-tenant scope targets with
    /// <c>ScopeTargetNotFoundOrForeign</c> before any write happens.
    ///
    /// One method, three branches by <see cref="VideoScopeType"/>, so the
    /// resolver doesn't need three separate calls. The repo dispatches
    /// internally.
    /// </summary>
    Task<bool> IsScopeTargetOwnedByTeacherAsync(
        long teacherId,
        VideoScopeType scopeType,
        long targetId);

    // ══════════════════════════════════════════════════════════════════════
    // ACCESS CHECK — student-facing endpoints
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns <c>true</c> when the given student is in the resolved scope of the
    /// given video — decided by the video's OWN <see cref="VideoScope"/> rows only
    /// (unit scope is a write-time boundary, not a runtime grant): (a) Session
    /// scopes whose session contains this student, (b) SessionGroup scopes whose
    /// group contains this student's session.
    ///
    /// This is the BR-VCM-01 enforcement gate called on every Start and Stop
    /// request before any analytics write. Backed by the three filtered
    /// scope-target indexes; expected sub-millisecond cost.
    /// </summary>
    Task<bool> IsStudentInVideoScopeAsync(
        long teacherStudentId,
        long videoAssetId,
        long teacherId);

    // ══════════════════════════════════════════════════════════════════════
    // ANALYTICS — WRITE PATH (atomic UPSERT for multi-device safety)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Atomic SQL-side UPDATE that increments <c>OpenCount</c> on the analytics
    /// row for the (video, student) pair when a row exists. Returns the resulting
    /// snapshot, or <c>null</c> when no row matched (caller handles by inserting
    /// a fresh row via <see cref="AddAnalyticsRowForFirstOpenAsync"/>).
    ///
    /// Implemented as a single <c>ExecuteUpdateAsync</c>; no SaveChanges needed
    /// because <c>ExecuteUpdateAsync</c> writes immediately.
    /// </summary>
    /// <returns>The analytics state after the increment, or <c>null</c> when
    /// no row exists yet for this (video, student) pair.</returns>
    Task<VideoAnalyticsSnapshot?> IncrementOpenCountIfExistsAsync(
        long videoAssetId,
        long teacherStudentId,
        DateTime utcNow);

    /// <summary>
    /// Queues an INSERT of a new <see cref="VideoAnalytics"/> row representing
    /// the student's first-ever open of the video. Does NOT call SaveChanges —
    /// the caller (service layer) must call <c>IUnitOfWork.SaveChangesAsync()</c>
    /// and handle <c>DbUpdateException</c> via the bounded retry pattern when
    /// the unique index <c>UX_VideoAnalytics_Video_Student</c> rejects a
    /// concurrent insert.
    ///
    /// On a unique-violation collision, the caller retries by calling
    /// <see cref="IncrementOpenCountIfExistsAsync"/> instead — the competing
    /// transaction has already created the row, so the increment path now
    /// applies.
    /// </summary>
    Task AddAnalyticsRowForFirstOpenAsync(VideoAnalytics row);

    /// <summary>
    /// Read-only snapshot of the analytics row for the (video, student) pair.
    /// Returns <c>null</c> when no row exists.
    ///
    /// Used by the StartWatch and StopWatch idempotency-replay paths: when a
    /// duplicate <c>ClientEventId</c> is detected, the service reads the
    /// current state and returns it without inserting anything new — distinct
    /// from <see cref="IncrementWatchAtomicAsync"/> which mutates
    /// <c>LastUpdated</c> on every call.
    /// </summary>
    Task<VideoAnalyticsSnapshot?> GetAnalyticsSnapshotAsync(
        long videoAssetId,
        long teacherStudentId);

    /// <summary>
    /// Atomic SQL-side UPDATE that adds the validated delta to
    /// <c>TotalWatchSeconds</c>, sets <c>LastResumePositionSeconds</c>, and
    /// stamps <c>LastUpdated</c>. Implemented via <c>ExecuteUpdateAsync</c>
    /// with no read-modify-write in application code.
    ///
    /// Concurrency: two simultaneous Stop calls from phone + tablet both run
    /// this UPDATE on the same row. SQL Server takes a row-level X lock; the
    /// second waits ~1ms; both deltas are correctly added.
    /// <c>LastResumePositionSeconds</c> and <c>LastUpdated</c> are last-writer-
    /// wins, which is the correct semantic for "current state" fields.
    ///
    /// Returns the resulting <c>TotalWatchSeconds</c> and
    /// <c>LastResumePositionSeconds</c> so the service can return them in the
    /// stop response without an extra SELECT.
    /// </summary>
    /// <returns>The analytics state after the increment, or <c>null</c> if no
    /// row matched the (video, student) key — caller treats this as
    /// <c>NoActiveSession</c> per Story C.</returns>
    Task<VideoAnalyticsIncrementResult?> IncrementWatchAtomicAsync(
        long videoAssetId,
        long teacherStudentId,
        long acceptedDeltaSeconds,
        int positionSeconds,
        DateTime utcNow);

    // ══════════════════════════════════════════════════════════════════════
    // ANALYTICS — READ PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Paged teacher analytics report for one video (Story F endpoint
    /// <c>GET /api/videos/{id}/analytics</c>). Returns one row per student in
    /// the resolved scope, including students who have never opened the video
    /// (LEFT JOIN to <c>VideoAnalytics</c>).
    ///
    /// Aggregates (<c>totalStudentsInScope</c>, <c>totalStudentsWatched</c>)
    /// are computed by separate count queries — see
    /// <see cref="GetAnalyticsAggregatesAsync"/> — because they don't change
    /// with pagination.
    /// </summary>
    /// <param name="teacherId">Tenant scope.</param>
    /// <param name="videoAssetId">Target video.</param>
    /// <param name="search">Optional partial-match filter on
    /// <c>StudentName</c> or <c>StudentCode</c>.</param>
    /// <param name="sortBy">Column to sort by — see
    /// <see cref="VideoAnalyticsSortBy"/>.</param>
    /// <param name="sortDirection">Asc or Desc.</param>
    /// <param name="statusFilter">Narrows rows to Seen/Unseen/Completed
    /// (G-ANL-4). "Completed" uses the same
    /// <c>VideoConstants.CompletionThresholdPercent</c> threshold as
    /// <see cref="GetAnalyticsAggregatesAsync"/>'s <c>CompletedCount</c>.</param>
    /// <param name="page">1-based.</param>
    /// <param name="pageSize">Clamped by caller.</param>
    Task<(IReadOnlyList<VideoAnalyticsReportRow> Items, int TotalCount)>
        GetAnalyticsRowsForTeacherAsync(
            long teacherId,
            long videoAssetId,
            string? search,
            VideoAnalyticsSortBy sortBy,
            SortDirection sortDirection,
            VideoAnalyticsStatusFilter statusFilter,
            int page,
            int pageSize);

    /// <summary>
    /// Top-of-report aggregates: total students in scope, total students who
    /// have opened the video at least once. Two scalar SELECTs in one round
    /// trip via tuple result.
    /// </summary>
    Task<VideoAnalyticsAggregates> GetAnalyticsAggregatesAsync(
        long teacherId,
        long videoAssetId);

    /// <summary>
    /// Materializes everything needed by the delete-snapshot serializer:
    /// every <see cref="VideoAnalytics"/> row joined with the student's name
    /// and code (denormalized into the snapshot so it survives student
    /// purge).
    /// </summary>
    Task<IReadOnlyList<VideoAnalyticsAuditRow>> GetAnalyticsForAuditSnapshotAsync(
        long videoAssetId);

    // ══════════════════════════════════════════════════════════════════════
    // WATCH EVENT — WRITE PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Inserts a single watch event row (Open or Stop). The unique filtered
    /// index <c>UX_VWE_ClientEventId</c> is the DB-level idempotency safety
    /// net; the service layer should call
    /// <see cref="HasEventWithClientIdAsync"/> first to detect retries
    /// without raising an exception.
    /// </summary>
    Task AddWatchEventAsync(VideoWatchEvent watchEvent);

    // ══════════════════════════════════════════════════════════════════════
    // WATCH EVENT — READ PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the <c>EventUtc</c> of the most recent prior event for the
    /// given (student, video, device) tuple. This is the "anchor" event that
    /// drives delta validation on Stop (Story C). Returns <c>null</c> if no
    /// prior event exists — caller treats this as <c>NoActiveSession</c>.
    ///
    /// Backed by <c>IX_VWE_Student_Video_Device_TimeDesc</c>: single seek +
    /// TOP 1, sub-millisecond.
    /// </summary>
    Task<DateTime?> GetLastEventUtcForDeviceAsync(
        long teacherStudentId,
        long videoAssetId,
        string deviceId);

    /// <summary>
    /// Returns <c>true</c> when an event with the same
    /// <c>ClientEventId</c> has already been recorded. Used by the service
    /// layer to short-circuit retried events with the previous response
    /// payload, instead of inserting a duplicate row and waiting for the
    /// unique index to throw.
    /// </summary>
    Task<bool> HasEventWithClientIdAsync(Guid clientEventId);

    /// <summary>Counts the videos owned by a teacher (free-tier quota enforcement).</summary>
    Task<int> CountByTeacherAsync(long teacherId);

    // ══════════════════════════════════════════════════════════════════════
    // AUDIT — WRITE PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Inserts the audit row that captures a deleted video's snapshot. Always
    /// called inside the same transaction as the NoAction-delete of the
    /// underlying VCM rows (Story E). If the surrounding transaction rolls
    /// back, the audit row rolls back too — no orphan audits.
    /// </summary>
    Task AddAuditAsync(VideoAssetAudit audit);

    // ══════════════════════════════════════════════════════════════════════
    // INTEGRATION HOOKS (called by Teacher / Student permanent-purge flows)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Hard-deletes every <see cref="VideoAsset"/> belonging to the given
    /// teacher, plus all VCM NoAction-children (audits are kept by
    /// configuration). Used by the admin "permanent-purge teacher" flow,
    /// which the DB NoAction does NOT cover (Phase 2.2 NoAction-graph decision:
    /// <c>Teachers → VideoAssets = NO_ACTION</c>).
    ///
    /// Service layer wraps this in a transaction and writes audit rows for
    /// every video before calling.
    /// </summary>
    Task DeleteAllVideosForTeacherAsync(long teacherId);

    /// <summary>
    /// Clears every VCM row that would BLOCK the hard delete of a roster record
    /// (<c>TeacherStudent</c>), called by the student permanent-purge flow.
    ///
    /// <c>VideoAnalytics.TeacherStudentId</c> and <c>VideoWatchEvent.TeacherStudentId</c> are
    /// NON-nullable with a <c>NoAction</c> FK, and <c>VideoScope.TeacherStudentId</c> is
    /// <c>NoAction</c> too — SQL Server does NOT clean any of them, so purging a student who
    /// ever watched a video used to fail with an FK violation (500). Per the entities' own
    /// contract ("student permanent-purge takes their scope/watch rows with them") these are
    /// DELETED rather than nulled: a per-student scope row cannot survive without its student
    /// (it would break the scope's exactly-one-target semantics), and watch history is
    /// meaningless once the student is gone.
    ///
    /// Also removes the student's <c>StudentVideoExamReport</c> rows (answers/options cascade
    /// at the DB level) so no orphan attempt survives the student.
    ///
    /// Set-based (ExecuteDelete) and idempotent — a second call deletes zero rows.
    /// </summary>
    Task PurgeStudentVideoDataAsync(long teacherStudentId);

    // Attachments and video photos are now central-registry FileObjects (referenced by
    // VideoAsset.VideoPhotoFileId / FileObject.VideoAssetId). See IFileObjectRepo.

    /// <summary>
    /// Resolves the video that OWNS a registry file, tenant-scoped — used by the gated file
    /// endpoint's scoped-student authorization to find which video a photo / video-exam-question
    /// image belongs to (attachments carry <c>FileObject.VideoAssetId</c> directly and don't
    /// need this). Returns null when no video of <paramref name="teacherId"/> references the file.
    /// </summary>
    Task<long?> GetOwningVideoAssetIdForFileAsync(long fileObjectId, FileCategory category, long teacherId);

    Task ReplaceUnitLinksAsync(
    long videoAssetId,
    IEnumerable<long> unitIds);

    Task<List<long>> GetLinkedUnitIdsAsync(
        long videoAssetId);
    // ══════════════════════════════════════════════════════════════════════
    // EXAM (merged-creation refactor) — write path only, no read/update/delete
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Queues an INSERT of a new exam (with its questions and options already
    /// attached via navigation properties). Caller calls SaveChanges. No
    /// separate AddQuestionAsync/AddOptionAsync — EF Core's change tracker
    /// cascades the insert through the object graph in one call.
    /// </summary>
    Task AddExamAsync(VideoExam exam);
    /// <summary>
    /// Hard-deletes a video's entire exam tree (single ExecuteDeleteAsync on
    /// VideoExams — questions/options cascade at the DB level via their
    /// Cascade FKs). Used by the replace-all exam edit in UpdateVideoAsync.
    /// No-op if the video has no exam.
    /// </summary>
    Task DeleteExamForVideoAsync(long videoAssetId);

    /// <summary>
    /// The non-null <c>ImageFileId</c>s of every question in a video's exam — the registry files to
    /// detach when the exam is replaced or the video is deleted. Empty if none.
    /// </summary>
    Task<IReadOnlyList<long>> GetExamQuestionImageFileIdsAsync(long videoAssetId);
    /// <summary>
    /// Fetches a video's exam with questions and options eagerly loaded, in
    /// SortOrder. Returns null if the video has no exam. AsNoTracking — used
    /// by GetVideoDetailAsync's read-only pre-fill mapping.
    /// </summary>
    Task<VideoExam?> GetExamWithQuestionsAsync(long videoAssetId, long teacherId);

    /// <summary>
    /// Counts the questions in a video's exam (0 when the video has no exam).
    /// A cheap COUNT — used by the overview/detail base mapper to populate
    /// <c>QuestionsNumber</c> without materializing the exam tree.
    /// </summary>
    Task<int> GetExamQuestionCountAsync(long videoAssetId);
}
