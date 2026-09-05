namespace Edvanz.Domain.Constants;

/// <summary>
/// Constants for the Video Content Management Module (Module 14).
/// Centralizes magic numbers, column-length limits, the module name string, and
/// localization keys so they are compile-time checked and single-sourced.
///
/// Same pattern as <see cref="AttendanceConstants"/> and <see cref="SubscriptionConstants"/>.
/// </summary>
public static class VideoConstants
{
    // ══════════════════════════════════════════════
    // MODULE IDENTITY
    // ══════════════════════════════════════════════

    /// <summary>
    /// The module name as registered in the <c>Modules</c> seed table and emitted
    /// in JWT <c>module</c> claims. This string appears in
    /// <c>[ModulePermission(Module = ModuleName, ...)]</c> attributes on every VCM
    /// endpoint, in <c>IModuleTeacherRepo.IsModuleActiveAsync</c> calls for the
    /// student/parent runtime gate, and in the permission seeder. Change it in
    /// exactly one place: here.
    /// </summary>
    public const string ModuleName = "Videos";

    // ══════════════════════════════════════════════
    // PERMISSION NAMES (seeded under the "Videos" module)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Read-only permission for teachers and assistants. Required by
    /// <c>GET /api/videos/teacher</c> and <c>GET /api/videos/{id}/analytics</c>.
    /// </summary>
    public const string PermissionView = "View";

    /// <summary>
    /// Write permission for teachers and assistants. Required by every
    /// create / scope-change / delete endpoint.
    /// </summary>
    public const string PermissionManageVideos = "ManageVideos";

    // ══════════════════════════════════════════════
    // VALIDATION TOLERANCES (server-side trust boundaries)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Tolerance for accepting a client-supplied <c>videoDurationSeconds</c> against
    /// the value already stored on <c>VideoAsset.DurationSeconds</c>. Expressed as a
    /// fraction (0.05 = ±5%). A duration outside this band is silently rejected and
    /// logged — never returned as an error to the client (Story B trust boundary).
    /// </summary>
    public const double DurationToleranceFraction = 0.05;

    /// <summary>
    /// Slack added to the server-elapsed-time check when validating the client's
    /// <c>deltaSeconds</c> on a Stop event. Allows for honest clock drift, request
    /// latency, and rounding without rejecting the report. Larger values than
    /// elapsed + this tolerance are clamped, never rejected (Story C trust boundary).
    /// </summary>
    public const int DeltaToleranceSeconds = 5;

    /// <summary>
    /// Number of seconds subtracted from <c>DurationSeconds</c> when computing
    /// <c>resumeFromSeconds</c>. Prevents the player from resuming so close to
    /// the end that the student perceives an "instant complete" glitch.
    /// </summary>
    public const int ResumeEndOfVideoBufferSeconds = 5;

    /// <summary>
    /// Minimum <c>EstimatedCompletionPct</c> (0-100) for a student to count as
    /// having "completed" a video. Drives the <c>completedCount</c> aggregate
    /// (G-ANL-1) and the <c>statusFilter=Completed</c> row filter (G-ANL-4) —
    /// one threshold, reused by both so the numbers never disagree.
    /// </summary>
    public const int CompletionThresholdPercent = 90;

    /// <summary>
    /// Maximum number of attachments a single video may hold (multi-attachment support,
    /// 2026-07-16). Mirrors <c>UploadConstants.MaxFilesPerRequest</c> so one upload batch can
    /// fill one video exactly.
    /// </summary>
    public const int MaxAttachmentsPerVideo = 10;

    /// <summary>Allowed content types for video photo (cover image) uploads.</summary>
    public static readonly string[] AllowedVideoPhotoContentTypes = { "image/jpeg", "image/png" };

    /// <summary>Allowed content types for video attachments — PDF only (ratified).</summary>
    public static readonly string[] AllowedAttachmentContentTypes = { "application/pdf" };

    // ══════════════════════════════════════════════
    // COLUMN-LENGTH LIMITS (mirrored in fluent API)
    // ══════════════════════════════════════════════

    /// <summary>Maximum length of <c>VideoAsset.Title</c>. Trimmed in service layer.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>Maximum length of <c>VideoAsset.Description</c>. Optional field.</summary>
    public const int DescriptionMaxLength = 2000;

    /// <summary>
    /// Maximum length of <c>VideoAsset.SourceUrl</c>. Long enough for any realistic
    /// YouTube or Drive URL including query parameters.
    /// </summary>
    public const int SourceUrlMaxLength = 500;

