using Edvanz.Application.Dtos.Exams;
using Edvanz.Domain.Enums;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Edvanz.Application.Dtos.VideoContentManagement;

// ════════════════════════════════════════════════════════════════════════════
// VIDEO CONTENT MANAGEMENT MODULE 14 — DATA TRANSFER OBJECTS
// ════════════════════════════════════════════════════════════════════════════
//
// All request and response DTOs for the VCM module. These are the contract
// between the Presentation layer (controller) and the Application layer
// (service). They never leak Domain entities and are never used inside the
// Domain or Infrastructure layers.
//
// CONVENTIONS observed from the existing codebaseTeacherVideoListItemD
//   - DTO classes use PascalCase property names (.NET default).
//   - PaginatedResponse fields use camelCase (existing convention — see
//     PaginatedResponse.cs).
//   - Required-on-the-wire fields use [Required] for ASP.NET model binding
//     to surface 400s with field names automatically.
//   - Enum-valued query parameters use JsonStringEnumConverter so Swagger
//     and clients see "YouTube" not "1".
// ════════════════════════════════════════════════════════════════════════════

// ══════════════════════════════════════════════════════════════════════════
// CREATE VIDEO (Story A)
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request body for <c>POST /api/videos</c>. Story A step 3.
///
/// Trust boundary: <c>SourceUrl</c> is parsed but NOT validated against the
/// upstream provider. Provider validation is documented as a known limitation
/// in the spec — Flutter's player surfaces "video not found" on first play.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] // typo'd/stale field → 400, never silently ignored (§7.2b precedent; caught live: 'attachmentFileId' singular was dropped and the video created without its attachment)
public sealed class CreateVideoRequest
{
    /// <summary>
    /// Display title shown on the student's video card and the teacher's
    /// list. Trimmed in the service layer; max length validated against
    /// <c>VideoConstants.TitleMaxLength</c>.
    /// </summary>
    [Required]
    public string Title { get; set; } = null!;

    /// <summary>
    /// Optional teacher notes. Max length validated against
    /// <c>VideoConstants.DescriptionMaxLength</c>.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The full URL pasted by the teacher. Must parse to a YouTube or Google
    /// Drive URL — see <see cref="ServiceContract.IVideoUrlParser"/> for the
    /// recognised formats. Service returns <c>InvalidUrl</c> or
    /// <c>UnsupportedSource</c> on parse failure.
    /// </summary>
    [Required]
    public string SourceUrl { get; set; } = null!;

    /// <summary>
    /// Optional scope assignment, folded into creation (merged-creation
    /// refactor — replaces the removed <c>POST /videos/{id}/scopes</c>
    /// endpoint). Null or empty = video is created scope-less; scope it
    /// later via <c>PUT /videos/{id}/scopes</c>. Multiple entries allowed
    /// (e.g. one Sessions entry + one Groups entry in the same request).
    /// </summary>
    public List<CreateVideoScopeDto>? Scopes { get; set; }

    /// <summary>
    /// Optional exam, created atomically with the video. Null = no exam.
    /// </summary>
    public CreateExamDto? Exam { get; set; }

    /// <summary>
    /// Optional initial status. Omitted/null = <see cref="VideoStatus.Published"/> (the entity
    /// default — unchanged behavior). <c>Draft</c> keeps the video (and its files) invisible to
    /// students until published.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoStatus? Status { get; set; }

    /// <summary>
    /// Optional scheduled-publish timestamp. A future date keeps the video hidden from students
    /// until reached, even when <see cref="Status"/> is Published. Null = publish immediately.
    /// </summary>
    public DateTime? PublishDate { get; set; }

    /// <summary>
    /// Optional explicit duration override. When set, <c>DurationSeconds</c> is stored and
    /// <c>IsDurationManuallySet</c> = true (student reports won't overwrite it — same semantics
    /// as the update endpoint). Null = 0, learned from the first student open.
    /// </summary>
    public int? DurationSeconds { get; set; }

    /// <summary>
    /// The unit(s) this video belongs to (M:N). REQUIRED and non-empty — a video must
    /// live inside at least one unit, and its scope must stay within those units' scope
    /// (containment). Units must belong to the calling teacher.
    /// </summary>
    [Required]
    [MinLength(1)]
    public List<long> UnitIds { get; set; } = new();

    /// <summary>
    /// Optional video photo (cover image) — the opaque <c>fileId</c> (FileObject.PublicId)
    /// previously returned by <c>POST /api/upload</c> (category <c>VideoPhoto</c>).
    /// Null = no video photo.
    /// </summary>
    public Guid? VideoPhotoFileId { get; set; }

    /// <summary>
    /// Optional attachments — <c>fileId</c>s from <c>POST /api/upload</c> (category
    /// <c>VideoAttachment</c>). Null or empty = no attachments. A video holds at most
    /// <c>VideoConstants.MaxAttachmentsPerVideo</c> attachments; duplicates are ignored.
    /// </summary>
    public List<Guid>? AttachmentFileIds { get; set; }
}

/// <summary>
/// Response body for <c>POST /api/videos</c>. The newly-created video has no
/// scope rows yet — Flutter chains a <c>POST /scopes</c> call next. Returning
/// only the id keeps the contract narrow.
/// </summary>
public sealed class CreateVideoResponse
{
    public long VideoAssetId { get; set; }

    /// <summary>The attached video photo's fileId (echoed for symmetry with the read endpoints), or null.</summary>
    public Guid? VideoPhotoFileId { get; set; }

    /// <summary>Stable gated read URL for the uploaded video photo, or null if none was provided.</summary>
    public string? VideoPhotoReadUrl { get; set; }

    /// <summary>The attached files' DTOs (each <c>Id</c> is its fileId). Empty when none were provided.</summary>
    public List<VideoAttachmentDto> Attachments { get; set; } = new();

    /// <summary>Count of scope rows created. 0 if the video was created scope-less.</summary>
    public int ScopesAdded { get; set; }

    /// <summary>Resolved student count across all scopes created, same computation ReplaceScopesAsync uses.</summary>
    public int StudentsInScope { get; set; }

