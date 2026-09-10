using Edvanz.Domain.Enums;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Edvanz.Application.Dtos.Attendance;

// ══════════════════════════════════════════════
// ENUMS FOR SORTING / REPORTING
// ══════════════════════════════════════════════

/// <summary>
/// Report types available in the Attendance Module.
/// REQ-ATT-040: Six distinct report types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttendanceReportType
{
    /// <summary>REQ-ATT-040 Type 1: Single student absence report across all sessions.</summary>
    SingleStudentAbsence = 1,
    /// <summary>REQ-ATT-040 Type 2: Session absence report (single session).</summary>
    SessionAbsence = 2,
    /// <summary>REQ-ATT-040 Type 3: All sessions absence report (aggregated).</summary>
    AllSessionsAbsence = 3,
    /// <summary>REQ-ATT-040 Type 4: Full attendance history for a specific session.</summary>
    SessionAttendanceHistory = 4,
    /// <summary>REQ-ATT-040 Type 5: Attendance report for a session group.</summary>
    SessionGroupAttendance = 5,
    /// <summary>REQ-ATT-040 Type 6: Linked sessions attendance report.</summary>
    LinkedSessionsAttendance = 6
}

/// <summary>
/// Sort options for the attendance timeline view.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttendanceSortBy
{
    OccurrenceDate,
    StudentName,
    StudentCode
}

// ══════════════════════════════════════════════
// TAKE ATTENDANCE INPUT DTOs
// ══════════════════════════════════════════════

/// <summary>
/// Input DTO for marking a single student's attendance.
/// REQ-ATT-006: Supports all three methods via AttendanceMethod field.
/// REQ-ATT-017: Cross-session attendance indicated by the student not being in the session.
/// </summary>
public class MarkAttendanceDto
{
    /// <summary>The owning teacher's Id. All data scoped to this teacher.</summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>The session to take attendance for.</summary>
    [Required]
    public long SessionId { get; set; }

    /// <summary>The student to mark attendance for.</summary>
    [Required]
    public long TeacherStudentId { get; set; }

    /// <summary>
    /// The attendance status to record. REQ-ATT-006: Present or Absent only.
    /// NULLABLE ON PURPOSE (ATT-2 / CC-6): a non-nullable enum defaults to <c>Absent</c> (0), so an
    /// omitted status would silently write a harmful Absent. Keeping it nullable lets the service
    /// detect omission and reject it with a clear 400 (<c>AttendanceStatusRequired</c>) instead.
    /// </summary>
    public AttendanceStatus? Status { get; set; }

    /// <summary>
    /// Which method was used. REQ-ATT-006.
    /// </summary>
    [Required]
    public AttendanceMethod AttendanceMethod { get; set; }

    /// <summary>
    /// The date of the session occurrence. Defaults to today if not specified.
    /// REQ-ATT-026: Future dates allowed via Edit Attendance.
    /// </summary>
    public DateTime? OccurrenceDate { get; set; }

    /// <summary>
    /// The user performing the action (teacher or assistant).
    /// </summary>
    public long? RecordedByUserId { get; set; }

    /// <summary>
    /// If true, the user has confirmed they want to mark this student
    /// even though they were absent in the last session (REQ-ATT-028/058).
    /// </summary>
    public bool AbsenceAlertConfirmed { get; set; } = false;
}

/// <summary>
/// Input DTO for marking multiple students' attendance in one action.
/// REQ-ATT-006 Method 2: Multi-select from student list.
/// REQ-ATT-055: "Mark All Present" uses this with all student Ids.
///
/// TWO ACCEPTED SHAPES (backward-compatible):
///   1. NEW per-student shape — <see cref="Items"/>: <c>[{ teacherStudentId, status }]</c>, where each
///      status may be <c>Present</c> / <c>Absent</c> / <c>Held</c>. This lets one call drive a mixed
///      "absent last session → Present/Hold" batch. <c>Held</c> is accepted ONLY through this items path.
///   2. LEGACY shape — <see cref="TeacherStudentIds"/> plus a single top-level <see cref="Status"/>
///      (<c>Present</c> / <c>Absent</c> only) applied to every id.
/// Exactly one shape must be supplied; when BOTH are sent, <see cref="Items"/> wins and the legacy
/// fields are ignored. Sending neither is a 400 (<c>AttendanceBulkTargetsRequired</c>).
/// </summary>
public class BulkMarkAttendanceDto
{
    /// <summary>The owning teacher's Id.</summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>The session to take attendance for.</summary>
    [Required]
    public long SessionId { get; set; }

    /// <summary>
    /// NEW per-student shape: one entry per student carrying its own status (Present / Absent / Held).
    /// Preferred over the legacy <see cref="TeacherStudentIds"/> + <see cref="Status"/> pair; when both
    /// are supplied this list wins. Repeated ids collapse to the last entry.
    /// </summary>
    public List<BulkMarkAttendanceItemDto>? Items { get; set; }

    /// <summary>
    /// LEGACY shape: list of student Ids to mark with the single top-level <see cref="Status"/>. Kept
    /// for backward compatibility; ignored when <see cref="Items"/> is provided.
    /// </summary>
    public List<long>? TeacherStudentIds { get; set; }

    /// <summary>
    /// LEGACY shape: the status to apply to all <see cref="TeacherStudentIds"/> (Present / Absent only —
    /// <c>Held</c> is rejected here, use <see cref="Items"/>). NULLABLE ON PURPOSE (ATT-2 / CC-6) — see
    /// <see cref="MarkAttendanceDto.Status"/>; the service rejects a missing status with a clear 400.
    /// Ignored when <see cref="Items"/> is provided.
    /// </summary>
    public AttendanceStatus? Status { get; set; }

    /// <summary>The method used (typically MultiSelect).</summary>
    [Required]
    public AttendanceMethod AttendanceMethod { get; set; }

    /// <summary>The occurrence date. Defaults to today.</summary>
    public DateTime? OccurrenceDate { get; set; }

    /// <summary>Who is recording.</summary>
    public long? RecordedByUserId { get; set; }
}

