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
/// Payment Module (Module 4) — teacher and assistant endpoints.
///
/// ARCHITECTURE CHANGES (auth rebase):
/// <list type="bullet">
///   <item>Base class changed from <see cref="ApiBaseController"/> →
///         <see cref="ModuleSixApiBaseController"/> to gain
///         <c>ResolveTeacherIdAsync()</c> and <c>GetActingUserId()</c>.</item>
///   <item>Class-level <c>[Authorize]</c> added — every action now requires a
///         valid JWT.</item>
///   <item><c>{teacherId}</c> removed from every route. The tenant id is
///         derived from the JWT only (catalog §1.3 / REQ-PAY-NFR-001).</item>
///   <item>Every action carries <c>[ModulePermission]</c>:
///         teacher → module-only; assistant → module + named permission;
///         SuperAdmin → bypass (per PermissionHandler decision matrix).</item>
///   <item>Actor-identity fields (<c>CollectedByUserId</c>,
///         <c>EditedByUserId</c>, <c>ResetByUserId</c>,
///         <c>ConfirmedByUserId</c>, <c>TransferredByUserId</c>) are now
///         always populated from <c>GetActingUserId()</c> — never from the
///         request body or query string (REQ-PAY-011 / BR-PAY-004).</item>
/// </list>
///
/// ABSOLUTE TUTOR-ONLY GATES (BR-PAY-002 / REQ-PAY-027 / REQ-PAY-036) —
/// money-modifying, never delegated:
///   Edit, Delete, Custom-Amount Set, Wallet reset/withdraw, Departure, Transfer —
///   all use <c>roleOnly: ["Teacher","SuperAdmin"]</c>.
///
/// INTERIM ASSISTANT-SCOPED READS — TODO(assistant-dashboard):
///   Dashboard, Wallets (view list + detail) and Collector-summary are permission-gated
///   (<c>ViewCollectorSummary</c>) and, for an assistant caller, return ONLY that assistant's
///   OWN data (own wallet / own collections; teacher-wide figures come back <c>null</c>).
///   Sessions collection-summary stays tutor-only. This is a first-cut — a proper dedicated
///   assistant dashboard is still to be built end-to-end by frontend + backend.
///
/// ASSISTANT-DELEGATABLE GATES (REQ-USR-018):
///   Collect, View History, View Unpaid Students, View Collector Summary,
///   Generate Reports, Sync — use named <c>[ModulePermission]</c> attributes
///   and are accessible to assistants when the permission is granted.
/// </summary>
[Authorize]
public sealed class PaymentController : ModuleSixApiBaseController
{
    private readonly IPaymentService _paymentService;
    private readonly IPaymentReportService _reportService;