    /// <summary>
    /// Maximum length of <c>VideoAsset.ExternalId</c>. Comfortably accommodates
    /// YouTube videoIds (~11 chars) and Drive fileIds (~33 chars).
    /// </summary>
    public const int ExternalIdMaxLength = 100;

    /// <summary>
    /// Maximum length of <c>VideoWatchEvent.DeviceId</c>. The client sends a
    /// stable per-install UUID; 100 chars is generous enough for any UUID format
    /// while bounding the column for indexing.
    /// </summary>
    public const int DeviceIdMaxLength = 100;

    /// <summary>Maximum length of <c>VideoAssetAudit.Action</c>.</summary>
    public const int AuditActionMaxLength = 20;

    /// <summary>
    /// Maximum length of <c>VideoAssetAudit.SnapshotArchiveUrl</c>. Reserved for a
    /// future cold-storage migration when the inline JSON snapshot grows large.
    /// Always <c>NULL</c> in v1 — the snapshot lives entirely in <c>SnapshotJson</c>.
    /// </summary>
    public const int SnapshotArchiveUrlMaxLength = 500;
    public const int MaxOpenRetries = 10;


    // ══════════════════════════════════════════════
    // LOCALIZATION KEYS
    // ══════════════════════════════════════════════

    /// <summary>
    /// Localization message keys. Names match entries in <c>Messages.en.resx</c> /
    /// <c>Messages.ar.resx</c>. Service code references these constants — never
    /// inline string literals — so a typo is a build failure, not a runtime miss.
    /// </summary>
    public static class Messages
    {
        // ── Success ──────────────────────────────────────────────────────────
        public const string VideoCreated   = "VideoCreated";
        public const string VideoUpdated   = "VideoUpdated";
        public const string VideoDeleted   = "VideoDeleted";
        public const string ScopesAssigned = "ScopesAssigned";
        public const string ScopesReplaced = "ScopesReplaced";
        public const string ScopeRemoved   = "ScopeRemoved";
        public const string WatchStarted   = "WatchStarted";
        public const string WatchStopped   = "WatchStopped";
        public const string VideoStatusUpdated = "VideoStatusUpdated";
        public const string ExamCreated = "ExamCreated";
        public const string AttachmentUpdateFailedVideoSaved = "AttachmentUpdateFailedVideoSaved";

        // ── Errors ───────────────────────────────────────────────────────────
        public const string VideoNotFound                = "VideoNotFound";
        public const string VideoNotInScope              = "VideoNotInScope";
        public const string ModuleDeactivated            = "ModuleDeactivated";
        public const string InvalidUrl                   = "InvalidUrl";
        public const string UnsupportedSource            = "UnsupportedSource";

        /// <summary>
        /// The URL parsed to a provider the student app cannot play — in
        /// practice Google Drive. The parser and <see cref="Domain.Enums.VideoSourceType"/>
        /// still accept Drive (and the service still builds its /preview embed
        /// URL), but no client has a Drive player, so a Drive-backed video is
        /// created successfully and then shows every student "unsupported
        /// source". Creating one is rejected here instead; existing rows are
        /// left alone because the update path only re-parses a CHANGED URL.
        /// </summary>
        public const string VideoSourceMustBeYouTube     = "VideoSourceMustBeYouTube";
        public const string ScopeTargetNotFoundOrForeign = "ScopeTargetNotFoundOrForeign";
        public const string ScopeShapeInvalid            = "ScopeShapeInvalid";
        public const string ScopeCannotBeEmpty           = "ScopeCannotBeEmpty";
        public const string LastScopeCannotBeRemoved     = "LastScopeCannotBeRemoved";
        public const string NoActiveSession              = "NoActiveSession";
        public const string NotVideoOwner                = "NotVideoOwner";
        public const string ScopeNotFound                = "ScopeNotFound";

        // ── Track C / G-UNIT ─────────────────────────────────────────────────
        public const string VideoUnitCreated  = "VideoUnitCreated";
        public const string VideoUnitUpdated  = "VideoUnitUpdated";
        public const string VideoUnitDeleted  = "VideoUnitDeleted";
        public const string VideoUnitNotFound = "VideoUnitNotFound";

