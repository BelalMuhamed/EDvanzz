using Edvanz.API.Attributes;
using Edvanz.Application.Dtos.Exams;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Edvanz.API.Controllers;

/// <summary>
/// Clean, resource-oriented API for the offline Exams module (<c>/api/exams</c>).
///
/// AUTHORIZATION: every endpoint is <c>[Authorize]</c>; <c>teacherId</c> is resolved from the JWT
/// via <see cref="ModuleSixApiBaseController.ResolveTeacherIdAsync"/> (never from the body/route).
/// Permissions reuse the existing <c>"Exams And Homework"</c> module claims.
/// </summary>
[Authorize]
[Route("api/exams")]
public class ExamsController : ModuleSixApiBaseController
{
    private readonly IExamService _exams;

    public ExamsController(
        IExamService exams,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
        : base(currentUser, unitOfWork)
    {
        _exams = exams;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CREATE EXAM
    // POST /api/exams
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost]
    [ModulePermission("Exams And Homework", "ManageAssignments")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Exams.ExamCreatedDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateExam([FromBody] CreateExamDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _exams.CreateExamAsync(teacherId.Value, GetActingUserId(), dto));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // UPDATE EXAM — resubmit of the create fields
    // PUT /api/exams/{examId}
    // Metadata (name, notes, grade bounds) always editable; structural fields
    // (delivery type, per-session occurrence picks / separate date, sessions/
    // groups/students) rebuild the exam and 409 once results exist. Homework
    // templates 404 here (this surface owns exams only).
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("{examId:long}")]
    [ModulePermission("Exams And Homework", "ManageAssignments")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Exams.ExamViewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateExam(
        [FromRoute] long examId, [FromBody] UpdateExamDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _exams.UpdateExamAsync(teacherId.Value, GetActingUserId(), examId, dto));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // SESSION EXAM-DATE PICKER (during-session)
    // GET /api/exams/session-dates?sessionId=&year=&month=
    // Returns the session's scheduled occurrences in the month. Called once PER resolved
    // session: the picked sessionOccurrenceId goes into create/update sessionOccurrences[].
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("session-dates")]
    [ModulePermission("Exams And Homework", "View")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.List<Edvanz.Application.Dtos.Exams.SessionExamDateDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSessionDates(
        [FromQuery] long sessionId, [FromQuery] int year, [FromQuery] int month)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _exams.GetSessionExamDatesAsync(teacherId.Value, sessionId, year, month));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // EXAM HOME — upcoming / past
    // GET /api/exams/home
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("home")]
    [ModulePermission("Exams And Homework", "View")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Exams.ExamHomeDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHome(
        [FromQuery] int upcomingPage = 1,
        [FromQuery] int pastPage = 1,
        [FromQuery] int pageSize = 20)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _exams.GetExamHomeAsync(teacherId.Value, upcomingPage, pastPage, pageSize));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // OPENED EXAM — grouped by session, with per-session + global statistics
    // GET /api/exams/{examId}
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("{examId:long}")]
    [ModulePermission("Exams And Homework", "View")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Exams.ExamViewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetExam([FromRoute] long examId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _exams.GetExamViewAsync(teacherId.Value, examId));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // OPENED EXAM — one session's roster, paged (drill-in / large sessions)
    // GET /api/exams/{examId}/sessions/{sessionId}?page=&pageSize=&search=&graded=
    // `graded` (optional): true = only students who already have a grade, false =
    // only those still waiting for one, omitted = both. Server-side on purpose —
    // the grade screen's chips must narrow the page, not just what is loaded.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("{examId:long}/sessions/{sessionId:long}")]
    [ModulePermission("Exams And Homework", "View")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Exams.ExamSessionRosterDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetExamSession(
        [FromRoute] long examId,
        [FromRoute] long sessionId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] bool? graded = null)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _exams.GetExamSessionRosterAsync(
            teacherId.Value, examId, sessionId, page, pageSize, search, graded));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // SAVE GRADES — batch of distinct per-student grades ("Saved (N) changes")
    // PUT /api/exams/grades
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("grades")]
    [ModulePermission("Exams And Homework", "RecordExamAttendanceAndGrades")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Exams.BatchGradeResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SaveGrades([FromBody] BatchGradeDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _exams.SaveGradesAsync(teacherId.Value, GetActingUserId(), dto));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // TAKE ATTENDANCE (separate-time exams) — batch present/absent
    // PUT /api/exams/attendance
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("attendance")]
    [ModulePermission("Exams And Homework", "RecordExamAttendanceAndGrades")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Exams.ExamAttendanceResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MarkAttendance([FromBody] ExamAttendanceDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _exams.MarkExamAttendanceAsync(teacherId.Value, GetActingUserId(), dto));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // SCAN ATTENDANCE (separate-time exams) — QR/code → mark attended (idempotent)
    // POST /api/exams/attendance/scan
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("attendance/scan")]
    [ModulePermission("Exams And Homework", "RecordExamAttendanceAndGrades")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Exams.ExamScanResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ScanAttendance([FromBody] ExamScanDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _exams.ScanExamAttendanceAsync(teacherId.Value, GetActingUserId(), dto));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DELETE EXAM (permanent — REQ-EXH-037)
    // DELETE /api/exams/{examId}?confirm=true
    // Hard-deletes the exam template, its per-session occurrences, all student
    // obligations and grade/attendance audit rows, after archiving a JSON snapshot
    // into AssignmentDeletionLogs. `confirm=true` is required (the UI shows the
    // confirmation dialog; the API enforces it). Homework templates are NOT
    // deletable here — they 404, this surface owns exams only.
    // Success is 200 + envelope code "ExamDeleted" (not 204: the exams frontend
    // contract branches on the body `code`).
    // ══════════════════════════════════════════════════════════════════════════
    [HttpDelete("{examId:long}")]
    [ModulePermission("Exams And Homework", "ManageAssignments")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteExam(
        [FromRoute] long examId, [FromQuery] bool confirm = false)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _exams.DeleteExamAsync(teacherId.Value, GetActingUserId(), examId, confirm));
    }
}