/// <summary>
/// One student's target status within the NEW per-student <see cref="BulkMarkAttendanceDto.Items"/>
/// shape. Lets a single mark-bulk call carry a mix of Present / Absent / Held per student.
/// </summary>
public class BulkMarkAttendanceItemDto
{
    /// <summary>The student to mark.</summary>
    [Required]
    public long TeacherStudentId { get; set; }

    /// <summary>
    /// The status for THIS student — one of <c>Present</c>, <c>Absent</c>, or <c>Held</c> (serialized as a
    /// string via the global <c>JsonStringEnumConverter</c>). NULLABLE ON PURPOSE (CC-6): a missing value
    /// is rejected with a clear 400 (<c>AttendanceStatusRequired</c>) instead of silently defaulting to
    /// <c>Absent</c>; an out-of-range value is rejected with <c>InvalidAttendanceStatus</c>. <c>Held</c>
    /// mirrors the single-student Hold flow (writes a Held record, never touches the absence counter, and
    /// is skipped when the student already has a record for the occurrence).
    /// </summary>
    [Required]
    public AttendanceStatus? Status { get; set; }
}

// ══════════════════════════════════════════════
// EDIT ATTENDANCE INPUT DTOs
// ══════════════════════════════════════════════

/// <summary>
/// Input DTO for editing an existing attendance record.
/// REQ-ATT-023/024: Distinct from Take Attendance — selects past/future date.
/// REQ-ATT-025: Edit timestamp preserved; original RecordedAt never altered.
/// </summary>
public class EditAttendanceDto
{
    /// <summary>The owning teacher's Id.</summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>The attendance record to edit.</summary>
    [Required]
    public long AttendanceRecordId { get; set; }

    /// <summary>
    /// The new status to apply. NULLABLE ON PURPOSE (ATT-2/ATT-3 / CC-6) — see
    /// <see cref="MarkAttendanceDto.Status"/>; the service rejects a missing/out-of-range status.
    /// </summary>
    public AttendanceStatus? NewStatus { get; set; }

    /// <summary>Optional reason for the edit (audit trail).</summary>
    public string? EditReason { get; set; }

    /// <summary>Who is making the edit.</summary>
    public long? EditedByUserId { get; set; }
}

/// <summary>
/// Input DTO for adding a new attendance record via Edit Attendance.
/// REQ-ATT-024: Add records for students missed during original attendance-taking.
/// REQ-ATT-026: Pre-record attendance for future occurrence dates.
/// </summary>
public class AddAttendanceRecordDto
{
    /// <summary>The owning teacher's Id.</summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>The session this record belongs to.</summary>
    [Required]
    public long SessionId { get; set; }

    /// <summary>The student to add a record for.</summary>
    [Required]
    public long TeacherStudentId { get; set; }

    /// <summary>
    /// The occurrence date to add the record for. NULLABLE ON PURPOSE (ATT-6 / CC-6): a non-nullable
    /// DateTime defaults to <c>0001-01-01</c>, which the service then reads as "no scheduled occurrence"
    /// and rejects with a misleading message. Keeping it nullable lets the service reject a missing
    /// date with a clear 400 (<c>AttendanceOccurrenceDateRequired</c>).
    /// </summary>
    public DateTime? OccurrenceDate { get; set; }

    /// <summary>
    /// The status to record. NULLABLE ON PURPOSE (ATT-2/ATT-3 / CC-6) — see
    /// <see cref="MarkAttendanceDto.Status"/>; the service rejects a missing/out-of-range status.
    /// </summary>
    public AttendanceStatus? Status { get; set; }

    /// <summary>Who is adding the record.</summary>
    public long? RecordedByUserId { get; set; }
    public StudentPaymentInfoDto? PaymentInfo { get; set; }
    public StudentAttendanceHistoryInfoDto? HistoryInfo { get; set; }
}

/// <summary>
/// Input DTO for deleting an attendance record via Edit Attendance.
/// REQ-ATT-024: Remove erroneously recorded entries.
/// </summary>
public class DeleteAttendanceRecordDto
{
    /// <summary>The owning teacher's Id.</summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>The attendance record to delete.</summary>
    [Required]
    public long AttendanceRecordId { get; set; }
}

// ══════════════════════════════════════════════
// REQUEST DTOs (PAGINATION / FILTERING)
// ══════════════════════════════════════════════

/// <summary>
/// Request DTO for the attendance dashboard.
/// REQ-ATT-049: Daily summary — sessions for today with status.
/// </summary>
public class AttendanceDashboardRequest
{
    /// <summary>The date to show the dashboard for. Defaults to today.</summary>
    public DateTime? Date { get; set; }
}

/// <summary>
/// Request DTO for listing sessions with attendance context.
/// REQ-ATT-003: Shows "Today's Session" badge for eligible sessions.
/// </summary>
public class AttendanceSessionListRequest
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

    /// <summary>Search by session name.</summary>
    public string? Search { get; set; }

    /// <summary>Filter by group Id.</summary>
    public long? GroupId { get; set; }
}

/// <summary>
/// Request DTO for the student list within Take Attendance / Edit Attendance screens.
/// REQ-ATT-036: Supports pagination for up to 50K students.
/// REQ-ATT-054: Separates unmarked from marked students.
/// </summary>
public class AttendanceStudentListRequest
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
        set => _pageSize = value < 1 ? 50 : value > 100 ? 100 : value;
    }

    /// <summary>Search by student name or code.</summary>
    public string? Search { get; set; }

    /// <summary>Filter to show only unmarked students.</summary>
    public bool UnmarkedOnly { get; set; } = false;
}

