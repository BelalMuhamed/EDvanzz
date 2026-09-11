using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Exams;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Application contract for the offline Exams module (the clean <c>/api/exams</c> surface).
/// Distinct from the shared <see cref="IExamHomeworkService"/>, which still serves homework and
/// the legacy assignment routes. All methods return <see cref="Result{T}"/> with a stable
/// <c>Code</c> and a localized message; teacherId/actingUserId are resolved from the JWT.
/// </summary>
public interface IExamService
{
    /// <summary>
    /// Creates an offline exam. Builds ONE occurrence per anchored session — DuringSession links
    /// EACH resolved session (group members included) to its own picked scheduled
    /// <c>SessionOccurrence</c> (its date + attendance drive the exam); SeparateTime applies the
    /// single exam date — and creates a Pending obligation for each session's students.
    /// </summary>
    Task<Result<ExamCreatedDto>> CreateExamAsync(long teacherId, long actingUserId, CreateExamDto dto);

    /// <summary>
    /// Edits an existing exam. Metadata (name, notes, grade bounds) is always editable; STRUCTURAL
    /// fields (delivery type, the per-session occurrence picks / separate date, and the assigned
    /// sessions/groups/students) rebuild the exam graph and are rejected once any attendance or
    /// grade exists (<c>ExamHasResultsCannotRestructure</c>). Scoped to exam templates
    /// (<c>AssignmentType.Exam</c>); homework returns <c>ExamNotFound</c>. Returns the refreshed
    /// opened-exam view.
    /// </summary>
    Task<Result<ExamViewDto>> UpdateExamAsync(long teacherId, long actingUserId, long examId, UpdateExamDto dto);

    /// <summary>
    /// Lists a session's scheduled occurrences within the given month, for the "during session"
    /// exam-date picker.
    /// </summary>
    Task<Result<List<SessionExamDateDto>>> GetSessionExamDatesAsync(
        long teacherId, long sessionId, int year, int month);

    /// <summary>
    /// Exam Home: every exam occurrence split into upcoming (date not yet reached) and past
    /// (date passed), each carrying delivery type, session, date, and assigned/attended/missed counts.
    /// </summary>
    Task<Result<ExamHomeDto>> GetExamHomeAsync(long teacherId, int upcomingPage, int pastPage, int pageSize);

    /// <summary>
    /// The opened-exam view: header + global statistics + per-session groups (each session with its
    /// own statistics and student roster). One call renders the whole screen.
    /// </summary>
    Task<Result<ExamViewDto>> GetExamViewAsync(long teacherId, long examId);

    /// <summary>
    /// Paged student roster for a single session within an exam (drill-in / large-session view).
    /// <paramref name="graded"/> narrows to students who already have a grade (true) or are
    /// still waiting for one (false); null returns both. It filters the SERVER page so the
    /// grade screen's chips stay honest past page 1.
    /// </summary>
    Task<Result<ExamSessionRosterDto>> GetExamSessionRosterAsync(
        long teacherId, long examId, long sessionId, int page, int pageSize, string? search,
        bool? graded);

    /// <summary>
    /// Saves a batch of distinct per-student grades in one transaction. Enforces 0 ≤ grade ≤ the
    /// exam's max (code <c>GradeExceedsMax</c>), rejects grading an absent student, and enforces the
    /// during-session grades-only guard (a during-session student not yet marked attended cannot be
    /// graded from the exam screen). A <c>null</c> grade clears a previously entered grade (reverting
    /// AttendedWithGrade back to Attended, attendance preserved). Valid rows are applied; invalid rows
    /// are reported per-item.
    /// </summary>
    Task<Result<BatchGradeResultDto>> SaveGradesAsync(long teacherId, long actingUserId, BatchGradeDto dto);

    /// <summary>
    /// Batch manual attendance (present/absent) for a SEPARATE-time exam occurrence. Rejected for
    /// during-session exams (their attendance comes from the session — <c>AttendanceReadOnlyForDuringSession</c>).
    /// Present keeps any entered grade; Absent clears it.
    /// </summary>
    Task<Result<ExamAttendanceResultDto>> MarkExamAttendanceAsync(
        long teacherId, long actingUserId, ExamAttendanceDto dto);

    /// <summary>
    /// Scan a student's QR/code to mark them attended for a SEPARATE-time exam occurrence (idempotent).
    /// Rejected for during-session exams.
    /// </summary>
    Task<Result<ExamScanResultDto>> ScanExamAttendanceAsync(
        long teacherId, long actingUserId, ExamScanDto dto);

    /// <summary>
    /// Permanently deletes an exam: the template, all its per-session occurrences, every student
    /// obligation, and the grade/attendance audit history — after archiving a JSON snapshot into
    /// <c>AssignmentDeletionLogs</c> (REQ-EXH-037 semantics, delegated to the shared template
    /// deletion flow). Scoped to <c>AssignmentType.Exam</c>: homework templates return
    /// <c>ExamNotFound</c> from this surface. <paramref name="confirm"/> must be true
    /// (<c>DeletionConfirmationRequired</c> otherwise) — there is no recovery.
    /// </summary>
    Task<Result<bool>> DeleteExamAsync(long teacherId, long actingUserId, long examId, bool confirm);
}
