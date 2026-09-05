using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Subscription;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Super-admin subscription operations (§4.4).
/// Covers manual activation (no payment), extension, end-date override,
/// and the pending-payment approval queue.
///
/// All write operations record CreatedByUserId for audit (FR-SUB-064 / REQ-ADM-016).
/// All methods return Result&lt;T&gt; for consistent error handling.
/// </summary>
public interface IAdminSubscriptionService
{
    // ══════════════════════════════════════════════
    // MANUAL OVERRIDES (REQ-ADM-012 / 015 / 016)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Manual activation that bypasses payment (FR-SUB-060 / REQ-ADM-012).
    /// Inserts a new TeacherSubscription with PaymentChannel = SuperAdminOverride
    /// and AmountPaidEGP = 0. Flips the previous current row IsCurrent = false.
    /// Cache is invalidated synchronously after commit; no payment notification fires.
    /// </summary>
    Task<Result<CurrentSubscriptionDto>> ActivateAsync(
        long adminUserId, AdminActivateRequest request);

    /// <summary>
    /// Manual activation of a MANAGERIAL subscription that bypasses payment. Identical to
    /// <see cref="ActivateAsync"/> (SuperAdminOverride row, AmountPaidEGP = 0, same period
    /// defaults) except the new row is stamped PlanType = Managerial: while it is the teacher's
    /// current active subscription, no student or parent account may be linked and no roster
    /// student may be added. When request.RemoveExistingLinks is true, all existing live
    /// student/parent links are severed atomically as part of the activation.
    /// </summary>
    Task<Result<CurrentSubscriptionDto>> ActivateManagerialAsync(
        long adminUserId, AdminActivateManagerialRequest request);

    /// <summary>
    /// Manual activation of a MANAGERIAL + PARENTS (ManagerialPlus) subscription. Identical to
    /// <see cref="ActivateManagerialAsync"/> (same request shape, same link-severing semantics —
    /// RemoveExistingLinks touches STUDENT/PARENT-ACCOUNT links only, never parent-portal
    /// follow-up grants) except the new row is stamped PlanType = ManagerialPlus, so the public
    /// parent follow-up page stays available while student accounts remain blocked.
    /// </summary>
    Task<Result<CurrentSubscriptionDto>> ActivateManagerialPlusAsync(
        long adminUserId, AdminActivateManagerialRequest request);

    /// <summary>
    /// Extends the teacher's current subscription EndDate by N days (FR-SUB-061 / REQ-ADM-016).
    /// Does NOT create a new row — mutates the current row's EndDate.
    /// Cache is invalidated synchronously after commit.
    /// </summary>
    Task<Result<CurrentSubscriptionDto>> ExtendAsync(
        long adminUserId, AdminExtendRequest request);

    /// <summary>
    /// Overrides the EndDate on a specific TeacherSubscription row (FR-SUB-062 / REQ-ADM-015).
    /// Used when the admin needs to correct an issued subscription.
    /// Cache is invalidated synchronously after commit.
    /// </summary>
    Task<Result<CurrentSubscriptionDto>> SetEndDateAsync(
        long adminUserId, AdminSetEndDateRequest request);

    // ══════════════════════════════════════════════
    // PENDING PAYMENT QUEUE (FR-SUB-063)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Paginated queue of pending payments awaiting super-admin approval.
    /// Encrypted submitted-details blob is decrypted server-side for review only;
    /// the decrypted view is returned in the DTO (BR-SUB-011: admin sees plaintext
    /// in the review endpoint, not in payment history).
    /// </summary>
    Task<Result<PaginatedResponse<List<AdminPendingQueueItemDto>>>> GetPendingQueueAsync(
        int page, int pageSize);

    /// <summary>
    /// Approves a pending payment. Delegates to ISubscriptionService.ConfirmPaymentAsync
    /// passing the admin's user id as resolvedByUserId.
    ///
    /// EC-24 GUARD: If the teacher's current subscription was created within the last
    /// 24 hours (i.e., the renewal was already paid via another path), this method
    /// returns a "duplicate payment detected" error and does NOT confirm — the admin
    /// must verify the situation manually before approving.
    /// </summary>
    Task<Result<ConfirmPaymentResultDto>> ApprovePendingAsync(
        long adminUserId, long pendingPaymentId);

    /// <summary>
    /// Rejects a pending payment. Sets Status = Rejected, persists the rejection reason,
    /// and enqueues IPendingPaymentRejectedNotificationJob to inform the teacher
    /// via push + WhatsApp + UserNotification row.
    /// </summary>
    Task<Result<bool>> RejectPendingAsync(
        long adminUserId, long pendingPaymentId, string rejectionReason);

    // ══════════════════════════════════════════════
    // CAPACITY-INCREASE REQUEST QUEUE
    // ══════════════════════════════════════════════

    /// <summary>
    /// Paginated FIFO queue of Pending capacity-increase requests, each enriched with
    /// live teacher context (current capacity, active student count) and the projected
    /// renewal price at the requested capacity.
    /// </summary>
    Task<Result<PaginatedResponse<List<AdminCapacityRequestQueueItemDto>>>> GetCapacityRequestQueueAsync(
        int page, int pageSize);

    /// <summary>
    /// Approves a capacity request: raises Teacher.StudentCapacity to the requested
    /// value immediately (never decreases — Math.Max guard) inside one transaction with
    /// the request's status flip, then notifies the teacher post-commit. The new price
    /// naturally applies from the NEXT renewal (initiation reads live capacity, BR-SUB-009).
    /// </summary>
    Task<Result<CapacityRequestDto>> ApproveCapacityRequestAsync(
        long adminUserId, long requestId);

