namespace Edvanz.Domain.Interfaces;

// ════════════════════════════════════════════════════════════════════════════
// VIDEO CONTENT MANAGEMENT MODULE 14 — REPOSITORY PROJECTION TYPES
// ════════════════════════════════════════════════════════════════════════════
//
// Query projections returned by IVideoAssetRepo. Living in the same namespace
// as the interface (Edvanz.Domain.Interfaces) but in a sibling file keeps the
// interface readable while preserving the project's "projections live in the
// Domain layer alongside the repo" convention (see TrackingViewRow,
// AbsenceReportRow, ScopeCountAggregate in IExamHomeworkRepo for the same
// pattern, which used to be in-file but is being progressively split out).
//
// These are NOT DTOs — they are intermediate types between the database
// projection and the Application service's mapping to client-facing DTOs.
// Repos return these; services map them to PaginatedResponse<...DTO>.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Projection row for the teacher's video list (Story B). Includes the
/// per-video aggregates that the list cards display so the service doesn't
/// need a second query to compose them.
/// </summary>
public sealed class TeacherVideoListRow
{
    public long Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string SourceUrl { get; set; } = null!;
    public Enums.VideoSourceType SourceType { get; set; }
    public int DurationSeconds { get; set; }
    public int StudentsInScope { get; set; }
    public int TotalOpens { get; set; }

    /// <summary>
    /// Distinct students who have opened the video at least once (G-ANL-3).
    /// Not the same as <see cref="TotalOpens"/>, which counts re-opens.
    /// </summary>
    public int SeenStudentCount { get; set; }

    /// <summary>= <see cref="StudentsInScope"/> - <see cref="SeenStudentCount"/> (G-ANL-3).</summary>
    public int UnseenStudentCount { get; set; }

    /// <summary>Publish state (Draft/Published), shown on the list card.</summary>
    public Enums.VideoStatus Status { get; set; }

    /// <summary>Scheduled-publish timestamp, or null — lets the card distinguish Scheduled from Published.</summary>
    public DateTime? PublishDate { get; set; }

    /// <summary>
    /// Registry file id (<c>FileObject.Id</c>) of the video's cover photo, or null. The service
    /// batch-resolves these to the opaque <c>PublicId</c> + gated URL for the list card (no
    /// per-row query) — same pattern as <see cref="StudentVideoListRow.VideoPhotoFileId"/>.
    /// </summary>
    public long? VideoPhotoFileId { get; set; }

    /// <summary>Number of questions in the video's exam (0 when the video has no exam).</summary>
    public int QuestionsNumber { get; set; }