/// <summary>
/// Request DTO for the Absence Overview panel.
/// REQ-ATT-032/034: Filterable, searchable absence overview.
/// </summary>
public class AbsenceOverviewRequest
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

    /// <summary>Search by student name or code. REQ-ATT-034.</summary>
    public string? Search { get; set; }

    /// <summary>Filter by session Id. REQ-ATT-034.</summary>
    public long? SessionId { get; set; }

    /// <summary>Filter students with missing phone number. REQ-ATT-034.</summary>
    public bool MissingStudentPhone { get; set; } = false;

    /// <summary>Filter students with missing parent phone number. REQ-ATT-034.</summary>
    public bool MissingParentPhone { get; set; } = false;

    /// <summary>
    /// Keep only students on an active absence streak &gt;= this value — the "absent students"
    /// violations view. Default 0 = no filter (the full absence overview). Negatives clamp to 0.
    /// </summary>
    public int MinConsecutiveAbsences
    {
        get => _minConsecutiveAbsences;
        set => _minConsecutiveAbsences = value < 0 ? 0 : value;
    }
    private int _minConsecutiveAbsences;

    /// <summary>
    /// View absence history for a specific past occurrence date.
    /// REQ-ATT-035: Default is latest occurrence.
    /// </summary>
    public DateTime? OccurrenceDate { get; set; }
}

/// <summary>
/// Request DTO for the Student Attendance Timeline view.
/// REQ-ATT-072/073: Filterable student list.
/// </summary>
public class AttendanceTimelineRequest
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

    /// <summary>Filter by specific session. REQ-ATT-073.</summary>
    public long? SessionId { get; set; }

    /// <summary>Filter by specific session group. REQ-ATT-073.</summary>
    public long? SessionGroupId { get; set; }

    /// <summary>Search by student name (partial match). REQ-ATT-073.</summary>
    public string? StudentName { get; set; }

    /// <summary>Search by student code (partial match). REQ-ATT-073.</summary>
    public string? StudentCode { get; set; }
}

/// <summary>
/// Request DTO for a student's timeline month data.
/// REQ-ATT-079: Load one month at a time on scroll.
/// </summary>
public class StudentTimelineMonthRequest
{
    /// <summary>
    /// Year of the month to load. Optional: when omitted (together with
    /// <see cref="Month"/>) the server defaults to the teacher's current local
    /// (Africa/Cairo) month, mirroring the payment-module month-scoping convention.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Month number (1-12) to load. Optional: see <see cref="Year"/> for the
    /// defaulting behavior. Ignored (falls back to current month) unless
    /// <see cref="Year"/> is also supplied.
    /// </summary>
    [Range(1, 12)]
    public int? Month { get; set; }
}

/// <summary>
/// Request for the session month-matrix view: one session's students × occurrences for a
/// month, with INDEPENDENT pagination of students (rows) and occurrences (columns) so the
/// client never loads the full roster or the full month at once.
/// </summary>
public class SessionMonthAttendanceRequest
{
    private int _page = 1;
    private int _pageSize = 25;
    private int _occurrencePage = 1;
    private int _occurrencePageSize = 10;

    /// <summary>Year of the month to load.</summary>
    [Required]
    [Range(2000, 2100)]
    public int Year { get; set; }

    /// <summary>Month number (1-12) to load.</summary>
    [Required]
    [Range(1, 12)]
    public int Month { get; set; }

    /// <summary>Student (row) page number, 1-based.</summary>
    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    /// <summary>Students per page (1–100, default 25).</summary>
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < 1 ? 25 : value > 100 ? 100 : value;
    }

    /// <summary>Occurrence (column) page number, 1-based.</summary>
    public int OccurrencePage
    {
        get => _occurrencePage;
        set => _occurrencePage = value < 1 ? 1 : value;
    }

    /// <summary>Occurrences per page (1–31, default 10 — a month has at most 31).</summary>
    public int OccurrencePageSize
    {
        get => _occurrencePageSize;
        set => _occurrencePageSize = value < 1 ? 10 : value > 31 ? 31 : value;
    }

    /// <summary>Optional student name/code filter for the rows.</summary>
    public string? Search { get; set; }
}

/// <summary>One occurrence column of the session month matrix.</summary>
public class SessionMonthOccurrenceDto
{
    public long OccurrenceId { get; set; }
    public DateTime Date { get; set; }
}

/// <summary>
/// One (student × occurrence) cell. <see cref="Status"/> is null when the student is
/// unmarked for that occurrence (<see cref="IsMarked"/> false).
/// </summary>
public class SessionMonthCellDto
{
    public long OccurrenceId { get; set; }
    public bool IsMarked { get; set; }
    public AttendanceStatus? Status { get; set; }
}

/// <summary>One student row of the session month matrix.</summary>
public class SessionMonthStudentRowDto
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;

    /// <summary>Present (incl. cross-session) count across the WHOLE month, not just the occurrence page.</summary>
    public int MonthPresentCount { get; set; }

    /// <summary>Absent count across the WHOLE month, not just the occurrence page.</summary>
    public int MonthAbsentCount { get; set; }

    /// <summary>Cells aligned 1:1 (same order) with the occurrence page in <c>occurrences.data</c>.</summary>
    public List<SessionMonthCellDto> Cells { get; set; } = new();
}

/// <summary>
/// Session month-matrix response: the paged occurrence columns plus the paged student rows,
/// each row carrying its cells for exactly the returned occurrence page.
/// </summary>
public class SessionMonthAttendanceDto
{
    public long SessionId { get; set; }
    public string SessionName { get; set; } = null!;
    public int Year { get; set; }
    public int Month { get; set; }
    public PaginatedResponse<List<SessionMonthOccurrenceDto>> Occurrences { get; set; } = null!;
    public PaginatedResponse<List<SessionMonthStudentRowDto>> Students { get; set; } = null!;
}

/// <summary>
/// Request DTO for report generation.
/// REQ-ATT-040: Multiple report types with date range.
/// </summary>
public class AttendanceReportRequest
{
    /// <summary>The type of report to generate. REQ-ATT-040.</summary>
    [Required]
    public AttendanceReportType ReportType { get; set; }

    /// <summary>Teacher Student Id for Type 1 (single student report).</summary>
    public long? TeacherStudentId { get; set; }

    /// <summary>Session Id for Type 2, 4 reports.</summary>
    public long? SessionId { get; set; }

    /// <summary>Session Group Id for Type 5 report.</summary>
    public long? SessionGroupId { get; set; }

    /// <summary>Start of the report date range.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>End of the report date range.</summary>
    public DateTime? EndDate { get; set; }
}

// ══════════════════════════════════════════════
// OUTPUT DTOs
// ══════════════════════════════════════════════

