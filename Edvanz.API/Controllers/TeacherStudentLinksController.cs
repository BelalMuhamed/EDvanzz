using Edvanz.API.Attributes;
using Edvanz.Application.Dtos.TeacherLinks;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Edvanz.API.Controllers;

/// <summary>
/// Teacher-side endpoints of the student-teacher link request/approval flow:
/// the shareable teacher code, the pending-requests inbox (accept/reject), the
/// linked-students list, and single/bulk removal.
///
/// AUTH &amp; TENANCY: class-level [Authorize] + per-action [ModulePermission]
/// against the Student module permissions. The acting teacherId is ALWAYS
/// derived from the JWT via ModuleSixApiBaseController.ResolveTeacherIdAsync —
/// never from the route or body (Catalog §1.3). Assistants resolve to their
/// owning tutor automatically.
///
/// The student-side half (sending/cancelling requests, viewing state) lives in
/// StudentUserController under api/studentuser/me/*.
/// </summary>
[Authorize]
[Route("api/teacher/student-links")]
public class TeacherStudentLinksController : ModuleSixApiBaseController
{
    private readonly ITeacherStudentLinkService _linkService;

    public TeacherStudentLinksController(
        ITeacherStudentLinkService linkService,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
        : base(currentUser, unitOfWork)
    {
        _linkService = linkService;
    }

    /// <summary>
    /// Returns the authenticated teacher's unique 8-digit code — the code a
    /// student types to send a link request. Safe to display in the app: a
    /// request created with it still requires this teacher's explicit approval.
    /// </summary>
    /// <response code="200">The teacher code.</response>
    /// <response code="404">Teacher record not found for the authenticated user.</response>
    [HttpGet("my-code")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewList)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherLinks.TeacherCodeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyCode()
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _linkService.GetMyTeacherCodeAsync(teacherId.Value);
        return ToResponse(result);
    }

    /// <summary>
    /// Pages the PENDING link requests sent to this teacher, newest first. Each
    /// item shows the name the student typed, their account identity (name,
    /// phone, account code) and — when the student supplied a student code — the
    /// matching roster record as a suggested binding for the accept call.
    /// </summary>
    /// <response code="200">Paginated pending requests.</response>
    [HttpGet("requests")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewList)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.TeacherLinks.TeacherLinkRequestListItemDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPendingRequests(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _linkService.GetPendingLinkRequestsAsync(teacherId.Value, page, pageSize);
        return ToResponse(result);
    }

    /// <summary>
    /// Accepts a pending request and binds it to one of this teacher's
    /// TeacherStudent roster records. Pass <c>teacherStudentId</c> to select the
    /// record explicitly; omit it to auto-match by the student-typed code. Every
    /// Active link must be bound to a roster record — attendance, payments,
    /// homework, exams and videos all resolve through that reference.
    /// </summary>
    /// <response code="200">Accepted; returns the new linked-student list item.</response>
    /// <response code="404">Request not found, or selected roster record not found.</response>
    /// <response code="409">Request already resolved, or roster record already claimed by another account.</response>
    /// <response code="422">No roster record could be resolved — selection required.</response>
    [HttpPost("requests/{linkId:long}/accept")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionAdd)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherLinks.LinkedStudentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AcceptRequest(
        [FromRoute] long linkId, [FromBody] AcceptLinkRequestDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _linkService.AcceptLinkRequestAsync(
            teacherId.Value, linkId, GetActingUserId(), dto);
        return ToResponse(result);
    }

    /// <summary>
    /// Links (or re-links) an accepted connection to one of the teacher's students —
    /// the separate step that unlocks the student's access. Bind by explicit
    /// <c>teacherStudentId</c> or by <c>studentCode</c>. Use the <c>linkId</c> from
    /// the requests inbox or the linked-students list. Requires <c>Student / Edit</c>.
    /// </summary>
    /// <response code="200">Linked; returns the updated linked-student row (IsLinked = true).</response>
    /// <response code="400">No bind target supplied.</response>
    /// <response code="404">Link, or the selected student record, not found.</response>
    /// <response code="409">Link is not Active, or the record is already linked to another account.</response>
    [HttpPost("{linkId:long}/bind")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionEdit)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherLinks.LinkedStudentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> BindStudent(
        [FromRoute] long linkId, [FromBody] BindStudentLinkDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _linkService.BindStudentLinkAsync(
            teacherId.Value, linkId, GetActingUserId(), dto);
        return ToResponse(result);
    }