    /// <summary>Number of live (Attached) attachments on the video.</summary>
    public int AttachmentsNumber { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Projection row for a teacher's unit list (Track C / G-UNIT, S2). Rolls up
/// child video counts and distinct seen/unseen students across the unit's
/// videos in one grouped query — same rationale as
/// <see cref="TeacherVideoListRow"/>'s per-video aggregates.
/// </summary>
public sealed class TeacherVideoUnitListRow
{
    public long Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public int VideoCount { get; set; }

    /// <summary>
    /// Member videos that are LIVE to students right now — Published with no
    /// PublishDate, or a PublishDate already reached. Same predicate the
    /// student unit query uses, so the teacher's badge can never claim a unit
    /// is live when students see nothing in it.
    /// </summary>
    public int PublishedVideoCount { get; set; }

    /// <summary>
    /// Member videos that are Published but still waiting on a future
    /// PublishDate. A unit with only these reads "Scheduled" on the card.
    /// </summary>
    public int ScheduledVideoCount { get; set; }

    /// <summary>
    /// DISTINCT students who have opened at least ONE member video. A student
    /// who opened four videos in the unit counts once.
    /// </summary>
    public int SeenStudentCount { get; set; }

    /// <summary>
    /// Distinct students the unit's member videos target who have opened none
    /// of them. Always the resolved audience minus <see cref="SeenStudentCount"/>,
    /// never a scope-row count.
    /// </summary>
    public int UnseenStudentCount { get; set; }

    /// <summary>
    /// Live attachments across the unit's member videos (Attached FileObjects
    /// of category VideoAttachment).
    /// </summary>
    public int AttachmentCount { get; set; }

    /// <summary>Member videos that carry a quiz (a VideoExam exists for them).</summary>
    public int QuizCount { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Projection row for the student's accessible video list. Includes the
/// per-(video, student) state from <c>VideoAnalytics</c> via LEFT JOIN —
/// drives the "REWATCH" / "NEW" / "OPENED" badges on Flutter's video cards.
/// </summary>
public sealed class StudentVideoListRow
{
    public long Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public Enums.VideoSourceType SourceType { get; set; }
    public string SourceUrl { get; set; } = null!;
    public int DurationSeconds { get; set; }
    public DateTime AssignedAt { get; set; }
    public bool HasOpened { get; set; }
    public DateTime? LastOpenedAt { get; set; }

    /// <summary>
    /// This student's accumulated watch seconds for the video (0 when never opened) — from the
    /// per-(video, student) <c>VideoAnalytics</c> LEFT JOIN. Drives the 3-state
    /// <c>WatchStatus</c> (V4) the service computes against <see cref="DurationSeconds"/>.
    /// </summary>
    public long TotalWatchSeconds { get; set; }

    /// <summary>True when the video has a quiz (<c>VideoExam</c>) attached (V2).</summary>
    public bool HasQuiz { get; set; }

    /// <summary>Number of questions in the video's quiz, 0 when it has none (V2).</summary>
    public int QuestionsCount { get; set; }

    /// <summary>
    /// Registry file id (<c>FileObject.Id</c>) of the video's cover photo, or null. The service
    /// batch-resolves these to gated URLs for the student card (no per-row query).
    /// </summary>
    public long? VideoPhotoFileId { get; set; }
}

/// <summary>
/// Projection row for the student's units view (V3). Rolls up, per unit, how many of the
/// student's visible videos it contains and how many of those carry a quiz. Computed from
/// the SAME "visible video" predicate the student video list uses, so the units view and
/// the video list never disagree.
/// </summary>
public sealed class StudentVideoUnitRow
{
    public long Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>Count of the student's visible videos linked to this unit.</summary>
    public int VideoCount { get; set; }

    /// <summary>Of <see cref="VideoCount"/>, how many carry a quiz (<c>VideoExam</c>).</summary>
    public int QuizVideoCount { get; set; }

    /// <summary>
    /// Of <see cref="VideoCount"/>, how many THIS student has watched to the
    /// completion threshold (<c>VideoWatchRules.IsWatched</c>). Drives the unit
    /// progress bar — the unit list previously reported only how much content
    /// existed, never how much of it the student had got through.
    /// </summary>
    public int WatchedVideoCount { get; set; }
}

/// <summary>
/// A teacher's subject, for the student video-list <c>Subject</c> column (replicates the
/// canonical <c>StudentUserService</c> pattern: <c>CustomSubject ?? Subject.NameEn/NameAr</c>
/// by the reader's language). Resolved once per list (all rows share one teacher), never per row.
/// </summary>
public sealed class TeacherSubjectInfo
{
    public string? CustomSubject { get; set; }
    public string? SubjectNameEn { get; set; }
    public string? SubjectNameAr { get; set; }
}

/// <summary>
/// Student-facing take-screen projection for a video quiz (<c>VideoExam</c>) — <c>IsCorrect</c>
/// does not exist on this type at all. Security by shape, mirroring
/// <c>StudentOnlineExamQuestionRow</c> (do-not-reintroduce #9): the correct-answer key can
/// never leak onto the take screen regardless of DTO-mapper discipline.
/// </summary>
public sealed class StudentVideoExamQuestionRow
{
    public long Id { get; set; }
    public string QuestionText { get; set; } = null!;
    public Enums.VideoExamQuestionType QuestionType { get; set; }

    /// <summary>Points for this question — always 1 (video questions are equally weighted).</summary>
    public decimal Degree { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Internal registry FK (<c>FileObject.Id</c>), repo-projected. Never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long? ImageFileInternalId { get; set; }

    /// <summary>Gated image URL, populated by the service (not the repo). Students only need the URL.</summary>
    public string? ImageUrl { get; set; }

    public List<StudentVideoExamQuestionOptionRow> Options { get; set; } = new();
}

public sealed class StudentVideoExamQuestionOptionRow
{
    public long Id { get; set; }
    public string OptionText { get; set; } = null!;
    public int SortOrder { get; set; }
}

/// <summary>
/// Lightweight header of a video's quiz (<c>VideoExam</c>) — id/title/description + existence,
/// without loading the question graph (or the answer key). Backs the student take-screen header.
/// </summary>
public sealed class VideoExamHeaderRow
{
    public long ExamId { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
}

/// <summary>
/// Snapshot returned from the Open UPSERT — used by the service layer to
/// build the Start response without an extra SELECT.
/// </summary>
public sealed class VideoAnalyticsSnapshot
{
    public int OpenCount { get; set; }
    public long TotalWatchSeconds { get; set; }
    public int LastResumePositionSeconds { get; set; }
}

/// <summary>
/// Snapshot returned from the atomic Stop increment — what the service layer
/// returns to Flutter so the local UI can update optimistically.
/// </summary>
public sealed class VideoAnalyticsIncrementResult
{
    public long TotalWatchSeconds { get; set; }
    public int LastResumePositionSeconds { get; set; }
}

/// <summary>
/// One row of the teacher analytics report. Mirrors the spec's Story F JSON
/// shape, computed in SQL via projection (no entity materialization).
/// </summary>
public sealed class VideoAnalyticsReportRow
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;

    /// <summary>
    /// The student's current session name (Track D1 §5 open question), read
    /// from their active <c>StudentSessionAssignment</c>. Null when the
    /// student has no active session assignment.
    /// </summary>
    public string? SessionName { get; set; }

    public bool HasOpened { get; set; }
    public int OpenCount { get; set; }
    public long TotalWatchSeconds { get; set; }
    public int VideoDurationSeconds { get; set; }
    public DateTime? FirstOpenedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }

    /// <summary>
    /// Capped at 100 server-side. <c>NULL</c> when video duration is 0
    /// (unknown — no student has opened it yet).
    /// </summary>
    public int? EstimatedCompletionPct { get; set; }

    /// <summary>
    /// Uncapped; values &gt; 100 indicate rewatching. <c>NULL</c> when video
    /// duration is 0.
    /// </summary>
    public int? RawWatchPct { get; set; }
}

/// <summary>
/// Top-of-report aggregates that don't change with pagination.
/// </summary>
public sealed class VideoAnalyticsAggregates
{
    public int TotalStudentsInScope { get; set; }
    public int TotalStudentsWatched { get; set; }

    /// <summary>= <see cref="TotalStudentsInScope"/> - <see cref="TotalStudentsWatched"/> (G-ANL-2).</summary>
    public int UnseenCount { get; set; }

    /// <summary>
    /// Students whose <c>EstimatedCompletionPct</c> meets
    /// <c>VideoConstants.CompletionThresholdPercent</c> (G-ANL-1).
    /// </summary>
    public int CompletedCount { get; set; }
}

/// <summary>
/// One row of the analytics audit snapshot — feeds the JSON serializer in the
/// delete flow. <see cref="StudentName"/> and <see cref="StudentCode"/> are
/// denormalized at snapshot time so the audit row remains readable even if
/// the student is later permanently purged.
/// </summary>
public sealed class VideoAnalyticsAuditRow
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public int OpenCount { get; set; }
    public long TotalWatchSeconds { get; set; }
    public DateTime FirstOpenedAt { get; set; }
    public DateTime LastUpdated { get; set; }
    public int LastResumePositionSeconds { get; set; }
}