/// <summary>
/// Output DTO for the attendance dashboard.
/// REQ-ATT-049: Daily summary at the top.
/// </summary>
public class AttendanceDashboardDto
{
    /// <summary>The date this dashboard is for.</summary>
    public DateTime Date { get; set; }

    /// <summary>Total sessions scheduled for this date. REQ-ATT-049.</summary>
    public int TotalSessionsToday { get; set; }

    /// <summary>Sessions with completed attendance. REQ-ATT-049.</summary>
    public int CompletedSessions { get; set; }

    /// <summary>Sessions still pending. REQ-ATT-049.</summary>
    public int PendingSessions { get; set; }

    /// <summary>Sessions currently in progress.</summary>
    public int InProgressSessions { get; set; }

    /// <summary>Session cards with attendance status. REQ-ATT-050/051/052.</summary>
    public List<AttendanceSessionCardDto> SessionCards { get; set; } = new();

    /// <summary>Exams: separate-time exams due today — the home "today's exams" section.</summary>
    public List<TodayExamCardDto> ExamsToday { get; set; } = new();
}

/// <summary>A separate-time exam due today, surfaced on the home "today's exams" section.</summary>
public class TodayExamCardDto
{
    public long ExamId { get; set; }
    public long OccurrenceId { get; set; }
    public string Name { get; set; } = null!;
    public long? SessionId { get; set; }
    public string? SessionName { get; set; }
    public int AssignedCount { get; set; }
    public int AttendedCount { get; set; }
    public int MissedCount { get; set; }
}

/// <summary>
/// Session card DTO for the attendance dashboard.
/// REQ-ATT-050: Live counter showing marked vs. total.
/// REQ-ATT-051: Color-coded status.
/// REQ-ATT-052: Today's sessions at top.
/// </summary>
public class AttendanceSessionCardDto
{
    public long SessionId { get; set; }
    public string SessionName { get; set; } = null!;
    public long? SessionGroupId { get; set; }
    public string? SessionGroupName { get; set; }

    /// <summary>Whether this session occurs today. REQ-ATT-003.</summary>
    public bool IsToday { get; set; }

    /// <summary>The occurrence Id for today (if applicable).</summary>
    public long? TodayOccurrenceId { get; set; }

    /// <summary>Attendance-taking progress. REQ-ATT-051: green/amber/red/grey.</summary>
    public OccurrenceStatus Status { get; set; }

    /// <summary>Number of students already marked. REQ-ATT-050.</summary>
    public int MarkedCount { get; set; }

    /// <summary>Total students assigned to this session. REQ-ATT-050.</summary>
    public int TotalStudents { get; set; }

    /// <summary>Session start time for ordering.</summary>
    public TimeSpan StartTime { get; set; }

    // ── Exams ──
    /// <summary>True when a during-session exam is held on this session's occurrence today.</summary>
    public bool IsExamSession { get; set; }
    /// <summary>The exam (template) id when <see cref="IsExamSession"/> is true.</summary>
    public long? ExamId { get; set; }
    /// <summary>The exam occurrence id for today when <see cref="IsExamSession"/> is true.</summary>
    public long? ExamOccurrenceId { get; set; }
    /// <summary>The exam name when <see cref="IsExamSession"/> is true.</summary>
    public string? ExamName { get; set; }
}

/// <summary>
/// Output DTO for a single attendance record.
/// Used in Take Attendance, Edit Attendance, and history views.
/// </summary>
public class AttendanceRecordDto
{
    public long Id { get; set; }
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public long? SessionOccurrenceId { get; set; }
    public long? SessionId { get; set; }
    public string SessionName { get; set; } = null!;
    public DateTime OccurrenceDate { get; set; }
    public AttendanceStatus Status { get; set; }
    public AttendanceMethod AttendanceMethod { get; set; }
    public bool IsCrossSession { get; set; }
    public long? CrossSessionId { get; set; }
    public string? CrossSessionName { get; set; }
    public DateTime? CrossSessionOccurrenceDate { get; set; }
    public DateTime RecordedAt { get; set; }
    public bool IsEdited { get; set; }
    public DateTime? LastEditedAt { get; set; }

    /// <summary>
    /// Payment/debt snapshot, present only when the teacher has
    /// <c>ShowPaymentInfoOnAttendanceScreen</c> enabled AND the caller populated it. Null on
    /// every endpoint that maps records via <c>MapToRecordDto</c> without this feature
    /// (mark/bulk-mark/edit/sync/reports) â€” only <c>GetOccurrenceStudentsAsync</c> (Edit
    /// Attendance past-date view) currently sets this.
    /// </summary>
    public StudentPaymentInfoDto? PaymentInfo { get; set; }

    /// <summary>
    /// Course-scoped and current-month absence counts, present only when the teacher has
    /// <c>ShowAttendanceHistoryOnAttendanceScreen</c> enabled AND the caller populated it. Same
    /// scoping as <see cref="PaymentInfo"/> â€” only <c>GetOccurrenceStudentsAsync</c> sets this.
    /// </summary>
    public StudentAttendanceHistoryInfoDto? HistoryInfo { get; set; }
}

/// <summary>
/// Output DTO returned when marking attendance, includes absence alert info.
/// REQ-ATT-028/029: Absence alert data for the frontend to display.
/// REQ-ATT-069/070: Duplicate detection info.
/// </summary>
public class MarkAttendanceResultDto
{
    /// <summary>The created or updated attendance record.</summary>
    public AttendanceRecordDto? Record { get; set; }

    /// <summary>Whether an absence alert should be shown. REQ-ATT-028.</summary>
    public bool HasAbsenceAlert { get; set; }

    /// <summary>Number of consecutive absences. REQ-ATT-029.</summary>
    public int ConsecutiveAbsences { get; set; }

    /// <summary>Date of last absence. REQ-ATT-028.</summary>
    public DateTime? LastAbsenceDate { get; set; }

    /// <summary>Session name of last absence. REQ-ATT-060.</summary>
    public string? LastAbsenceSessionName { get; set; }

    /// <summary>Whether the last absence was in a cross-session. REQ-ATT-060.</summary>
    public bool LastAbsenceWasCrossSession { get; set; }