    /// <summary>
    /// SuperAdmin-only: lists a teacher's Active links that are NOT yet bound to any
    /// roster record — the pool of connected student accounts available to attach to
    /// a Student Management row. Pass the returned LinkId to <see cref="BindStudentForAdmin"/>
    /// with the target <c>teacherStudentId</c> to complete the link.
    /// </summary>
    /// <response code="200">Unbound Active links for the teacher (possibly empty).</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller is not a SuperAdmin.</response>
    /// <response code="404">Teacher not found.</response>
    [HttpGet("admin/teachers/{teacherId:long}/unbound-links")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.List<Edvanz.Application.Dtos.TeacherLinks.LinkedStudentListItemDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUnboundActiveLinksForAdmin([FromRoute] long teacherId)
    {
        var result = await _linkService.GetUnboundActiveLinksForAdminAsync(teacherId);
        return ToResponse(result);
    }

    /// <summary>
    /// Admin variant of <see cref="BindStudent"/> — links (or re-links) an accepted
    /// connection to one of the owning teacher's students, with no tenant scope.
    /// SuperAdmin only.
    /// </summary>
    /// <remarks>
    /// The linkId already fully identifies the owning teacher, so no TeacherId is
    /// supplied or guessed — it is resolved from the link row itself, then the
    /// identical bind/re-point/claim-check logic runs.
    /// AUTH: SuperAdmin role only (roleOnly gate).
    /// </remarks>
    /// <response code="200">Linked; returns the updated linked-student row (IsLinked = true).</response>
    /// <response code="400">No bind target supplied.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller is not a SuperAdmin.</response>
    /// <response code="404">Link, or the selected student record, not found.</response>
    /// <response code="409">Link is not Active, or the record is already linked to another account.</response>
    [HttpPost("admin/{linkId:long}/bind")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherLinks.LinkedStudentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> BindStudentForAdmin(
        [FromRoute] long linkId, [FromBody] BindStudentLinkDto dto)
    {
        var result = await _linkService.BindStudentLinkForAdminAsync(linkId, GetActingUserId(), dto);
        return ToResponse(result);
    }

    /// <summary>
    /// Admin variant of <see cref="UnbindStudent"/> — removes the student-record
    /// binding from an accepted link, with no tenant scope. SuperAdmin only.
    /// </summary>
    /// <remarks>
    /// The owning teacher is resolved from the link row itself. AUTH: SuperAdmin
    /// role only (roleOnly gate).
    /// </remarks>
    /// <response code="200">Unlinked; returns the updated row (now Not linked).</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller is not a SuperAdmin.</response>
    /// <response code="404">Link not found.</response>
    /// <response code="409">Link is not Active.</response>
    [HttpPost("admin/{linkId:long}/unbind")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherLinks.LinkedStudentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UnbindStudentForAdmin([FromRoute] long linkId)
    {
        var result = await _linkService.UnbindStudentLinkForAdminAsync(linkId, GetActingUserId());
        return ToResponse(result);
    }

    /// <summary>
    /// Removes the student-record binding from an accepted link. The student stays
    /// connected (Accepted) but loses access until re-linked. Requires <c>Student / Edit</c>.
    /// </summary>
    /// <response code="200">Unlinked; returns the updated row (now Not linked).</response>
    /// <response code="404">Link not found.</response>
    /// <response code="409">Link is not Active.</response>
    [HttpPost("{linkId:long}/unbind")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionEdit)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherLinks.LinkedStudentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UnbindStudent([FromRoute] long linkId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _linkService.UnbindStudentLinkAsync(
            teacherId.Value, linkId, GetActingUserId());
        return ToResponse(result);
    }

    /// <summary>
    /// Device lock: resets (clears) a linked student's registered device so they can open the
    /// teacher from a new phone. The next time the student opens the teacher they are asked to
    /// register the new device. Idempotent. Requires <c>Student / Edit</c>.
    /// </summary>
    /// <response code="200">Device reset; returns the updated linked-student row.</response>
    /// <response code="400">Invalid link id.</response>
    /// <response code="404">Link not found for this teacher.</response>
    [HttpPost("{linkId:long}/reset-device")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionEdit)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherLinks.LinkedStudentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetStudentDevice([FromRoute] long linkId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _linkService.ResetStudentDeviceAsync(
            teacherId.Value, linkId, GetActingUserId());
        return ToResponse(result);
    }

    /// <summary>
    /// Rejects a pending request. The row is kept (status Rejected) so the
    /// student sees the outcome; they may send a new request later.
    /// </summary>
    /// <response code="200">Rejected.</response>
    /// <response code="404">Request not found.</response>
    /// <response code="409">Request already resolved.</response>
    [HttpPost("requests/{linkId:long}/reject")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionAdd)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RejectRequest([FromRoute] long linkId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _linkService.RejectLinkRequestAsync(
            teacherId.Value, linkId, GetActingUserId());
        return ToResponse(result);
    }

    /// <summary>
    /// Pages this teacher's linked students (Active links), newest first —
    /// account identity plus the bound roster record for each.
    /// </summary>
    /// <response code="200">Paginated linked students.</response>
    [HttpGet]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewList)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherLinks.LinkedStudentsPageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLinkedStudents(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        // Optional filter over the student's account name/code and bound roster name/code.
        [FromQuery] string? search = null,
        // All (default) | Linked | NotLinked. Narrows the returned page only — the response's
        // allCount/linkedCount/unlinkedCount stay counted over the whole searched set so the
        // client's filter chips keep showing what is on the tab the user is NOT looking at.
        [FromQuery] LinkedStudentFilter filter = LinkedStudentFilter.All)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _linkService.GetLinkedStudentsAsync(
            teacherId.Value, page, pageSize, search, filter);
        return ToResponse(result);
    }

    /// <summary>
    /// Removes one or many linked students (soft — LinkStatus becomes
    /// RemovedByTeacher; the student is notified). Ids not owned by this teacher
    /// or not currently Active are skipped and echoed back in the result.
    /// </summary>
    /// <response code="200">Removal outcome: removed count + skipped ids.</response>
    /// <response code="400">Empty selection.</response>
    /// <response code="404">None of the ids matched an Active link of this teacher.</response>
    [HttpPost("remove")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionDelete)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherLinks.RemoveLinkedStudentsResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveLinkedStudents([FromBody] RemoveLinkedStudentsDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _linkService.RemoveLinkedStudentsAsync(teacherId.Value, GetActingUserId(), dto);
        return ToResponse(result);
    }
}