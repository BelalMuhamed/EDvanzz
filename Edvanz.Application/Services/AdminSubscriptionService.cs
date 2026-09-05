using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Subscription;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.Options;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.Resources;
using Hangfire;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;

namespace Edvanz.Application.Services;

/// <summary>
/// Super-admin subscription operations (§4.4 / FR-SUB-060…064).
///
/// MANUAL OVERRIDES (Activate / Extend / SetEndDate):
///   These bypass payment. The new TeacherSubscription row carries
///   PaymentChannel = SuperAdminOverride and AmountPaidEGP = 0.
///   CreatedByUserId records the admin user for audit (REQ-ADM-016 / FR-SUB-064).
///
/// PENDING QUEUE:
///   Approval delegates to ISubscriptionService.ConfirmPaymentAsync — one confirm
///   pipeline serves both webhook and manual-approval paths (§6.3).
///   Rejection enqueues IPendingPaymentRejectedNotificationJob to inform the tutor.
///
/// EC-24 GUARD on ApprovePendingAsync:
///   Refuses to approve when the teacher already has a CURRENT subscription
///   created within the last 24 hours — prevents accidental double-renewal when
///   the tutor paid via Paymob (auto-confirmed) and admin reviews a stale manual
///   submission for the same period.
/// </summary>
public class AdminSubscriptionService : IAdminSubscriptionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ISubscriptionCacheService _cache;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly SubscriptionDefaultsOptions _defaults;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly ILogger<AdminSubscriptionService> _logger;
    private readonly IEncryptionService _encryption;

    public AdminSubscriptionService(IEncryptionService encryption,
        IUnitOfWork unitOfWork,
        ISubscriptionService subscriptionService,
        ISubscriptionCacheService cache,
        IBackgroundJobClient backgroundJobs,
        IOptions<SubscriptionDefaultsOptions> defaults,
        IStringLocalizer<Messages> localizer,
        ILogger<AdminSubscriptionService> logger)
    {
        _unitOfWork = unitOfWork;
        _subscriptionService = subscriptionService;
        _cache = cache;
        _backgroundJobs = backgroundJobs;
        _defaults = defaults.Value;
        _localizer = localizer;
        _logger = logger;
        _encryption = encryption;

    }

    // ════════════════════════════════════════════════
    // MANUAL OVERRIDES
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<CurrentSubscriptionDto>> ActivateAsync(
        long adminUserId, AdminActivateRequest request)
        => await ActivateCoreAsync(
            adminUserId, request.TeacherId, request.StartDate, request.EndDate,
            SubscriptionPlanType.Full, removeExistingLinks: false,
            SubscriptionConstants.Messages.SubscriptionActivated);

    /// <inheritdoc />
    public async Task<Result<CurrentSubscriptionDto>> ActivateManagerialAsync(
        long adminUserId, AdminActivateManagerialRequest request)
        => await ActivateCoreAsync(
            adminUserId, request.TeacherId, request.StartDate, request.EndDate,
            SubscriptionPlanType.Managerial, request.RemoveExistingLinks,
            SubscriptionConstants.Messages.SubscriptionManagerialActivated);

    /// <inheritdoc />
    public async Task<Result<CurrentSubscriptionDto>> ActivateManagerialPlusAsync(
        long adminUserId, AdminActivateManagerialRequest request)
        => await ActivateCoreAsync(
            adminUserId, request.TeacherId, request.StartDate, request.EndDate,
            SubscriptionPlanType.ManagerialPlus, request.RemoveExistingLinks,
            SubscriptionConstants.Messages.SubscriptionManagerialPlusActivated);

    /// <summary>
    /// Shared no-payment activation core for both Full and Managerial plans. Inserts a new
    /// IsCurrent = true TeacherSubscription (PaymentChannel = SuperAdminOverride, AmountPaidEGP = 0)
    /// stamped with <paramref name="planType"/> and flips the previous current row, all inside one
    /// transaction. When <paramref name="removeExistingLinks"/> is true (managerial only), it also
    /// severs every live student link and active parent link for the teacher in the SAME transaction.
    /// </summary>
    private async Task<Result<CurrentSubscriptionDto>> ActivateCoreAsync(
        long adminUserId, long teacherId, DateTime? startDateOpt, DateTime? endDateOpt,
        SubscriptionPlanType planType, bool removeExistingLinks, string successMessageKey)
    {
        // ── Validation ──
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
        {
            return Result<CurrentSubscriptionDto>.Failure(
                _localizer, SubscriptionConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);
        }

        DateTime now = DateTime.UtcNow;
        DateTime startDate = startDateOpt ?? now;
        DateTime endDate = endDateOpt ?? startDate.AddDays(_defaults.PeriodDays);

        if (endDate <= startDate)
        {
            return Result<CurrentSubscriptionDto>.Failure(
                _localizer, SubscriptionConstants.Messages.EndDateMustBeAfterStart);
        }

        // ── Build the override row ──
        var newSubscription = new TeacherSubscription
        {
            TeacherId = teacherId,
            StartDate = startDate,
            EndDate = endDate,
            IsCurrent = true,
            PlanType = planType,
            PaymentMethod = PaymentMethod.SuperAdminManual,
            PaymentChannel = PaymentChannel.SuperAdminOverride,
            AmountPaidEGP = 0m,
            TransactionReference = null,
            EncryptedPaymentDetails = null,
            PaymentConfirmedAt = now,
            CreatedByUserId = adminUserId,
            CreateAt = now
        };

        // ── Atomically flip previous current + insert new (filtered unique index protects us) ──
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var previousCurrent = await _unitOfWork.Users
                .GetCurrentSubscriptionForUpdateAsync(teacherId);

            await _unitOfWork.Users.FlipCurrentAndInsertNewAsync(previousCurrent, newSubscription);
            await _unitOfWork.SaveChangesAsync();

            // Managerial "remove existing links": sever every live STUDENT-ACCOUNT link and every
            // active PARENT link in the same transaction so no student/parent account remains
            // connected the moment the plan takes effect. (The roster itself is allowed under
            // managerial, so TeacherStudent records are NOT touched.)
            if (removeExistingLinks)
            {
                int studentsRemoved = await _unitOfWork.Users
                    .RemoveAllLiveStudentLinksForTeacherAsync(teacherId, adminUserId);
                int parentsRemoved = await _unitOfWork.Users
                    .RemoveAllActiveParentLinksForTeacherAsync(teacherId);

                _logger.LogInformation(
                    "Managerial activation for teacher {TeacherId} removed {StudentLinks} student-account link(s) and {ParentLinks} parent link(s)",
                    teacherId, studentsRemoved, parentsRemoved);
            }

            await _unitOfWork.CommitAsync();
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            throw;
        }

        // ── Invalidate cache so the next request sees the new row ──
        await _cache.InvalidateAsync(teacherId);

        return await BuildCurrentDtoResultAsync(teacherId, successMessageKey);
    }

    /// <inheritdoc />
    public async Task<Result<CurrentSubscriptionDto>> ExtendAsync(
        long adminUserId, AdminExtendRequest request)
    {
        // ── Validation ──
        if (request.ExtensionDays <= 0)
        {
            return Result<CurrentSubscriptionDto>.Failure(
                _localizer, SubscriptionConstants.Messages.ExtensionDaysMustBePositive);
        }

        // ── Mutate the current row's EndDate inside a transaction ──
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var currentSub = await _unitOfWork.Users
                .GetCurrentSubscriptionForUpdateAsync(request.TeacherId);

            if (currentSub is null)
            {
                await _unitOfWork.RollbackAsync();
                return Result<CurrentSubscriptionDto>.Failure(
                    _localizer, SubscriptionConstants.Messages.NoActiveSubscription, HttpStatusCode.NotFound);
            }

            currentSub.EndDate = currentSub.EndDate.AddDays(request.ExtensionDays);
            // Audit: stamp the admin user as the most recent modifier.
            currentSub.CreatedByUserId = adminUserId;

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            throw;
        }

        await _cache.InvalidateAsync(request.TeacherId);

        return await BuildCurrentDtoResultAsync(
            request.TeacherId, SubscriptionConstants.Messages.SubscriptionExtended);
    }

    /// <inheritdoc />
    public async Task<Result<CurrentSubscriptionDto>> SetEndDateAsync(
        long adminUserId, AdminSetEndDateRequest request)
    {
        // ── Load the row by id (any historical row may be the target — not just current) ──
        var subscription = await _unitOfWork.GetRepository<TeacherSubscription, long>()
            .GetByIdAsync(request.SubscriptionId);

        if (subscription is null)
        {
            return Result<CurrentSubscriptionDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionNotFound, HttpStatusCode.NotFound);
        }

        if (request.NewEndDate <= subscription.StartDate)
        {
            return Result<CurrentSubscriptionDto>.Failure(
                _localizer, SubscriptionConstants.Messages.EndDateMustBeAfterStart);
        }

        // ── Persist the override ──
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            subscription.EndDate = request.NewEndDate;
            subscription.CreatedByUserId = adminUserId;

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            throw;
        }

        // Cache invalidation only matters when the row touched IS the current row.
        if (subscription.IsCurrent)
        {
            await _cache.InvalidateAsync(subscription.TeacherId);
        }

        return await BuildCurrentDtoResultAsync(
            subscription.TeacherId, SubscriptionConstants.Messages.SubscriptionEndDateUpdated);
    }

    // ════════════════════════════════════════════════
    // PENDING PAYMENT QUEUE
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<AdminPendingQueueItemDto>>>> GetPendingQueueAsync(
        int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var (items, totalCount) = await _unitOfWork.SubscriptionPaymentsRepo
            .GetAdminQueuePagedAsync(page, pageSize);

        // Enrich each row with teacher identity. Single round-trip per teacher is
        // acceptable here — the admin queue is rarely deeper than a few dozen rows.
        var dtoList = new List<AdminPendingQueueItemDto>(items.Count);
        foreach (var pending in items)
        {
            var teacherInfo = await _unitOfWork.Users
                .GetTeacherForReminderAsync(pending.TeacherId);

            (string phoneNumber, string transactionRef) = DecryptSubmittedDetails(
                pending.EncryptedSubmittedDetails, pending.SubmittedTransactionReference);

            dtoList.Add(new AdminPendingQueueItemDto
            {
                Id = pending.Id,
                TeacherId = pending.TeacherId,
                TeacherName = teacherInfo?.FullName ?? string.Empty,
                TeacherCode = string.Empty, // populated below if available
                PaymentMethod = pending.PaymentMethod,
                PaymentChannel = pending.PaymentChannel,
                AmountEGP = pending.AmountEGP,
                PaymentPhoneNumber = phoneNumber,
                TransactionReference = transactionRef,
                InitiatedAt = pending.InitiatedAt
            });
        }

        var paged = new PaginatedResponse<List<AdminPendingQueueItemDto>>
        {
            data= dtoList,
            page = page,
            pageSize = pageSize,
            totalCount = totalCount
        };

        return Result<PaginatedResponse<List<AdminPendingQueueItemDto>>>.Success(paged, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<ConfirmPaymentResultDto>> ApprovePendingAsync(
        long adminUserId, long pendingPaymentId)
    {
        // ── Load the pending row ──
        var pending = await _unitOfWork.SubscriptionPaymentsRepo
            .GetByIdForAdminAsync(pendingPaymentId);

        if (pending is null)
        {
            return Result<ConfirmPaymentResultDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PendingPaymentNotFound, HttpStatusCode.NotFound);
        }

        if (pending.Status != PendingPaymentStatus.AwaitingSuperAdminApproval)
        {
            return Result<ConfirmPaymentResultDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PendingPaymentNotAwaitingApproval,
                HttpStatusCode.Conflict);
        }

        // ── EC-24 guard: refuse if a current sub was created within the last 24 hours ──
        var currentSub = await _unitOfWork.Users
            .GetCurrentSubscriptionStatusAsync(pending.TeacherId);

        if (currentSub is not null && IsRecentlyCreated(currentSub, pending.InitiatedAt))
        {
            _logger.LogWarning(
                "EC-24 duplicate-payment guard tripped for pending {PendingId}: current sub {SubId} created at {StartDate}",
                pendingPaymentId, currentSub.SubscriptionId, currentSub.StartDate);

            return Result<ConfirmPaymentResultDto>.Failure(
                _localizer, SubscriptionConstants.Messages.DuplicatePaymentDetected,
                HttpStatusCode.Conflict);
        }

        // ── Delegate to the shared confirm pipeline (§6.3) ──
        return await _subscriptionService.ConfirmPaymentAsync(pendingPaymentId, adminUserId);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> RejectPendingAsync(
        long adminUserId, long pendingPaymentId, string rejectionReason)
    {
        // ── Validation ──
        if (string.IsNullOrWhiteSpace(rejectionReason))
        {
            return Result<bool>.Failure(
                _localizer, SubscriptionConstants.Messages.RejectionReasonRequired);
        }

        // ── Load + state check ──
        var pending = await _unitOfWork.SubscriptionPaymentsRepo
            .GetByIdForAdminAsync(pendingPaymentId);

        if (pending is null)
        {
            return Result<bool>.Failure(
                _localizer, SubscriptionConstants.Messages.PendingPaymentNotFound, HttpStatusCode.NotFound);
        }

        if (pending.Status != PendingPaymentStatus.AwaitingSuperAdminApproval)
        {
            return Result<bool>.Failure(
                _localizer, SubscriptionConstants.Messages.PendingPaymentNotAwaitingApproval,
                HttpStatusCode.Conflict);
        }

        // ── Persist rejection ──
        string trimmedReason = rejectionReason.Trim();
        if (trimmedReason.Length > SubscriptionConstants.RejectionReasonMaxLength)
        {
            trimmedReason = trimmedReason[..SubscriptionConstants.RejectionReasonMaxLength];
        }

        pending.Status = PendingPaymentStatus.Rejected;
        pending.ResolvedAt = DateTime.UtcNow;
        pending.ResolvedByUserId = adminUserId;
        pending.RejectionReason = trimmedReason;

        _unitOfWork.SubscriptionPaymentsRepo.UpdatePending(pending);
        await _unitOfWork.SaveChangesAsync();

        // ── Fire the rejection notification (push + WhatsApp + UserNotification) ──
        _backgroundJobs.Enqueue<IPendingPaymentRejectedNotificationJob>(
            job => job.SendAsync(pending.TeacherId, pending.Id, trimmedReason));

        return Result<bool>.Success(true, _localizer, SubscriptionConstants.Messages.PendingPaymentRejected);
    }

    // ════════════════════════════════════════════════
    // CAPACITY-INCREASE REQUEST QUEUE
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<AdminCapacityRequestQueueItemDto>>>> GetCapacityRequestQueueAsync(
        int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var (items, totalCount) = await _unitOfWork.CapacityRequestsRepo
            .GetAdminQueuePagedAsync(page, pageSize);

        // One rate read serves every row; 0 when unconfigured (mirrors the renewal display).
        decimal rate = await _unitOfWork.SubscriptionPricingRepo.GetPricePerStudentAsync() ?? 0m;

        // Enrich each row with live teacher context. A few round-trips per row is
        // acceptable here — the admin queue is rarely deeper than a few dozen rows
        // (same trade-off as GetPendingQueueAsync above).
        var dtoList = new List<AdminCapacityRequestQueueItemDto>(items.Count);
        foreach (var request in items)
        {
            var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(request.TeacherId);
            var teacherInfo = await _unitOfWork.Users.GetTeacherForReminderAsync(request.TeacherId);
            int activeStudents = await _unitOfWork.Students.CountActiveStudentsAsync(request.TeacherId);

            bool isLinkedKind = request.CapacityKind == CapacityKind.LinkedStudents;

            // Every capacity figure on the row is expressed in terms of the limit the request
            // targets — reading a linked-accounts request against the students-in-the-account
            // number would show the admin a nonsense "current".
            int liveCapacity = teacher is null
                ? request.CapacityAtRequest
                : (isLinkedKind ? teacher.LinkedStudentCapacity : teacher.StudentCapacity);

            // Only student APP ACCOUNTS are priced. Raising the students-in-the-account quota is
            // free, so such a request projects the teacher's CURRENT linked limit × rate — i.e.
            // "approving this changes nothing about the bill".
            int pricedCapacity = isLinkedKind
                ? request.RequestedCapacity
                : (teacher?.LinkedStudentCapacity ?? 0);

            dtoList.Add(new AdminCapacityRequestQueueItemDto
            {
                Id = request.Id,
                TeacherId = request.TeacherId,
                TeacherName = teacherInfo?.FullName ?? string.Empty,
                TeacherCode = teacher?.TeacherCode ?? string.Empty,
                CapacityKind = request.CapacityKind,
                CurrentCapacity = liveCapacity,
                CapacityAtRequest = request.CapacityAtRequest,
                RequestedCapacity = request.RequestedCapacity,
                ActiveStudentCount = activeStudents,
                ProjectedMonthlyPriceEGP = rate <= 0m ? 0m : pricedCapacity * rate,
                Note = request.Note,
                RequestedAt = request.RequestedAt
            });
        }

        var paged = new PaginatedResponse<List<AdminCapacityRequestQueueItemDto>>
        {
            data = dtoList,
            page = page,
            pageSize = pageSize,
            totalCount = totalCount
        };

        return Result<PaginatedResponse<List<AdminCapacityRequestQueueItemDto>>>.Success(paged, _localizer);
    }

    // ══════════════════════════════════════════════
    // NEW-SUBSCRIPTION REQUEST QUEUE (teacher chose plan + student count)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<AdminSubscriptionRequestQueueItemDto>>>> GetSubscriptionRequestQueueAsync(
        int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var (items, totalCount) = await _unitOfWork.SubscriptionRequestsRepo
            .GetAdminQueuePagedAsync(page, pageSize);

        var dtoList = new List<AdminSubscriptionRequestQueueItemDto>(items.Count);
        foreach (var request in items)
        {
            var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(request.TeacherId);
            var teacherInfo = await _unitOfWork.Users.GetTeacherForReminderAsync(request.TeacherId);

            dtoList.Add(new AdminSubscriptionRequestQueueItemDto
            {
                Id = request.Id,
                TeacherId = request.TeacherId,
                TeacherName = teacherInfo?.FullName ?? string.Empty,
                TeacherCode = teacher?.TeacherCode ?? string.Empty,
                PlanType = request.PlanType,
                RequestedStudents = request.RequestedStudents,
                ComputedAmountEGP = request.ComputedAmountEGP,
                Note = request.Note,
                RequestedAt = request.RequestedAt
            });
        }

        var paged = new PaginatedResponse<List<AdminSubscriptionRequestQueueItemDto>>
        {
            data = dtoList,
            page = page,
            pageSize = pageSize,
            totalCount = totalCount
        };

        return Result<PaginatedResponse<List<AdminSubscriptionRequestQueueItemDto>>>.Success(paged, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<SubscriptionRequestDto>> ApproveSubscriptionRequestAsync(
        long adminUserId, long requestId)
    {
        var request = await _unitOfWork.SubscriptionRequestsRepo.GetByIdForAdminAsync(requestId);
        if (request is null)
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequestNotFound, HttpStatusCode.NotFound);
        }

        if (request.Status != SubscriptionRequestStatus.Pending)
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequestNotPending, HttpStatusCode.Conflict);
        }

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(request.TeacherId);
        if (teacher is null)
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);
        }

        DateTime now = DateTime.UtcNow;
        var newSubscription = new TeacherSubscription
        {
            TeacherId = request.TeacherId,
            StartDate = now,
            EndDate = now.AddDays(_defaults.PeriodDays),
            IsCurrent = true,
            PlanType = request.PlanType,
            PaymentMethod = PaymentMethod.SuperAdminManual,
            PaymentChannel = PaymentChannel.SuperAdminOverride,
            AmountPaidEGP = 0m,
            TransactionReference = null,
            EncryptedPaymentDetails = null,
            PaymentConfirmedAt = now,
            CreatedByUserId = adminUserId,
            CreateAt = now
        };

        // Grant capacity (Full only), activate the subscription, and flip the request — atomically.
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            if (request.PlanType == SubscriptionPlanType.Full && request.RequestedStudents > 0)
            {
                teacher.StudentCapacity = request.RequestedStudents;
                // Rows written by app builds that predate the student-app-account limit carry 0 —
                // fall back to the students number so they are granted exactly as before.
                teacher.LinkedStudentCapacity = request.RequestedLinkedStudents > 0
                    ? request.RequestedLinkedStudents
                    : request.RequestedStudents;
                EnforceCapacityInvariant(teacher);
                await _unitOfWork.Users.UpdateTeacherAsync(teacher);
            }

            var previousCurrent = await _unitOfWork.Users
                .GetCurrentSubscriptionForUpdateAsync(request.TeacherId);
            await _unitOfWork.Users.FlipCurrentAndInsertNewAsync(previousCurrent, newSubscription);

            request.Status = SubscriptionRequestStatus.Approved;
            request.ResolvedAt = now;
            request.ResolvedByUserId = adminUserId;
            _unitOfWork.SubscriptionRequestsRepo.UpdateRequest(request);

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            throw;
        }

        await _cache.InvalidateAsync(request.TeacherId);

        return Result<SubscriptionRequestDto>.Success(
            SubscriptionService.ToSubscriptionRequestDto(request),
            _localizer,
            SubscriptionConstants.Messages.SubscriptionRequestApproved);
    }

    /// <inheritdoc />
    public async Task<Result<SubscriptionRequestDto>> RejectSubscriptionRequestAsync(
        long adminUserId, long requestId, string rejectionReason)
    {
        if (string.IsNullOrWhiteSpace(rejectionReason))
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.RejectionReasonRequired);
        }

        var request = await _unitOfWork.SubscriptionRequestsRepo.GetByIdForAdminAsync(requestId);
        if (request is null)
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequestNotFound, HttpStatusCode.NotFound);
        }

        if (request.Status != SubscriptionRequestStatus.Pending)
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequestNotPending, HttpStatusCode.Conflict);
        }

        string trimmedReason = rejectionReason.Trim();
        if (trimmedReason.Length > SubscriptionConstants.RejectionReasonMaxLength)
        {
            trimmedReason = trimmedReason[..SubscriptionConstants.RejectionReasonMaxLength];
        }

        request.Status = SubscriptionRequestStatus.Rejected;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedByUserId = adminUserId;
        request.RejectionReason = trimmedReason;
        _unitOfWork.SubscriptionRequestsRepo.UpdateRequest(request);
        await _unitOfWork.SaveChangesAsync();

        return Result<SubscriptionRequestDto>.Success(
            SubscriptionService.ToSubscriptionRequestDto(request),
            _localizer,
            SubscriptionConstants.Messages.SubscriptionRequestRejected);
    }

    /// <inheritdoc />
    public async Task<Result<CapacityRequestDto>> ApproveCapacityRequestAsync(
        long adminUserId, long requestId)
    {
        // ── Load + state check ──
        var request = await _unitOfWork.CapacityRequestsRepo.GetByIdForAdminAsync(requestId);
        if (request is null)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.CapacityRequestNotFound, HttpStatusCode.NotFound);
        }

        if (request.Status != CapacityRequestStatus.Pending)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.CapacityRequestNotPending,
                HttpStatusCode.Conflict);
        }

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(request.TeacherId);
        if (teacher is null)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);
        }

        // ── Capacity raise (on the limit the request targets) + status flip in ONE transaction ──
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            await ApplyApprovedCapacityAsync(teacher, request, adminUserId, isNewRow: false);
            await _unitOfWork.CommitAsync();
        }
        catch { await _unitOfWork.RollbackAsync(); throw; }

        EnqueueCapacityApprovedNotification(request.TeacherId, request.Id);
        return Result<CapacityRequestDto>.Success(
            ToCapacityRequestDto(request), _localizer, SubscriptionConstants.Messages.CapacityRequestApproved);
    }

    /// <summary>
    /// Thin alias for <see cref="TeacherCapacityRules.EnforceInvariant"/> — the ONE implementation
    /// of <c>LinkedStudentCapacity &lt;= StudentCapacity</c>, shared with the teacher-creation path
    /// (TeacherService) so the rule cannot drift between the two.
    /// </summary>
    private static void EnforceCapacityInvariant(Teacher teacher) =>
        TeacherCapacityRules.EnforceInvariant(teacher);

    /// <summary>
    /// Increase-only capacity raise on the limit the request TARGETS + Approved audit stamp,
    /// inside the caller's transaction. Math.Max: never lower a limit, even if an admin already
    /// raised it past the requested value while the request sat in the queue.
    /// </summary>
    private async Task ApplyApprovedCapacityAsync(
        Teacher teacher, CapacityIncreaseRequest request, long adminUserId, bool isNewRow)
    {
        if (request.CapacityKind == CapacityKind.LinkedStudents)
        {
            teacher.LinkedStudentCapacity =
                Math.Max(teacher.LinkedStudentCapacity, request.RequestedCapacity);
            EnforceCapacityInvariant(teacher);
        }
        else
        {
            teacher.StudentCapacity = Math.Max(teacher.StudentCapacity, request.RequestedCapacity);
        }

        await _unitOfWork.Users.UpdateTeacherAsync(teacher);

        request.Status = CapacityRequestStatus.Approved;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedByUserId = adminUserId;

        if (isNewRow) await _unitOfWork.CapacityRequestsRepo.AddAsync(request);
        else _unitOfWork.CapacityRequestsRepo.UpdateRequest(request);

        await _unitOfWork.SaveChangesAsync();
    }

    private void EnqueueCapacityApprovedNotification(long teacherId, long requestId) =>
        _backgroundJobs.Enqueue<ICapacityRequestResolvedNotificationJob>(
            j => j.SendAsync(teacherId, requestId, true, null));

    /// <inheritdoc />
    public async Task<Result<CapacityRequestDto>> SetTeacherCapacityAsync(
        long adminUserId, long teacherId, AdminSetCapacityRequest request)
    {
        if (request.NewCapacity <= 0 || request.NewCapacity > SubscriptionConstants.MaxStudentCapacity)
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.RequestedCapacityTooLarge);

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);

        // Increase-only (product decision): decreases are not supported here.
        if (request.NewCapacity <= teacher.StudentCapacity)
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.RequestedCapacityMustExceedCurrent);

        string? note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 500 }) note = note[..500];

        var row = new CapacityIncreaseRequest
        {
            TeacherId = teacherId,
            CapacityKind = CapacityKind.AccountStudents,
            CapacityAtRequest = teacher.StudentCapacity,
            RequestedCapacity = request.NewCapacity,
            Note = note,
            RequestedAt = DateTime.UtcNow,
            RequestedByUserId = adminUserId,   // admin acted on the teacher's behalf
            CreateAt = DateTime.UtcNow,
        };

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            await ApplyApprovedCapacityAsync(teacher, row, adminUserId, isNewRow: true);
            await _unitOfWork.CommitAsync();
        }
        catch { await _unitOfWork.RollbackAsync(); throw; }

        EnqueueCapacityApprovedNotification(row.TeacherId, row.Id);
        return Result<CapacityRequestDto>.Success(
            ToCapacityRequestDto(row), _localizer, SubscriptionConstants.Messages.CapacityRequestApproved);
    }

    /// <inheritdoc />
    public async Task<Result<CapacityRequestDto>> SetTeacherLinkedCapacityAsync(
        long adminUserId, long teacherId, AdminSetLinkedCapacityRequest request)
    {
        if (request.NewCapacity <= 0 || request.NewCapacity > SubscriptionConstants.MaxStudentCapacity)
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.RequestedLinkedStudentsTooLarge);

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);

        string? note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 500 }) note = note[..500];

        // Audit row mirroring SetTeacherCapacityAsync: an Approved CapacityIncreaseRequest with
        // the admin as both requester and resolver, so the history reads the same for both limits.
        var row = new CapacityIncreaseRequest
        {
            TeacherId = teacherId,
            CapacityKind = CapacityKind.LinkedStudents,
            CapacityAtRequest = teacher.LinkedStudentCapacity,
            RequestedCapacity = request.NewCapacity,
            Note = note,
            Status = CapacityRequestStatus.Approved,
            RequestedAt = DateTime.UtcNow,
            RequestedByUserId = adminUserId,   // admin acted on the teacher's behalf
            ResolvedAt = DateTime.UtcNow,
            ResolvedByUserId = adminUserId,
            CreateAt = DateTime.UtcNow,
        };

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            // DIRECT assignment, not Math.Max — unlike the students-in-the-account endpoint this
            // one is deliberately two-way: tuning the paid limit DOWN (e.g. moving a teacher to a
            // smaller package) is the main reason it exists. Lowering never touches links that are
            // already bound; it only stops new ones until usage falls back under the limit.
            teacher.LinkedStudentCapacity = request.NewCapacity;
            EnforceCapacityInvariant(teacher);
            await _unitOfWork.Users.UpdateTeacherAsync(teacher);

            await _unitOfWork.CapacityRequestsRepo.AddAsync(row);
            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitAsync();
        }
        catch { await _unitOfWork.RollbackAsync(); throw; }

        // The teacher is told about a RAISE only — a decrease is an internal/commercial change
        // the admin communicates out of band, and a push saying "your limit went down" on a plan
        // the teacher did not just change would be alarming.
        if (row.RequestedCapacity > row.CapacityAtRequest)
            EnqueueCapacityApprovedNotification(row.TeacherId, row.Id);

        return Result<CapacityRequestDto>.Success(
            ToCapacityRequestDto(row), _localizer,
            SubscriptionConstants.Messages.LinkedStudentCapacityUpdated);
    }

    /// <inheritdoc />
    public async Task<Result<CapacityRequestDto>> RejectCapacityRequestAsync(
        long adminUserId, long requestId, string rejectionReason)
    {
        // ── Validation ──
        if (string.IsNullOrWhiteSpace(rejectionReason))
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.RejectionReasonRequired);
        }

        // ── Load + state check ──
        var request = await _unitOfWork.CapacityRequestsRepo.GetByIdForAdminAsync(requestId);
        if (request is null)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.CapacityRequestNotFound, HttpStatusCode.NotFound);
        }

        if (request.Status != CapacityRequestStatus.Pending)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.CapacityRequestNotPending,
                HttpStatusCode.Conflict);
        }

        // ── Persist rejection ──
        string trimmedReason = rejectionReason.Trim();
        if (trimmedReason.Length > SubscriptionConstants.RejectionReasonMaxLength)
        {
            trimmedReason = trimmedReason[..SubscriptionConstants.RejectionReasonMaxLength];
        }

        request.Status = CapacityRequestStatus.Rejected;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedByUserId = adminUserId;
        request.RejectionReason = trimmedReason;

        _unitOfWork.CapacityRequestsRepo.UpdateRequest(request);
        await _unitOfWork.SaveChangesAsync();

        // ── Fire the rejection notification (push + UserNotification) ──
        _backgroundJobs.Enqueue<ICapacityRequestResolvedNotificationJob>(
            job => job.SendAsync(request.TeacherId, request.Id, false, trimmedReason));

        return Result<CapacityRequestDto>.Success(
            ToCapacityRequestDto(request),
            _localizer,
            SubscriptionConstants.Messages.CapacityRequestRejected);
    }

    // ════════════════════════════════════════════════
    // PRICING (per-student rate: renewal = capacity × rate)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<SubscriptionPricingDto>> GetPricingAsync()
    {
        var setting = await _unitOfWork.SubscriptionPricingRepo.GetSettingAsync();
        if (setting is null)
        {
            // Defensive — the row is HasData-seeded; missing means the migration never ran.
            return Result<SubscriptionPricingDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PerStudentRateNotConfigured, HttpStatusCode.NotFound);
        }

        return Result<SubscriptionPricingDto>.Success(ToPricingDto(setting), _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<SubscriptionPricingDto>> UpdatePricingAsync(
        long adminUserId, UpdateSubscriptionPricingRequest request)
    {
        // The flat plan prices are NULLABLE (omitted = unchanged) but never zero/negative:
        // the same "> 0" rule as the per-student rate, checked only when a value was sent.
        if (request.PricePerStudentEGP <= 0m
            || request.ManagerialMonthlyPriceEGP is <= 0m
            || request.ManagerialPlusMonthlyPriceEGP is <= 0m)
        {
            return Result<SubscriptionPricingDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PricePerStudentMustBePositive);
        }

        var setting = await _unitOfWork.SubscriptionPricingRepo.GetSettingAsync();
        if (setting is null)
        {
            return Result<SubscriptionPricingDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PerStudentRateNotConfigured, HttpStatusCode.NotFound);
        }

        setting.PricePerStudentEGP = request.PricePerStudentEGP;
        if (request.ManagerialMonthlyPriceEGP is decimal managerial)
            setting.ManagerialMonthlyPriceEGP = managerial;
        if (request.ManagerialPlusMonthlyPriceEGP is decimal managerialPlus)
            setting.ManagerialPlusMonthlyPriceEGP = managerialPlus;
        setting.UpdatedAt = DateTime.UtcNow;
        setting.UpdatedByUserId = adminUserId;
        await _unitOfWork.SaveChangesAsync();

        // BR-SUB-009: in-flight pending payments retain their initiation-time amount
        // snapshot. No mass-update of pending rows here.

        return Result<SubscriptionPricingDto>.Success(
            ToPricingDto(setting), _localizer, SubscriptionConstants.Messages.PricePerStudentUpdated);
    }

    // ════════════════════════════════════════════════
    // MODULE QUOTAS (free-tier limits table)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<List<ModuleQuotaDto>>> GetModuleQuotasAsync()
    {
        var rows = await _unitOfWork.ModuleQuotaRepo.GetAllAsync();
        var dtos = rows.Select(ToModuleQuotaDto).ToList();
        return Result<List<ModuleQuotaDto>>.Success(dtos, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<ModuleQuotaDto>> UpdateModuleQuotaAsync(
        long adminUserId, string moduleKey, UpdateModuleQuotaRequest request)
    {
        if (request.FreeTierLimit < 0 || request.FreeTierLimit > SubscriptionConstants.MaxFreeTierLimit)
        {
            return Result<ModuleQuotaDto>.Failure(
                _localizer, SubscriptionConstants.Messages.FreeTierLimitInvalid);
        }

        // Keys are code-defined (ModuleQuotaKeys) and seeded by migration — an unknown
        // key 404s rather than inserting a row no gate would ever read.
        var quota = await _unitOfWork.ModuleQuotaRepo.GetByKeyAsync(moduleKey?.Trim() ?? string.Empty);
        if (quota is null)
        {
            return Result<ModuleQuotaDto>.Failure(
                _localizer, SubscriptionConstants.Messages.ModuleQuotaNotFound, HttpStatusCode.NotFound);
        }

        quota.FreeTierLimit = request.FreeTierLimit;
        if (request.Description is not null)
        {
            quota.Description = string.IsNullOrWhiteSpace(request.Description)
                ? null
                : request.Description.Trim();
        }
        quota.UpdatedAt = DateTime.UtcNow;
        quota.UpdatedByUserId = adminUserId;

        await _unitOfWork.SaveChangesAsync();

        // Immediate effect on this instance; other instances converge via the 60s TTL.
        SubscriptionGateService.InvalidateLimitsCache();

        return Result<ModuleQuotaDto>.Success(
            ToModuleQuotaDto(quota), _localizer, SubscriptionConstants.Messages.ModuleQuotaUpdated);
    }

    // ════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ════════════════════════════════════════════════

    /// <summary>
    /// Reads the post-mutation current subscription and wraps it in a
    /// CurrentSubscriptionDto Result. Used by all three manual-override paths so
    /// they return the same shape as the teacher-facing GetCurrentAsync.
    /// </summary>
    private async Task<Result<CurrentSubscriptionDto>> BuildCurrentDtoResultAsync(
        long teacherId, string successMessageKey)
    {
        var freshResult = await _subscriptionService.GetCurrentAsync(teacherId);
        if (!freshResult.IsSuccess || freshResult.Data is null)
        {
            // Defensive — should never happen since we just inserted/updated a row.
            return Result<CurrentSubscriptionDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionNotFound, HttpStatusCode.NotFound);
        }

        return Result<CurrentSubscriptionDto>.Success(
            freshResult.Data, _localizer, successMessageKey);
    }

    /// <summary>
    /// EC-24: returns true if the current subscription's StartDate is within the
    /// last <see cref="SubscriptionConstants.DuplicatePaymentGuardHours"/> hours
    /// relative to the supplied <paramref name="pendingInitiatedAt"/>.
    /// </summary>
    private static bool IsRecentlyCreated(
        Domain.Interfaces.CurrentSubscriptionStatusProjection currentSub,
        DateTime pendingInitiatedAt)
    {
        TimeSpan since = pendingInitiatedAt - currentSub.StartDate;
        return since.TotalHours >= 0 && since.TotalHours < SubscriptionConstants.DuplicatePaymentGuardHours;
    }

    /// <summary>
    /// Decrypts the manual-submit payload (BR-SUB-011 admin-only view).
    /// EncryptedSubmittedDetails has the format "phone:{number};ref:{txid}".
    /// On decrypt failure (corrupt blob, key rotation), returns ("", trimmed-reference).
    /// </summary>
    private (string PhoneNumber, string TransactionRef) DecryptSubmittedDetails(
          string? encryptedBlob, string? plainReferenceFallback)
    {
        if (string.IsNullOrEmpty(encryptedBlob))
        {
            return (string.Empty, plainReferenceFallback ?? string.Empty);
        }

        try
        {
            // Format produced by SubscriptionService.SubmitManualAsync:
            //   "phone:{paymentPhone};ref:{transactionReference}"
            string plain = _encryption.Decrypt(encryptedBlob);

            string phone = ExtractField(plain, "phone:");
            string txRef = ExtractField(plain, "ref:");

            return (
                phone,
                string.IsNullOrEmpty(txRef) ? (plainReferenceFallback ?? string.Empty) : txRef);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt pending-payment submitted details");
            return (string.Empty, plainReferenceFallback ?? string.Empty);
        }
    }

    private static string ExtractField(string blob, string prefix)
    {
        int start = blob.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        start += prefix.Length;
        int end = blob.IndexOf(';', start);
        return end < 0 ? blob[start..].Trim() : blob[start..end].Trim();
    }

    private static CapacityRequestDto ToCapacityRequestDto(CapacityIncreaseRequest row) => new()
    {
        Id = row.Id,
        CapacityKind = row.CapacityKind,
        RequestedCapacity = row.RequestedCapacity,
        CapacityAtRequest = row.CapacityAtRequest,
        Status = row.Status,
        Note = row.Note,
        RejectionReason = row.RejectionReason,
        RequestedAt = row.RequestedAt,
        ResolvedAt = row.ResolvedAt
    };

    private static SubscriptionPricingDto ToPricingDto(SubscriptionPricingSetting setting) => new()
    {
        PricePerStudentEGP = setting.PricePerStudentEGP,
        ManagerialMonthlyPriceEGP = setting.ManagerialMonthlyPriceEGP,
        ManagerialPlusMonthlyPriceEGP = setting.ManagerialPlusMonthlyPriceEGP,
        UpdatedAt = setting.UpdatedAt,
        UpdatedByUserId = setting.UpdatedByUserId
    };

    private static ModuleQuotaDto ToModuleQuotaDto(ModuleQuota quota) => new()
    {
        ModuleKey = quota.ModuleKey,
        FreeTierLimit = quota.FreeTierLimit,
        Description = quota.Description,
        UpdatedAt = quota.UpdatedAt
    };
    /// <inheritdoc />
    public async Task<Result<CurrentSubscriptionDto>> CancelAsync(
        long adminUserId, AdminCancelRequest request)
    {
        DateTime now = DateTime.UtcNow;

        // ── Expire the current row in place inside a transaction ──
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            var currentSub = await _unitOfWork.Users
                .GetCurrentSubscriptionForUpdateAsync(request.TeacherId);

            if (currentSub is null)
            {
                // No current row → nothing to cancel (mirrors ExtendAsync).
                await _unitOfWork.RollbackAsync();
                return Result<CurrentSubscriptionDto>.Failure(
                    _localizer, SubscriptionConstants.Messages.NoActiveSubscription, HttpStatusCode.NotFound);
            }

            // Idempotent: already expired → no write, just report the current state.
            if (currentSub.EndDate > now)
            {
                currentSub.EndDate = now;
                // Stamp the admin as most-recent modifier (FR-SUB-064), same as Extend/SetEndDate.
                currentSub.CreatedByUserId = adminUserId;

                await _unitOfWork.SaveChangesAsync();
            }

            await _unitOfWork.CommitAsync();
        }
        catch
        {
            await _unitOfWork.RollbackAsync();
            throw;
        }

        await _cache.InvalidateAsync(request.TeacherId);

        return await BuildCurrentDtoResultAsync(
            request.TeacherId, SubscriptionConstants.Messages.SubscriptionCancelled);
    }
}