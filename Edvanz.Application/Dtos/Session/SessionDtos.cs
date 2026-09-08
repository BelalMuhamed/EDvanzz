using Edvanz.Domain.Enums;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Edvanz.Application.Dtos.Session;

// ══════════════════════════════════════════════
// SESSION INPUT DTOs
// ══════════════════════════════════════════════

/// <summary>
/// Input DTO for creating a new session.
/// REQ-SES-006: All fields mandatory except session name (which can be auto-generated).
/// </summary>
public class CreateSessionDto
{
    /// <summary>
    /// The owning teacher's Id. All data is scoped to this teacher.
    /// </summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>
    /// Optional session name. If null/empty AND teacher's config is Auto,
    /// the system auto-generates the name per REQ-SES-002.
    /// If provided, used as-is (manual entry per REQ-SES-002).
    /// REQ-SES-003: Supports Arabic and English.
    /// </summary>
    // SES-3: matches the Sessions.SessionName nvarchar(200) column so an over-length value returns a
    // field-named 400 instead of a DB-truncation 409 "DatabaseConflict".
    [MaxLength(200, ErrorMessage = "Session name must be at most 200 characters.")]
    public string? SessionName { get; set; }

    /// <summary>
    /// Recurrence pattern. REQ-SES-007: Weekly, BiWeekly, or Monthly.
    /// </summary>
    [Required]
    public OccurrenceType OccurrenceType { get; set; }

    /// <summary>
    /// Day-of-week indices for Weekly/BiWeekly. 
    /// Values: 0=Saturday, 1=Sunday, 2=Monday, 3=Tuesday, 4=Wednesday, 5=Thursday, 6=Friday.
    /// REQ-SES-008: At least one day required for Weekly/BiWeekly.
    /// Ignored for Monthly.
    /// </summary>
    public List<int>? SelectedDays { get; set; }

    /// <summary>
    /// Day of month (1–31) for Monthly occurrence type.
    /// Required when OccurrenceType == Monthly.
    /// Ignored for Weekly/BiWeekly.
    /// </summary>
    public byte? MonthlyDayOfMonth { get; set; }

    /// <summary>
    /// Payment type. REQ-SES-010: Monthly or PerSession.
    /// </summary>
    [Required]
    public PaymentType PaymentType { get; set; }

    /// <summary>
    /// Monetary amount in EGP. REQ-SES-011.
    /// </summary>
    [Required]
    [Range(0.01, 999999.99, ErrorMessage = "Session amount must be a positive value.")]
    public decimal SessionAmount { get; set; }

    /// <summary>
    /// Session start date. REQ-SES-013.
    /// </summary>
    [Required]
    public DateTime StartDate { get; set; }

    /// <summary>
    /// Session end date. REQ-SES-013/014: Must be after StartDate.
    /// </summary>
    [Required]
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Session start time (HH:mm). REQ-SES-006.
    /// </summary>
    [Required]
    public TimeSpan StartTime { get; set; }

    /// <summary>
    /// Duration in minutes. REQ-SES-006.
    /// </summary>
    [Required]
    [Range(1, 1440, ErrorMessage = "Duration must be between 1 and 1440 minutes.")]
    public short DurationMinutes { get; set; }

    /// <summary>
    /// Optional group to assign the session to during creation.
    /// REQ-SES-026: Null = ungrouped.
    /// </summary>
    public long? SessionGroupId { get; set; }
}

/// <summary>
/// Input DTO for updating an existing session.
/// REQ-SES-005: Session name editable. REQ-SES-012: Payment type and amount editable.
/// REQ-SES-009: OccurrenceType NOT editable if session has student assignments or links.
/// </summary>
public class UpdateSessionDto
{
    /// <summary>
    /// Updated session name. REQ-SES-005: Editable at any time.
    /// </summary>
    // SES-3: matches the Sessions.SessionName nvarchar(200) column (clean 400 over a truncation 409).
    [Required]
    [MaxLength(200, ErrorMessage = "Session name must be at most 200 characters.")]
    public string SessionName { get; set; } = null!;

    /// <summary>
    /// Updated occurrence type. REQ-SES-009: Only editable if no assignments/links.
    /// </summary>
    [Required]
    public OccurrenceType OccurrenceType { get; set; }

    /// <summary>
    /// Updated selected days. REQ-SES-008.
    /// </summary>
    public List<int>? SelectedDays { get; set; }

    /// <summary>
    /// Updated monthly day. For Monthly occurrence only.
    /// </summary>
    public byte? MonthlyDayOfMonth { get; set; }

    /// <summary>
    /// Updated payment type. REQ-SES-012.
    /// </summary>
    [Required]
    public PaymentType PaymentType { get; set; }

    /// <summary>
    /// Updated amount. REQ-SES-012.
    /// </summary>
    [Required]
    [Range(0.01, 999999.99)]
    public decimal SessionAmount { get; set; }

    /// <summary>
    /// Updated start date.
    /// </summary>
    [Required]
    public DateTime StartDate { get; set; }

