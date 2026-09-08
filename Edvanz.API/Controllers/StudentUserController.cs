using System.Net;
using Edvanz.API.Attributes;
using Edvanz.API.Common;
using Edvanz.Application.Common;
using Edvanz.Application.Dtos.StudentUser;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Edvanz.API.Controllers;

/// <summary>
/// API endpoints for the Student User module (request/approval linking design).
///
/// IDENTITY: every "me" endpoint resolves the calling StudentUser from the JWT
/// (User.Id → StudentUsers.UserId) — the studentUserId is NEVER read from the
/// route or body (same IDOR-safe pattern as StudentAttendanceController /
/// StudentVideosController; the old route-id endpoints of this controller were
/// the last violators and have been removed).
///
/// LINKING: the student sends a link REQUEST (teacher code + own name + optional
/// student code); the teacher accepts or rejects it from the teacher app
/// (TeacherStudentLinksController). GET me/teachers surfaces the request state
/// (Pending/Active/Rejected/...) so the student always knows the outcome, and a
/// push notification is sent on accept/reject.
///
/// NOTE: StudentUser initialization is performed internally by the User module
/// during registration (UserService → IStudentUserService.InitializeStudentUserAsync);
/// it is intentionally no longer exposed as an HTTP endpoint.
///
/// All responses follow the unified JSON shape; messages localize via the
/// Accept-Language header ("ar" / "en").
/// </summary>
[Authorize]
public class StudentUserController : ApiBaseController
{
    private readonly IStudentUserService _studentUserService;
    private readonly IStudentTeacherHomeService _homeService;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;

