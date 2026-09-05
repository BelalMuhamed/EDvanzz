using Edvanz.API.Attributes;
using Edvanz.Application.Dtos.Subscription;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace Edvanz.API.Controllers;

/// <summary>
/// Subscription Management Module — super-admin endpoints (§4.4 / FR-SUB-060…064).
///
/// AUTHORIZATION:
///   Class-level [Authorize] requires authentication.
///   Every action carries [ModulePermission(roles: ["SuperAdmin"], roleOnly: true)].
///   The ActiveSubscriptionHandler short-circuits SuperAdmin role on its fast path
///   so no [AllowExpiredSubscription] is needed here.
///
/// AUDIT:
///   Every write captures the calling admin's user id via ICurrentUserService and
///   passes it to the service layer as resolvedByUserId / adminUserId so
///   TeacherSubscription.CreatedByUserId records who took the action (REQ-ADM-016 / FR-SUB-064).
/// </summary>
[Route("api/admin/subscriptions")]
[Authorize]
public class AdminSubscriptionController : ApiBaseController
{
    private readonly IAdminSubscriptionService _adminService;
    private readonly ICurrentUserService _currentUser;

    public AdminSubscriptionController(
        IAdminSubscriptionService adminService,
        ICurrentUserService currentUser)
    {
        _adminService = adminService;
        _currentUser = currentUser;
    }
    [HttpPut("teachers/{teacherId:long}/capacity")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.CapacityRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetTeacherCapacity(
    [FromRoute] long teacherId,
    [FromBody] Edvanz.Application.Dtos.Subscription.AdminSetCapacityRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.SetTeacherCapacityAsync(adminUserId.Value, teacherId, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT: SET TEACHER STUDENT-APP-ACCOUNT LIMIT
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Sets Teacher.LinkedStudentCapacity — how many student APP ACCOUNTS may be
    //   linked at once. This is the PRICED limit (limit × per-student rate); the
    //   students-in-the-account limit above is a free operational quota.
    //
    //   Unlike the capacity endpoint above this allows BOTH increase and decrease —
    //   tuning the paid limit down is the point. A decrease never unlinks anyone; it
    //   only blocks NEW links until usage falls back under the limit. Raising it past
    //   StudentCapacity raises that limit to match.
    //
    // TABLES WRITTEN: Teachers (LinkedStudentCapacity, possibly StudentCapacity),
    //                 CapacityIncreaseRequests (Approved audit row, kind LinkedStudents)
    //
    // SAMPLE: PUT /api/admin/subscriptions/teachers/42/linked-capacity
    //   { "newCapacity": 300, "note": "moved to the 300-account package" }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("teachers/{teacherId:long}/linked-capacity")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.CapacityRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetTeacherLinkedCapacity(
        [FromRoute] long teacherId,
        [FromBody] Edvanz.Application.Dtos.Subscription.AdminSetLinkedCapacityRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.SetTeacherLinkedCapacityAsync(adminUserId.Value, teacherId, request);
        return ToResponse(result);
    }
    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT: CANCEL (REQ-ADM-013)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Immediately expires the teacher's CURRENT subscription in place
    //   (EndDate = UtcNow, IsCurrent unchanged). Tutor drops to free tier on next
    //   request. Reversible via activate/extend/end-date; history preserved.
    //   404 NoActiveSubscription when the teacher has no current subscription row.
    //
    // TABLES WRITTEN: TeacherSubscriptions (EndDate update on current row)
    //
    // SAMPLE: POST /api/admin/subscriptions/cancel
    //   { "teacherId": 42 }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("cancel")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.CurrentSubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel([FromBody] AdminCancelRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.CancelAsync(adminUserId.Value, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 1: ACTIVATE (FR-SUB-060 / REQ-ADM-012)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Manually activates a teacher's subscription with no payment record.
    //   Inserts a new TeacherSubscription row with PaymentChannel = SuperAdminOverride.
    //
    // TABLES WRITTEN: TeacherSubscriptions (flips previous IsCurrent + inserts new)
    // CACHE: invalidated synchronously after commit
    //
    // SAMPLE: POST /api/admin/subscriptions/activate
    //   { "teacherId": 42, "startDate": null, "endDate": null }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("activate")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.CurrentSubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate([FromBody] AdminActivateRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.ActivateAsync(adminUserId.Value, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 1b: ACTIVATE MANAGERIAL
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Manually activates a teacher's subscription as a MANAGERIAL plan (no payment
    //   record, PaymentChannel = SuperAdminOverride, PlanType = Managerial). While the
    //   managerial subscription is current + active, no student or parent account may be
    //   linked to the teacher and no roster student may be added.
    //   When removeExistingLinks = true, every existing live student/parent link is
    //   severed atomically as part of the activation; when false, existing links are kept
    //   and only NEW links are blocked.
    //
    // TABLES WRITTEN: TeacherSubscriptions (flips previous IsCurrent + inserts new);
    //                 StudentTeacherLinks / ParentChildTeacherLinks (only if removeExistingLinks)
    // CACHE: invalidated synchronously after commit
    //
    // SAMPLE: POST /api/admin/subscriptions/activate-managerial
    //   { "teacherId": 42, "startDate": null, "endDate": null, "removeExistingLinks": false }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("activate-managerial")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.CurrentSubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateManagerial([FromBody] AdminActivateManagerialRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.ActivateManagerialAsync(adminUserId.Value, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 1c: ACTIVATE MANAGERIAL + PARENTS (ManagerialPlus)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // Identical to activate-managerial (same request shape, same RemoveExistingLinks
    // semantics — it severs student/parent-ACCOUNT links only, never parent-portal
    // follow-up grants) but the row is stamped PlanType = ManagerialPlus: student
    // accounts stay blocked while the public parent follow-up page remains available.
    //
    // SAMPLE: POST /api/admin/subscriptions/activate-managerial-plus
    //   { "teacherId": 42, "startDate": null, "endDate": null, "removeExistingLinks": false }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("activate-managerial-plus")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.CurrentSubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateManagerialPlus([FromBody] AdminActivateManagerialRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.ActivateManagerialPlusAsync(adminUserId.Value, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 2: EXTEND (FR-SUB-061 / REQ-ADM-016)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Extends the teacher's CURRENT subscription EndDate by N days.
    //   Does NOT create a new row — mutates the current row in place.
    //
    // TABLES WRITTEN: TeacherSubscriptions (EndDate update on current row)
    //
    // SAMPLE: POST /api/admin/subscriptions/extend
    //   { "teacherId": 42, "extensionDays": 7 }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("extend")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.CurrentSubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Extend([FromBody] AdminExtendRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.ExtendAsync(adminUserId.Value, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 3: SET END DATE (FR-SUB-062 / REQ-ADM-015)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Overrides the EndDate on a specific TeacherSubscription row (current or historical).
    //
    // TABLES WRITTEN: TeacherSubscriptions
    //
    // SAMPLE: PUT /api/admin/subscriptions/end-date
    //   { "subscriptionId": 9001, "newEndDate": "2026-06-30T00:00:00Z" }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPut("end-date")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.CurrentSubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetEndDate([FromBody] AdminSetEndDateRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.SetEndDateAsync(adminUserId.Value, request);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 4: GET PENDING APPROVAL QUEUE (FR-SUB-063)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Paginated list of pending payments awaiting super-admin approval.
    //   Phone number and transaction reference are decrypted server-side for review.
    //
    // TABLES READ: PendingSubscriptionPayments, Teachers, Users
    //
    // SAMPLE: GET /api/admin/subscriptions/pending?page=1&pageSize=20
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("pending")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.Subscription.AdminPendingQueueItemDto>>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingQueue(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _adminService.GetPendingQueueAsync(page, pageSize);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 5: APPROVE PENDING PAYMENT (FR-SUB-063)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Approves a pending payment. Delegates to ISubscriptionService.ConfirmPaymentAsync —
    //   one confirm pipeline (§6.3) serves both webhook and manual-approval paths.
    //   EC-24 guard: refuses approval if a current sub was created within the last 24h.
    //
    // TABLES WRITTEN: TeacherSubscriptions (new IsCurrent row), PendingSubscriptionPayments (resolution)
    //
    // SAMPLE: POST /api/admin/subscriptions/pending/5001/approve
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("pending/{pendingPaymentId:long}/approve")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.ConfirmPaymentResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApprovePending([FromRoute] long pendingPaymentId)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.ApprovePendingAsync(adminUserId.Value, pendingPaymentId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 6: REJECT PENDING PAYMENT (FR-SUB-063)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Rejects a pending payment with a reason. Enqueues
    //   IPendingPaymentRejectedNotificationJob to inform the teacher (push + WA + inbox).
    //
    // TABLES WRITTEN: PendingSubscriptionPayments (status = Rejected, RejectionReason)
    //
    // SAMPLE: POST /api/admin/subscriptions/pending/5001/reject
    //   { "rejectionReason": "Transaction reference not found in provider dashboard" }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("pending/{pendingPaymentId:long}/reject")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RejectPending(
        [FromRoute] long pendingPaymentId,
        [FromBody] RejectPendingRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.RejectPendingAsync(
            adminUserId.Value, pendingPaymentId, request.RejectionReason);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 7: CAPACITY-INCREASE REQUEST QUEUE
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Paginated FIFO queue of Pending capacity-increase requests, enriched with
    //   the teacher's live capacity, active student count, and the projected
    //   renewal price at the requested capacity.
    //
    // TABLES READ: CapacityIncreaseRequests, Teachers, Users, TeacherStudents,
    //              SubscriptionPricingSettings
    //
    // SAMPLE: GET /api/admin/subscriptions/capacity-requests?page=1&pageSize=20
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("capacity-requests")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.Subscription.AdminCapacityRequestQueueItemDto>>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCapacityRequestQueue(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.GetCapacityRequestQueueAsync(page, pageSize);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 8: APPROVE CAPACITY REQUEST
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Raises Teacher.StudentCapacity to the requested value immediately (never
    //   decreases) and flips the request to Approved in one transaction, then
    //   notifies the teacher. The new price applies from the NEXT renewal
    //   (initiation reads live capacity — BR-SUB-009).
    //
    // TABLES WRITTEN: Teachers (StudentCapacity), CapacityIncreaseRequests
    //
    // SAMPLE: POST /api/admin/subscriptions/capacity-requests/42/approve
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("capacity-requests/{requestId:long}/approve")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.CapacityRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApproveCapacityRequest([FromRoute] long requestId)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.ApproveCapacityRequestAsync(adminUserId.Value, requestId);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 9: REJECT CAPACITY REQUEST
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Rejects a Pending capacity request with a required reason (max 500 chars)
    //   and notifies the teacher with that reason.
    //
    // TABLES WRITTEN: CapacityIncreaseRequests
    //
    // SAMPLE: POST /api/admin/subscriptions/capacity-requests/42/reject
    //   { "rejectionReason": "Please contact support to discuss your plan first" }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("capacity-requests/{requestId:long}/reject")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.CapacityRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RejectCapacityRequest(
        [FromRoute] long requestId,
        [FromBody] RejectCapacityRequestRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.RejectCapacityRequestAsync(
            adminUserId.Value, requestId, request.RejectionReason);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // NEW-SUBSCRIPTION REQUEST QUEUE (teacher chose plan + student count)
    // ══════════════════════════════════════════════════════════════════════════

    // GET /api/admin/subscriptions/requests?page=1&pageSize=20 — FIFO pending queue.
    [HttpGet("requests")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.PaginatedResponse<System.Collections.Generic.List<Edvanz.Application.Dtos.Subscription.AdminSubscriptionRequestQueueItemDto>>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSubscriptionRequestQueue(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.GetSubscriptionRequestQueueAsync(page, pageSize);
        return ToResponse(result);
    }

    // POST /api/admin/subscriptions/requests/42/approve — activates the requested plan
    // (Full → capacity = requested students; Managerial → managerial activation).
    [HttpPost("requests/{requestId:long}/approve")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.SubscriptionRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApproveSubscriptionRequest([FromRoute] long requestId)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.ApproveSubscriptionRequestAsync(adminUserId.Value, requestId);
        return ToResponse(result);
    }

    // POST /api/admin/subscriptions/requests/42/reject { "rejectionReason": "..." }
    [HttpPost("requests/{requestId:long}/reject")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.SubscriptionRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RejectSubscriptionRequest(
        [FromRoute] long requestId,
        [FromBody] RejectSubscriptionRequestRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.RejectSubscriptionRequestAsync(
            adminUserId.Value, requestId, request.RejectionReason);
        return ToResponse(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ENDPOINT 10: GET / UPDATE PER-STUDENT PRICING
    // ══════════════════════════════════════════════════════════════════════════
    //
    // WHAT IT DOES:
    //   Reads / sets the per-student monthly rate that drives renewal pricing
    //   (Teacher.StudentCapacity × rate — "1 student = 2.5 EGP/month").
    //   BR-SUB-009: in-flight pending payments keep their initiation-time snapshot.
    //   Replaces the retired per-package price endpoint.
    //
    // TABLES: SubscriptionPricingSettings (single row)
    //
    // SAMPLE: PUT /api/admin/subscriptions/pricing
    //   { "pricePerStudentEGP": 2.50 }
    //
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("pricing")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.SubscriptionPricingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPricing()
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.GetPricingAsync();
        return ToResponse(result);
    }

    [HttpPut("pricing")]
    [ModulePermission(roles: new[] { "SuperAdmin" }, roleOnly: true)]
    [ProducesResponseType(typeof(Edvanz.Application.Dtos.Result<Edvanz.Application.Dtos.Subscription.SubscriptionPricingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdatePricing([FromBody] UpdateSubscriptionPricingRequest request)
    {
        long? adminUserId = _currentUser.UserId;
        if (adminUserId is null) return AdminNotResolved();

        var result = await _adminService.UpdatePricingAsync(adminUserId.Value, request);
        return ToResponse(result);
    }

    // ════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ════════════════════════════════════════════════

    /// <summary>
    /// Standardized response when the calling admin's user id cannot be read
    /// from claims. The [Authorize] + [ModulePermission] attributes already
    /// enforce that the caller is authenticated as SuperAdmin, so reaching this
    /// branch indicates a token/claim shape mismatch.
    /// </summary>
    private IActionResult AdminNotResolved()
    {
        return new ObjectResult(new { success = false, message = "Admin user not resolved" })
        {
            StatusCode = (int)HttpStatusCode.Unauthorized
        };
    }
}