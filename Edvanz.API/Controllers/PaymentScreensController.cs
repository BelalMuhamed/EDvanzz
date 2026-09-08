using Edvanz.API.Attributes;
using Edvanz.Application.Dtos.Payment;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Edvanz.API.Controllers;

/// <summary>
/// Payment "Screens" API (<c>api/v1/*</c>) — screen-oriented endpoints backing the frontend
/// <c>payment.json</c> spec. Deliberately a SEPARATE controller from <see cref="PaymentController"/>
/// so it forms its own Swagger heading ("PaymentScreens"), keeping the new screen contract clearly
/// distinguishable from the existing <c>api/payment/*</c> endpoints.
///
/// DESIGN:
/// <list type="bullet">
///   <item>Additive &amp; reuse-first — delegates to <see cref="IPaymentScreenService"/>, which
///         composes existing PaymentRepo/PaymentService logic. No money logic is duplicated.</item>
///   <item>Absolute route templates (<c>/api/v1/...</c>) match the frontend paths exactly,
///         independent of the inherited <c>api/[controller]</c> convention.</item>
///   <item><c>teacherId</c> is always resolved from the JWT via <c>ResolveTeacherIdAsync()</c> —
///         never from the route/body (REQ-PAY-NFR-001 / no IDOR).</item>
///   <item>Every action carries <c>[ModulePermission]</c> exactly like <see cref="PaymentController"/>.</item>
/// </list>
/// </summary>
[Authorize]
public sealed class PaymentScreensController : ModuleSixApiBaseController
{
    private readonly IPaymentScreenService _screenService;

    public PaymentScreensController(
        IPaymentScreenService screenService,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
        : base(currentUser, unitOfWork)
    {
        _screenService = screenService;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: SessionPaymentCollectedByMonth
    // GET /api/v1/payments/collections?month=&year=&page=&limit=
    // Paginated ledger of collected payments for a month + year.
    // DASH-1: `month` accepts the unified "YYYY-MM" string (same as tracking/students) OR the
    //   legacy integer month (1-12) + separate `year`. Both optional → current local month/year.
    // AUTH: Teacher (module) OR Assistant with Payment.ViewHistory — OR Payment.ViewCollectorSummary,
    //   which is the grant that already opens their wallet screen over the very same rows. An
    //   assistant caller is FORCE-SCOPED to their own collections below, so the widened grant adds
    //   no data they could not already read.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("/api/v1/payments/collections")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewHistory,
        alsoAllowPermission: PaymentConstants.PermissionViewCollectorSummary)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.CollectionsByMonthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetCollectionsByMonth(
        [FromQuery] string? month,
        [FromQuery] int? year,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        // Optional: restrict to one collector's own collections (e.g. the teacher tapping their own
        // dashboard card to see just what THEY collected). Scoped within the resolved teacher.
        [FromQuery] long? collectedByUserId = null,
        // Optional date-range filter. Date-only values ⇒ inclusive whole days; a time component in
        // the raw value ⇒ precise instants [from, to) (see exactRange below). When BOTH are supplied
        // they take precedence over month/year; echoed on FromDate/ToDate. Omitted → month/year path.
        [FromQuery(Name = "from")] DateTime? fromDate = null,
        [FromQuery(Name = "to")] DateTime? toDate = null,
        // Optional filter over the ledger (collections + refund/edit lines) by student name/code.
        [FromQuery] string? search = null,
        // "Collections only" toggle: false omits the negative refund/withdrawal lines (and paginates
        // collections alone). Default true = the full signed ledger (unchanged behaviour).
        [FromQuery] bool includeAdjustments = true)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Assistant → their OWN ledger, always: the caller-supplied collectedByUserId is IGNORED, so
        // omitting it (or forging a peer's / the tutor's id) can never widen the read beyond their own
        // collections. Mirrors the wallet / wallets / collector-summary endpoints, which have forced
        // own-scope since the interim assistant scoping landed; this one was missed. Teacher/SuperAdmin
        // (scope null) keep the caller's value, so their behaviour is byte-identical.
        collectedByUserId = AssistantScopeUserId() ?? collectedByUserId;

        // Exact-range detection must read the RAW query text: a midnight-to-midnight exact
        // window ("…T00:00:00" → "…T00:00:00", the wallet day filter) parses to the very same
        // DateTimes as a date-only day filter ("2026-09-01"), so TimeOfDay on the bound values
        // cannot tell them apart — that ambiguity leaked the whole next day into a one-day
        // wallet filter. A time component (':') in either raw value ⇒ precise instants [from, to).
        bool? exactRange = null;
        if (fromDate.HasValue && toDate.HasValue)
        {
            exactRange = Request.Query["from"].ToString().Contains(':')
                || Request.Query["to"].ToString().Contains(':');
        }

        var result = await _screenService.GetCollectionsByMonthAsync(
            teacherId.Value, month, year, page, limit, collectedByUserId, fromDate, toDate, search,
            includeAdjustments, exactRange);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: Collections summary (date-filtered)
    // GET /api/v1/payments/collections/summary?from=&to=&asOfMonth=&sessionId=
    // Period overview: money / activity / departures / per-collector honour the [from,to] range;
    // the paid/partial/prorated/unpaid student counts are anchored to asOfMonth (defaults to the
    // month of `to`) because payment status is defined per calendar month. Both dates omitted →
    // the teacher's current local month.
    // AUTH: Teacher (module) OR Assistant with Payment.ViewCollectorSummary — an assistant caller is
    //   force-scoped to their own collector figures (see below).
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("/api/v1/payments/collections/summary")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewCollectorSummary)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.CollectionsSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetCollectionsSummary(
        [FromQuery(Name = "from")] DateTime? fromDate,
        [FromQuery(Name = "to")] DateTime? toDate,
        [FromQuery] string? asOfMonth = null,
        [FromQuery] long? sessionId = null,
        // Optional: scope the money/activity/departure figures to one collector (the drill-in
        // screens' day strip). Omitted → account-wide, byte-identical to the old behaviour.
        [FromQuery] long? collectedByUserId = null)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Assistant → forced to their OWN collector figures (same rule as the ledger above), so the
        // day strip on their screen can never report the account-wide day. Teacher/SuperAdmin unchanged.
        collectedByUserId = AssistantScopeUserId() ?? collectedByUserId;