    /// <summary>
    /// Updated end date. REQ-SES-014.
    /// </summary>
    [Required]
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Updated start time.
    /// </summary>
    [Required]
    public TimeSpan StartTime { get; set; }

    /// <summary>
    /// Updated duration in minutes.
    /// </summary>
    [Required]
    [Range(1, 1440)]
    public short DurationMinutes { get; set; }

    /// <summary>
    /// Updated group assignment. Null to ungroup.
    /// REQ-SES-030: Movable between groups at any time.
    /// </summary>
    public long? SessionGroupId { get; set; }
}

// ══════════════════════════════════════════════
// SESSION GROUP INPUT DTOs
// ══════════════════════════════════════════════

/// <summary>
/// Input DTO for creating a session group.
/// REQ-SES-024/025.
/// </summary>
public class CreateSessionGroupDto
{
    /// <summary>
    /// The owning teacher's Id.
    /// </summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>
    /// Group name. REQ-SES-025: Supports Arabic and English.
    /// </summary>
    // SES-3: matches the SessionGroups.GroupName nvarchar(200) column (clean 400 over a truncation 409).
    [Required]
    [MaxLength(200, ErrorMessage = "Group name must be at most 200 characters.")]
    public string GroupName { get; set; } = null!;

    /// <summary>
    /// Optional group description. REQ-SES-025: Supports Arabic and English.
    /// Persisted and echoed back on the group DTO.
    /// </summary>
    [MaxLength(1000, ErrorMessage = "Group description must be at most 1000 characters.")]
    public string? Description { get; set; }
}

/// <summary>
/// Input DTO for renaming a session group.
/// REQ-SES-031: Groups are renamable.
/// </summary>
public class RenameSessionGroupDto
{
    /// <summary>
    /// New group name.
    /// </summary>
    // SES-3: matches the SessionGroups.GroupName nvarchar(200) column (clean 400 over a truncation 409).
    [Required]
    [MaxLength(200, ErrorMessage = "Group name must be at most 200 characters.")]
    public string GroupName { get; set; } = null!;

    /// <summary>
    /// Optional updated description. Null leaves the existing description unchanged;
    /// pass an empty string to clear it.
    /// </summary>
    [MaxLength(1000, ErrorMessage = "Group description must be at most 1000 characters.")]
    public string? Description { get; set; }
}

// ══════════════════════════════════════════════
// SESSION LINK INPUT DTOs
// ══════════════════════════════════════════════

/// <summary>
/// Input DTO for creating a session link (membership).
/// REQ-SES-032/033.
/// </summary>
public class CreateSessionLinkDto
{
    /// <summary>
    /// The owning teacher's Id.
    /// </summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>
    /// First session Id.
    /// </summary>
    [Required]
    public long SessionIdA { get; set; }

    /// <summary>
    /// Second session Id.
    /// </summary>
    [Required]
    public long SessionIdB { get; set; }
}

// ══════════════════════════════════════════════
// STUDENT ASSIGNMENT INPUT DTOs
// ══════════════════════════════════════════════

/// <summary>
/// Input DTO for bulk assigning students to a session.
/// REQ-SES-017: Assign multiple students simultaneously via checkboxes.
/// </summary>
public class AssignStudentsToSessionDto
{
    /// <summary>
    /// The owning teacher's Id.
    /// </summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>
    /// The session Id to assign students to.
    /// </summary>
    [Required]
    public long SessionId { get; set; }

    /// <summary>
    /// List of student Ids to assign.
    /// REQ-SES-017: Multiple students via checkboxes.
    /// </summary>
    [Required]
    public List<long> StudentIds { get; set; } = new();
}

// ══════════════════════════════════════════════
// SESSION LIST REQUEST DTO
// ══════════════════════════════════════════════

/// <summary>
/// Paginated request for the session list with session-specific filters.
/// REQ-SES-044/045: Search and filter options.
/// </summary>
public class SessionListRequest
{
    private int _page = 1;
    private int _pageSize = 20;

    /// <summary>
    /// Page number (1-based). Defaults to 1.
    /// </summary>
    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    /// <summary>
    /// Records per page. Defaults to 20. Max 100.
    /// </summary>
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < 1 ? 20 : value > 100 ? 100 : value;
    }

    /// <summary>
    /// Search term for session name.
    /// REQ-SES-044: Search by session name.
    /// </summary>
    [Description("Search by session name (partial match)")]
    public string? Search { get; set; }

    /// <summary>
    /// Filter by session group Id.
    /// REQ-SES-045: Filter by group. Use -1 for ungrouped only.
    /// </summary>
    public long? GroupId { get; set; }

    /// <summary>
    /// Filter by occurrence type.
    /// REQ-SES-045: Filter by occurrence type.
    /// </summary>
    public OccurrenceType? OccurrenceType { get; set; }

    /// <summary>
    /// If true, show only active sessions (EndDate >= today).
    /// REQ-SES-045: Filter by active status.
    /// </summary>
    public bool ActiveOnly { get; set; } = false;

    /// <summary>
    /// If true, show only expired sessions (EndDate &lt; today).
    /// REQ-SES-045: Filter by expired status.
    /// </summary>
    public bool ExpiredOnly { get; set; } = false;

    /// <summary>
    /// Column to sort by. Defaults to DateAdded.
    /// </summary>
    public SessionSortBy SortBy { get; set; } = SessionSortBy.DateAdded;

    /// <summary>
    /// Sort direction. Defaults to Desc (newest first).
    /// </summary>
    public SortDirection SortDirection { get; set; } = SortDirection.Desc;
}