    /// <summary>Whether a duplicate was detected. REQ-ATT-069/070.</summary>
    public bool IsDuplicate { get; set; }

    /// <summary>If duplicate: which session already recorded attendance. REQ-ATT-070.</summary>
    public string? DuplicateSessionName { get; set; }

    /// <summary>If duplicate: when the original attendance was recorded. REQ-ATT-070.</summary>
    public DateTime? DuplicateRecordedAt { get; set; }
    /// <summary>
    /// Audit Fix (REQ-ATT-013): The student's currently assigned session Id.
    /// Populated when cross-session attendance is detected.
    /// </summary>
    public long? AssignedSessionId { get; set; }

    /// <summary>
    /// Audit Fix (REQ-ATT-013): The student's currently assigned session name.
    /// </summary>
    public string? AssignedSessionName { get; set; }
}

/// <summary>
/// Output DTO for bulk mark attendance result.
/// REQ-ATT-055/056: Completion summary.
/// </summary>
public class BulkMarkAttendanceResultDto
{
    /// <summary>Number of students successfully marked.</summary>
    public int SuccessCount { get; set; }

    /// <summary>Number of students skipped (already marked/duplicate).</summary>
    public int SkippedCount { get; set; }

    /// <summary>Number of students with absence alerts. REQ-ATT-056.</summary>
    public int AbsenceAlertCount { get; set; }

    /// <summary>Students flagged for consecutive absences. REQ-ATT-056.</summary>
    public List<AbsenceAlertStudentDto> AbsenceAlerts { get; set; } = new();

    /// <summary>Total present after this operation. REQ-ATT-056.</summary>
    public int TotalPresent { get; set; }

    /// <summary>Total absent after this operation. REQ-ATT-056.</summary>
    public int TotalAbsent { get; set; }

    /// <summary>
    /// ATT-5: Per-student outcome for every DISTINCT student id submitted — so the client can
    /// reconcile a multi-select instead of trusting a top-level "all marked" message. One entry per
    /// deduped id (ATT-4 collapses repeated ids); mirrors the per-entry contract of <c>/sync</c>.
    /// </summary>
    public List<BulkMarkStudentResultDto> Results { get; set; } = new();
}

/// <summary>
/// ATT-5: The outcome of a single student within a <c>mark-bulk</c> batch. <see cref="Success"/> is
/// true when the student was marked; otherwise <see cref="Code"/>/<see cref="Reason"/> explain why the
/// student was skipped (already marked, not assigned, cross-session not linked, before enrollment, …).
/// </summary>
public class BulkMarkStudentResultDto
{
    public long TeacherStudentId { get; set; }

    /// <summary>True when a record was created for this student in this batch.</summary>
    public bool Success { get; set; }

    /// <summary>
    /// Stable, language-independent reason code when skipped (e.g. <c>AttendanceDuplicateDetected</c>,
    /// <c>AttendanceStudentNotAssigned</c>); null on success.
    /// </summary>
    public string? Code { get; set; }

    /// <summary>Localized human-readable reason when skipped; null on success.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Absence alert info for a single student.
/// REQ-ATT-028/029/057/058: Alert data for frontend.
/// </summary>
public class AbsenceAlertStudentDto
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public int ConsecutiveAbsences { get; set; }
    public DateTime? LastAbsenceDate { get; set; }
    public string? LastAbsenceSessionName { get; set; }
    public bool WasCrossSession { get; set; }
}

/// <summary>
/// Student row DTO for the Take Attendance screen.
/// REQ-ATT-008: Real-time marked vs. unmarked display.
/// REQ-ATT-054: Unmarked students displayed first.
/// REQ-ATT-071: Already-marked indicator badge.
/// </summary>
public class AttendanceStudentRowDto
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public string? Barcode { get; set; }

    /// <summary>Current attendance status for this occurrence. Null if unmarked.</summary>
    public AttendanceStatus? CurrentStatus { get; set; }

    /// <summary>Whether this student is already marked. REQ-ATT-071.</summary>
    public bool IsMarked { get; set; }

    /// <summary>Whether this student is "held" (REQ-ATT-061).</summary>
    public bool IsHeld { get; set; }

    /// <summary>Whether this student is from a linked session (secondary panel). REQ-ATT-014.</summary>
    public bool IsCrossSessionStudent { get; set; }

    /// <summary>The linked session name (if cross-session student). REQ-ATT-015.</summary>
    public string? SourceSessionName { get; set; }

    /// <summary>Consecutive absences for alert display. REQ-ATT-029.</summary>
    public int ConsecutiveAbsences { get; set; }

    /// <summary>Total absences for display.</summary>
    public int TotalAbsences { get; set; }

    /// <summary>
    /// True when the student is currently carrying an absence streak (<see cref="ConsecutiveAbsences"/>
    /// &gt; 0) — i.e. they were absent last session. Lets the app render the "was absent last session"
    /// warning straight from the roster row without a second lookup. REQ-ATT-028/029.
    /// Sourced from <c>StudentAbsenceCounter</c> (the same source as <c>MarkAttendanceResultDto</c>).
    /// </summary>
    public bool WasAbsentLastSession { get; set; }

    /// <summary>
    /// Date of the student's most recent absence, or null if they have none. REQ-ATT-028.
    /// From <c>StudentAbsenceCounter.LastAbsenceDate</c>.
    /// </summary>
    public DateTime? LastAbsenceDate { get; set; }

    /// <summary>
    /// Session name where the student's most recent absence occurred, or null if none. REQ-ATT-060.
    /// From <c>StudentAbsenceCounter.LastAbsenceSessionName</c>.
    /// </summary>
    public string? LastAbsenceSessionName { get; set; }

    /// <summary>
    /// Payment/debt snapshot, present only when the teacher has
    /// <c>ShowPaymentInfoOnAttendanceScreen</c> enabled. Null means "not shown" per the teacher's
    /// configuration â€” NOT "no debt" (a fully-paid student still gets a populated, zeroed object).
    /// </summary>
    public StudentPaymentInfoDto? PaymentInfo { get; set; }

    /// <summary>
    /// Course-scoped and current-month absence counts, present only when the teacher has
    /// <c>ShowAttendanceHistoryOnAttendanceScreen</c> enabled. Deliberately separate from
    /// <see cref="WasAbsentLastSession"/>/<see cref="LastAbsenceDate"/>/<see cref="LastAbsenceSessionName"/>
    /// above, which stay unconditional (REQ-ATT-028/029/060).
    /// </summary>
    public StudentAttendanceHistoryInfoDto? HistoryInfo { get; set; }
}

