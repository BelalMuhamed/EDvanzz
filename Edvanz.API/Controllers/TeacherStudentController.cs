
using Edvanz.API.Attributes;
using Edvanz.Application.Dtos.TeacherStudent;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using Edvanz.Application.Json;

namespace Edvanz.API.Controllers;

/// <summary>
/// API endpoints for the Student Module (Module 1) — teacher-scoped student records:
/// CRUD, search/filter/sort, recycle bin, restore, bulk import, and counts.
///
/// AUTH &amp; TENANCY: class-level [Authorize] + per-action
/// [ModulePermission(StudentConstants.ModuleName, ...)]. The acting teacherId is
/// ALWAYS derived from the JWT via ModuleSixApiBaseController.ResolveTeacherIdAsync —
/// never read from the route or body (Catalog §1.3). Assistants resolve to their
/// owning tutor automatically; SuperAdmin bypasses the permission filter.
///
/// These manage TeacherStudent records (student DATA owned by a teacher), NOT
/// StudentUser accounts — those live in StudentUserController.
/// </summary>
[Authorize]
public class TeacherStudentController : ModuleSixApiBaseController
{
    private readonly ITeacherStudentService _studentService;

    /// <summary>
    /// Web-default (camelCase) JSON + string enums, matching the global controller options —
    /// used to hand-serialize the NDJSON progress/result lines on the streaming bulk-import
    /// endpoint. This stream builds its own options instead of using the pipeline's, so it has
    /// to repeat every converter — including the UTC stamping — or a timestamp would travel in
    /// one shape on this route and another everywhere else. Keep in sync with Program.cs.
    /// </summary>
    private static readonly JsonSerializerOptions StreamJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters =
            {
                new JsonStringEnumConverter(),
                new UtcDateTimeJsonConverter(),
                new NullableUtcDateTimeJsonConverter(),
            },
        };
    private readonly IStudentBarcodeService _barcodeService;

    public TeacherStudentController(
        ITeacherStudentService studentService,
        IStudentBarcodeService barcodeService,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
        : base(currentUser, unitOfWork)
    {
        _studentService = studentService;
        _barcodeService = barcodeService;
    }

    /// <summary>Creates a new student record under the authenticated teacher's account.</summary>
    /// <remarks>
    /// REQ-STU-012: Manual student entry form. Only <c>StudentName</c> is required; all other fields are optional.
    /// REQ-STU-005: Only StudentName is mandatory on creation.
    /// AUTH: requires <c>Student / Add</c> permission.
    /// TENANCY: teacherId resolved from JWT — never from route or body (IDOR-safe, §1.3).
    /// Assistants with <c>Add</c> permission resolve to their owning tutor.
    /// </remarks>
    /// <param name="dto">New student data. Only <c>StudentName</c> is mandatory (REQ-STU-014).</param>
    /// <response code="201">Student created successfully. Returns the new <c>TeacherStudentDto</c>.</response>
    /// <response code="400">Validation failure (e.g. empty name, duplicate student code, capacity exceeded).</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / Add</c> permission.</response>
    /// <response code="404">Teacher record not found for the authenticated user.</response>
    [HttpPost]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionAdd)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.TeacherStudentDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateStudent([FromBody] CreateTeacherStudentDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.CreateStudentAsync(teacherId.Value, dto);
        return ToResponse(result);
    }

    /// <summary>Returns a single student record by its primary key, scoped to the authenticated teacher.</summary>
    /// <remarks>
    /// AUTH: requires <c>Student / ViewProfile</c> permission.
    /// TENANCY: teacherId from JWT — the service rejects studentIds that do not belong to the resolved teacher.
    /// </remarks>
    /// <param name="studentId">Primary key of the TeacherStudent record.</param>
    /// <response code="200">Returns the <c>TeacherStudentDto</c> for the requested student.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / ViewProfile</c> permission.</response>
    /// <response code="404">Student not found, soft-deleted, or does not belong to this teacher; or teacher record not found.</response>
    [HttpGet("students/{studentId:long}")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewProfile)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.TeacherStudentProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentById([FromRoute] long studentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.GetStudentByIdAsync(teacherId.Value, studentId);
        return ToResponse(result);
    }

    /// <summary>Resolves a scanned/typed student code to the student (canonical scan-resolution endpoint).</summary>
    /// <remarks>
    /// The single, EXACT code→student resolver shared by every scanning surface (session attendance,
    /// exam attendance, and future link scans). The barcode/QR encodes the plain per-teacher
    /// <c>StudentCode</c>; the client decodes it and resolves it here (or from the already-loaded
    /// roster for the hot path — this endpoint is the fallback + manual-entry target). EXACT match,
    /// not the partial roster search, so <c>A1</c> can never resolve to <c>A10</c>.
    /// AUTH: requires <c>Student / ViewProfile</c> (same as the barcode endpoints).
    /// TENANCY: teacherId from JWT — the same code resolves only within THIS teacher's roster.
    /// <c>code</c> is a query param (not a path segment) so non-ASCII (Arabic) codes and whitespace
    /// survive URL-encoding intact.
    /// </remarks>
    /// <response code="200">Returns the resolved <c>StudentCodeResolveDto</c> (id, name, code, session).</response>
    /// <response code="400">Blank code (<c>BarcodeRequired</c>).</response>
    /// <response code="404">No active student carries this code for the teacher (<c>StudentCodeNotFound</c>).</response>
    [HttpGet("students/resolve")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewProfile)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.StudentCodeResolveDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResolveStudentByCode([FromQuery] string? code)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        return ToResponse(await _studentService.ResolveByCodeAsync(teacherId.Value, code));
    }

    /// <summary>Returns the student's barcode as an SVG image for the profile screen.</summary>
    /// <remarks>
    /// REQ-STU-047: barcode encodes the immutable student code. Rendered on demand (Code 128,
    /// ZXing) — nothing is stored. The image is a pure function of the code, so it is cacheable:
    /// the response carries a strong <c>ETag</c> and a long <c>Cache-Control</c>, and a matching
    /// <c>If-None-Match</c> short-circuits to 304 without re-rendering.
    /// AUTH: requires <c>Student / ViewProfile</c> permission.
    /// TENANCY: teacherId from JWT — a studentId outside the resolved teacher returns 404.
    /// </remarks>
    /// <param name="studentId">Primary key of the TeacherStudent record.</param>
    /// <response code="200">Returns <c>image/svg+xml</c> barcode markup.</response>
    /// <response code="304">Client's cached copy (ETag) is still current.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / ViewProfile</c> permission.</response>
    /// <response code="404">Student not found, soft-deleted, or not owned by this teacher.</response>
    [HttpGet("students/{studentId:long}/barcode.svg")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewProfile)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.StudentBarcodeSvgDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentBarcodeSvg([FromRoute] long studentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _barcodeService.GetBarcodeSvgAsync(teacherId.Value, studentId);
        if (!result.IsSuccess) return ToResponse(result);

        // Strong ETag over the code — the barcode never changes for a given code (REQ-STU-048),
        // so clients (and any CDN) can cache aggressively and revalidate cheaply.
        var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
            $"\"barcode-{result.Data!.StudentCode}\"");
        Response.GetTypedHeaders().ETag = etag;
        Response.GetTypedHeaders().CacheControl = new Microsoft.Net.Http.Headers.CacheControlHeaderValue
        {
            Private = true,
            MaxAge = TimeSpan.FromDays(30)
        };

        var ifNoneMatch = Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch.Any(t => t.Compare(etag, useStrongComparison: false)))
            return StatusCode(StatusCodes.Status304NotModified);

        return Content(result.Data!.Svg, "image/svg+xml");
    }

    /// <summary>Exports a printable PDF of barcode cards for one or many students.</summary>
    /// <remarks>
    /// REQ-STU-052: bulk barcode export. A single id produces one card; many ids produce a grid.
    /// Server-side render (QuestPDF) so weak devices never draw hundreds of barcodes.
    /// AUTH: requires <c>Student / ViewProfile</c> permission.
    /// TENANCY: teacherId from JWT — ids not owned by the resolved teacher are silently dropped
    /// (IDOR-safe). Page direction follows the request UI culture (Accept-Language).
    /// </remarks>
    /// <param name="request">The student ids to export. An empty roster returns a localized 400, not a PDF.</param>
    /// <response code="200">Returns an <c>application/pdf</c> file of barcode cards.</response>
    /// <response code="400">No student ids supplied (e.g. the session has no students) — localized <c>NoStudentsInSession</c>.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / ViewProfile</c> permission.</response>
    /// <response code="404">None of the supplied ids belong to this teacher.</response>
    [HttpPost("students/barcodes/export")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewProfile)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportStudentBarcodes([FromBody] ExportStudentBarcodesRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        bool rtl = System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
        // Null-coalesce so an explicit "studentIds": null (or an omitted body) reaches the service
        // as an empty list and yields the localized NoStudentsSelected message rather than a 500.
        var result = await _barcodeService.ExportBarcodesPdfAsync(
            teacherId.Value, request.StudentIds ?? new List<long>(), rtl);
        if (!result.IsSuccess) return ToResponse(result);

        return File(result.Data!, "application/pdf", "student-barcodes.pdf");
    }

    /// <summary>Updates an existing student record. Supply null for optional fields to clear them.</summary>
    /// <remarks>
    /// REQ-STU-006: All student fields remain fully editable after creation.
    /// REQ-STU-048: Optimistic concurrency via RowVersion — returns 409 if the record was modified concurrently.
    /// AUTH: requires <c>Student / Edit</c> permission.
    /// TENANCY: teacherId from JWT — the service rejects studentIds not owned by this teacher.
    /// </remarks>
    /// <param name="studentId">Primary key of the student record to update.</param>
    /// <param name="dto">Updated student fields. <c>StudentName</c> is mandatory.</param>
    /// <response code="200">Student updated successfully. Returns the updated <c>TeacherStudentDto</c>.</response>
    /// <response code="400">Validation failure (e.g. empty name, invalid student code format).</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / Edit</c> permission.</response>
    /// <response code="404">Student not found or does not belong to this teacher; or teacher record not found.</response>
    /// <response code="409">Optimistic concurrency conflict — record was modified by another request. Refresh and retry.</response>
    [HttpPut("students/{studentId:long}")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionEdit)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.TeacherStudentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateStudent(
        [FromRoute] long studentId,
        [FromBody] UpdateTeacherStudentDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.UpdateStudentAsync(teacherId.Value, studentId, dto);
        return ToResponse(result);
    }

    /// <summary>Returns a paginated, filtered, and sorted list of active students for the authenticated teacher.</summary>
    /// <remarks>
    /// REQ-STU-032 through REQ-STU-046: Student list with filter panel and sort options.
    /// REQ-STU-SRT-001 through REQ-STU-SRT-007: Sort columns (DateAdded desc by default).
    /// AUTH: requires <c>Student / ViewList</c> permission.
    /// TENANCY: teacherId from JWT — only this teacher's students are returned.
    /// Response <c>data</c> is a <c>PaginatedResponse&lt;TeacherStudentDto&gt;</c> with <c>data[]</c>,
    /// <c>page</c>, <c>pageSize</c>, and <c>totalCount</c> fields.
    /// </remarks>
    /// <param name="request">Pagination, search, filter, and sort parameters.</param>
    /// <response code="200">Paginated list of active students.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / ViewList</c> permission.</response>
    /// <response code="404">Teacher record not found for the authenticated user.</response>
    [HttpGet("students")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewList)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.TeacherStudent.TeacherStudentDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentList([FromQuery] StudentListRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.GetStudentListAsync(teacherId.Value, request);
        return ToResponse(result);
    }

    /// <summary>Returns aggregate student counts (total active, filtered, recycle bin) for the authenticated teacher.</summary>
    /// <remarks>
    /// REQ-STU-UX-001: Total active student count badge.
    /// REQ-STU-UX-002: Filtered count when the filter panel is active.
    /// REQ-STU-UX-009: Recycle bin count for the bin badge.
    /// AUTH: requires <c>Student / ViewList</c> permission.
    /// TENANCY: teacherId from JWT.
    /// Accepts the same <see cref="StudentListRequest"/> filter params as the list endpoint
    /// so the UI can display the filtered count without fetching a full page.
    /// </remarks>
    /// <param name="request">Filter parameters (pagination fields are ignored for count purposes).</param>
    /// <response code="200">Returns <c>StudentCountsDto</c> with <c>totalActiveStudents</c>, <c>filteredCount</c>, and <c>recycleBinCount</c>.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / ViewList</c> permission.</response>
    /// <response code="404">Teacher record not found for the authenticated user.</response>
    [HttpGet("counts")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewList)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.StudentCountsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentCounts([FromQuery] StudentListRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.GetStudentCountsAsync(teacherId.Value, request);
        return ToResponse(result);
    }

    /// <summary>Soft-deletes a single student record (moves to recycle bin). Reversible via the restore endpoint.</summary>
    /// <remarks>
    /// REQ-STU-021: Student can be moved to the recycle bin.
    /// REQ-STU-025: Recycle bin auto-purges records after the retention window expires.
    /// AUTH: requires <c>Student / Delete</c> permission.
    /// TENANCY: teacherId from JWT — the service rejects studentIds not owned by this teacher.
    /// </remarks>
    /// <param name="studentId">Primary key of the student record to soft-delete.</param>
    /// <response code="200">Student moved to recycle bin successfully.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / Delete</c> permission.</response>
    /// <response code="404">Student not found, already deleted, or not owned by this teacher; or teacher record not found.</response>
    [HttpDelete("students/{studentId:long}")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionDelete)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SoftDeleteStudent([FromRoute] long studentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // actingUserId = JWT user id → recorded as StudentTeacherLink.RemovedByUserId when the
        // delete ends the student's account link (assistant deletes are attributed correctly).
        var result = await _studentService.SoftDeleteStudentAsync(teacherId.Value, studentId, GetActingUserId());
        return ToResponse(result);
    }

    /// <summary>Soft-deletes multiple student records in a single request (bulk move to recycle bin).</summary>
    /// <remarks>
    /// REQ-STU-022: Bulk delete by selecting multiple student IDs from the list view.
    /// AUTH: requires <c>Student / Delete</c> permission.
    /// TENANCY: teacherId from JWT — IDs not belonging to this teacher are rejected entirely.
    /// </remarks>
    /// <param name="dto">List of student record IDs to soft-delete.</param>
    /// <response code="200">All selected students moved to recycle bin.</response>
    /// <response code="400">Request body is invalid or <c>studentIds</c> list is empty.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / Delete</c> permission.</response>
    /// <response code="404">One or more IDs not found or not owned by this teacher; or teacher record not found.</response>
    [HttpPost("bulk-delete")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionDelete)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkSoftDeleteStudents([FromBody] BulkStudentIdsDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.BulkSoftDeleteStudentsAsync(teacherId.Value, dto, GetActingUserId());
        return ToResponse(result);
    }

    /// <summary>Returns a paginated list of soft-deleted students in the recycle bin for the authenticated teacher.</summary>
    /// <remarks>
    /// REQ-STU-029: Recycle bin view.
    /// REQ-STU-UX-010: Each row includes <c>daysRemaining</c> before permanent purge, used for color-coded urgency.
    /// AUTH: requires <c>Student / Delete</c> permission.
    /// TENANCY: teacherId from JWT — only this teacher's deleted students are returned.
    /// </remarks>
    /// <param name="page">Page number (1-based). Defaults to 1.</param>
    /// <param name="pageSize">Records per page. Defaults to 20.</param>
    /// <response code="200">Paginated list of deleted students. Each item includes <c>daysRemaining</c> before permanent purge.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / Delete</c> permission.</response>
    /// <response code="404">Teacher record not found for the authenticated user.</response>
    [HttpGet("recycle-bin")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionDelete)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.TeacherStudent.RecycleBinStudentDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRecycleBin(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.GetRecycleBinAsync(teacherId.Value, page, pageSize);
        return ToResponse(result);
    }

    /// <summary>Restores a single soft-deleted student from the recycle bin back to the active list.</summary>
    /// <remarks>
    /// REQ-STU-026: Restore a deleted student.
    /// REQ-STU-031: Restore clears <c>IsDeleted</c> and <c>DeletedAt</c>.
    /// AUTH: requires <c>Student / Delete</c> permission.
    /// TENANCY: teacherId from JWT — only students in this teacher's recycle bin can be restored.
    /// Returns 400 if the student is not currently in the recycle bin (already active or permanently deleted).
    /// </remarks>
    /// <param name="studentId">Primary key of the soft-deleted student to restore.</param>
    /// <response code="200">Student restored to the active list.</response>
    /// <response code="400">Student is not in the recycle bin (not currently soft-deleted).</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / Delete</c> permission.</response>
    /// <response code="404">Student not found or not owned by this teacher; or teacher record not found.</response>
    [HttpPost("recycle-bin/{studentId:long}/restore")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionDelete)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.TeacherStudentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RestoreStudent([FromRoute] long studentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.RestoreStudentAsync(teacherId.Value, studentId);
        return ToResponse(result);
    }

    /// <summary>Restores multiple soft-deleted students from the recycle bin in a single request.</summary>
    /// <remarks>
    /// REQ-STU-031.1: Bulk restore from recycle bin.
    /// AUTH: requires <c>Student / Delete</c> permission.
    /// TENANCY: teacherId from JWT — IDs not belonging to this teacher's recycle bin are rejected.
    /// </remarks>
    /// <param name="dto">List of soft-deleted student record IDs to restore.</param>
    /// <response code="200">All selected students restored to the active list.</response>
    /// <response code="400">Request body is invalid, <c>studentIds</c> list is empty, or one or more students are not in the recycle bin.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / Delete</c> permission.</response>
    /// <response code="404">One or more IDs not found or not owned by this teacher; or teacher record not found.</response>
    [HttpPost("bulk-restore")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionDelete)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkRestoreStudents([FromBody] BulkStudentIdsDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.BulkRestoreStudentsAsync(teacherId.Value, dto);
        return ToResponse(result);
    }

    /// <summary>Permanently deletes a single student from the recycle bin. IRREVERSIBLE — no restore path exists after this call.</summary>
    /// <remarks>
    /// REQ-STU-029: Hard-delete from recycle bin.
    /// AUTH: requires <c>Student / Delete</c> permission.
    /// TENANCY: teacherId from JWT — the service rejects studentIds not in this teacher's recycle bin.
    /// The student must be in the recycle bin (soft-deleted) before this endpoint can be called.
    /// </remarks>
    /// <param name="studentId">Primary key of the soft-deleted student to permanently remove.</param>
    /// <response code="200">Student permanently deleted.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / Delete</c> permission.</response>
    /// <response code="404">Student not found in this teacher's recycle bin; or teacher record not found.</response>
    [HttpDelete("recycle-bin/{studentId:long}/permanent")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionDelete)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PermanentDeleteStudent([FromRoute] long studentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.PermanentDeleteStudentAsync(teacherId.Value, studentId);
        return ToResponse(result);
    }

    /// <summary>Imports multiple student records in bulk from a structured list (e.g. Google Sheets export).</summary>
    /// <remarks>
    /// REQ-STU-015 through REQ-STU-020: Bulk import flow.
    /// REQ-STU-018: Rows with empty <c>studentName</c> are skipped. Student codes are auto-generated when blank.
    /// REQ-STU-019: Returns a summary report with per-row success/failure detail.
    /// AUTH: requires <c>Student / Import</c> permission.
    /// TENANCY: teacherId from JWT — all imported students are created under this teacher.
    /// The operation is best-effort: valid rows succeed even if some rows fail validation.
    /// </remarks>
    /// <param name="dto">Import envelope containing the list of student rows to process.</param>
    /// <response code="200">Import completed. Returns <c>BulkImportResultDto</c> with <c>totalProcessed</c>, <c>successCount</c>, <c>failedCount</c>, and per-row failure details.</response>
    /// <response code="400">Request body is invalid or <c>students</c> list is null/empty.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / Import</c> permission.</response>
    /// <response code="404">Teacher record not found for the authenticated user.</response>
    [HttpPost("bulk-import")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionImport)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.BulkImportResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkImportStudents([FromBody] BulkImportTeacherStudentsDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.BulkImportStudentsAsync(teacherId.Value, dto);
        return ToResponse(result);
    }

    /// <summary>
    /// Streaming variant of <see cref="BulkImportStudents"/> for large sheets: runs the import in ONE
    /// all-or-nothing transaction while streaming live progress as newline-delimited JSON (NDJSON), so
    /// the client can show a "processed / total" counter and the request never idles into a timeout.
    /// The import commits only at the very end and honors <c>HttpContext.RequestAborted</c>, so a client
    /// that disconnects or cancels mid-import saves NOTHING (full rollback).
    /// </summary>
    /// <remarks>
    /// Response is <c>application/x-ndjson</c>, one JSON object per line:
    /// <code>
    /// {"type":"progress","processed":150,"total":1000}
    /// ... (repeated) ...
    /// {"type":"result","success":true,"code":"BulkImportComplete","message":"…","data":{…BulkImportResultDto…}}
    /// </code>
    /// A pre/uncommitted failure (capacity exceeded, unresolved teacher, unexpected error) is a terminal
    /// <c>{"type":"error","code":"…","message":"…"}</c> or a <c>result</c> line with <c>success:false</c>.
    /// AUTH/TENANCY identical to <c>bulk-import</c> (Student/Import permission; teacherId from the JWT).
    /// </remarks>
    [HttpPost("bulk-import/stream")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionImport)]
    public async Task BulkImportStudentsStream([FromBody] BulkImportTeacherStudentsDto dto)
    {
        var ct = HttpContext.RequestAborted;
        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-cache";
        // Push each line to the client immediately instead of buffering the whole response.
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        async Task WriteLineAsync(object payload)
        {
            var json = JsonSerializer.Serialize(payload, StreamJsonOptions);
            await Response.WriteAsync(json + "\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null)
        {
            await WriteLineAsync(new { type = "error", code = "TeacherNotResolved", message = "Teacher not resolved" });
            return;
        }

        try
        {
            var result = await _studentService.BulkImportStudentsAsync(
                teacherId.Value,
                dto,
                onProgress: (processed, total) =>
                    WriteLineAsync(new { type = "progress", processed, total }),
                cancellationToken: ct);

            await WriteLineAsync(new
            {
                type = "result",
                success = result.IsSuccess,
                code = result.Code,
                message = result.Message,
                data = result.Data,
            });
        }
        catch (OperationCanceledException)
        {
            // Client disconnected / cancelled — the import was rolled back (nothing saved). The socket
            // is gone, so there is nothing to write back; just end the request.
        }
        catch (Exception)
        {
            // Best-effort terminal error line (no-ops if the connection is already gone).
            try
            {
                await WriteLineAsync(new { type = "error", code = "BulkImportFailed", message = "Import failed" });
            }
            catch
            {
                // ignore — connection likely gone
            }
        }
    }
    /// <summary>Returns the chip-strip data for the "Assign Students" screen: All / Unassigned counts plus one chip per session with its assigned-student count.</summary>
    /// <remarks>
    /// REQ-SES-016/017: student picker for session assignment.
    /// Counts reflect the optional <c>search</c> term; the session set is the full catalog (stable while searching).
    /// AUTH: requires <c>Student / ViewList</c> permission.
    /// TENANCY: teacherId from JWT.
    /// </remarks>
    /// <param name="search">Optional name/code partial match applied to the counts.</param>
    /// <response code="200">Returns <c>SessionAssignmentChipsDto</c>.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / ViewList</c> permission.</response>
    /// <response code="404">Teacher record not found for the authenticated user.</response>
    [HttpGet("assignment-chips")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewList)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.SessionAssignmentChipsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSessionAssignmentChips([FromQuery] string? search)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.GetSessionAssignmentChipsAsync(teacherId.Value, search);
        return ToResponse(result);
    }
    /// <summary>
    /// Returns the student-list screen payload: the tenant-wide active-student total
    /// plus a paginated, searchable, filterable, sortable student page — in one call.
    /// </summary>
    /// <remarks>
    /// REQ-STU-UX-001: <c>noOfStudentsForTenant</c> is the total ACTIVE student count
    /// for the header badge, independent of any filter.
    /// REQ-STU-032..046 / REQ-STU-SRT-001..007: <c>students</c> carries the standard
    /// <c>PaginatedResponse&lt;TeacherStudentDto&gt;</c> page; its <c>totalCount</c>
    /// reflects the filtered set.
    /// AUTH: requires <c>Student / ViewList</c> permission.
    /// TENANCY: teacherId from JWT — only this teacher's students are counted and returned.
    /// </remarks>
    /// <param name="request">Pagination, search, filter, and sort parameters (same as the list endpoint).</param>
    /// <response code="200">The tenant total plus the paginated student page.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller lacks the <c>Student / ViewList</c> permission.</response>
    /// <response code="404">Teacher record not found for the authenticated user.</response>
    [HttpGet("students/overview")]
    [ModulePermission(StudentConstants.ModuleName, StudentConstants.PermissionViewList)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.TenantStudentListDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentsOverview([FromQuery] StudentListRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _studentService.GetTenantStudentListAsync(teacherId.Value, request);
        return ToResponse(result);
    }
    /// <summary>Creates a new student record under a teacher specified by the caller. SuperAdmin only.</summary>
    /// <remarks>
    /// REQ-STU-012 / REQ-STU-005: identical validation and creation behavior as the
    /// teacher-facing <see cref="CreateStudent"/> action — this exists solely so a
    /// SuperAdmin can operate on behalf of a teacher for whom no JWT-derived tenant
    /// scope exists (SuperAdmin has no Teacher row — see ModuleSixApiBaseController.ResolveTeacherIdAsync).
    /// AUTH: SuperAdmin role only (roleOnly gate) — NOT reachable by Teacher/Assistant,
    /// because TeacherId is caller-supplied here and would otherwise be an IDOR vector
    /// (the only action in this controller where that is true).
    /// TENANCY: TeacherId comes from the request body, by design, only under the roleOnly
    /// SuperAdmin gate above.
    /// </remarks>
    /// <param name="dto">New student data plus the target TeacherId.</param>
    /// <response code="201">Student created successfully under the specified teacher.</response>
    /// <response code="400">Validation failure (e.g. empty name, duplicate student code, capacity exceeded).</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller is not a SuperAdmin.</response>
    /// <response code="404">TeacherId does not reference an existing active teacher.</response>
    [HttpPost("admin")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.TeacherStudentDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateStudentForTeacher([FromBody] CreateTeacherStudentByAdminDto dto)
    {
        var result = await _studentService.CreateStudentAsync(dto.TeacherId, dto);
        return ToResponse(result);
    }
    [HttpGet("admin/students")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.TeacherStudent.TeacherStudentDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentListForTeacher([FromQuery] StudentListByAdminRequest request)
    {
        var result = await _studentService.GetStudentListForAdminAsync(request.TeacherId, request);
        return ToResponse(result);
    }

    /// <summary>Returns a single student record by its primary key, with no tenant scope. SuperAdmin only.</summary>
    /// <remarks>
    /// Admin variant of <see cref="GetStudentById"/> — resolves the owning teacher from the
    /// student record itself, so no TeacherId needs to be supplied or guessed by the caller.
    /// AUTH: SuperAdmin role only (roleOnly gate).
    /// TENANCY: none — studentId alone identifies the record; the resolved TeacherId is used
    /// internally to assemble the profile (assigned-session card, etc.).
    /// </remarks>
    /// <param name="studentId">Primary key of the TeacherStudent record.</param>
    /// <response code="200">Returns the <c>TeacherStudentProfileDto</c> for the requested student.</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller is not a SuperAdmin.</response>
    /// <response code="404">Student not found or soft-deleted.</response>
    [HttpGet("admin/students/{studentId:long}")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.TeacherStudentProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentByIdForAdmin([FromRoute] long studentId)
    {
        var result = await _studentService.GetStudentByIdForAdminAsync(studentId);
        return ToResponse(result);
    }

    /// <summary>Updates an existing student record, with no tenant scope. SuperAdmin only.</summary>
    /// <remarks>
    /// Admin variant of <see cref="UpdateStudent"/> — identical validation and update behavior
    /// (student code rules, session reassignment, RowVersion concurrency), resolving the owning
    /// teacher from the student record itself.
    /// AUTH: SuperAdmin role only (roleOnly gate).
    /// TENANCY: none — studentId alone identifies the record.
    /// </remarks>
    /// <param name="studentId">Primary key of the student record to update.</param>
    /// <param name="dto">Updated student fields. <c>StudentName</c> is mandatory.</param>
    /// <response code="200">Student updated successfully. Returns the updated <c>TeacherStudentDto</c>.</response>
    /// <response code="400">Validation failure (e.g. empty name, invalid student code format).</response>
    /// <response code="401">JWT missing or expired.</response>
    /// <response code="403">Caller is not a SuperAdmin.</response>
    /// <response code="404">Student not found.</response>
    /// <response code="409">Optimistic concurrency conflict — record was modified by another request. Refresh and retry.</response>
    [HttpPut("admin/students/{studentId:long}")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.TeacherStudent.TeacherStudentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateStudentForAdmin(
        [FromRoute] long studentId,
        [FromBody] UpdateTeacherStudentDto dto)
    {
        var result = await _studentService.UpdateStudentForAdminAsync(studentId, dto);
        return ToResponse(result);
    }
}