// ══════════════════════════════════════════════
// OUTPUT DTOs
// ══════════════════════════════════════════════

/// <summary>
/// Output DTO for a session record. Used in list, detail, and creation responses.
/// </summary>
public class SessionDto
{
    public long Id { get; set; }
    public long TeacherId { get; set; }
    public string SessionName { get; set; } = null!;
    public OccurrenceType OccurrenceType { get; set; }
    public List<int>? SelectedDays { get; set; }
    public byte? MonthlyDayOfMonth { get; set; }
    public PaymentType PaymentType { get; set; }
    public decimal SessionAmount { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public TimeSpan StartTime { get; set; }
    public short DurationMinutes { get; set; }
    public long? SessionGroupId { get; set; }
    public string? SessionGroupName { get; set; }

    /// <summary>
    /// REQ-SES-NFR-009: Live student count badge on session cards.
    /// </summary>
    public int StudentCount { get; set; }

    /// <summary>
    /// REQ-SES-015: Whether the session's end date has passed.
    /// </summary>
    public bool IsExpired { get; set; }

    /// <summary>
    /// REQ-SES-034: Names of linked sessions (membership indicator).
    /// </summary>
    public List<LinkedSessionInfo>? LinkedSessions { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// What the price change did to this session's still-owed bills — set ONLY on the update
    /// response when <c>SessionAmount</c> actually changed, so the app can report "N students
    /// re-priced from {month}" instead of moving figures silently. Null everywhere else, and
    /// omitted from the JSON when null so the (hot, paginated) session LIST response is unchanged.
    /// Additive — older clients ignore it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Payment.SessionRepriceSummary? PriceReconcile { get; set; }
}

/// <summary>
/// Lightweight DTO for linked session display.
/// REQ-SES-034: Visual indicator showing linked session name(s).
/// </summary>
public class LinkedSessionInfo
{
    public long Id { get; set; }
    public string SessionName { get; set; } = null!;
}

/// <summary>
/// Output DTO for a session group.
/// REQ-SES-027: Rendered as box/card in the session list.
/// </summary>
public class SessionGroupDto
{
    public long Id { get; set; }
    public long TeacherId { get; set; }
    public string GroupName { get; set; } = null!;

    /// <summary>
    /// Optional group description entered by the tutor. Null when none was set.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Number of sessions in this group.
    /// </summary>
    public int SessionCount { get; set; }

    /// <summary>
    /// Number of active students currently assigned to this group's sessions.
    /// Serialized as <c>students_number</c> to match the mobile app's key.
    /// </summary>
    [JsonPropertyName("students_number")]
    public int StudentsNumber { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Output DTO for session deletion confirmation data.
/// REQ-SES-040/047: Provides data needed for the confirmation dialog.
/// </summary>
public class SessionDeleteConfirmationDto
{
    public long SessionId { get; set; }
    public string SessionName { get; set; } = null!;

    /// <summary>
    /// REQ-SES-040: Total students currently assigned.
    /// </summary>
    public int AssignedStudentCount { get; set; }

    /// <summary>
    /// REQ-SES-047: Names of linked sessions that will lose membership.
    /// </summary>
    public List<LinkedSessionInfo> AffectedLinkedSessions { get; set; } = new();
}

/// <summary>
/// Output DTO for student reassignment warnings during bulk assign.
/// REQ-SES-018: Warning for students already assigned to another session.
/// </summary>
public class StudentReassignmentWarning
{
    public long StudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public long CurrentSessionId { get; set; }
    public string CurrentSessionName { get; set; } = null!;
    public long NewSessionId { get; set; }
    public string NewSessionName { get; set; } = null!;
}

/// <summary>
/// Output DTO for the bulk assign operation result.
/// Contains warnings for students requiring reassignment confirmation.
/// </summary>
public class AssignStudentsResultDto
{
    /// <summary>
    /// Students successfully assigned (no conflict).
    /// </summary>
    public int AssignedCount { get; set; }

    /// <summary>
    /// REQ-SES-018: Students that need confirmation because they are already assigned elsewhere.
    /// The client should present these to the tutor for confirm/cancel per student.
    /// </summary>
    public List<StudentReassignmentWarning> Warnings { get; set; } = new();
}
/// <summary>
/// Minimal session identity for select/dropdown controls — Id + SessionName only.
/// Not paginated, not filtered: returns the teacher's full session catalog in one call.
/// Same pattern as <see cref="Edvanz.Application.Dtos.Teacher.TeacherLookupItemDto"/>.
/// </summary>
public class SessionLookupItemDto
{
    public long Id { get; set; }
    public string SessionName { get; set; } = null!;
}