/// <summary>
/// Payment/debt snapshot for one student on the Attendance student-list screen
/// (<c>ShowPaymentInfoOnAttendanceScreen</c>). Every figure is judged through the current
/// teacher-local month cutoff (CLAUDE.md Â§7.4) â€” see
/// <see cref="Edvanz.Domain.Interfaces.AttendanceScreenPaymentInfoRow"/>.
/// </summary>
public class StudentPaymentInfoDto
{
    /// <summary>True when the calendar month immediately before the teacher's current local
    /// month has an unpaid Monthly obligation.</summary>
    public bool HasUnpaidLastMonth { get; set; }

    /// <summary>True when the teacher's current local month has an unpaid Monthly obligation.</summary>
    public bool HasUnpaidCurrentMonth { get; set; }

    /// <summary>Count of unpaid periods through the current month cutoff.</summary>
    public int UnpaidMonthsCount { get; set; }

    /// <summary>Sum of (AmountDue - AmountPaid) over those periods.</summary>
    public decimal UnpaidAmount { get; set; }

    /// <summary>Display labels for each unpaid month/period, earliest first â€” e.g.
    /// ["July 2026", "August 2026"]. Formatted via <c>PaymentLabelFormatter</c>.</summary>
    public List<string> UnpaidMonthLabels { get; set; } = new();
}

/// <summary>
/// Course-scoped and current-month absence counts for one student on the Attendance
/// student-list screen (<c>ShowAttendanceHistoryOnAttendanceScreen</c>).
/// </summary>
public class StudentAttendanceHistoryInfoDto
{
    public bool WasAbsentLastSession { get; set; }
    public int ConsecutiveAbsences { get; set; }
    public int TotalAbsences { get; set; }
    public string? AbsenceConsequenceLabel { get; set; }
    public DateTime? LastAbsenceDate { get; set; }
    public string? LastAbsenceSessionName { get; set; }
}

/// <summary>
/// Paged student list for the Take / Edit Attendance screen, extended with two headcount counters.
/// Extends <see cref="PaginatedResponse{T}"/> so the existing paged shape is unchanged — the counters
/// are added alongside. They split the returned list and sum to <c>totalCount</c>.
/// </summary>
public class AttendanceStudentListDto : PaginatedResponse<List<AttendanceStudentRowDto>>
{
    /// <summary>Students assigned to THIS session (not pulled from a linked session).</summary>
    [JsonPropertyName("assigned_count")]
    public int AssignedCount { get; set; }

    /// <summary>Students shown from LINKED sessions (attending but not assigned to this session).</summary>
    [JsonPropertyName("not_assigned_count")]
    public int NotAssignedCount { get; set; }

    /// <summary>
    /// Count of students currently on "hold" for this occurrence (<c>currentStatus == Held</c>), across the
    /// WHOLE filtered roster — not just the returned page — so the app can badge the held bucket without a
    /// second call. REQ-ATT-061. Not part of the assigned/not-assigned split.
    /// </summary>
    [JsonPropertyName("hold_count")]
    public int HoldCount { get; set; }

    /// <summary>
    /// Whether this teacher has the attendance-history alert enabled
    /// (<c>ShowAttendanceHistoryOnAttendanceScreen</c>). Explicit gate for the app's absence
    /// pop-up/row indicator — the client relies on this flag, not on whether <c>HistoryInfo</c>
    /// happens to be present on a row.
    /// </summary>
    public bool ShowAttendanceHistory { get; set; }

    /// <summary>
    /// Whether this teacher has the unpaid-payment alert enabled
    /// (<c>ShowPaymentInfoOnAttendanceScreen</c>). Explicit gate for the app's payment
    /// pop-up/row chip — the client relies on this flag, not on <c>PaymentInfo</c> presence.
    /// </summary>
    public bool ShowPaymentInfo { get; set; }
}

/// <summary>
/// Output DTO for Absence Overview panel.
/// REQ-ATT-032: Absent students with consecutive absence count.
/// REQ-ATT-068: Last 5 occurrence status indicators.
/// </summary>
public class AbsenceOverviewStudentDto
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public long? SessionId { get; set; }
    public string? SessionName { get; set; }
    public int ConsecutiveAbsences { get; set; }
    public int TotalAbsences { get; set; }
    public DateTime? LastAbsenceDate { get; set; }

    /// <summary>
    /// Last 5 occurrence statuses for compact visual indicator.
    /// REQ-ATT-068: Red dot = absent, green dot = present.
    /// </summary>
    public List<AttendanceStatus> RecentStatuses { get; set; } = new();
}

/// <summary>
/// Output DTO for the Student Attendance Timeline all-time summary.
/// REQ-ATT-078: Displayed at top of student's timeline.
/// </summary>
public class StudentAttendanceSummaryDto
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;

    /// <summary>Total occurrences across entire history. REQ-ATT-078.</summary>
    public int TotalOccurrences { get; set; }

    /// <summary>Total absences across all sessions and all time. REQ-ATT-078.</summary>
    public int TotalAbsences { get; set; }

    /// <summary>Overall attendance percentage. REQ-ATT-078.</summary>
    public decimal AttendancePercentage { get; set; }

    /// <summary>Current consecutive absence streak. REQ-ATT-078.</summary>
    public int ConsecutiveAbsences { get; set; }

    /// <summary>All assignment periods, chronologically ordered. REQ-ATT-046.</summary>
    public List<AssignmentPeriodDto> AssignmentPeriods { get; set; } = new();
}

