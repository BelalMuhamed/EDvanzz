using Edvanz.API.Attributes;
using Edvanz.Application.Dtos;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Edvanz.API.Controllers;

/// <summary>
/// Online Exam Module — teacher/assistant endpoints (T1–T14).
/// TeacherId always via ResolveTeacherIdAsync() — never route/body (catalog §1.3).
///
/// PERMISSIONS: read endpoints require <see cref="OnlineExamConstants.PermissionView"/>;
/// every create/edit/publish/delete endpoint requires
/// <see cref="OnlineExamConstants.PermissionManageExams"/>. Both are seeded under the
/// <see cref="OnlineExamConstants.ModuleName"/> module — see
/// SeedOnlineExamModuleAndPermissions migration.
/// </summary>
[Route("api/online-exams")]
[Authorize]
public sealed class OnlineExamsController : ModuleSixApiBaseController
{
    private readonly IOnlineExamService _service;

    public OnlineExamsController(
        IOnlineExamService service, ICurrentUserService currentUser, IUnitOfWork unitOfWork)
        : base(currentUser, unitOfWork)
    {
        _service = service;
    }

    /// <summary>
    /// T1 — Creates a new online exam in <c>Draft</c> status. Questions and scopes may be
    /// supplied at creation time or added later while still in Draft.
    /// </summary>
    /// <param name="request">Exam metadata, scope targets, and optional initial questions.</param>
    /// <response code="201">Exam created.</response>
    /// <response code="400">Validation failed (dates, pass percentage, question rules, scope ownership).</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.ManageExams</c> permission.</response>
    [HttpPost]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionManageExams)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.OnlineExamDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateOnlineExamRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.CreateAsync(teacherId.Value, GetActingUserId(), request));
    }

    /// <summary>
    /// T8 — Edits an exam's metadata (title, description, dates, pass percentage, visibility).
    /// Does not touch questions — use the questions endpoints for that. Requires the current
    /// <c>RowVersion</c> for optimistic concurrency.
    /// </summary>
    /// <param name="onlineExamId">The exam to edit.</param>
    /// <param name="request">Updated metadata including the RowVersion read from the last GET.</param>
    /// <response code="200">Exam updated.</response>
    /// <response code="400">Validation failed.</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="409">RowVersion is stale — someone else edited this exam first.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.ManageExams</c> permission.</response>
    [HttpPut("{onlineExamId:long}")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionManageExams)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.OnlineExamDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update([FromRoute] long onlineExamId, [FromBody] UpdateOnlineExamRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.UpdateAsync(teacherId.Value, GetActingUserId(), onlineExamId, request));
    }

    /// <summary>T9 — Fetches a single exam's edit-screen detail (metadata + scopes, no questions).</summary>
    /// <param name="onlineExamId">The exam to fetch.</param>
    /// <response code="200">Exam detail returned.</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.View</c> permission.</response>
    [HttpGet("{onlineExamId:long}")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.OnlineExamDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetById([FromRoute] long onlineExamId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.GetByIdAsync(teacherId.Value, onlineExamId));
    }

    /// <summary>T4 — Lists the tenant's exams, optionally filtered by status/title, paged, with per-exam outcome counts.</summary>
    /// <param name="request">Status filter, search text, and paging.</param>
    /// <response code="200">Paged exam list returned.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.View</c> permission.</response>
    [HttpGet]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.OnlineExamListItemDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetList([FromQuery] OnlineExamListRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.GetListAsync(teacherId.Value, request));
    }

    /// <summary>T6 — Exam-level analytics: exam/pass grade, assigned count, average/high/low percentage, and outcome totals per scope.</summary>
    /// <param name="onlineExamId">The exam to summarize.</param>
    /// <response code="200">Overview returned.</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.View</c> permission.</response>
    [HttpGet("{onlineExamId:long}/overview")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.OnlineExamOverviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOverview([FromRoute] long onlineExamId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.GetOverviewAsync(teacherId.Value, onlineExamId));
    }

    /// <summary>
    /// T7 — Per-student result grid for the exam. Includes students who submitted and were
    /// later unassigned (flagged <c>isOutOfScope=true</c>) rather than silently dropping them.
    /// </summary>
    /// <param name="onlineExamId">The exam to analyze.</param>
    /// <response code="200">Grid returned.</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.View</c> permission.</response>
    [HttpGet("{onlineExamId:long}/scope-analysis")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.List<Edvanz.Application.Dtos.OnlineExamScopeAnalysisRowDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetScopeAnalysis([FromRoute] long onlineExamId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.GetScopeAnalysisAsync(teacherId.Value, onlineExamId));
    }

    /// <summary>T12 — Fetches all questions and options (including <c>isCorrect</c>) for the edit screen.</summary>
    /// <param name="onlineExamId">The exam whose questions are requested.</param>
    /// <response code="200">Questions returned.</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.View</c> permission.</response>
    [HttpGet("{onlineExamId:long}/questions")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.List<Edvanz.Domain.Interfaces.OnlineExamQuestionRow>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetQuestions([FromRoute] long onlineExamId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.GetQuestionsAsync(teacherId.Value, onlineExamId));
    }

    /// <summary>
    /// T15 — Per-question difficulty across finalized attempts, hardest first, each with the wrong
    /// option the class picked most. The exam analytics could say the class averaged 62% but never
    /// which questions caused it.
    /// </summary>
    /// <param name="onlineExamId">The exam to analyze.</param>
    /// <response code="200">Analysis returned. Empty question list when nobody has submitted yet.</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.View</c> permission.</response>
    [HttpGet("{onlineExamId:long}/question-analysis")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.OnlineExamQuestionAnalysisDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetQuestionAnalysis([FromRoute] long onlineExamId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.GetQuestionAnalysisAsync(teacherId.Value, onlineExamId));
    }

    /// <summary>
    /// T16 — One student's answer sheet. Same shape the student's own review returns, with correct
    /// answers always marked (the teacher owns the exam) and the student's selections overlaid.
    /// Previously the teacher could see a score and nothing behind it.
    /// </summary>
    /// <param name="onlineExamId">The exam.</param>
    /// <param name="teacherStudentId">The student whose sheet is requested.</param>
    /// <response code="200">Answer sheet returned.</response>
    /// <response code="404">Exam not found, or the student has no attempt at it.</response>
    /// <response code="403">The student does not belong to the caller's tenant, or the caller lacks <c>OnlineExam.View</c>.</response>
    /// <response code="401">Caller is not authenticated.</response>
    [HttpGet("{onlineExamId:long}/students/{teacherStudentId:long}/answers")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.OnlineExamReviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStudentAnswerSheet(
        [FromRoute] long onlineExamId, [FromRoute] long teacherStudentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.GetStudentAnswerSheetAsync(
            teacherId.Value, onlineExamId, teacherStudentId));
    }

    /// <summary>T10 — Lightweight question-type counts (single-choice / multiple-choice) plus current status.</summary>
    /// <param name="onlineExamId">The exam to summarize.</param>
    /// <response code="200">Counts returned.</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.View</c> permission.</response>
    [HttpGet("{onlineExamId:long}/questions/overview")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionView)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.OnlineExamQuestionsOverviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetQuestionsOverview([FromRoute] long onlineExamId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.GetQuestionsOverviewAsync(teacherId.Value, onlineExamId));
    }

    /// <summary>T2 — Appends a single question to a Draft exam.</summary>
    /// <param name="onlineExamId">The exam to add the question to.</param>
    /// <param name="request">The question text, type, degree, and options.</param>
    /// <response code="200">Question added.</response>
    /// <response code="400">Validation failed (option-count/correct-answer rules per question type).</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="409">Exam is not in Draft status — questions are Draft-only.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.ManageExams</c> permission.</response>
    [HttpPost("{onlineExamId:long}/questions")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionManageExams)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AddQuestion([FromRoute] long onlineExamId, [FromBody] CreateOnlineExamQuestionDto request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.AddQuestionAsync(teacherId.Value, onlineExamId, request));
    }

    /// <summary>T3 — Appends multiple questions to a Draft exam in one call.</summary>
    /// <param name="onlineExamId">The exam to add questions to.</param>
    /// <param name="request">The questions to append.</param>
    /// <response code="200">Questions added.</response>
    /// <response code="400">Validation failed on one or more questions.</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="409">Exam is not in Draft status — questions are Draft-only.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.ManageExams</c> permission.</response>
    [HttpPost("{onlineExamId:long}/questions/bulk")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionManageExams)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AddQuestionsBulk([FromRoute] long onlineExamId, [FromBody] List<Application.Dtos.CreateOnlineExamQuestionDto> request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.AddQuestionsBulkAsync(teacherId.Value, onlineExamId, request));
    }

    /// <summary>T13 — Replaces the exam's entire question set. Draft-only, same as every other question mutation.</summary>
    /// <param name="onlineExamId">The exam whose questions are being replaced.</param>
    /// <param name="request">The full new question set.</param>
    /// <response code="200">Questions replaced.</response>
    /// <response code="400">Validation failed on one or more questions.</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="409">Exam is not in Draft status — questions are Draft-only.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.ManageExams</c> permission.</response>
    [HttpPut("{onlineExamId:long}/questions")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionManageExams)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ReplaceQuestions([FromRoute] long onlineExamId, [FromBody] ReplaceOnlineExamQuestionsRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.ReplaceQuestionsAsync(teacherId.Value, onlineExamId, request));
    }

    /// <summary>
    /// T11 — Transitions exam status (Draft→Published→Closed). Publishing validates at least
    /// one question, one scope, and a positive total degree; no report rows are created here
    /// (lazy provisioning — §3.6). Requires the current <c>RowVersion</c>.
    /// </summary>
    /// <param name="onlineExamId">The exam to transition.</param>
    /// <param name="request">Target status and the RowVersion read from the last GET.</param>
    /// <response code="200">Status updated.</response>
    /// <response code="400">Publish gate failed (missing question/scope/degree) or an invalid transition was requested.</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="409">RowVersion is stale, or students have already submitted (blocks unpublish).</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.ManageExams</c> permission.</response>
    [HttpPatch("{onlineExamId:long}/status")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionManageExams)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateStatus([FromRoute] long onlineExamId, [FromBody] UpdateOnlineExamStatusRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.UpdateStatusAsync(teacherId.Value, onlineExamId, request));
    }

    /// <summary>T14 — Hard-deletes the exam and its full graph (leaf-first: answers → options → questions → scopes → exam).</summary>
    /// <param name="onlineExamId">The exam to delete.</param>
    /// <response code="200">Exam deleted (standard success envelope — OE-2; was a 204 empty body).</response>
    /// <response code="404">Exam not found or not owned by the caller's tenant.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.ManageExams</c> permission.</response>
    [HttpDelete("{onlineExamId:long}")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionManageExams)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete([FromRoute] long onlineExamId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.DeleteAsync(teacherId.Value, onlineExamId));
    }

    /// <summary>
    /// T5s — Manually sets a single student's report status (currently: Blocked). Creates the
    /// report row lazily if the student has no report yet. Returns the same stats DTO shape as
    /// the student-facing submit/result endpoints.
    /// </summary>
    /// <param name="onlineExamId">The exam.</param>
    /// <param name="teacherStudentId">The student whose report status is being set.</param>
    /// <param name="request">The target status.</param>
    /// <remarks>
    /// Who performed the change is taken from the JWT (never the body) and stamped on the
    /// report — assistants can hold this permission, so an unblock on a live exam has to be
    /// attributable after the fact.
    /// </remarks>
    /// <response code="200">Status updated; current stats returned.</response>
    /// <response code="400">The requested status is not allowed to be set manually.</response>
    /// <response code="404">Exam or student not found, or student not owned by the caller's tenant.</response>
    /// <response code="401">Caller is not authenticated.</response>
    /// <response code="403">Caller lacks the <c>OnlineExam.ManageExams</c> permission.</response>
    [HttpPatch("{onlineExamId:long}/students/{teacherStudentId:long}/status")]
    [ModulePermission(OnlineExamConstants.ModuleName, OnlineExamConstants.PermissionManageExams)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.OnlineExamStatsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateStudentStatus(
        [FromRoute] long onlineExamId, [FromRoute] long teacherStudentId,
        [FromBody] UpdateOnlineExamStudentStatusRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();
        return ToResponse(await _service.UpdateStudentStatusAsync(
            teacherId.Value, GetActingUserId(), onlineExamId, teacherStudentId, request));
    }
}