        // ── Unit ↔ Video scope containment (a video's scope must stay within its units' scope) ──
        /// <summary>Error (422) — a video was saved/updated without belonging to any unit. Arg {0} = video title.</summary>
        public const string VideoMustBelongToUnit = "VideoMustBelongToUnit";
        /// <summary>Error (422) — a video targets sessions its units don't cover. Arg {0} = video title, {1} = offending session names.</summary>
        public const string VideoScopeExceedsUnitScope = "VideoScopeExceedsUnitScope";
        /// <summary>Error (409) — shrinking/removing a unit scope would leave member videos targeting uncovered sessions. Arg {0} = video titles.</summary>
        public const string UnitScopeChangeUncoversVideos = "UnitScopeChangeUncoversVideos";
        /// <summary>Error (409) — deleting a unit would leave member videos without a unit or with uncovered sessions. Arg {0} = video titles.</summary>
        public const string UnitDeleteWouldOrphanVideos = "UnitDeleteWouldOrphanVideos";

        // ── Track F / §5 Attachments ─────────────────────────────────────────
        public const string AttachmentsLimitExceeded = "VideoAttachmentsLimitExceeded";
        // "Thumbnail" renamed to "VideoPhoto" app-wide — same messages, new terminology.
        public const string VideoPhotoUploaded    = "VideoPhotoUploaded";
        public const string VideoPhotoReplaced    = "VideoPhotoReplaced";
        public const string VideoPhotoInvalidType = "VideoPhotoInvalidType";
        public const string VideoPhotoNotFound    = "VideoPhotoNotFound";

        // ── Phase 3 — multipart create request shape ──────────────────────────
        // ── Phase 3 — multipart create request shape ──────────────────────────
        public const string RequestValidationFailed = "RequestValidationFailed";

        // ── Phase 4 — full update (optimistic concurrency) ─────────────────────
        public const string ConcurrencyConflict = "ConcurrencyConflict";
        // ── Merged create: scope + exam ─────────────────────────────────────────
        public const string ExamTitleRequired = "ExamTitleRequired";
        public const string ExamMustHaveQuestions = "ExamMustHaveQuestions";
        public const string ExamQuestionTextRequired = "ExamQuestionTextRequired";
        public const string ExamQuestionNeedsOptions = "ExamQuestionNeedsOptions";
        public const string SingleChoiceNeedsExactlyOneCorrect = "SingleChoiceNeedsExactlyOneCorrect";
        public const string MultipleChoiceNeedsAtLeastOneCorrect = "MultipleChoiceNeedsAtLeastOneCorrect";

        public const string VideoUnitsAssigned = "VideoUnitsAssigned";

        // ── Student video-quiz flow (Module 14) ──────────────────────────────
        /// <summary>Success — a video-quiz attempt was submitted (finalized).</summary>
        public const string VideoQuizSubmitted = "VideoQuizSubmitted";
        /// <summary>Success — a video-quiz attempt was reset for a retake.</summary>
        public const string VideoQuizReset = "VideoQuizReset";
        /// <summary>Error — the video has no quiz attached.</summary>
        public const string VideoQuizNotFound = "VideoQuizNotFound";
        /// <summary>Error — a submitted answer references a question not in this quiz.</summary>
        public const string VideoQuizQuestionNotFound = "VideoQuizQuestionNotFound";
        /// <summary>Error — a selected option does not belong to its question.</summary>
        public const string VideoQuizInvalidOptionSelection = "VideoQuizInvalidOptionSelection";
        /// <summary>Error — a single-choice question was answered with more than one option.</summary>
        public const string VideoQuizSelectOneAnswer = "VideoQuizSelectOneAnswer";
        /// <summary>Error (409) — the attempt is already submitted; retry before submitting again.</summary>
        public const string VideoQuizAlreadySubmitted = "VideoQuizAlreadySubmitted";
        /// <summary>Error (404) — the student has no recorded attempt for this quiz.</summary>
        public const string VideoQuizAttemptNotFound = "VideoQuizAttemptNotFound";

        /// <summary>
        /// Maximum retry attempts for the StartWatch flow when a concurrent first-time
        /// open from the same student (e.g., simultaneously on phone and tablet)
        /// races to insert the analytics row. The unique index
        /// <c>UX_VideoAnalytics_Video_Student</c> rejects the second insert; the
        /// service retries by switching to the increment branch (the row now exists).
        ///
        /// Two retries is generous — in practice the second attempt always succeeds
        /// because the competing transaction has already committed by then.
        /// </summary>
        public const int MaxOpenRetries = 2;

    }
}