    /// <summary>Id of the created exam, or null if none was provided.</summary>
    public long? ExamId { get; set; }
}
/// <summary>
/// Request body for <c>PUT /api/videos/{id}</c>.
///
/// Confirmed rule: if <see cref="SourceUrl"/> differs from the stored value,
/// the service treats it as a different video — <c>VideoAnalytics</c> and
/// <c>VideoWatchEvent</c> rows for this asset are cleared and
/// <c>DurationSeconds</c> resets to 0 to be re-learned. Every other
/// field-only edit preserves analytics.
///
/// Scope replacement is optional here (Phase 4): if <see cref="Scopes"/> is
/// provided, it replaces all scope rows via the same logic backing
/// <c>PUT /videos/{id}/scopes</c>; if omitted, existing scopes are left
/// untouched. <c>PUT /videos/{id}/scopes</c> remains available as a
/// standalone endpoint for scope-only updates (e.g. a "Manage Access" screen
/// that doesn't want to resend the whole video form) — both routes share one
/// code path.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] // typo'd/stale field → 400, never silently ignored
public sealed class UpdateVideoRequest
{
    [Required]
    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    [Required]
    public string SourceUrl { get; set; } = null!;

    /// <summary>
    /// Scheduled-publish instant. <c>null</c> or omitted = <b>leave the stored
    /// schedule untouched</b> — an edit that does not mention the date must never
    /// silently drop it (renaming a video used to push it live early). To remove a
    /// schedule, send <see cref="ClearPublishDate"/>; the quick toggle
    /// <c>PATCH /api/videos/{id}/status</c> keeps its own "null = publish now" rule.
    /// </summary>
    public DateTime? PublishDate { get; set; }

    /// <summary>
    /// <c>true</c> = remove any scheduled-publish date, making the video visible as
    /// soon as it is Published. Defaults to false, so callers that never send it
    /// (every shipped client) keep the stored date. Wins over
    /// <see cref="PublishDate"/> if both are sent.
    /// </summary>
    public bool ClearPublishDate { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoStatus? Status { get; set; }

    /// <summary>
    /// Replaces the video's unit links (M:N). <c>null</c> = leave unit links
    /// unchanged. If provided it must be non-empty (min 1) — a video must stay in
    /// at least one unit, so "unlink from all" ([]) is rejected (400). The new
    /// units must still cover the video's scope, else 409/422.
    /// </summary>
    [MinLength(1)]
    public List<long>? UnitIds { get; set; }

    /// <summary>
    /// Optional explicit duration override. When set, <c>DurationSeconds</c>
    /// is updated directly and <c>IsDurationManuallySet</c> is set true.
    /// Null = leave duration as-is (student-reported value, or whatever was
    /// previously set).
    /// </summary>
    public int? DurationSeconds { get; set; }

    /// <summary>
    /// Optional scope replacement, folded into this endpoint. Null = leave
    /// existing scopes untouched. Non-null (including empty list) delegates
    /// to the same <c>ReplaceScopesInternalAsync</c> path as
    /// <c>PUT /videos/{id}/scopes</c> — an empty list correctly fails with
    /// <c>ScopeCannotBeEmpty</c> via that shared logic.
    /// </summary>
    public List<VideoScopeInputDto>? Scopes { get; set; }

    /// <summary>
    /// Optimistic concurrency token, echoed back from the last GET/PUT
    /// response. Required to prevent silent lost updates between concurrent
    /// editors.
    /// </summary>
    [Required]
    public byte[] RowVersion { get; set; } = null!;
     /// <summary>
    /// Optional exam replace-all. Null = leave the existing exam (if any)
    /// untouched. Non-null = delete the old exam tree and insert this one —
    /// same replace-all semantics as <see cref="Scopes"/>. If the video has
    /// no exam yet, this creates one.
    /// </summary>
    public CreateExamDto? Exam { get; set; }

    /// <summary>
    /// Optional video photo replacement — the <c>fileId</c> (FileObject.PublicId) from
    /// <c>POST /api/upload</c>. Null = leave the current video photo unchanged. Resending the
    /// current video photo's id is an idempotent no-op.
    /// </summary>
    public Guid? VideoPhotoFileId { get; set; }

    /// <summary>
    /// Attachments replace-all — same null/empty/list semantics as <see cref="Scopes"/> and
    /// <see cref="UnitIds"/>: null = leave the current attachments unchanged; empty list =
    /// remove all; a list of <c>fileId</c>s = the exact new set (ids already on the video are
    /// kept, missing ones are removed, new ones are attached). Wins over
    /// <see cref="RemoveAttachment"/>.
    /// </summary>
    public List<Guid>? AttachmentFileIds { get; set; }

    /// <summary>
    /// True to remove ALL the video's attachments without providing replacements — equivalent
    /// to sending an empty <see cref="AttachmentFileIds"/> list. Ignored if
    /// <see cref="AttachmentFileIds"/> is non-null.
    /// </summary>
    public bool RemoveAttachment { get; set; }
}

/// <summary>
/// Fields common to both the edit pre-fill response (<see cref="VideoDetailDto"/>)
/// and the overview/analytics-summary response (<see cref="VideoOverviewDto"/>).
/// Built once by <c>VideoService.MapVideoBaseAsync</c> — each endpoint layers
/// its own additional data on top, so nothing here is mapped twice.
/// </summary>
public abstract class VideoBaseDto
{
    public long Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoSourceType SourceType { get; set; }