    /// <summary>
    /// Rejects a capacity request with a required reason (max 500 chars), then notifies
    /// the teacher post-commit with that reason.
    /// </summary>
    Task<Result<CapacityRequestDto>> RejectCapacityRequestAsync(
        long adminUserId, long requestId, string rejectionReason);

    // ══════════════════════════════════════════════
    // PRICING (per-student rate: renewal = capacity × rate)
    // ══════════════════════════════════════════════

    // ══════════════════════════════════════════════
    // NEW-SUBSCRIPTION REQUEST QUEUE (teacher chose plan + student count)
    // ══════════════════════════════════════════════

    /// <summary>Paginated FIFO queue of Pending subscription requests, enriched with teacher context.</summary>
    Task<Result<PaginatedResponse<List<AdminSubscriptionRequestQueueItemDto>>>> GetSubscriptionRequestQueueAsync(
        int page, int pageSize);

    /// <summary>
    /// Approves a subscription request: activates the matching subscription for the teacher
    /// (Full → capacity = RequestedStudents; Managerial → managerial activation) inside one
    /// transaction with the request's status flip, then notifies the teacher post-commit.
    /// </summary>
    Task<Result<SubscriptionRequestDto>> ApproveSubscriptionRequestAsync(
        long adminUserId, long requestId);

    /// <summary>Rejects a subscription request with a required reason, then notifies the teacher.</summary>
    Task<Result<SubscriptionRequestDto>> RejectSubscriptionRequestAsync(
        long adminUserId, long requestId, string rejectionReason);

    /// <summary>Returns the current per-student monthly rate with its audit fields.</summary>
    Task<Result<SubscriptionPricingDto>> GetPricingAsync();

    /// <summary>
    /// Updates the per-student monthly rate (must be &gt; 0) and, when sent (nullable =
    /// leave unchanged, wire-compat with older admin clients), the flat Managerial /
    /// ManagerialPlus monthly prices (each must be &gt; 0 when present). BR-SUB-009:
    /// in-flight pending payments retain their initiation-time amount snapshot.
    /// </summary>
    Task<Result<SubscriptionPricingDto>> UpdatePricingAsync(
        long adminUserId, UpdateSubscriptionPricingRequest request);

    // ══════════════════════════════════════════════
    // MODULE QUOTAS (free-tier limits table)
    // ══════════════════════════════════════════════

    /// <summary>All free-tier module quota rows, ordered by ModuleKey.</summary>
    Task<Result<List<ModuleQuotaDto>>> GetModuleQuotasAsync();

    /// <summary>
    /// Updates one module's FreeTierLimit (0–10,000; 0 = subscriber-only) and optional
    /// description, records the audit columns, and invalidates the gate cache so the
    /// change applies immediately on this instance (≤60s elsewhere). 404 when the key
    /// has no seeded row.
    /// </summary>
    Task<Result<ModuleQuotaDto>> UpdateModuleQuotaAsync(
        long adminUserId, string moduleKey, UpdateModuleQuotaRequest request);
    /// <summary>
    /// Cancels the tutor's CURRENT subscription immediately (REQ-ADM-013) by
    /// expiring it in place: sets EndDate = UtcNow on the current row, leaving
    /// IsCurrent = true. The derived status becomes Expired, so the tutor drops to
    /// the free tier on the next request; the cache is invalidated synchronously
    /// after commit. Reversible via Activate/Extend/SetEndDate — no new row, no
    /// history loss. Idempotent when the current row is already expired (no write).
    /// Returns 404 NoActiveSubscription when the teacher has no current row
    /// (mirrors ExtendAsync).
    /// </summary>
    Task<Result<CurrentSubscriptionDto>> CancelAsync(
        long adminUserId, AdminCancelRequest request);

    /// <summary>
    /// Admin-initiated, increase-only raise of the students-in-the-account limit for a teacher
    /// WITHOUT a prior request. Same core as ApproveCapacityRequestAsync: raises
    /// Teacher.StudentCapacity (Math.Max), writes an Approved CapacityIncreaseRequest audit row
    /// (CapacityKind = AccountStudents, RequestedByUserId = ResolvedByUserId = admin), notifies
    /// the teacher post-commit. This limit is FREE — the price follows LinkedStudentCapacity
    /// (see <see cref="SetTeacherLinkedCapacityAsync"/>). 400 when newCapacity is out of range or
    /// not greater than current.
    /// </summary>
    Task<Result<CapacityRequestDto>> SetTeacherCapacityAsync(
        long adminUserId, long teacherId, AdminSetCapacityRequest request);

    /// <summary>
    /// Admin-initiated set of the teacher's STUDENT APP ACCOUNT limit
    /// (Teacher.LinkedStudentCapacity) — the limit the subscription price is computed from.
    ///
    /// Unlike <see cref="SetTeacherCapacityAsync"/> this accepts BOTH increases and decreases:
    /// tuning the paid limit down is the normal way to move a teacher to a smaller package.
    /// A decrease never breaks already-linked students — it only stops NEW links until usage
    /// falls back under the limit. When the new value exceeds Teacher.StudentCapacity that limit
    /// is raised to match (a linked account needs a student record; the students-in-the-account
    /// quota is free). Writes an Approved CapacityIncreaseRequest audit row (CapacityKind =
    /// LinkedStudents, RequestedByUserId = ResolvedByUserId = admin) in the same transaction and
    /// notifies the teacher post-commit ON A RAISE ONLY. New price from the next renewal
    /// (BR-SUB-009). 400 when newCapacity is out of range, 404 when the teacher does not exist.
    /// </summary>
    Task<Result<CapacityRequestDto>> SetTeacherLinkedCapacityAsync(
        long adminUserId, long teacherId, AdminSetLinkedCapacityRequest request);
}