/// <summary>
/// A single assignment period in a student's timeline.
/// REQ-ATT-046: Session name, assignment start/end date, and records within.
/// </summary>
public class AssignmentPeriodDto
{
    public long StudentSessionAssignmentId { get; set; }
    public long? SessionId { get; set; }
    public string SessionName { get; set; } = null!;
    public DateTime AssignedAt { get; set; }
    public DateTime? UnassignedAt { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// Monthly summary within a student's timeline.
/// REQ-ATT-077: Displayed beneath month header.
/// REQ-ATT-080: Shown even when month is collapsed.
/// </summary>
public class MonthlyAttendanceSummaryDto
{
    public int Year { get; set; }
    public int Month { get; set; }

    /// <summary>
    /// Session shown in the screen header. Resolved from the student's assignment
    /// overlapping the month (active/most-recent), independent of whether any
    /// attendance was recorded. Null only when the student had no session assignment
    /// that month and no records to fall back on.
    /// </summary>
    public long? SessionId { get; set; }

    /// <summary>Display name of <see cref="SessionId"/>. REQ-ATT-044 / BR-ATT-005.</summary>
    public string? SessionName { get; set; }

    /// <summary>
    /// Total scheduled class days this month within the student's enrollment window —
    /// the count of <see cref="Days"/>. Includes upcoming and not-yet-marked
    /// occurrences, so it is NOT the denominator of <see cref="AttendancePercentage"/>.
    /// </summary>
    public int TotalOccurrences { get; set; }

    /// <summary>
    /// How many of <see cref="TotalOccurrences"/> actually carry an attendance record
    /// (Present / Absent / CrossSessionPresent / Held). REQ-ATT-077.
    /// </summary>
    public int MarkedOccurrences { get; set; }

    /// <summary>Present + CrossSessionPresent for the month (the "blue" days).</summary>
    public int TotalPresent { get; set; }

    /// <summary>Absent for the month (the "red" days).</summary>
    public int TotalAbsences { get; set; }

    /// <summary>
    /// Attendance percentage for the month: present / (present + absent), rounded to
    /// one decimal. Held and unmarked/upcoming occurrences are excluded from the
    /// denominator. REQ-ATT-077: "7 / 9 — 77%".
    /// </summary>
    public decimal AttendancePercentage { get; set; }

    /// <summary>
    /// Per-class-day calendar cells for the month — the scheduled occurrences overlaid
    /// with the student's status where a record exists. Drives the attendance calendar;
    /// ordered by date. REQ-ATT-065.
    /// </summary>
    public List<StudentAttendanceDayDto> Days { get; set; } = new();

    /// <summary>Individual attendance records for this month. REQ-ATT-075.</summary>
    public List<AttendanceRecordDto> Records { get; set; } = new();
}

/// <summary>
/// One calendar cell in the student attendance month view: a single scheduled class
/// day, overlaid with the student's attendance status where a record exists.
/// REQ-ATT-065 / REQ-ATT-075: color-coded occurrence dates.
/// </summary>
public class StudentAttendanceDayDto
{
    /// <summary>The class occurrence date (date-only).</summary>
    public DateTime Date { get; set; }

    /// <summary>The SessionOccurrence this cell maps to, when known.</summary>
    public long? SessionOccurrenceId { get; set; }

    /// <summary>The session this occurrence belongs to.</summary>
    public long? SessionId { get; set; }

    /// <summary>Display name of the session for this occurrence. BR-ATT-005.</summary>
    public string SessionName { get; set; } = null!;

    /// <summary>
    /// The student's attendance status for this day. Null means the class is
    /// scheduled but has no record yet (upcoming, or the teacher hasn't taken
    /// attendance) — render as a neutral cell, NOT as absent.
    /// </summary>
    public AttendanceStatus? Status { get; set; }

    /// <summary>
    /// True when the occurrence date is on or before the teacher's local today —
    /// i.e. the class has already happened. Future occurrences are false.
    /// </summary>
    public bool IsPast { get; set; }
}

/// <summary>
/// Output DTO for the Edit Attendance calendar view.
/// REQ-ATT-065: Color-coded occurrence dates.
/// </summary>
public class OccurrenceCalendarItemDto
{
    public long OccurrenceId { get; set; }
    public DateTime OccurrenceDate { get; set; }
    public OccurrenceStatus Status { get; set; }
    public int MarkedCount { get; set; }
    public int TotalStudents { get; set; }
}

/// <summary>
/// Output DTO for attendance edit log entry.
/// REQ-ATT-025: Audit trail display.
/// </summary>
public class AttendanceEditLogDto
{
    public long Id { get; set; }
    public AttendanceStatus PreviousStatus { get; set; }
    public AttendanceStatus NewStatus { get; set; }
    public DateTime EditedAt { get; set; }
    public long? EditedByUserId { get; set; }
    public string? EditReason { get; set; }
}

/// <summary>
/// FIX 4.1: Input DTO for placing a student on "hold" during attendance taking.
/// REQ-ATT-061: Held students are visually distinguished from marked and unmarked students.
/// REQ-ATT-058: "Hold" cancels the attendance action without recording anything.
/// </summary>
public class HoldStudentDto
{
    /// <summary>The owning teacher's Id.</summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>The session where attendance is being taken.</summary>
    [Required]
    public long SessionId { get; set; }

    /// <summary>The student to place on hold.</summary>
    [Required]
    public long TeacherStudentId { get; set; }

    /// <summary>The occurrence date. Defaults to today.</summary>
    public DateTime? OccurrenceDate { get; set; }

    /// <summary>The user performing the action (teacher or assistant).</summary>
    public long? RecordedByUserId { get; set; }
}

/// <summary>
/// FIX 4.1: Input DTO for releasing a held student.
/// REQ-ATT-061: Held students can be returned to and processed later in the same session.
/// </summary>
public class ReleaseHoldDto
{
    /// <summary>The owning teacher's Id.</summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>The session where the student is held.</summary>
    [Required]
    public long SessionId { get; set; }

    /// <summary>The held student to release.</summary>
    [Required]
    public long TeacherStudentId { get; set; }

    /// <summary>The occurrence date.</summary>
    public DateTime? OccurrenceDate { get; set; }

    /// <summary>
    /// True = mark as Present (confirm attendance).
    /// False = discard the hold (return to unmarked).
    /// </summary>
    [Required]
    public bool MarkAsPresent { get; set; }

    /// <summary>The user performing the action.</summary>
    public long? RecordedByUserId { get; set; }
}

// ══════════════════════════════════════════════
// EXPORT DTOs (FIX 4.2 — REQ-ATT-041/081)
// ══════════════════════════════════════════════

/// <summary>
/// FIX 4.2: Request DTO for exporting a student's attendance timeline.
/// REQ-ATT-081: Exportable as PDF or Excel covering a date range or full history.
/// </summary>
public class ExportTimelineRequest
{
    /// <summary>Optional start of export date range. Null = from first assignment.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>Optional end of export date range. Null = to current date.</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Export format: "xlsx" or "pdf".</summary>
    [Required]
    public string Format { get; set; } = "xlsx";
}

// ══════════════════════════════════════════════
// OFFLINE SYNC DTOs (FIX 4.3 — REQ-ATT-084/085)
// ══════════════════════════════════════════════

/// <summary>
/// FIX 4.3: A single offline-recorded attendance entry.
/// REQ-ATT-082: Records stored locally and synced when connectivity is restored.
/// </summary>
public class OfflineAttendanceEntryDto
{
    /// <summary>The student to mark attendance for.</summary>
    [Required]
    public long TeacherStudentId { get; set; }

    /// <summary>The session the attendance was taken for.</summary>
    [Required]
    public long SessionId { get; set; }

    /// <summary>The attendance status recorded offline.</summary>
    [Required]
    public AttendanceStatus Status { get; set; }

    /// <summary>The method used to record (ManualCode, MultiSelect, BarcodeScan).</summary>
    [Required]
    public AttendanceMethod AttendanceMethod { get; set; }

    /// <summary>The occurrence date for this attendance entry.</summary>
    [Required]
    public DateTime OccurrenceDate { get; set; }

    /// <summary>The client-side timestamp when the entry was recorded.</summary>
    [Required]
    public DateTime ClientRecordedAt { get; set; }

    /// <summary>Client-generated unique Id for conflict detection.</summary>
    [Required]
    public string ClientEntryId { get; set; } = null!;

    /// <summary>
    /// True when the tutor explicitly confirmed the absence alert at the time
    /// the entry was recorded offline (REQ-ATT-057/058 — the confirmation
    /// happened, just without connectivity). Defaults to false, preserving the
    /// audit stance for entries recorded without a confirmation.
    /// </summary>
    public bool AbsenceAlertConfirmed { get; set; } = false;
}

/// <summary>
/// FIX 4.3: Batch request for syncing offline attendance records.
/// REQ-ATT-084: Automatic sync when connectivity is restored.
/// </summary>
public class OfflineSyncRequestDto
{
    /// <summary>The owning teacher's Id.</summary>
    [Required]
    public long TeacherId { get; set; }

    /// <summary>The user who recorded the attendance offline.</summary>
    public long? RecordedByUserId { get; set; }

    /// <summary>The batch of offline entries to sync.</summary>
    [Required]
    public List<OfflineAttendanceEntryDto> Entries { get; set; } = new();
}

/// <summary>
/// FIX 4.3: Result of a sync operation for a single entry.
/// </summary>
public class SyncEntryResultDto
{
    /// <summary>The client-generated unique Id from the offline entry.</summary>
    public string ClientEntryId { get; set; } = null!;

    /// <summary>Whether this entry was successfully synced.</summary>
    public bool Success { get; set; }

    /// <summary>If not successful, whether this is a conflict requiring resolution.</summary>
    public bool IsConflict { get; set; }

    /// <summary>The server-side record if a conflict was detected.</summary>
    public AttendanceRecordDto? ServerRecord { get; set; }

    /// <summary>Error message if sync failed for non-conflict reasons.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Stable machine-readable reason this entry was not recorded — the message key behind
    /// <see cref="ErrorMessage"/>, or <c>NoOccurrenceOnDate</c> for the one rejection raised
    /// here rather than by the mark logic. The app shows the teacher WHY each student failed,
    /// and cannot present an English server sentence to an Arabic tutor; a code lets it pick
    /// its own wording. Null on success. Additive.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Audit Fix: True if this entry needs absence alert confirmation before sync.
    /// REQ-ATT-057/058: Explicit tutor confirmation required.
    /// </summary>
    public bool RequiresAbsenceConfirmation { get; set; }

    /// <summary>
    /// Audit Fix: Absence alert details for entries requiring confirmation.
    /// </summary>
    public AbsenceAlertStudentDto? AbsenceAlertInfo { get; set; }

    /// <summary>
    /// The entry WAS recorded (<see cref="Success"/> is true) and the student carries an absence
    /// history the tutor should follow up on — <see cref="AbsenceAlertInfo"/> carries the detail.
    /// INFORMATIONAL only: never a reason to hold the record back.
    ///
    /// A background replay has no human to answer a prompt, so the sync path records the mark and
    /// reports the alert afterwards instead of withholding it (which silently lost the mark and
    /// left the student absent — the same students every week, because a non-zero absence counter
    /// re-triggered it). The INTERACTIVE path (POST mark) is unchanged: it still asks first
    /// (REQ-ATT-057/058). Additive — older clients ignore unknown members and simply see a success.
    /// </summary>
    public bool AbsenceAlertRaised { get; set; }
}

/// <summary>
/// FIX 4.3: Batch result of the offline sync operation.
/// REQ-ATT-085: Presents conflicts for tutor resolution.
/// </summary>
public class SyncResultDto
{
    /// <summary>Total entries submitted for sync.</summary>
    public int TotalSubmitted { get; set; }

    /// <summary>Entries successfully synced without conflicts.</summary>
    public int SuccessCount { get; set; }

    /// <summary>Entries with conflicts requiring resolution.</summary>
    public int ConflictCount { get; set; }

    /// <summary>Entries that failed for non-conflict reasons.</summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Audit Fix: Number of entries that need absence alert confirmation.
    /// </summary>
    public int RequiresConfirmationCount { get; set; }

    /// <summary>Detailed result per entry.</summary>
    public List<SyncEntryResultDto> EntryResults { get; set; } = new();
}