    public string SourceUrl { get; set; } = null!;
    public int DurationSeconds { get; set; }
    public DateTime? PublishDate { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoStatus Status { get; set; }

    /// <summary>Concurrency token to echo back on the next PUT.</summary>
    public byte[] RowVersion { get; set; } = null!;

    /// <summary>
    /// The current video photo's <c>fileId</c> (FileObject.PublicId), or null if none. This is
    /// the id the edit screen resends (create/update <c>videoPhotoFileId</c>, or
    /// <c>PUT /videos/{id}/video-photo</c>) to keep or re-point the photo.
    /// </summary>
    public Guid? VideoPhotoFileId { get; set; }

    /// <summary>Stable gated URL of the current video photo, or null.</summary>
    public string? VideoPhotoUrl { get; set; }

    /// <summary>Current attachments (each <c>Id</c> is its fileId). Empty when the video has none.</summary>
    public List<VideoAttachmentDto> Attachments { get; set; } = new();

    /// <summary>Number of questions in the video's exam (0 when the video has no exam).</summary>
    public int QuestionsNumber { get; set; }

    /// <summary>Number of attachments on the video (= <see cref="Attachments"/>.Count).</summary>
    public int AttachmentsNumber { get; set; }
}

/// <summary>
/// Response for <c>PUT /api/videos/{id}</c> and <c>GET /api/videos/{id}</c>.
/// Pre-fill payload for the Edit screen — includes everything needed to
/// reconstruct the create/update form: base fields, current Exam (replace-all
/// shape, same DTO used to submit an edit), and current Scopes (grouped by
/// type, same shape used to submit an edit). Deliberately does NOT include
/// analytics summary counts — see <see cref="VideoOverviewDto"/> for that.
/// </summary>
public sealed class VideoDetailDto : VideoBaseDto
{
    /// <summary>
    /// Non-null only when the video's other fields updated successfully but a
    /// requested attachment change failed (blob I/O can't share the SQL
    /// transaction the rest of the update runs in). Status stays 200 — the
    /// video data in this response IS current; only the file change failed.
    /// Always null on a plain GET.
    /// </summary>
    public string? Warning { get; set; }

    /// <summary>Current exam, or null if the video has none. Same shape used to
    /// create/replace one — resend this verbatim (edited) to update it.</summary>
    public CreateExamDto? Exam { get; set; }

    /// <summary>Current scopes, grouped by type. Same shape used to
    /// create/replace scopes — resend this verbatim (edited) to update them.</summary>
    public List<CreateVideoScopeDto>? Scopes { get; set; }

    /// <summary>The ids of the unit(s) this video belongs to (M:N). Resend as
    /// <c>unitIds</c> on the update to change unit membership.</summary>
    public List<long> UnitIds { get; set; } = new();

    /// <summary>
    /// The sessions/groups this video MAY be scoped to — the union of its units'
    /// scope (groups expanded to sessions, with display names). Populates the
    /// scope picker directly, so the Edit screen needs no separate call.
    /// </summary>
    public AllowedScopeTargetsDto AllowedScopeTargets { get; set; } = new();
}

/// <summary>
/// Response for the overview/details endpoint. Same base fields as
/// <see cref="VideoDetailDto"/>, but no Exam/Scopes (not needed for a
/// read-only overview page) — instead carries the analytics summary counts.
/// Backed entirely by the existing <c>GetAnalyticsAggregatesAsync</c> — no
/// new aggregate query was needed.
/// </summary>
public sealed class VideoOverviewDto : VideoBaseDto
{
    /// <summary>Distinct students who have opened the video at least once.</summary>
    public int SeenStudentCount { get; set; }

    /// <summary>Students in scope who have never opened the video.</summary>
    public int UnseenStudentCount { get; set; }

    /// <summary>Students whose completion meets <c>VideoConstants.CompletionThresholdPercent</c>.</summary>
    public int CompletedStudentCount { get; set; }

    /// <summary>
    /// Who this video is published to — its scope rows with resolved session /
    /// group names and student counts. Reuses the unit screen's scope shape.
    ///
    /// "Who can see this?" is the question a teacher asks most often about a
    /// published video, and answering it used to mean opening the Edit form on
    /// live content. Empty when the video is scope-less. Additive.
    /// </summary>
    public List<VideoUnitScopeDto> Audience { get; set; } = new();

