using Edvanz.API.Attributes;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.AdminInsights;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Edvanz.API.Controllers;

/// <summary>
/// SuperAdmin usage intelligence: who is actually using Edvanz, how often, how deeply, and who on the
/// account is doing the work — plus the sales attribution and internal notes that turn those numbers
/// into a conversation someone can have.
///
/// WHY THIS EXISTS: before it, the admin portal could only report logins and row counts. Neither
/// answers the business question. A teacher who opens the app once and marks nothing looked identical
/// to one running their whole business on it, and ten students meant nothing if none of them were
/// assigned to a session. Everything here is measured from real writes.
///
/// THE MODEL IS THREE INDEPENDENT AXES, and the UI always shows all three:
///   1. CADENCE  — how often (daily · most days · weekly · rarely · dormant · never)
///   2. DEPTH    — which modules (attendance only vs attendance + payments + exams)
///   3. OPERATOR — who works it (teacher only · assistants only · both)
///
/// DATA FRESHNESS: reads come from TeacherUsageSnapshots, rebuilt nightly at 03:15 Africa/Cairo by
/// the `teacher-usage-rollup` Hangfire job. Every response carries a computedAt so a stale number can
/// be told apart from a quiet one, and `POST teachers/{id}/recompute` refreshes one teacher on demand.
///
/// AUTHORIZATION: class-level [Authorize]; every action
/// [ModulePermission(roles: ["SuperAdmin"], roleOnly: true)] — mirrors AdminSubscriptionController.
/// </summary>
[Route("api/admin/insights")]
[Authorize]
public class AdminInsightsController : ApiBaseController
{
    private readonly IAdminInsightsService _insights;
    private readonly ICurrentUserService _currentUser;
    private readonly ITimeZoneService _timeZone;