        // Same RAW-query-text exact-range sniff as the ledger action above — the summary MUST resolve
        // the window identically or the day-insight cards describe a different span than the rows they
        // sit above (the wallet's "in drawer now" scope reported the whole day). Reading the raw text
        // is load-bearing: a midnight-to-midnight exact window parses to the same DateTimes as a
        // date-only day filter, so TimeOfDay cannot tell them apart. Date-only callers — every
        // deployed client — leave this null and hit the original whole-day path unchanged.
        bool? exactRange = null;
        if (fromDate.HasValue && toDate.HasValue)
        {
            exactRange = Request.Query["from"].ToString().Contains(':')
                || Request.Query["to"].ToString().Contains(':');
        }

        var result = await _screenService.GetCollectionsSummaryAsync(
            teacherId.Value, fromDate, toDate, asOfMonth, sessionId, collectedByUserId, exactRange);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: AssistantWallet
    // GET /api/v1/assistants/{assistantId}/wallet?page=&limit=
    // Wallet card + paginated recent collections.
    // AUTH: Teacher (module) → requested assistant. Assistant with Payment.ViewCollectorSummary →
    //   forced to their OWN wallet. Withdraw (below) stays tutor-only. [interim — TODO(assistant-dashboard)]
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("/api/v1/assistants/{assistantId:long}/wallet")]
    // TODO(assistant-dashboard): interim. Was roleOnly tutor-only; an assistant caller is now forced
    // to their OWN wallet (route assistantId ignored for assistants — see the service). This is the
    // assistant's own-collections view; a dedicated assistant dashboard is TBD (frontend + backend).
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewCollectorSummary)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.AssistantWalletScreenResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAssistantWallet(
        long assistantId,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Assistant → forced to their own wallet; Teacher/SuperAdmin → the requested assistant.
        // B2: optional `search` filters the collections list by student name/code.
        var result = await _screenService.GetAssistantWalletScreenAsync(
            teacherId.Value, assistantId, page, limit, AssistantScopeUserId(), search);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: AssistantWallet — withdrawal (reset) history
    // GET /api/v1/assistants/{assistantId}/wallet/withdrawals
    // Every cash hand-over the teacher took from this assistant's wallet (newest first) — the
    // "receipt" trail so withdrawn money is no longer invisible after it clears the wallet.
    // AUTH: Teacher only. An assistant is blocked (they must not read a peer's withdrawal history,
    // and the service is teacher-scoped by assistantId, not caller-scoped).
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("/api/v1/assistants/{assistantId:long}/wallet/withdrawals")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewCollectorSummary)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<List<Edvanz.Application.Dtos.Payment.WalletResetLogDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAssistantWalletWithdrawals(long assistantId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Teacher-only: an assistant caller (own scope resolves non-null) is forbidden.
        if (AssistantScopeUserId() is not null) return Forbid();

        var result = await _screenService.GetWalletWithdrawalHistoryAsync(
            teacherId.Value, assistantId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: CollectPayment (student list)
    // GET /api/v1/payments/collect/students?filter=&search=&page=&limit=&sessionId=
    // sessionId (optional): scope to that session PLUS its linked sessions (mirrors the take-attendance
    //   roster) so a session-launched collect lists only the relevant students. Omitted = teacher-wide.
    // AUTH: Teacher (module) OR Assistant with Payment.Collect.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("/api/v1/payments/collect/students")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.CollectStudentsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetCollectStudents(
        [FromQuery] string? filter = "all",
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] long? sessionId = null)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _screenService.GetCollectStudentsAsync(
            teacherId.Value, filter, search, page, limit, sessionId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: PaymentTracking (students by status)
    // GET /api/v1/payments/students?month=YYYY-MM&status=paid|prorated|unpaid&sessionId=&search=&page=&limit=
    // B1: `status` is OPTIONAL — omitted returns the whole (session-scoped) roster, each student
    //   carrying its own status (paid|prorated|unpaid). `sessionId` restricts to one session's
    //   assigned students; `search` filters by student name OR studentCode (case-insensitive).
    // AUTH: Teacher (module) OR Assistant with Payment.ViewUnpaidStudents.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("/api/v1/payments/students")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewUnpaidStudents)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.StudentsByStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetStudentsByStatus(
        [FromQuery] string? month,
        [FromQuery] string? status,
        [FromQuery] long? sessionId = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _screenService.GetStudentsByStatusAsync(
            teacherId.Value, month, status, page, limit, sessionId, search);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: SessionPaymentCollectedByYear
    // GET /api/v1/payments/collections/yearly?month=&year=&page=&limit=
    // DASH-1: a yearly view needs only the YEAR — derived from the unified "YYYY-MM" `month`
    //   string (its year) OR the legacy integer `year`. Both optional → current local year.
    // AUTH: Teacher (module) OR Assistant with Payment.ViewHistory.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("/api/v1/payments/collections/yearly")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewHistory)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.YearlyCollectionsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetYearlyCollections(
        [FromQuery] string? month,
        [FromQuery] int? year,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _screenService.GetYearlyCollectionsAsync(
            teacherId.Value, month, year, page, limit);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: CollectPaymentSession (lookup)
    // GET /api/v1/collect/lookup?qr=&code=&name=&month=YYYY-MM
    // month (optional): arrears computed THROUGH that month (month-scoped screens) instead
    // of through the current month.
    // AUTH: Teacher (module) OR Assistant with Payment.Collect.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("/api/v1/collect/lookup")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.CollectLookupResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ResolveLookup(
        [FromQuery] string? qr,
        [FromQuery] string? code,
        [FromQuery] string? name,
        [FromQuery] string? month)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _screenService.ResolveLookupAsync(teacherId.Value, qr, code, name, month);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: PaymentTracking (monthly aggregate — loads the whole screen)
    // GET /api/v1/payments/tracking?month=YYYY-MM
    // AUTH: Teacher (module) OR Assistant with Payment.ViewUnpaidStudents.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("/api/v1/payments/tracking")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewUnpaidStudents)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.TrackingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetTracking([FromQuery] string? month)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _screenService.GetTrackingAsync(teacherId.Value, month);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: CollectPayment — bulk mark-paid  (MONEY)
    // POST /api/v1/payments/collect/mark-paid   body { studentIds: [], month? }
    // month (optional YYYY-MM): charge arrears THROUGH that month only (the month the
    // initiating screen was opened on) instead of through the current month.
    // Header: Idempotency-Key (optional) — replay returns the original result.
    // AUTH: Teacher (module) OR Assistant with Payment.Collect.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("/api/v1/payments/collect/mark-paid")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.MarkPaidResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> MarkPaid(
        [FromBody] MarkPaidRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _screenService.MarkPaidAsync(
            teacherId.Value, GetActingUserId(),
            request?.StudentIds ?? new List<long>(), idempotencyKey, request?.Month,
            request?.DuplicateConfirmed ?? false);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: CollectPaymentSession — submit batch  (MONEY)
    // POST /api/v1/collect/submit   body { month?, classSessionId?, note?, students: [{studentId, amount, note?}] }
    // Header: Idempotency-Key (optional) — replay returns the original result. 409 on empty batch.
    // AUTH: Teacher (module) OR Assistant with Payment.Collect.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("/api/v1/collect/submit")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.SubmitCollectionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SubmitCollection(
        [FromBody] SubmitCollectionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _screenService.SubmitCollectionAsync(
            teacherId.Value, GetActingUserId(),
            request?.Month, request?.ClassSessionId,
            request?.Students ?? new List<SubmitCollectionItem>(), request?.Note, idempotencyKey);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: AssistantWallet — withdraw  (MONEY, TUTOR-ONLY)
    // POST /api/v1/assistants/{assistantId}/wallet/withdraw   body { amount? }
    // Header: Idempotency-Key (optional). The tutor takes collected cash from the assistant.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("/api/v1/assistants/{assistantId:long}/wallet/withdraw")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.WalletWithdrawResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> WithdrawFromWallet(
        long assistantId,
        [FromBody] WalletWithdrawRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Tutor / center OWNER only — an assistant caller (teacher- or center-assistant) cannot withdraw.
        if (AssistantScopeUserId() is not null) return Forbid();

        var result = await _screenService.WithdrawAsync(
            teacherId.Value, assistantId, request?.Amount, GetActingUserId(), idempotencyKey);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: Forgive balance  (MONEY, TUTOR-ONLY — assistants/center-assistants 403)
    // POST /api/v1/payments/forgive   body { teacherStudentId, amount, note? }
    // Waives part of a student's outstanding balance. NOT cash: no wallet/transaction/collector
    // effect. Applied oldest-unpaid-month first (cascade). Reversible + audited.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("/api/v1/payments/forgive")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.ForgiveBalanceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ForgiveBalance([FromBody] ForgiveBalanceRequest request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Tutor / center OWNER only — an assistant caller (teacher- or center-assistant) is blocked.
        // Forgiving reduces revenue owed; it must never be delegated (BR-PAY-002 class of action).
        if (AssistantScopeUserId() is not null) return Forbid();

        var result = await _screenService.ForgiveBalanceAsync(
            teacherId.Value, GetActingUserId(),
            request?.TeacherStudentId ?? 0, request?.Amount ?? 0m, request?.Note);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: Reverse a forgiveness  (MONEY, TUTOR-ONLY)
    // POST /api/v1/payments/forgive/{forgivenessId}/reverse   body { note? }
    // Restores the exact per-period balance the forgiveness waived; audits the reversal.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("/api/v1/payments/forgive/{forgivenessId:long}/reverse")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.ForgiveBalanceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReverseForgiveness(
        long forgivenessId,
        [FromBody] ReverseForgivenessRequest? request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        if (AssistantScopeUserId() is not null) return Forbid();

        var result = await _screenService.ReverseForgivenessAsync(
            teacherId.Value, GetActingUserId(), forgivenessId, request?.Note);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Screen: CollectPayment — set/clear a student's JOINING-MONTH proration amount (REQ-PAY-021/022)
    // PUT /api/v1/payments/students/{teacherStudentId}/proration   body { amount: number|null }
    // Teacher-decided proration: type the exact first-month amount (snapped to 5, 0..full month), or
    // send null to clear the override and revert to the method's auto suggestion. Allowed for the
    // teacher AND assistants (the acting user is resolved from the JWT and recorded).
    // AUTH: Teacher (module) OR Assistant with Payment.Collect.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("/api/v1/payments/students/{teacherStudentId:long}/proration")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.ProrationUpdateResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetProration(
        long teacherStudentId,
        [FromBody] SetProrationRequest? request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _screenService.SetProrationAmountAsync(
            teacherId.Value, GetActingUserId(), teacherStudentId, request?.Amount);
        return ToResponse(result);
    }
}