    public StudentUserController(
        IStudentUserService studentUserService,
        IStudentTeacherHomeService homeService,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
    {
        _studentUserService = studentUserService;
        _homeService = homeService;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ME: PROFILE
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Returns the authenticated student's own profile.</summary>
    /// <response code="200">Profile including account code and linked-teacher count.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="404">The authenticated user has no student account.</response>
    [HttpGet("me")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.StudentUser.StudentUserProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyProfile()
    {
        var studentUserId = await ResolveStudentUserIdAsync();
        if (studentUserId is null) return StudentNotResolved();

        var result = await _studentUserService.GetStudentUserProfileAsync(studentUserId.Value);
        return ToResponse(result);
    }

    /// <summary>Updates the authenticated student's own profile settings (language preference).</summary>
    /// <response code="200">Updated profile.</response>
    /// <response code="400">Invalid language preference.</response>
    [HttpPut("me/profile")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.StudentUser.StudentUserProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMyProfile([FromBody] UpdateStudentUserProfileDto dto)
    {
        var studentUserId = await ResolveStudentUserIdAsync();
        if (studentUserId is null) return StudentNotResolved();

        var result = await _studentUserService.UpdateStudentUserProfileAsync(studentUserId.Value, dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ME: DASHBOARD + TEACHERS
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the authenticated student's dashboard: first-login flag, account
    /// code, and one entry per teacher with the CURRENT link state
    /// (Pending / Active / Rejected / Unlinked / RemovedByTeacher / CancelledByStudent)
    /// plus the teacher's visibility flags (AAM-FR-05.8).
    /// </summary>
    /// <response code="200">Dashboard payload.</response>
    [HttpGet("me/dashboard")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.StudentUser.StudentDashboardDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyDashboard()
    {
        var studentUserId = await ResolveStudentUserIdAsync();
        if (studentUserId is null) return StudentNotResolved();

        var result = await _studentUserService.GetDashboardAsync(studentUserId.Value);
        return ToResponse(result);
    }

    /// <summary>
    /// Returns the authenticated student's teachers including request states —
    /// this is how the student sees whether a request is still Pending, was
    /// accepted (Active), or was rejected (Rejected).
    /// </summary>
    /// <response code="200">One entry per teacher (latest state), live entries first.</response>
    [HttpGet("me/teachers")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.List<Edvanz.Application.Dtos.StudentUser.StudentDashboardTeacherDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyTeachers()
    {
        var studentUserId = await ResolveStudentUserIdAsync();
        if (studentUserId is null) return StudentNotResolved();

        var result = await _studentUserService.GetMyTeachersAsync(studentUserId.Value);
        return ToResponse(result);
    }

    /// <summary>
    /// Sends a link REQUEST to a teacher. The student supplies the teacher's
    /// 8-digit code and their own name; the student code the teacher assigned
    /// them is optional and only helps the teacher match the request to a roster
    /// record. The link becomes usable ONLY after the teacher accepts.
    /// </summary>
    /// <response code="201">Request created (entry returned with status Pending).</response>
    /// <response code="400">Invalid teacher code or missing student name.</response>
    /// <response code="404">No active teacher with this code.</response>
    /// <response code="409">Already linked to this teacher, or a request is already pending.</response>
    [HttpPost("me/link-requests")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.StudentUser.StudentDashboardTeacherDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateLinkRequest([FromBody] CreateLinkRequestDto dto)
    {
        var studentUserId = await ResolveStudentUserIdAsync();
        if (studentUserId is null) return StudentNotResolved();

        var result = await _studentUserService.CreateLinkRequestAsync(studentUserId.Value, dto);
        return ToResponse(result);
    }

    /// <summary>
    /// UNLINK a teacher from the student's side: cancels a Pending request or unlinks
    /// an Active link (soft transition, preserved for audit). The teacher CARD STAYS on
    /// the student's home showing "Unlinked", and the student can re-send a request to
    /// the same teacher afterwards. For the stronger "delete card entirely" action, see
    /// <see cref="DeleteMyTeacher"/>.
    /// </summary>
    /// <response code="200">Cancelled or unlinked.</response>
    /// <response code="404">No pending request or active link with this teacher.</response>
    [HttpDelete("me/teachers/{teacherId:long}")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMyTeacher([FromRoute] long teacherId)
    {
        var studentUserId = await ResolveStudentUserIdAsync();
        if (studentUserId is null) return StudentNotResolved();

        // Non-null: ResolveStudentUserIdAsync already required a JWT user id
        long actingUserId = _currentUser.UserId!.Value;

        var result = await _studentUserService.UnlinkTeacherAsync(studentUserId.Value, teacherId, actingUserId);
        return ToResponse(result);
    }

    /// <summary>
    /// DELETE a teacher from the student's side — the stronger action. It ends any live
    /// link (Active → Unlinked, Pending → Cancelled) AND removes the card from the
    /// student's home entirely (it will not reappear unless the student sends a new
    /// request), and it notifies the teacher that a linked student left. It NEVER touches
    /// the teacher's roster / attendance / payment data. Idempotent, and works even on a
    /// dead card (Rejected / RemovedByTeacher / already-Unlinked) so the student can clear it.
    /// </summary>
    /// <response code="200">Deleted (card dismissed).</response>
    /// <response code="404">No link history with this teacher.</response>
    [HttpDelete("me/teachers/{teacherId:long}/delete")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteMyTeacher([FromRoute] long teacherId)
    {
        var studentUserId = await ResolveStudentUserIdAsync();
        if (studentUserId is null) return StudentNotResolved();

        // Non-null: ResolveStudentUserIdAsync already required a JWT user id
        long actingUserId = _currentUser.UserId!.Value;

        var result = await _studentUserService.DeleteTeacherAsync(studentUserId.Value, teacherId, actingUserId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ME: PER-TEACHER QR ("Show QR Code")
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the authenticated student's per-teacher attendance QR ("Show QR Code").
    /// The student displays the QR and the teacher scans it (from the teacher app's
    /// "Scan QR Code from Student account") to record attendance / exam presence / payment;
    /// it encodes the per-teacher student code the teacher's scan resolves.
    ///
    /// The <c>{teacherId}</c> route segment is the TEACHER — never the student id. Identity
    /// is the JWT student (same IDOR-safe pattern as the other "me" endpoints): the student
    /// must be ACTIVELY linked to, and bound under, this teacher. When the teacher issues
    /// hard-copy cards only, the response carries code <c>BarcodeNotAvailableInApp</c> so the
    /// frontend can hide the button (AAM-FR-04.7).
    /// </summary>
    /// <param name="teacherId">The linked teacher whose per-teacher code the QR encodes.</param>
    /// <response code="200">Teacher name, the per-teacher code, and the rendered QR SVG.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Not linked to / not bound under this teacher, or the teacher issues hard-copy cards only.</response>
    /// <response code="404">The authenticated user has no student account.</response>
    [HttpGet("me/teachers/{teacherId:long}/barcode")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.StudentUser.StudentTeacherBarcodeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyTeacherBarcode([FromRoute] long teacherId)
    {
        var studentUserId = await ResolveStudentUserIdAsync();
        if (studentUserId is null) return StudentNotResolved();

        var result = await _studentUserService.GetTeacherBarcodeForStudentAsync(
            studentUserId.Value, teacherId, this.ReadDeviceId(), this.ReadPreviousDeviceId());
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ME: PER-TEACHER HOME (aggregated screen — Figma 232:7033)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the aggregated "teacher home" screen for a selected linked teacher:
    /// attendance, payment, videos, online/offline exams and homework in ONE call,
    /// each section gated by the teacher's student-visibility toggles (a hidden
    /// section carries <c>visible:false</c> with empty data). The merged
    /// <c>exams.upcoming</c> feed tags every row with <c>examType</c> (Online/Offline).
    ///
    /// Identity is the JWT student (same IDOR-safe pattern as the other "me"
    /// endpoints); the student must be ACTIVELY linked to, and bound under, this
    /// teacher. Month figures default to the teacher-local (Africa/Cairo) current
    /// month unless both <paramref name="year"/> and <paramref name="month"/> are supplied.
    /// </summary>
    /// <param name="teacherId">The selected linked teacher whose home screen to build.</param>
    /// <param name="year">Optional scoping year (used only together with <paramref name="month"/>).</param>
    /// <param name="month">Optional scoping month 1-12 (used only together with <paramref name="year"/>).</param>
    /// <response code="200">Aggregated home payload; hidden modules carry visible=false with empty data.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Not linked to / not bound under this teacher.</response>
    /// <response code="404">The authenticated user has no student account.</response>
    [HttpGet("me/teachers/{teacherId:long}/home")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.StudentUser.StudentTeacherHomeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyTeacherHome(
        [FromRoute] long teacherId, [FromQuery] int? year, [FromQuery] int? month)
    {
        var studentUserId = await ResolveStudentUserIdAsync();
        if (studentUserId is null) return StudentNotResolved();

        var result = await _homeService.GetTeacherHomeAsync(
            studentUserId.Value, teacherId, year, month, this.ReadDeviceId(), this.ReadPreviousDeviceId());
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ME: PER-TEACHER DEVICE REGISTRATION (device-lock feature)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Registers the caller's current device (X-Device-Id header) as the one allowed to open this
    /// teacher, after the student has confirmed the device lock. Only relevant when the teacher has
    /// turned the lock on. Idempotent for the same device; returns <c>DeviceMismatch</c> (403) when a
    /// different device is already bound (the teacher/assistant must reset it first).
    /// </summary>
    /// <param name="teacherId">The linked teacher to bind this device under.</param>
    /// <response code="200">Device registered (or already registered / lock off).</response>
    /// <response code="400">The X-Device-Id header was missing.</response>
    /// <response code="403">A different device is already registered, or the student is not linked/bound.</response>
    [HttpPost("me/teachers/{teacherId:long}/register-device")]
    [ModulePermission(roles: new[] { "Student" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RegisterDeviceForTeacher([FromRoute] long teacherId)
    {
        var studentUserId = await ResolveStudentUserIdAsync();
        if (studentUserId is null) return StudentNotResolved();

        var result = await _studentUserService.RegisterDeviceForTeacherAsync(
            studentUserId.Value, teacherId, this.ReadDeviceId(), this.ReadPreviousDeviceId());
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // SUPER ADMIN: STUDENT ACCOUNTS LIST
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Pages ALL student accounts on the platform (not scoped to any teacher).
    //   Each row is enriched with the list of teachers that account currently
    //   holds an ACTIVE link to — teacher identity plus the per-teacher student
    //   code that teacher assigned this account — in one call, no N+1.
    //
    // QUERY PARAMETERS:
    //   - page (int, default 1): Page number (1-based)
    //   - pageSize (int, default 20, max 100): Records per page
    //   - search (string, optional): Matches account full name, OR the
    //     per-teacher student code of any of the account's ACTIVE teacher links
    //   - teacherId (long, optional): Only accounts ACTIVE-linked to this teacher
    //
    // TABLES READ:
    //   StudentUsers, Users, StudentTeacherLinks, Teachers, TeacherStudents
    //
    // SOFT-DELETE:
    //   Deleted student accounts (StudentUser.DeletedAt != null) and deleted
    //   users (User.DeletedAt != null) are excluded by the EF Core global query
    //   filters — they never appear in this list.
    //
    // SAMPLE REQUEST:
    //   GET /api/studentuser/list?page=1&pageSize=20&search=ahmed&teacherId=12
    //
    // SAMPLE RESPONSE (200 OK):
    //   {
    //     "success": true,
    //     "message": "Done successfully",
    //     "data": {
    //       "totalCount": 3,
    //       "page": 1,
    //       "pageSize": 20,
    //       "totalPages": 1,
    //       "data": [
    //         {
    //           "studentAccountId": 501,
    //           "fullName": "Ahmed Mostafa",
    //           "userName": "ahmed.m",
    //           "phoneNumber": "01000000000",
    //           "teachers": [
    //             {
    //               "teacherId": 12,
    //               "teacherCode": "48291057",
    //               "studentCode": "S001",
    //               "teacherName": "Mariam Hassan"
    //             }
    //           ]
    //         }
    //       ]
    //     }
    //   }
    //
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SuperAdmin-only: paginated list of every student account on the platform,
    /// searchable by name / per-teacher student code and filterable by teacher.
    /// Each row carries the account's ACTIVE teacher links (teacher identity +
    /// the per-teacher student code), loaded via one batched query — no N+1.
    /// </summary>
    /// <response code="200">Paginated student-accounts page.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller is not a SuperAdmin.</response>
    [HttpGet("list")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.StudentUser.StudentAccountListItemDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStudentAccounts([FromQuery] StudentAccountListRequest request)
    {
        var result = await _studentUserService.GetStudentAccountsAsync(request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // LOOKUP BY ACCOUNT CODE (used by the Parent module, AAM-FR-06.3 Method A)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Looks up a student user by their unique StudentAccountCode. Used by the
    /// Parent module when linking to a child's account — intentionally NOT
    /// restricted to the Student role.
    /// </summary>
    /// <response code="200">Basic student profile info.</response>
    /// <response code="400">Blank account code.</response>
    /// <response code="404">No active student with this code.</response>
    [HttpGet("by-code/{accountCode}")]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.StudentUser.StudentUserProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentUserByAccountCode([FromRoute] string accountCode)
    {
        var result = await _studentUserService.GetStudentUserByAccountCodeAsync(accountCode);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the calling user's StudentUser.Id from the JWT:
    /// User.Id → StudentUsers.UserId (active, non-deleted). Returns null when the
    /// JWT is missing a user id or the user has no student account.
    /// </summary>
    private async Task<long?> ResolveStudentUserIdAsync()
    {
        long? userId = _currentUser.UserId;
        if (userId is null) return null;

        var studentUser = await _unitOfWork.Users.GetActiveStudentUserByUserIdAsync(userId.Value);
        return studentUser?.Id;
    }

    /// <summary>
    /// Standardized response when the JWT cannot be resolved to a student account.
    /// </summary>
    private IActionResult StudentNotResolved() =>
        new ObjectResult(new { success = false, message = _resolveMessage })
        {
            StatusCode = (int)HttpStatusCode.NotFound
        };

    private const string _resolveMessage = "Student account not found for the authenticated user";
}