    public AdminInsightsController(
        IAdminInsightsService insights,
        ICurrentUserService currentUser,
        ITimeZoneService timeZone)
    {
        _insights = insights;
        _currentUser = currentUser;
        _timeZone = timeZone;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 0: THE NUMBERS — the landing page
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   How the SUBSCRIBED base is doing, and where the product is not landing.
    //
    //   The centrepiece is the feature-adoption table: for every feature, how many teachers are
    //   ENTITLED to it, how many use it, and how many have never opened it. Adoption is counted
    //   only over the entitled — a plan that does not include videos must not drag the videos
    //   number down, or the table stops meaning anything.
    //
    // WHY SUBSCRIBED-ONLY BY DEFAULT:
    //   The free and expired accounts outnumber the paying ones and made every platform figure look
    //   like a failure when the subscribers were fine. Pass subscribedOnly=false to fold them in.
    //
    // SAMPLE: GET /api/admin/insights/numbers
    //         GET /api/admin/insights/numbers?subscribedOnly=false
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("numbers")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<AdminNumbersDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNumbers([FromQuery] bool subscribedOnly = true)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.GetNumbersAsync(subscribedOnly));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 0b: THE CALL LIST
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Answers the only question the landing page exists for: which teachers do I contact today,
    //   and what do I say? One ranked list, each teacher exactly ONCE, under their single most
    //   urgent reason, with the evidence in plain words and a concrete action.
    //
    // WHY IT REPLACED THE CARDS:
    //   The overview showed nine insight lists side by side. Across 171 teachers they held 241
    //   entries, the same teacher sat on several of them, nothing said which to work first, and no
    //   card said what to DO. That is a report to study, not a list to work.
    //
    // ORDERING (the priority is what is at stake and how perishable, NOT bucket size):
    //   1 paid & not started · 2 working but expiring · 3 teacher stopped while assistants carry on
    //   4 went quiet · 5 students stranded · 6 ready but idle · 7 never started · 8 one module only
    //   "Never started" is the biggest group and sits near the bottom on purpose — those accounts
    //   have been stuck for weeks and will keep; a teacher who paid on Tuesday will not.
    //
    // SAMPLE: GET /api/admin/insights/call-list
    //         GET /api/admin/insights/call-list?reason=PaidNotStarted&take=50
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("call-list")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<CallListDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCallList(
        [FromQuery] string? reason = null,
        [FromQuery] int take = 0)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.GetCallListAsync(reason, take));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 1: PLATFORM NUMBERS (distributions + module adoption)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   The admin landing page in one call — headline counts with a real previous-period delta, the
    //   three axis distributions, module adoption, and the named insight cards.
    //
    //   Every insight card NAMES TEACHERS. A count cannot be worked; a list of five people with
    //   phone numbers and a sales rep can.
    //
    // TABLES READ: TeacherUsageSnapshots, TeacherUsageDays, Teachers, SalesReps, TeacherSubscriptions
    //
    // SAMPLE: GET /api/admin/insights/overview
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("overview")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<AdminOverviewDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOverview()
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.GetOverviewAsync());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 2: USAGE GRID
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   One page of teachers with all three axes, the setup-health pairs and a 30-day sparkline.
    //   Every filter is optional and they compose, so "assistants only, using attendance, sold by
    //   Ahmed, registered in August" is one request.
    //
    //   Filtering, sorting, counting and paging all happen in SQL against the pre-computed snapshot.
    //   Search folds Arabic variants (مصطفي ≡ مصطفى).
    //
    // TABLES READ: TeacherUsageSnapshots, TeacherUsageDays, Teachers, Users, SalesReps,
    //              TeacherSubscriptions, AdminNotes
    //
    // SAMPLE: GET /api/admin/insights/teachers?operators=AssistantsOnly&usingModule=Attendance&page=1
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("teachers")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<PaginatedResponse<List<TeacherUsageListItemDto>>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUsageGrid([FromQuery] TeacherUsageQueryRequest request)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.GetUsageGridAsync(request));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 2b: EXPORT THE FILTERED TEACHERS AS CSV
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Takes the SAME query as the grid and returns the whole filtered set as a CSV — everything
    //   needed to identify and contact each teacher, plus their admin notes flattened into one cell.
    //
    //   It exports the FULL filtered set rather than the page on screen: the point is to hand a rep
    //   their call list, and a list cut off at 25 rows is worse than none. Capped at 5,000 rows.
    //
    //   The file is UTF-8 WITH A BOM so Excel renders Arabic names correctly rather than as
    //   mojibake, and every cell that could start a formula is neutralised — these rows carry
    //   free text written by admins and names supplied at sign-up.
    //
    // SAMPLE: GET /api/admin/insights/teachers/export?subscribedWithinDays=30
    //         GET /api/admin/insights/teachers/export?operators=AssistantsOnly
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("teachers/export")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportTeachers([FromQuery] TeacherUsageQueryRequest request)
    {
        if (_currentUser.UserId is null) return UserNotResolved();

        var result = await _insights.ExportTeachersCsvAsync(request);
        if (!result.IsSuccess) return ToResponse(result);

        // Filename stamped in the teacher-tenant's local time, never the server's (Azure runs UTC).
        var localNow = _timeZone.ConvertUtcToLocal(DateTime.UtcNow);
        return File(result.Data!, "text/csv", $"edvanz-teachers_{localNow:yyyyMMdd_HHmm}.csv");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 3: ONE TEACHER'S USAGE (Teacher 360 — Usage tab)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   The full picture for one teacher: the grid row, 90 days of daily activity (zero-filled so
    //   quiet stretches read as flat rather than absent), writes per module over 30 days, and the
    //   PEOPLE on the account — teacher and every assistant, removed ones included — each with their
    //   own last-seen. That last list is the operator axis made concrete: a name to call.
    //
    // SAMPLE: GET /api/admin/insights/teachers/42
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("teachers/{teacherId:long}")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<TeacherUsageDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTeacherUsage([FromRoute] long teacherId)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.GetTeacherUsageAsync(teacherId));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 4: THE FULL LIST BEHIND AN INSIGHT CARD
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   What "see all 34" opens. `insightKey` is one of the AdminInsightKind names the overview
    //   returns — WentQuiet, AssistantOnly, NeverStarted, SetUpNotRunning, SingleModule,
    //   SessionLessRoster, NewlyLive, ExpiringWhileActive. Each keeps its own relevance ordering.
    //
    // SAMPLE: GET /api/admin/insights/cards/WentQuiet?page=1&pageSize=20
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("cards/{insightKey}")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<PaginatedResponse<List<TeacherUsageListItemDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetInsightTeachers(
        [FromRoute] string insightKey,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.GetInsightTeachersAsync(insightKey, page, pageSize));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 5: RECOMPUTE ONE TEACHER NOW
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Rebuilds one teacher's usage immediately instead of waiting for tonight's run — after a
    //   support action, or when an admin distrusts a number in front of them. Idempotent: the
    //   window is deleted and rewritten, so running it twice gives the same answer.
    //
    //   `days` defaults to the full backfill window. Runs INLINE (not queued) because the caller is
    //   a human waiting for the refreshed screen.
    //
    // TABLES WRITTEN: TeacherUsageDays, TeacherUsageSnapshots
    //
    // SAMPLE: POST /api/admin/insights/teachers/42/recompute?days=30
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("teachers/{teacherId:long}/recompute")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<TeacherUsageDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecomputeTeacherUsage(
        [FromRoute] long teacherId,
        [FromQuery] int days = 0)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.RecomputeTeacherUsageAsync(teacherId, days));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINTS 6-9: SALES ATTRIBUTION
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHY: nothing recorded who sold an account — attribution lived only in a Google Sheet with no
    // shared identifier, so no screen could answer "which of this rep's accounts actually went
    // live?". The rollup columns on each rep are that answer.
    //
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Every sales rep with their book of accounts rolled up by outcome.</summary>
    [HttpGet("sales-reps")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<List<SalesRepDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSalesReps([FromQuery] bool includeInactive = false)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.GetSalesRepsAsync(includeInactive));
    }

    /// <summary>Adds a sales rep. Names must be unique — duplicates make attribution unreadable.</summary>
    [HttpPost("sales-reps")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<SalesRepDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateSalesRep([FromBody] SaveSalesRepRequest request)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.CreateSalesRepAsync(request));
    }

    /// <summary>
    /// Updates a sales rep. Setting <c>isActive: false</c> hides them from the assign picker but
    /// KEEPS their name on every teacher they brought in — historical attribution never disappears.
    /// </summary>
    [HttpPut("sales-reps/{salesRepId:long}")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<SalesRepDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateSalesRep(
        [FromRoute] long salesRepId,
        [FromBody] SaveSalesRepRequest request)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.UpdateSalesRepAsync(salesRepId, request));
    }

    /// <summary>
    /// Assigns (or, with a null <c>salesRepId</c>, clears) a teacher's sales attribution and
    /// acquisition source. Returns the refreshed grid row.
    /// </summary>
    [HttpPut("teachers/{teacherId:long}/sales")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<TeacherUsageListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignSalesRep(
        [FromRoute] long teacherId,
        [FromBody] AssignSalesRepRequest request)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.AssignSalesRepAsync(teacherId, request));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINTS 10-12: INTERNAL NOTES
    // ══════════════════════════════════════════════════════════════════════════
    //
    // INTERNAL ONLY — never surfaced on any teacher-, assistant-, student- or parent-facing
    // endpoint. These are written ABOUT the teacher, not for them.
    //
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A teacher's notes, pinned first then newest.</summary>
    [HttpGet("teachers/{teacherId:long}/notes")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<List<AdminNoteDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotes([FromRoute] long teacherId)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.GetNotesAsync(teacherId));
    }

    /// <summary>
    /// Adds a note, stamped with the acting admin's id and name. The author name is captured at
    /// write time so the note stays readable even if that admin account is later renamed or removed.
    /// </summary>
    [HttpPost("teachers/{teacherId:long}/notes")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<AdminNoteDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateNote(
        [FromRoute] long teacherId,
        [FromBody] CreateAdminNoteRequest request)
    {
        // The author is taken from the TOKEN, never from the body (CLAUDE.md §3.3 / BUG-12).
        long? authorUserId = _currentUser.UserId;
        if (authorUserId is null) return UserNotResolved();

        return ToResponse(await _insights.CreateNoteAsync(teacherId, request, authorUserId.Value));
    }

    /// <summary>Soft-deletes a note. The row survives for audit.</summary>
    [HttpDelete("teachers/{teacherId:long}/notes/{noteId:long}")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteNote(
        [FromRoute] long teacherId,
        [FromRoute] long noteId)
    {
        if (_currentUser.UserId is null) return UserNotResolved();
        return ToResponse(await _insights.DeleteNoteAsync(teacherId, noteId));
    }
}