    /// <summary>
    /// Distinct active students reachable across <see cref="Audience"/> — a session
    /// also covered by a scoped group is counted once, not twice.
    /// </summary>
    public int AudienceStudentCount { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════
// VIDEO UNIT (Track C / G-UNIT) — api/video-units
// ══════════════════════════════════════════════════════════════════════════

/// <summary>Request body for <c>POST /api/video-units</c> and <c>PUT /api/video-units/{id}</c>.</summary>
public sealed class VideoUnitRequest
{
    [Required]
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    /// <summary>Optional unit‑level scopes to be created together with the unit.</summary>
    public List<VideoScopeInputDto>? Scopes { get; set; }
    /// <summary>On update: when a scope is REMOVED that member videos still use,
    /// true = also remove that scope from those videos (cascade); false = reject
    /// with the specific "still used by video(s): {names}" conflict.</summary>
    public bool CascadeToVideos { get; set; }
}

/// <summary>Response body for <c>POST /api/video-units</c>.</summary>
public sealed class CreateVideoUnitResponse
{
    public long VideoUnitId { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════
// VIDEO UNIT SCOPE — collection-level Target Scope (final decision)
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request body for both <c>POST /api/video-units/{id}/scopes</c> (append)
/// and <c>PUT /api/video-units/{id}/scopes</c> (replace-all). Reuses
/// <see cref="VideoScopeInputDto"/> — the row shape is identical to a
/// per-video scope input, so a separate DTO would only duplicate it.
/// </summary>
public sealed class AssignUnitScopesRequest
{
    [Required]
    public List<VideoScopeInputDto> Scopes { get; set; } = new();
    /// <summary>Replace-only: when the new set REMOVES a scope member videos still
    /// use, true = cascade the removal to those videos; false = reject (409).</summary>
    public bool CascadeToVideos { get; set; }
}

/// <summary>
/// Response body for <c>POST /api/video-units/{id}/scopes</c>. Mirrors
/// <see cref="AppendScopesResponse"/>.
/// </summary>
public sealed class AppendUnitScopesResponse
{
    public int ScopesAdded { get; set; }
    public int ScopesSkipped { get; set; }
    public int StudentsInScope { get; set; }
}

/// <summary>
/// Response body for <c>PUT /api/video-units/{id}/scopes</c>. Mirrors
/// <see cref="ReplaceScopesResponse"/>.
/// </summary>
public sealed class ReplaceUnitScopesResponse
{
    public int ScopesAdded { get; set; }
    public int ScopesRemoved { get; set; }
    public int StudentsInScope { get; set; }
}

/// <summary>Request DTO for <c>GET /api/video-units</c>.</summary>
public sealed class TeacherVideoUnitListRequest
{
    private int _page = 1;
    private int _pageSize = 20;

    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < 1 ? 20 : value > 100 ? 100 : value;
    }

    public string? Search { get; set; }
}

/// <summary>
/// One row of the teacher's unit list (S2) — rolled-up child aggregates
/// across the unit's videos.
/// </summary>
public sealed class TeacherVideoUnitListItemDto
{
    public long Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public int VideoCount { get; set; }

    /// <summary>
    /// Of <see cref="VideoCount"/>, how many are LIVE to students right now
    /// (Published, and any PublishDate already reached). A unit carries no
    /// publish state of its own — the client derives the card badge from this:
    /// &gt; 0 = "Published".
    /// </summary>
    public int PublishedVideoCount { get; set; }

    /// <summary>
    /// Of <see cref="VideoCount"/>, how many are Published but still waiting on
    /// a future PublishDate. With <see cref="PublishedVideoCount"/> at 0 and
    /// this above 0 the card reads "Scheduled"; both at 0 reads "Draft".
    /// </summary>
    public int ScheduledVideoCount { get; set; }

    /// <summary>
    /// DISTINCT students who have opened at least one video in the unit — a
    /// student who opened four of them counts once.
    /// </summary>
    public int SeenStudentCount { get; set; }

    /// <summary>
    /// Distinct students the unit's videos target who have opened none of them
    /// (resolved audience minus <see cref="SeenStudentCount"/>).
    /// </summary>
    public int UnseenStudentCount { get; set; }

    /// <summary>
    /// Live attachments across the unit's videos — drives the 📎 chip on the
    /// unit card, which the client used to hard-code to 0.
    /// </summary>
    public int AttachmentCount { get; set; }

    /// <summary>
    /// Videos in the unit that carry a quiz — drives the "Quiz" chip on the
    /// unit card, which the client used to hard-code to 0.
    /// </summary>
    public int QuizCount { get; set; }

    public DateTime CreatedAt { get; set; }
    /// <summary>Display names of the unit's audience (session + group names) — shown on the card.</summary>
    public List<string> RecipientLabels { get; set; } = new();
}

// ══════════════════════════════════════════════════════════════════════════
// ATTACHMENTS (Track F / §5) — Azure Blob Storage
// ══════════════════════════════════════════════════════════════════════════

public sealed class VideoAttachmentDto
{
    /// <summary>The attachment's registry id (FileObject.PublicId).</summary>
    public Guid Id { get; set; }
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long FileSizeBytes { get; set; }

    /// <summary>Stable gated URL (<c>/api/files/{id}</c>) — re-checks access per fetch.</summary>
    public string ReadUrl { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Request body for <c>PUT /api/videos/{id}/video-photo</c> — the uploaded file's id.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ReplaceVideoPhotoRequest
{
    /// <summary>The video photo's <c>fileId</c> (FileObject.PublicId) from <c>POST /api/upload</c>.</summary>
    [Required]
    public Guid VideoPhotoFileId { get; set; }
}

/// <summary>Response for <c>PUT /api/videos/{id}/video-photo</c> — the photo's fileId + gated URL.</summary>
public sealed class VideoPhotoDto
{
    /// <summary>The current photo's fileId (FileObject.PublicId) — resend it to keep the photo.</summary>
    public Guid VideoPhotoFileId { get; set; }

    public string ReadUrl { get; set; } = null!;
}

// ══════════════════════════════════════════════════════════════════════════
// SCOPE INPUT — shared by POST /scopes and PUT /scopes
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// One scope row to add or replace. Exactly one of
/// <see cref="TeacherStudentId"/>, <see cref="SessionId"/>, or
/// <see cref="SessionGroupId"/> must be populated, matching
/// <see cref="ScopeType"/>. Service returns <c>ScopeShapeInvalid</c> when
/// the shape doesn't match.
/// </summary>
public sealed class VideoScopeInputDto
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoScopeType ScopeType { get; set; }

    public long? SessionId { get; set; }
    public long? SessionGroupId { get; set; }

}

/// <summary>
/// Request body for both <c>POST /api/videos/{id}/scopes</c> (append) and
/// <c>PUT /api/videos/{id}/scopes</c> (replace-all).
/// </summary>
public sealed class AssignScopesRequest
{
    [Required]
    public List<VideoScopeInputDto> Scopes { get; set; } = new();
}

/// <summary>
/// Response body for <c>POST /api/videos/{id}/scopes</c>. The append flow is
/// idempotent on duplicates — <c>scopesSkipped</c> reports how many input
/// rows were rejected by the unique index because they already existed.
/// </summary>
public sealed class AppendScopesResponse
{
    public int ScopesAdded { get; set; }
    public int ScopesSkipped { get; set; }
    public int StudentsInScope { get; set; }
}

/// <summary>
/// Response body for <c>PUT /api/videos/{id}/scopes</c>. <c>scopesAdded</c>
/// and <c>scopesRemoved</c> are computed by the service: it deletes all old
/// scopes and inserts the new set in one transaction (Story D).
/// </summary>
public sealed class ReplaceScopesResponse
{
    public int ScopesAdded { get; set; }
    public int ScopesRemoved { get; set; }
    public int StudentsInScope { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════
// VISIBILITY (Track D1 — Draft/Published + scheduled publish)
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request body for <c>PATCH /api/videos/{id}/status</c> — the Settings-row
/// quick toggle, distinct from the full edit form (which also accepts these
/// two fields).
/// </summary>
public sealed class SetVideoStatusRequest
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoStatus Status { get; set; }

    /// <summary>
    /// Null = publish immediately once <see cref="Status"/> is Published. A
    /// future date schedules the video — hidden from students until reached.
    /// </summary>
    public DateTime? PublishDate { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════
// TEACHER VIDEO LIST (Story B teacher endpoint, Story F summary card)
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request DTO for <c>GET /api/videos/teacher</c>. Inherits the shared
/// pagination shape but provides only Search filtering — the spec does not
/// define sort options for the teacher list (newest-first is the default).
/// </summary>
public sealed class TeacherVideoListRequest
{
    private int _page = 1;
    private int _pageSize = 20;

    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < 1 ? 20 : value > 100 ? 100 : value;
    }

    public string? Search { get; set; }

    /// <summary>
    /// Optional publish-state filter for the list. Null (the default) returns
    /// Draft and Published alike, exactly as before. Serialized as a string.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoStatus? Status { get; set; }
}

/// <summary>
/// One row of the teacher's own video list. Mirrors the contract table for
/// endpoint #6 in the spec.
/// </summary>
public sealed class TeacherVideoListItemDto
{
    public long Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string SourceUrl { get; set; } = null!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoSourceType SourceType { get; set; }

    public int DurationSeconds { get; set; }
    public int StudentsInScope { get; set; }
    public int TotalOpens { get; set; }

    /// <summary>Distinct students who have opened the video at least once (G-ANL-3).</summary>
    public int SeenStudentCount { get; set; }

    /// <summary>= <see cref="StudentsInScope"/> - <see cref="SeenStudentCount"/> (G-ANL-3).</summary>
    public int UnseenStudentCount { get; set; }

    /// <summary>
    /// Publish state — <c>Draft</c> or <c>Published</c>. Combined with
    /// <see cref="PublishDate"/> the client can show Draft / Published / Scheduled on the card.
    /// Serialized as a string (global <c>JsonStringEnumConverter</c>).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoStatus Status { get; set; }

    /// <summary>
    /// Scheduled-publish timestamp, or null. When <see cref="Status"/> is Published and this is a
    /// future date the video is "Scheduled" (hidden from students until reached).
    /// </summary>
    public DateTime? PublishDate { get; set; }

    /// <summary>
    /// The current video photo's <c>fileId</c> (FileObject.PublicId), or null if none. Mirrors
    /// the edit-screen field on <see cref="VideoDetailDto"/> so the list card can render the cover.
    /// </summary>
    public Guid? VideoPhotoFileId { get; set; }

    /// <summary>Stable gated URL of the current video photo (<c>/api/files/{fileId}</c>), or null.</summary>
    public string? VideoPhotoUrl { get; set; }

    /// <summary>Number of questions in the video's exam (0 when the video has no exam).</summary>
    public int QuestionsNumber { get; set; }

    /// <summary>Number of attachments on the video.</summary>
    public int AttachmentsNumber { get; set; }

    public DateTime CreatedAt { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════
// STUDENT VIDEO LIST (Story B student endpoint)
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request DTO for <c>GET /api/videos/student</c>. Pagination only —
/// students don't search or sort their list in v1.
/// </summary>
public sealed class StudentVideoListRequest
{
    private int _page = 1;
    private int _pageSize = 20;

    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < 1 ? 20 : value > 100 ? 100 : value;
    }

    /// <summary>Optional title search. Omitted/blank behaves exactly as before.</summary>
    public string? Search { get; set; }

    /// <summary>
    /// LEGACY (kept for the shipped 4.0.0+17 app): when true, returns only videos the student
    /// has not started. Exactly equivalent to <c>watchStatus=NotStarted</c>. Defaults to false,
    /// so an older app build sees the unchanged full list. <see cref="WatchStatus"/> wins when
    /// both are sent; do not remove this until the old builds are gone.
    /// </summary>
    public bool UnwatchedOnly { get; set; }

    /// <summary>
    /// Tri-state watch filter: <c>NotStarted</c> (never opened) / <c>InProgress</c> (started but
    /// not finished) / <c>Completed</c> (watched through). Omitted = every visible video. Bound
    /// from the query string by name or number, and judged by the same
    /// <c>VideoWatchRules</c> the row's own <c>watchStatus</c> badge uses.
    /// </summary>
    public VideoWatchStatus? WatchStatus { get; set; }

    /// <summary>
    /// The filter actually applied: <see cref="WatchStatus"/> when the caller sent one,
    /// otherwise the legacy <see cref="UnwatchedOnly"/> flag mapped to
    /// <c>VideoWatchStatus.NotStarted</c>, otherwise no filter. One place decides, so the two
    /// parameters can never be resolved differently on two endpoints. A METHOD, not a
    /// property, so model binding and Swagger never mistake it for a third query parameter.
    /// </summary>
    public VideoWatchStatus? ResolveWatchStatus()
        => WatchStatus ?? (UnwatchedOnly ? VideoWatchStatus.NotStarted : null);
}

/// <summary>
/// One row of the student's accessible video list. Includes the student's
/// own watch state via the analytics LEFT JOIN — drives the "REWATCH" /
/// "NEW" / "OPENED" badges on Flutter's video cards.
/// </summary>
public sealed class StudentVideoListItemDto
{
    public long Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoSourceType SourceType { get; set; }

    /// <summary>
    /// The original watch URL. Flutter uses this for share/copy actions.
    /// The actual player loads the embed URL from
    /// <c>POST /start</c>'s response — never this.
    /// </summary>
    public string SourceUrl { get; set; } = null!;

    public int DurationSeconds { get; set; }
    public DateTime AssignedAt { get; set; }
    public bool HasOpened { get; set; }
    public DateTime? LastOpenedAt { get; set; }

    /// <summary>V2 — true when the video has a quiz (<c>VideoExam</c>) attached.</summary>
    public bool HasQuiz { get; set; }

    /// <summary>V2 — number of questions in the video's quiz, 0 when it has none.</summary>
    public int QuestionsCount { get; set; }

    /// <summary>
    /// V4 — 3-state watch indicator (NotStarted / InProgress / Completed) computed from the
    /// student's watch seconds vs. the video duration (Completed ≥
    /// <c>VideoConstants.CompletionThresholdPercent</c>). Serialized as a string.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoWatchStatus WatchStatus { get; set; }

    /// <summary>
    /// The owning teacher's subject (<c>CustomSubject</c>, else the ministry subject name in the
    /// student's language) — same resolution as the student dashboard. Empty if none set.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Stable gated URL of the video's cover photo (<c>/api/files/{fileId}</c>), or null if the
    /// video has none. Load with the student's JWT; access is re-checked per fetch (scoped
    /// students only).
    /// </summary>
    public string? VideoPhotoUrl { get; set; }

    /// <summary>
    /// The video's attachments (e.g. PDF handouts). Empty when none. Each <c>ReadUrl</c> is the
    /// stable gated URL — same access rules as the video photo.
    /// </summary>
    public List<VideoAttachmentDto> Attachments { get; set; } = new();
}

/// <summary>
/// V3 — one unit the student can see under a teacher, with per-unit counts. A unit is visible
/// when it contains at least one video visible to the student (same predicate as the student
/// video list, so the two never disagree).
/// </summary>
/// <summary>
/// Scalar rollup behind the student home's Videos tile: how many videos this
/// student can see, and how many of those they have never opened.
///
/// The tile used to read only a total, so "12 videos" looked identical whether
/// eleven were new or none were, and a student had no reason to open the
/// module after the first visit.
/// </summary>
public sealed class StudentVideoProgressDto
{
    /// <summary>Videos currently visible to this student under this teacher.</summary>
    public int Total { get; set; }

    /// <summary>
    /// Of <see cref="Total"/>, how many the student has actually STARTED — i.e.
    /// <see cref="InProgress"/> + <see cref="Watched"/>. Until 2026-09-09 this counted analytics
    /// ROWS, which are created with 0 watch seconds on the first play report, so a video the
    /// student never watched dropped out of the "new" badge while the list still badged it
    /// NotStarted.
    /// </summary>
    public int Seen { get; set; }

    /// <summary>Of <see cref="Total"/>, how many have never been opened — the "new" badge.</summary>
    public int NotStarted { get; set; }

    /// <summary>
    /// Of <see cref="Total"/>, how many are started but not finished. Additive field — an older
    /// app build ignores it. <c>NotStarted + InProgress + Watched == Total</c> always.
    /// </summary>
    public int InProgress { get; set; }

    /// <summary>
    /// Of <see cref="Total"/>, how many the student has watched through (the same completion
    /// threshold the unit progress rail and the teacher's <c>completedCount</c> use). Additive.
    /// </summary>
    public int Watched { get; set; }
}

public sealed class StudentVideoUnitDto
{
    public long Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>Count of the student's visible videos in this unit.</summary>
    public int VideoCount { get; set; }

    /// <summary>Of <see cref="VideoCount"/>, how many carry a quiz.</summary>
    public int QuizCount { get; set; }

    /// <summary>
    /// Of <see cref="VideoCount"/>, how many this student has already watched
    /// through. Additive field — an older app build simply ignores it and keeps
    /// rendering the unit exactly as before.
    /// </summary>
    public int WatchedCount { get; set; }

    /// <summary>The owning teacher's subject (same resolution as the video list), empty if none.</summary>
    public string Subject { get; set; } = string.Empty;
}



// ══════════════════════════════════════════════════════════════════════════
// START / STOP (Story B + Story C)
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request body for <c>POST /api/videos/{id}/start</c>.
///
/// Trust boundary on <see cref="VideoDurationSeconds"/>: the server stores
/// the value only when (a) no prior duration is known (current = 0) or (b)
/// the report is within ±<c>VideoConstants.DurationToleranceFraction</c> of
/// the stored value. Out-of-band reports are silently ignored.
/// </summary>
public sealed class StartWatchRequest
{
    /// <summary>
    /// Stable per-install UUID generated by Flutter and persisted on-device.
    /// Length-bounded to <c>VideoConstants.DeviceIdMaxLength</c>; never used
    /// for authorization, only for partitioning anchor lookups.
    /// </summary>
    [Required]
    public string DeviceId { get; set; } = null!;

    /// <summary>
    /// The video's total length in seconds, read by Flutter from the
    /// player's metadata. May be zero if the player doesn't yet know.
    /// </summary>
    public int VideoDurationSeconds { get; set; }

    /// <summary>
    /// Optional idempotency key (Q4(b)). When supplied and a row with the
    /// same <c>ClientEventId</c> already exists, the server short-circuits
    /// with the previous response payload instead of inserting a duplicate
    /// Open event.
    /// </summary>
    public Guid? ClientEventId { get; set; }
}

/// <summary>
/// Response body for <c>POST /api/videos/{id}/start</c>. Drives Flutter's
/// secure-WebView load.
/// </summary>
public sealed class StartWatchResponse
{
    public long VideoAssetId { get; set; }

    /// <summary>
    /// The provider-specific embed URL built from <c>SourceType</c> and
    /// <c>ExternalId</c>. Flutter loads THIS into the WebView, not the
    /// original watch URL.
    /// </summary>
    public string EmbedUrl { get; set; } = null!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoSourceType SourceType { get; set; }

    /// <summary>
    /// Where Flutter should seek to. Capped at
    /// <c>DurationSeconds - VideoConstants.ResumeEndOfVideoBufferSeconds</c>
    /// to avoid the "instant complete" glitch.
    /// </summary>
    public int ResumeFromSeconds { get; set; }

    /// <summary>
    /// The duration the server believes the video has. May be zero on
    /// first-ever open if the client also reported zero.
    /// </summary>
    public int DurationSeconds { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/videos/{id}/stop</c>.
///
/// Trust boundaries on the deltas:
/// <list type="bullet">
///   <item><see cref="DeltaSeconds"/> is clamped at the server to
///         <c>min(serverElapsedSinceLastEvent + tolerance, DurationSeconds)</c>.</item>
///   <item><see cref="PositionSeconds"/> is bounded to <c>[0, DurationSeconds]</c>.</item>
/// </list>
/// Both clamps happen silently — the server never errors on out-of-bounds
/// values; it just records the sanitised version.
/// </summary>
public sealed class StopWatchRequest
{
    [Required]
    public string DeviceId { get; set; } = null!;

    public int PositionSeconds { get; set; }
    public int DeltaSeconds { get; set; }

    /// <summary>
    /// The video's total length in seconds, same trust boundary and tolerance rule as
    /// <see cref="StartWatchRequest.VideoDurationSeconds"/>. Zero / omitted = "the player
    /// still doesn't know", which changes nothing.
    ///
    /// Start-watch used to be the ONLY place a duration could be learned, and it fires on
    /// the play transition — before the player's metadata is necessarily populated. A first
    /// play reporting 0 therefore left the video at 0 until someone opened it again and
    /// happened to have metadata ready, and everything percentage-shaped stayed dead in the
    /// meantime: no completion percent, "completed" stuck at 0 for students who had watched
    /// the whole thing. Progress reports arrive every few seconds, so accepting it here is
    /// what makes a duration that loads a moment after playback still land.
    /// </summary>
    public int VideoDurationSeconds { get; set; }

    /// <summary>
    /// Optional idempotency key (Q4(b)). See <see cref="StartWatchRequest.ClientEventId"/>.
    /// </summary>
    public Guid? ClientEventId { get; set; }
}

/// <summary>
/// Response body for <c>POST /api/videos/{id}/stop</c>. Reflects the
/// server-validated state so Flutter can update its UI optimistically.
/// </summary>
public sealed class StopWatchResponse
{
    public long TotalWatchSeconds { get; set; }
    public int LastResumePositionSeconds { get; set; }
    public int AcceptedDelta { get; set; }
    public bool DeltaWasClamped { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════
// ANALYTICS (Story F)
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request DTO for <c>GET /api/videos/{id}/analytics</c>.
/// </summary>
public sealed class VideoAnalyticsRequest
{
    private int _page = 1;
    private int _pageSize = 50;

    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < 1 ? 50 : value > 200 ? 200 : value;
    }

    /// <summary>
    /// Optional partial match on student name or student code.
    /// </summary>
    public string? Search { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoAnalyticsSortBy SortBy { get; set; } = VideoAnalyticsSortBy.StudentName;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SortDirection SortDirection { get; set; } = SortDirection.Asc;

    /// <summary>
    /// Server-side row filter (G-ANL-4). Narrows <c>Rows</c> and its
    /// pagination/<c>TotalCount</c> to the requested bucket; the top-level
    /// aggregates in <see cref="VideoAnalyticsResponse"/> are unaffected —
    /// they always reflect the full scope, not the filtered view.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoAnalyticsStatusFilter StatusFilter { get; set; } = VideoAnalyticsStatusFilter.All;

    /// <summary>
    /// Optional: only students whose ACTIVE session assignment is this session.
    /// Same key the by-session breakdown groups on, so a chip and its row always
    /// report the same number.
    /// </summary>
    public long? SessionId { get; set; }

    /// <summary>
    /// Optional: only students whose active session belongs to this group.
    /// Ignored when <see cref="SessionId"/> is also supplied — the narrower
    /// filter wins rather than silently widening the result.
    /// </summary>
    public long? SessionGroupId { get; set; }
}

/// <summary>
/// Top-level analytics response. Aggregates appear once at the top; per-row
/// data is paginated below.
/// </summary>
public sealed class VideoAnalyticsResponse
{
    public long VideoAssetId { get; set; }
    public string Title { get; set; } = null!;
    public int DurationSeconds { get; set; }
    public int TotalStudentsInScope { get; set; }
    public int TotalStudentsWatched { get; set; }

    /// <summary>= <c>TotalStudentsInScope - TotalStudentsWatched</c> (G-ANL-2).</summary>
    public int UnseenCount { get; set; }

    /// <summary>
    /// Students whose completion meets <c>VideoConstants.CompletionThresholdPercent</c> (G-ANL-1).
    /// </summary>
    public int CompletedCount { get; set; }

    public List<VideoAnalyticsRowDto> Rows { get; set; } = new();

    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// One row of the analytics report, mapped from
/// <see cref="Domain.Interfaces.VideoAnalyticsReportRow"/>.
/// </summary>
public sealed class VideoAnalyticsRowDto
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;

    /// <summary>The student's current session name, or null if unassigned.</summary>
    public string? SessionName { get; set; }

    /// <summary>
    /// The id behind <see cref="SessionName"/>, so the client can key a filter or a
    /// jump-to-session on it instead of matching display text. Null if unassigned.
    /// </summary>
    public long? SessionId { get; set; }

    public bool HasOpened { get; set; }
    public int OpenCount { get; set; }
    public long TotalWatchSeconds { get; set; }
    public int VideoDurationSeconds { get; set; }
    public DateTime? FirstOpenedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }

    /// <summary>0–100 inclusive. <c>null</c> when video duration is unknown.</summary>
    public int? EstimatedCompletionPct { get; set; }

    /// <summary>Uncapped; values &gt; 100 indicate rewatching.</summary>
    public int? RawWatchPct { get; set; }
}

/// <summary>
/// "How many of each class watched this?" — the video's audience sliced by the
/// students' own sessions. Backs <c>GET /api/videos/{id}/analytics/by-session</c>.
///
/// <see cref="Rows"/> sums exactly to <see cref="TotalStudentsInScope"/>: rows are keyed
/// by the student's active session, so a session reachable both directly and through a
/// scoped group is counted once, and the totals are SUMMED FROM the rows rather than
/// counted separately — the header can never disagree with the list under it.
///
/// These totals may therefore differ from the video detail card's seen/unseen counts on
/// legacy data carrying a per-student <c>VideoScope</c> row whose student no longer
/// exists: that row is counted by the audience query (which does not join
/// <c>TeacherStudents</c>) but has no student to appear in any class row.
/// </summary>
public sealed class VideoSessionWatchBreakdownDto
{
    public long VideoAssetId { get; set; }
    public string Title { get; set; } = null!;

    public int TotalStudentsInScope { get; set; }
    public int TotalStudentsWatched { get; set; }
    public int UnseenCount { get; set; }
    public int CompletedCount { get; set; }

    public List<VideoSessionWatchRowDto> Rows { get; set; } = new();
}

/// <summary>One session's row in <see cref="VideoSessionWatchBreakdownDto"/>.</summary>
public sealed class VideoSessionWatchRowDto
{
    /// <summary>Null for the single "not assigned to a session" bucket, which sorts last.</summary>
    public long? SessionId { get; set; }
    public string? SessionName { get; set; }

    /// <summary>The session's group, shown as the row's subtitle. Null when ungrouped.</summary>
    public long? SessionGroupId { get; set; }
    public string? SessionGroupName { get; set; }

    public int StudentsInScope { get; set; }
    public int WatchedCount { get; set; }

    /// <summary>= <see cref="StudentsInScope"/> - <see cref="WatchedCount"/>.</summary>
    public int UnseenCount { get; set; }

    /// <summary>Watchers meeting <c>VideoConstants.CompletionThresholdPercent</c>.</summary>
    public int CompletedCount { get; set; }

    /// <summary>0–100. Share of the session that has opened the video; 0 when the session is empty.</summary>
    public int WatchedPct { get; set; }
}
/// <summary>
/// One resolved scope target (a session or a session-group) with its display name
/// and current active-student count. Populates <see cref="VideoUnitScopeDto.Target"/>.
/// </summary>
public sealed class VideoScopeTargetDto
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;
    public int StudentCount { get; set; }
}

/// <summary>
/// One read-only scope row on <see cref="VideoUnitResponse"/> — scope type plus its
/// resolved target, enriched with name and student count. To edit the underlying
/// scope, send <see cref="VideoScopeInputDto"/> rows to
/// <c>PUT /api/video-units/{unitId}/scopes</c> — that request shape is unchanged.
/// </summary>
public sealed class VideoUnitScopeDto
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoScopeType ScopeType { get; set; }

    public VideoScopeTargetDto Target { get; set; } = null!;
}

public sealed class VideoUnitResponse
{
    public long Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>
    /// Deduplicated count of active students reachable across every scope row below —
    /// a session also covered by a scoped group is counted once, not twice.
    /// </summary>
    public int TotalStudentsInScope { get; set; }

    /// <summary>
    /// The unit's scope rows (the sessions/groups the unit targets), each enriched
    /// with the resolved target's display name and student count. Edit the underlying
    /// scope with <c>PUT /api/video-units/{unitId}/scopes</c> (send the full desired
    /// set — an empty list clears the scope) using <see cref="VideoScopeInputDto"/>.
    /// </summary>
    public List<VideoUnitScopeDto> Scopes { get; set; } = new();
}
/// <summary>
/// The sessions and session-groups a video is ALLOWED to be scoped to, derived
/// from the target scope of the unit(s) it belongs to. Backs
/// <c>GET /api/video-units/allowed-scope-targets</c> so the video-scope picker
/// only offers targets the video's units cover — enforcing the containment rule
/// that a video's audience must stay within its units' audience.
/// </summary>
public sealed class AllowedScopeTargetsDto
{
    /// <summary>
    /// Sessions the video may target: the units' own Session scopes PLUS every
    /// session inside the units' SessionGroup scopes (a group expands to its
    /// members, so a teacher can pick an individual session within a covered group).
    /// </summary>
    public List<AllowedSessionTargetDto> Sessions { get; set; } = new();

    /// <summary>Session-groups the video may target: the units' SessionGroup scopes.</summary>
    public List<AllowedGroupTargetDto> Groups { get; set; } = new();
}

/// <summary>One selectable session in <see cref="AllowedScopeTargetsDto"/>.</summary>
public sealed class AllowedSessionTargetDto
{
    public long SessionId { get; set; }
    public string SessionName { get; set; } = null!;
}

/// <summary>One selectable session-group in <see cref="AllowedScopeTargetsDto"/>.</summary>
public sealed class AllowedGroupTargetDto
{
    public long SessionGroupId { get; set; }
    public string GroupName { get; set; } = null!;
}
// <summary>
/// One scope-type group for the merged create-video request. <c>Ids</c> are
/// session ids when <c>ScopeType = Session</c>, or session-group ids when
/// <c>ScopeType = SessionGroup</c>. Flattened server-side into one
/// <see cref="VideoScopeInputDto"/> per id and validated/persisted through
/// the exact same pipeline <c>PUT /videos/{id}/scopes</c> uses.
/// </summary>
public sealed class CreateVideoScopeDto
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoScopeType ScopeType { get; set; }

    [Required]
    public List<long> Ids { get; set; } = new();
}

/// <summary>One answer option for a create-time exam question.</summary>
public sealed class CreateExamQuestionOptionDto
{
    [Required]
    public string Text { get; set; } = null!;

    /// <summary>Answer key. SingleChoice: exactly one option across the
    /// question must be true. MultipleChoice: one or more.</summary>
    public bool IsCorrect { get; set; }
}

/// <summary>One question for a create-time exam. Also the read shape (request + response).</summary>
public sealed class CreateExamQuestionDto
{
    [Required]
    public string Text { get; set; } = null!;

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoExamQuestionType QuestionType { get; set; }

    /// <summary>
    /// Optional question image. On write: the <c>fileId</c> (FileObject.PublicId) from
    /// <c>POST /api/upload</c> (category <c>VideoExamQuestionImage</c>). On read: the same
    /// <c>fileId</c>, alongside <see cref="ImageUrl"/>. Teacher/assistant-scoped access.
    /// </summary>
    public Guid? ImageFileId { get; set; }

    /// <summary>Read-only: the stable gated URL for <see cref="ImageFileId"/>, or null. Ignored on write.</summary>
    public string? ImageUrl { get; set; }

    [Required]
    public List<CreateExamQuestionOptionDto> Options { get; set; } = new();
}

/// <summary>
/// Optional exam definition, embedded in <see cref="CreateVideoRequest"/>.
/// Persisted atomically with the video and its scopes — no separate exam
/// endpoint exists yet.
/// </summary>
public sealed class CreateExamDto
{
    [Required]
    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    [Required]
    public List<CreateExamQuestionDto> Questions { get; set; } = new();
}
/// <summary>
/// Request body for <c>PUT /api/videos/{id}/units</c> — the dedicated
/// assign-to-unit endpoint. Replace-all: null or empty <c>UnitIds</c>
/// unlinks the video from every unit.
/// </summary>
public sealed class AssignVideoUnitsRequest
{
    public List<long>? UnitIds { get; set; }
}

/// <summary>Response for <c>PUT /api/videos/{id}/units</c> — the resulting unit links.</summary>
public sealed class AssignVideoUnitsResponse
{
    public List<long> UnitIds { get; set; } = new();
}