    public PaymentController(
        IPaymentService paymentService,
        IPaymentReportService reportService,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
        : base(currentUser, unitOfWork)
    {
        _paymentService = paymentService;
        _reportService = reportService;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 1: COLLECT PAYMENT
    // POST api/payment/collect
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Collects a payment from a student for a session.
    //   REQ-PAY-001/002: All four collection methods produce a unified record.
    //   REQ-PAY-011: CollectedByUserId always sourced from JWT (acting user).
    //   REQ-PAY-018/019: Auto-assigned to earliest unpaid period (BR-PAY-001).
    //   REQ-PAY-020/026: Duplicate and already-paid detection.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.Collect permission.
    //
    // TABLES WRITTEN: PaymentTransactions, PaymentPeriods, StudentPaymentCounters, AssistantWallets
    // TABLES READ: Teachers, Sessions, TeacherStudents, StudentSessionAssignments, PaymentPeriods
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("collect")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.CollectPaymentResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CollectPayment([FromBody] CollectPaymentDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // REQ-PAY-011: actor identity is always the authenticated user — never client-supplied.
        dto.TeacherId = teacherId.Value;
        dto.CollectedByUserId = GetActingUserId();

        var result = await _paymentService.CollectPaymentAsync(dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 2: GET STUDENT PAYMENT STATUS
    // GET api/payment/students/{teacherStudentId}/payment-status
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns the payment status for a student on the collection screen.
    //   REQ-PAY-NFR-006: Status shown immediately after student identification.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.Collect permission.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("students/{teacherStudentId:long}/payment-status")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.PaymentStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentPaymentStatus([FromRoute] long teacherStudentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetStudentPaymentStatusAsync(teacherId.Value, teacherStudentId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 3: CHECK DUPLICATE PAYMENT
    // GET api/payment/students/{teacherStudentId}/duplicate-check
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Checks for same-day or already-paid-period duplicates before collection.
    //   REQ-PAY-020/026: Pre-collection safety check.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.Collect permission.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("students/{teacherStudentId:long}/duplicate-check")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.DuplicateCheckResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CheckDuplicatePayment([FromRoute] long teacherStudentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.CheckDuplicatePaymentAsync(teacherId.Value, teacherStudentId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 4: EDIT PAYMENT
    // PUT api/payment/transactions/{transactionId}
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Edits an existing payment transaction.
    //   BR-PAY-002: ABSOLUTE — only the tutor role may edit payment records.
    //   This restriction cannot be delegated to assistants under any configuration.
    //
    // AUTH: Teacher or SuperAdmin ONLY (roleOnly gate).
    //
    // TABLES WRITTEN: PaymentTransactions, PaymentEditLogs, PaymentPeriods, StudentPaymentCounters
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("transactions/{transactionId:long}")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.PaymentTransactionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EditPayment(
        [FromRoute] long transactionId,
        [FromBody] EditPaymentDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Actor identity from JWT — never from request body (BR-PAY-002).
        dto.TeacherId = teacherId.Value;
        dto.TransactionId = transactionId;
        dto.EditedByUserId = GetActingUserId();
        // Feature B: this single-edit endpoint requires a note when the amount becomes partial/custom.
        dto.EnforceNoteOnPartial = true;

        var result = await _paymentService.EditPaymentAsync(dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 5: DELETE PAYMENT
    // DELETE api/payment/transactions/{transactionId}
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Soft-deletes a payment transaction.
    //   BR-PAY-002: ABSOLUTE — only the tutor role may delete payment records.
    //
    // AUTH: Teacher or SuperAdmin ONLY (roleOnly gate).
    //   deletedByUserId is sourced from JWT — no longer a query param.
    //
    // TABLES WRITTEN: PaymentTransactions, PaymentEditLogs, PaymentPeriods, StudentPaymentCounters
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpDelete("transactions/{transactionId:long}")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePayment([FromRoute] long transactionId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // deletedByUserId is the authenticated user — never a client-supplied query param.
        long actingUserId = GetActingUserId();
        var result = await _paymentService.DeletePaymentAsync(teacherId.Value, transactionId, actingUserId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 5b: PAYMENT EDIT HISTORY
    // GET api/payment/transactions/{transactionId}/edit-logs
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns the edit/modification log for a specific payment transaction.
    //   REQ-PAY-027: Audit trail of changes.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.ViewHistory permission.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("transactions/{transactionId:long}/edit-logs")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewHistory)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.List<Edvanz.Application.Dtos.Payment.PaymentEditLogDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPaymentEditLogs([FromRoute] long transactionId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetPaymentEditHistoryAsync(teacherId.Value, transactionId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 6: STUDENT PAYMENT HISTORY
    // GET api/payment/students/{teacherStudentId}/history
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns complete payment history for a student across all sessions.
    //   REQ-PAY-052/092: Unified timeline.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.ViewHistory permission.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("students/{teacherStudentId:long}/history")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewHistory)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<Edvanz.Application.Dtos.Payment.PaymentHistoryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentHistory(
        [FromRoute] long teacherStudentId,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetStudentPaymentHistoryAsync(
            teacherId.Value, teacherStudentId, startDate, endDate, page, pageSize);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 7: GET CUSTOM AMOUNT
    // GET api/payment/students/{teacherStudentId}/custom-amount
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns the student's current custom payment amount (null = session default).
    //   REQ-PAY-016: Custom amount override per student.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.ViewHistory permission.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("students/{teacherStudentId:long}/custom-amount")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewHistory)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<decimal?>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCustomAmount([FromRoute] long teacherStudentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetCustomAmountAsync(teacherId.Value, teacherStudentId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 8: SET CUSTOM AMOUNT
    // PUT api/payment/students/{teacherStudentId}/custom-amount
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Sets a custom payment amount for a student.
    //   REQ-PAY-016 / BR-PAY-003: Tutor-only; overrides session default permanently.
    //
    // AUTH: Teacher or SuperAdmin ONLY (roleOnly gate).
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("students/{teacherStudentId:long}/custom-amount")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.CustomAmountRepriceSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetCustomAmount(
        [FromRoute] long teacherStudentId,
        [FromBody] SetCustomAmountDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        dto.TeacherId = teacherId.Value;
        dto.TeacherStudentId = teacherStudentId;

        var result = await _paymentService.SetCustomAmountAsync(dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 9: UNPAID STUDENTS OVERVIEW
    // GET api/payment/unpaid
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns paginated unpaid students view with filters.
    //   REQ-PAY-031/032/033: Filterable, searchable, with consecutive count.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.ViewUnpaidStudents permission.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("unpaid")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewUnpaidStudents)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.Payment.UnpaidStudentDto>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetUnpaidStudents([FromQuery] UnpaidStudentsFilterDto filter)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetUnpaidStudentsAsync(teacherId.Value, filter);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 10: UNPAID COUNT BADGE (PER SESSION)
    // GET api/payment/sessions/{sessionId}/unpaid-count
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns the unpaid student count badge for a session.
    //   REQ-PAY-028: Notification badge on session view.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.ViewUnpaidStudents permission.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("sessions/{sessionId:long}/unpaid-count")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewUnpaidStudents)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetUnpaidCountBySession([FromRoute] long sessionId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetUnpaidCountBySessionAsync(teacherId.Value, sessionId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 11: COLLECTOR SUMMARY
    // GET api/payment/collectors
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns per-user collection breakdown.
    //   REQ-PAY-013: User Collection View.
    //   REQ-PAY-014 / REQ-USR-018: Full view is tutor-only; the service layer
    //   MUST filter results to the assistant's own records when caller is Assistant.
    //   TODO (service gap): PaymentService.GetCollectorSummaryAsync does not yet
    //   filter by caller role — add caller role + userId parameter and filter there.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.ViewCollectorSummary permission.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("collectors")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewCollectorSummary)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.List<Edvanz.Application.Dtos.Payment.CollectorSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCollectorSummary(
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Assistant → only their own collection summary; Teacher/SuperAdmin → all collectors.
        var result = await _paymentService.GetCollectorSummaryAsync(
            teacherId.Value, startDate, endDate, AssistantScopeUserId());
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 12: GET ALL WALLETS
    // GET api/payment/wallets
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns all assistant wallets for the teacher, plus the combined total
    //   currently held across all assistants (sum of each wallet's CurrentBalance).
    //   REQ-PAY-035: Tutor views current wallet balance of each assistant.
    //
    // AUTH: Teacher (module) → all assistants. Assistant with Payment.ViewCollectorSummary →
    //   ONLY their own wallet. [interim — TODO(assistant-dashboard)]
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("wallets")]
    // TODO(assistant-dashboard): interim. Was roleOnly tutor-only; an assistant now sees ONLY
    // their own wallet (scoped in the service). Full multi-assistant view stays for the tutor.
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewCollectorSummary)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.AssistantWalletsSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllWallets()
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Assistant → only their own wallet; Teacher/SuperAdmin → every assistant's wallet.
        var result = await _paymentService.GetAllWalletsAsync(
            teacherId.Value, AssistantScopeUserId());
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 13: GET WALLET DETAIL
    // GET api/payment/wallets/{assistantId}
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns the itemized wallet detail for a specific assistant.
    //   REQ-PAY-035: Full collection list per assistant.
    //
    // AUTH: Teacher (module) → requested assistant. Assistant with Payment.ViewCollectorSummary →
    //   forced to their OWN wallet. [interim — TODO(assistant-dashboard)]
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("wallets/{assistantId:long}")]
    // TODO(assistant-dashboard): interim. Was roleOnly tutor-only; an assistant caller is now forced
    // to their OWN wallet (the route assistantId is ignored for assistants — see the service).
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewCollectorSummary)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.AssistantWalletDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWalletDetail([FromRoute] long assistantId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Assistant → forced to their own wallet; Teacher/SuperAdmin → the requested assistant.
        var result = await _paymentService.GetWalletDetailAsync(
            teacherId.Value, assistantId, AssistantScopeUserId());
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 14: RESET WALLET
    // POST api/payment/wallets/{assistantId}/reset
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Resets an assistant's wallet to zero (cash handover confirmed).
    //   REQ-PAY-036/037: Permanent ledger event — never deleted.
    //   REQ-PAY-NFR-009: Confirmation UI required before calling this endpoint.
    //   BR-PAY-002: ABSOLUTE — only the tutor role may reset wallets.
    //
    // AUTH: Teacher or SuperAdmin ONLY (roleOnly gate).
    //   ResetByUserId sourced from JWT — never from request body.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("wallets/{assistantId:long}/reset")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.WalletResetLogDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetWallet(
        [FromRoute] long assistantId,
        [FromBody] WalletResetDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Only the owning tutor / center OWNER may reset a wallet (BR-PAY-002). An assistant caller —
        // teacher-assistant OR center-assistant — is blocked (they collect, they don't take the cash).
        if (AssistantScopeUserId() is not null) return Forbid();

        // Actor identity from JWT — WalletResetDto.ResetByUserId is never trusted from the body.
        dto.TeacherId = teacherId.Value;
        dto.AssistantId = assistantId;
        dto.ResetByUserId = GetActingUserId();

        var result = await _paymentService.ResetWalletAsync(dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 15: PAYMENT OVERVIEW DASHBOARD
    // GET api/payment/dashboard
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns expected/collected/remaining revenue with session and collector
    //   breakdowns.
    //   REQ-PAY-039–044: Payment Overview Dashboard.
    //
    // AUTH: Teacher (module) → full view. Assistant with Payment.ViewCollectorSummary → OWN-scoped
    //   view (their collected only; teacher-wide figures null). [interim — TODO(assistant-dashboard)]
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("dashboard")]
    // TODO(assistant-dashboard): interim. Was roleOnly tutor-only; now permission-gated so an
    // assistant can open an OWN-scoped dashboard (teacher-wide figures come back null — scoped in
    // the service). A proper dedicated assistant dashboard is to be built by frontend + backend.
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewCollectorSummary)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.PaymentDashboardDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetDashboard([FromQuery] PaymentDashboardFilterDto filter)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Assistant → dashboard scoped to their own collections; Teacher/SuperAdmin → full view.
        var result = await _paymentService.GetDashboardAsync(
            teacherId.Value, filter, AssistantScopeUserId());
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 15b: SESSIONS COLLECTION SUMMARY
    // GET api/payment/sessions/collection-summary
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns the "Collected by Sessions" card: one row per currently active
    //   session (EndDate >= today) with collected amount, paid/total student
    //   counts, and progress percentage.
    //   REQ-PAY-043: Per-session collection progress while the session is active.
    //
    // AUTH: Teacher or SuperAdmin ONLY (roleOnly gate) — same tutor-only gate
    // as Dashboard/Wallets.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("sessions/collection-summary")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<System.Collections.Generic.List<Edvanz.Application.Dtos.Payment.SessionCollectionSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSessionsCollectionSummary()
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetSessionsCollectionSummaryAsync(teacherId.Value);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 16: DEPARTURE SUMMARY
    // GET api/payment/students/{teacherStudentId}/departure-summary
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Calculates and returns the pre-departure financial summary.
    //   REQ-PAY-067/068/072: Pro-rated obligation, departure summary screen.
    //
    // AUTH: Payment.Collect permission (module-gated). Teacher/SuperAdmin pass
    //   automatically; assistants with Collect can view the departure summary
    //   (kept consistent with ConfirmDeparture below).
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("students/{teacherStudentId:long}/departure-summary")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.DepartureSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetDepartureSummary([FromRoute] long teacherStudentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetDepartureSummaryAsync(teacherId.Value, teacherStudentId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 17: CONFIRM DEPARTURE
    // POST api/payment/departure/confirm
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Confirms a student departure: unassigns, records event, handles refund/charge.
    //   REQ-PAY-073/075: Departure confirmed, optional tutor override.
    //
    // AUTH: Payment.Collect permission (module-gated, not role-only).
    //   Teacher/SuperAdmin still pass automatically — Teacher via the module-only gate,
    //   SuperAdmin via the unconditional bypass in PermissionHandler. Assistants who hold
    //   Collect can confirm departures (it moves money, same class of action as a
    //   collection/refund). Merged onto Collect because the dedicated ConfirmDeparture
    //   permission was never seeded in DbInitializer and so was ungrantable — no assistant
    //   could ever be given it. ConfirmedByUserId sourced from JWT — never from request body.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("departure/confirm")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.StudentDepartureDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ConfirmDeparture([FromBody] ConfirmDepartureDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        dto.TeacherId = teacherId.Value;
        dto.ConfirmedByUserId = GetActingUserId();

        var result = await _paymentService.ConfirmDepartureAsync(dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DEPARTED STUDENTS LIST
    // GET api/payment/departures?search=&page=&limit=&from=&to=
    // Teacher-wide list of departed students (from the permanent StudentDeparture
    // records), newest first. AUTH: Payment.Collect (Teacher/SuperAdmin auto-pass).
    //
    // from/to bound DepartedAt as [from, to) over RAW UTC INSTANTS — DepartedAt is UTC and the
    // per-row dayKey is derived from it, so the client must send full instants ('Z'), never bare
    // dates. That is a deliberate contract, not an inference: the collections endpoint's habit of
    // guessing intent from whether a value carried a time component is exactly what mis-scoped the
    // wallet day filter, and these are brand-new parameters with no legacy caller to accommodate.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("departures")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.DeparturesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetDepartures(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int limit = 20,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetDeparturesAsync(
            teacherId.Value, search, page, limit, from, to);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 18: TRANSFER SUMMARY
    // GET api/payment/students/{teacherStudentId}/transfer-summary
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Calculates the pre-transfer financial summary.
    //   REQ-PAY-088: Pre-transfer summary shown before confirmation.
    //
    // AUTH: Teacher or SuperAdmin ONLY (roleOnly gate).
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("students/{teacherStudentId:long}/transfer-summary")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.TransferSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransferSummary(
        [FromRoute] long teacherStudentId,
        [FromQuery] long sourceSessionId,
        [FromQuery] long destinationSessionId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetTransferSummaryAsync(
            teacherId.Value, teacherStudentId, sourceSessionId, destinationSessionId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 19: CONFIRM TRANSFER
    // POST api/payment/transfer/confirm
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Confirms a session transfer with full payment data continuity.
    //   REQ-PAY-085–092: History preserved, balance carried forward.
    //
    // AUTH: Teacher or SuperAdmin ONLY (roleOnly gate).
    //   TransferredByUserId sourced from JWT — never from request body.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("transfer/confirm")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.SessionTransferEventDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ConfirmTransfer([FromBody] ConfirmTransferDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        dto.TeacherId = teacherId.Value;
        dto.TransferredByUserId = GetActingUserId();

        var result = await _paymentService.ConfirmTransferAsync(dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 20: OFFLINE SYNC
    // POST api/payment/sync
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Syncs offline-collected payment records to the server.
    //   REQ-PAY-080/081: Background sync on reconnection.
    //   REQ-PAY-082: Conflict detection and resolution.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.Collect permission.
    //   CollectedByUserId on each offline record is set to the authenticated
    //   user — the same person who collected and is now syncing.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("sync")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.PaymentSyncResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SyncOfflinePayments([FromBody] OfflinePaymentSyncRequestDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        long actingUserId = GetActingUserId();
        dto.TeacherId = teacherId.Value;

        // Actor identity applied to every offline record — client-supplied CollectedByUserId is discarded.
        foreach (var record in dto.OfflineRecords)
        {
            record.TeacherId = teacherId.Value;
            record.CollectedByUserId = actingUserId;
        }

        var result = await _paymentService.SyncOfflinePaymentsAsync(dto);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 21: STUDENT/PARENT PAYMENT VIEW
    // GET api/payment/students/{teacherStudentId}/payment-view
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Returns payment data for a student/parent viewing their own payments.
    //   Gated by TeacherConfiguration visibility settings (service-side).
    //
    // AUTH: Teacher (module) OR Assistant with Payment.ViewHistory permission.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("students/{teacherStudentId:long}/payment-view")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionViewHistory)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.StudentPaymentViewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStudentPaymentView([FromRoute] long teacherStudentId)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _paymentService.GetStudentPaymentViewAsync(teacherId.Value, teacherStudentId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 22: GENERATE REPORT
    // POST api/payment/reports/generate
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Generates one of the 10 payment report types (REQ-PAY-052–061).
    //   REQ-PAY-048/049: Standard header on all reports.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.GenerateReports permission.
    //
    // NOTE (P2): IPaymentReportExportService is a stub — PDF/Excel export
    //   (REQ-PAY-050) is not yet implemented. Returns stub bytes.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("reports/generate")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionGenerateReports)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GenerateReport([FromBody] PaymentReportRequestDto request)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _reportService.GenerateReportAsync(teacherId.Value, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 23: EXPORT REPORT
    // POST api/payment/reports/export
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Exports a generated payment report as PDF or XLSX.
    //   REQ-PAY-050: Exportable formats.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.GenerateReports permission.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("reports/export")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionGenerateReports)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ExportReport(
        [FromBody] PaymentReportRequestDto request,
        [FromQuery] string format = "xlsx")
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        var result = await _reportService.ExportReportAsync(teacherId.Value, request, format);
        if (!result.IsSuccess)
            return ToResponse(result);

        var contentType = format == "pdf"
            ? "application/pdf"
            : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        var fileName = $"payment-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{format}";

        return File(result.Data!, contentType, fileName);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 1b: BATCH COLLECT PAYMENT
    // POST api/payment/collect/batch
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Collects payments for multiple students in one request ("Mark N students
    //   as Paid" — the multi-select collection screen). Best-effort partial success:
    //   each student is processed independently and reported as Collected,
    //   NeedsConfirmation, or Failed. Delegates every business rule to
    //   CollectPaymentAsync (single source of truth) — no logic duplicated.
    //
    //   No explicit REQ-PAY covers batch collection; it is UI-derived. Per-student
    //   behavior is governed by REQ-PAY-001/002/018/019/020/026 and BR-PAY-001.
    //
    // AUTH: Teacher (module) OR Assistant with Payment.Collect permission.
    //   TeacherId and CollectedByUserId come from the JWT, never the request body
    //   (REQ-PAY-011 / BR-PAY-004).
    //
    // TABLES WRITTEN (per collected item): PaymentTransactions, PaymentPeriods,
    //   StudentPaymentCounters, AssistantWallets
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("collect/batch")]
    [ModulePermission(PaymentConstants.ModuleName, PaymentConstants.PermissionCollect)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.BatchCollectResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BatchCollectPayment([FromBody] BatchCollectPaymentDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // REQ-PAY-011: actor identity is always the authenticated user — never client-supplied.
        long actingUserId = GetActingUserId();

        var result = await _paymentService.BatchCollectPaymentAsync(dto, teacherId.Value, actingUserId);
        return ToResponse(result);
    }
    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 4b: BATCH EDIT PAYMENTS
    // PUT api/payment/transactions/batch
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Edits multiple payment transactions in one request ("Saved N changes" — the
    //   multi-row edit screen, D2). Best-effort partial success: each transaction is
    //   edited independently and reported as Succeeded or Failed. Delegates every
    //   business rule to EditPaymentAsync (single source of truth) — no logic duplicated.
    //   Reversal is expressed as a status change per item and logged as Reversed.
    //
    // AUTH: Teacher or SuperAdmin ONLY (roleOnly gate) — BR-PAY-002, identical to Edit.
    //   TeacherId and EditedByUserId come from the JWT, never the request body.
    //
    // TABLES WRITTEN (per succeeded item): PaymentTransactions, PaymentEditLogs,
    //   PaymentPeriods, StudentPaymentCounters
    //
    // ROUTING: "transactions/batch" cannot collide with "transactions/{transactionId:long}"
    //   — the :long constraint rejects the non-numeric "batch" segment.
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("transactions/batch")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.BatchEditResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BatchEditPayment([FromBody] BatchEditPaymentDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Actor identity is always the authenticated user — never client-supplied (BR-PAY-002).
        long actingUserId = GetActingUserId();

        var result = await _paymentService.BatchEditPaymentAsync(dto, teacherId.Value, actingUserId);
        return ToResponse(result);
    }
    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 5c: BATCH REVERT PAYMENTS
    // POST api/payment/transactions/batch-revert
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Reverts multiple payment transactions in one request ("Revert (N students)" — D1).
    //   A revert zeroes the collected amount and marks the transaction Unpaid, reusing
    //   EditPaymentAsync (single source of truth) — no new reversal logic. Best-effort
    //   partial success: each transaction is reverted independently and reported as
    //   Succeeded or Failed. The shared Reason is recorded on every PaymentEditLog.
    //
    //   NOTE: reverts log as PaymentEditAction.AmountChanged today — the Reversed enum
    //   value is not yet emitted by the edit path (see follow-up R2).
    //
    // AUTH: Teacher or SuperAdmin ONLY (roleOnly gate) — BR-PAY-002, identical to Edit/Delete.
    //   TeacherId and EditedByUserId come from the JWT, never the request body.
    //
    // TABLES WRITTEN (per reverted item): PaymentTransactions, PaymentEditLogs,
    //   PaymentPeriods, StudentPaymentCounters
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("transactions/batch-revert")]
    [ModulePermission(roles: new[] { "Teacher", "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Payment.BatchRevertResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BatchRevertPayment([FromBody] BatchRevertPaymentDto dto)
    {
        long? teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null) return TeacherNotResolved();

        // Actor identity is always the authenticated user — never client-supplied (BR-PAY-002).
        long actingUserId = GetActingUserId();

        var result = await _paymentService.BatchRevertPaymentAsync(dto, teacherId.Value, actingUserId);
        return ToResponse(result);
    }
}
