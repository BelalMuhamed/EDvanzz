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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Net;

namespace Edvanz.Application.Services;

/// <summary>
/// Teacher-facing subscription operations (§4.1 / §6.3 / §6.4).
///
/// CRITICAL FLOW — ConfirmPaymentAsync:
///   This service owns the §6.3 confirm pipeline shared by the Paymob webhook
///   and the admin manual-approval path. The flow:
///     (1) Capture paymentConfirmedAt = UtcNow ONCE (BR-SUB-013).
///     (2) Begin Serializable transaction.
///     (3) Pessimistic-lock currentSub via UPDLOCK + HOLDLOCK.
///     (4) Compute new StartDate per §6.2.
///     (5) Flip previous IsCurrent = false, insert new IsCurrent = true.
///     (6) Persist pending row resolution.
///     (7) SaveChanges — RowVersion concurrency check fires here.
///     (8) Commit.
///     (9) Synchronously invalidate Redis cache.
///     (10) Enqueue IRenewalNotificationJob fire-and-forget.
///   Bounded retry (max 2) on DbUpdateConcurrencyException or unique-violation.
///
/// IDEMPOTENCY:
///   Every confirm path is idempotent. A second call with a pending row already in
///   Status = Confirmed returns Success with WasIdempotentReplay = true (EC-03).
/// </summary>
public class SubscriptionService : ISubscriptionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISubscriptionCacheService _cache;
    private readonly IEncryptionService _encryption;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly SubscriptionDefaultsOptions _defaults;
    private readonly SupportOptions _support;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(
        IUnitOfWork unitOfWork,
        ISubscriptionCacheService cache,
        IEncryptionService encryption,
        IBackgroundJobClient backgroundJobs,
        IOptions<SubscriptionDefaultsOptions> defaults,
        IOptions<SupportOptions> support,
        IStringLocalizer<Messages> localizer,
        ILogger<SubscriptionService> logger)
    {
        _unitOfWork = unitOfWork;
        _cache = cache;
        _encryption = encryption;
        _backgroundJobs = backgroundJobs;
        _defaults = defaults.Value;
        _support = support.Value;
        _localizer = localizer;
        _logger = logger;
    }

    // ════════════════════════════════════════════════
    // QUERY: GET CURRENT SUBSCRIPTION (FR-SUB-003)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<CurrentSubscriptionDto>> GetCurrentAsync(long teacherId)
    {
        // Status is DERIVED at read time per D-08 — never stored.
        var projection = await _unitOfWork.Users.GetCurrentSubscriptionStatusAsync(teacherId);

        if (projection is null)
        {
            return Result<CurrentSubscriptionDto>.Failure(
                _localizer, SubscriptionConstants.Messages.NoActiveSubscription, HttpStatusCode.NotFound);
        }

        // The limit the Full-plan price is computed from, so the screen can show the two together.
        // Loaded ONCE and handed to the price helper — it used to fetch the same row itself.
        var pricedTeacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);

        // Load the renewal price separately — plan-aware: Full = Teacher.LinkedStudentCapacity ×
        // the per-student rate; the managerial plans = their flat price. Not stored on the
        // subscription row (BR-SUB-009).
        decimal renewalAmount = await ComputeRenewalPriceAsync(pricedTeacher, projection.PlanType);

        // SubscriptionStatusCalculator.Derive expects a TeacherSubscription instance.
        // Build a transient one from the projection's two dates — IsCurrent = true so
        // Derive returns Active/ExpiringSoon/Expired (not Historical).
        var subForStatus = new TeacherSubscription
        {
            StartDate = projection.StartDate,
            EndDate = projection.EndDate,
            IsCurrent = true
        };

        var dto = new CurrentSubscriptionDto
        {
            Id = projection.SubscriptionId,
            StartDate = projection.StartDate,
            EndDate = projection.EndDate,
            DaysRemaining = SubscriptionStatusCalculator.DeriveDaysRemaining(subForStatus, DateTime.UtcNow),
            Status = SubscriptionStatusCalculator.Derive(subForStatus, DateTime.UtcNow),
            PlanType = projection.PlanType,
            RenewalAmountEGP = renewalAmount,
            LinkedStudentCapacity = pricedTeacher?.LinkedStudentCapacity ?? 0
        };

        return Result<CurrentSubscriptionDto>.Success(dto, _localizer);
    }

    // ════════════════════════════════════════════════
    // QUERY: STATUS (backend-driven indicator/banner contract)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<SubscriptionStatusDto>> GetStatusAsync(long teacherId)
    {
        var projection = await _unitOfWork.Users.GetCurrentSubscriptionStatusAsync(teacherId);
        bool hasPending = await _unitOfWork.SubscriptionRequestsRepo.HasPendingRequestAsync(teacherId);
        string? whatsApp = string.IsNullOrWhiteSpace(_support.WhatsAppNumber) ? null : _support.WhatsAppNumber;

        // Student-app-account seats: the limit the price is based on, plus live usage (Active AND
        // bound links only). Reported on BOTH branches — a teacher with no subscription still has
        // a limit, and the app renders "X of Y app accounts used" either way.
        var seatTeacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        int linkedCapacity = seatTeacher?.LinkedStudentCapacity ?? 0;
        int linkedUsed = await _unitOfWork.studentTeacherLinkRepo.CountBoundLinksAsync(teacherId);

        // Brand-new tutor (or a purged subscription) — no subscription at all.
        if (projection is null)
        {
            return Result<SubscriptionStatusDto>.Success(new SubscriptionStatusDto
            {
                HasSubscription = false,
                DaysRemaining = 0,
                AttentionLevel = "critical",
                CtaType = hasPending ? "none" : "subscribe",
                HasPendingRequest = hasPending,
                WhatsAppNumber = whatsApp,
                Features = new SubscriptionFeaturesDto
                {
                    LinkedStudentCapacity = linkedCapacity,
                    LinkedStudentsUsed = linkedUsed
                },
                Message = hasPending
                    ? _localizer[SubscriptionConstants.Messages.SubscriptionStatusRequestPending]
                    : _localizer[SubscriptionConstants.Messages.SubscriptionStatusNone]
            }, _localizer);
        }

        var subForStatus = new TeacherSubscription
        {
            StartDate = projection.StartDate,
            EndDate = projection.EndDate,
            IsCurrent = true
        };
        var status = SubscriptionStatusCalculator.Derive(subForStatus, DateTime.UtcNow);
        int daysRemaining = SubscriptionStatusCalculator.DeriveDaysRemaining(subForStatus, DateTime.UtcNow);
        decimal renewalAmount = await ComputeRenewalPriceAsync(seatTeacher, projection.PlanType);

        // ALL presentation logic decided here so the client renders it verbatim.
        string attention;
        string cta;
        string message;
        if (status == SubscriptionStatus.Expired)
        {
            attention = "critical";
            cta = hasPending ? "none" : "renew";
            message = hasPending
                ? _localizer[SubscriptionConstants.Messages.SubscriptionStatusRequestPending]
                : _localizer[SubscriptionConstants.Messages.SubscriptionStatusExpired];
        }
        else if (status == SubscriptionStatus.ExpiringSoon)
        {
            attention = "warning";
            cta = hasPending ? "none" : "renew";
            message = hasPending
                ? _localizer[SubscriptionConstants.Messages.SubscriptionStatusRequestPending]
                : _localizer[SubscriptionConstants.Messages.SubscriptionStatusExpiringSoon, daysRemaining];
        }
        else
        {
            attention = "none";
            cta = "none";
            message = _localizer[SubscriptionConstants.Messages.SubscriptionStatusActive];
        }

        // Plan restrictions only bite while the plan is live — an expired plan restricts nothing
        // (free-tier behavior). Same derivation as SubscriptionGateService.GetPlanEntitlementsAsync,
        // computed off the already-loaded projection to avoid a second query.
        bool planIsLive = status == SubscriptionStatus.Active || status == SubscriptionStatus.ExpiringSoon;
        var features = new SubscriptionFeaturesDto
        {
            StudentAccountsAllowed = !planIsLive
                || !SubscriptionPlanCapabilities.BlocksStudentAndParentAccounts(projection.PlanType),
            ParentFollowUpAllowed = !planIsLive
                || !SubscriptionPlanCapabilities.BlocksParentFollowUp(projection.PlanType),
            LinkedStudentCapacity = linkedCapacity,
            LinkedStudentsUsed = linkedUsed
        };

        return Result<SubscriptionStatusDto>.Success(new SubscriptionStatusDto
        {
            HasSubscription = true,
            PlanType = projection.PlanType,
            Status = status,
            DaysRemaining = daysRemaining,
            EndDate = projection.EndDate,
            RenewalAmountEGP = renewalAmount,
            AttentionLevel = attention,
            CtaType = cta,
            Message = message,
            HasPendingRequest = hasPending,
            WhatsAppNumber = whatsApp,
            Features = features
        }, _localizer);
    }

    // ════════════════════════════════════════════════
    // QUERY: PLANS / PRICING (display-only fee)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<SubscriptionPlansDto>> GetPlansAsync()
    {
        var setting = await _unitOfWork.SubscriptionPricingRepo.GetSettingAsync();
        return Result<SubscriptionPlansDto>.Success(new SubscriptionPlansDto
        {
            PerStudentMonthlyEGP = setting?.PricePerStudentEGP ?? 0m,
            ManagerialMonthlyEGP = setting?.ManagerialMonthlyPriceEGP ?? 0m,
            ManagerialPlusMonthlyEGP = setting?.ManagerialPlusMonthlyPriceEGP ?? 0m
        }, _localizer);
    }

    // ════════════════════════════════════════════════
    // NEW-SUBSCRIPTION REQUESTS (teacher side)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<SubscriptionRequestDto>> CreateSubscriptionRequestAsync(
        long teacherId, long actingUserId, CreateSubscriptionRequestRequest request)
    {
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);
        }
        // A center-owned teacher is covered by the center's subscription — they cannot self-provision.
        // (To run their own plan they must first go independent — see the independence-request flow.)
        if (teacher.CenterId != null)
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, "SubscriptionManagedByCenter", HttpStatusCode.Forbidden);
        }

        var setting = await _unitOfWork.SubscriptionPricingRepo.GetSettingAsync();
        if (setting is null || setting.PricePerStudentEGP <= 0)
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PerStudentRateNotConfigured);
        }

        // ── Server-authoritative fee: Full = student APP ACCOUNTS × rate; the two managerial
        // plans are flat. Students-in-the-account is a free operational quota, so it never
        // enters the fee. ──
        int students;
        int linkedStudents;
        decimal amount;
        if (request.PlanType == SubscriptionPlanType.Managerial)
        {
            students = 0;
            linkedStudents = 0;
            amount = setting.ManagerialMonthlyPriceEGP;
        }
        else if (request.PlanType == SubscriptionPlanType.ManagerialPlus)
        {
            students = 0;
            linkedStudents = 0;
            amount = setting.ManagerialPlusMonthlyPriceEGP;
        }
        else
        {
            if (request.RequestedStudents <= 0)
            {
                return Result<SubscriptionRequestDto>.Failure(
                    _localizer, SubscriptionConstants.Messages.RequestedStudentsRequired);
            }
            if (request.RequestedStudents > SubscriptionConstants.MaxStudentCapacity)
            {
                return Result<SubscriptionRequestDto>.Failure(
                    _localizer, SubscriptionConstants.Messages.RequestedStudentsTooLarge);
            }

            // An app build that predates the second limit sends only RequestedStudents — fall
            // back to it so such a request is priced and granted EXACTLY as it was before.
            int linked = request.RequestedLinkedStudents ?? request.RequestedStudents;

            if (linked <= 0)
            {
                return Result<SubscriptionRequestDto>.Failure(
                    _localizer, SubscriptionConstants.Messages.RequestedLinkedStudentsRequired);
            }
            if (linked > SubscriptionConstants.MaxStudentCapacity)
            {
                return Result<SubscriptionRequestDto>.Failure(
                    _localizer, SubscriptionConstants.Messages.RequestedLinkedStudentsTooLarge);
            }
            // A linked account always needs a student record behind it, so app accounts can
            // never exceed the students in the account.
            if (linked > request.RequestedStudents)
            {
                return Result<SubscriptionRequestDto>.Failure(
                    _localizer, SubscriptionConstants.Messages.RequestedLinkedStudentsExceedsAccountStudents);
            }

            students = request.RequestedStudents;
            linkedStudents = linked;
            amount = linkedStudents * setting.PricePerStudentEGP;
        }

        if (await _unitOfWork.SubscriptionRequestsRepo.HasPendingRequestAsync(teacherId))
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequestAlreadyPending,
                HttpStatusCode.Conflict);
        }

        string? note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 500 }) note = note[..500];

        var row = new SubscriptionRequest
        {
            TeacherId = teacherId,
            PlanType = request.PlanType,
            RequestedStudents = students,
            RequestedLinkedStudents = linkedStudents,
            ComputedAmountEGP = amount,
            Note = note,
            Status = SubscriptionRequestStatus.Pending,
            RequestedAt = DateTime.UtcNow,
            RequestedByUserId = actingUserId,
            CreateAt = DateTime.UtcNow
        };

        await _unitOfWork.SubscriptionRequestsRepo.AddAsync(row);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race-loss on UX_SubscriptionRequests_Teacher_Pending.
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequestAlreadyPending,
                HttpStatusCode.Conflict);
        }

        return Result<SubscriptionRequestDto>.Success(
            ToSubscriptionRequestDto(row),
            _localizer,
            SubscriptionConstants.Messages.SubscriptionRequestSubmitted);
    }

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<SubscriptionRequestDto>>>> GetSubscriptionRequestsPagedAsync(
        long teacherId, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var (items, totalCount) = await _unitOfWork.SubscriptionRequestsRepo
            .GetByTeacherPagedAsync(teacherId, page, pageSize);

        var paged = new PaginatedResponse<List<SubscriptionRequestDto>>
        {
            data = items.Select(ToSubscriptionRequestDto).ToList(),
            page = page,
            pageSize = pageSize,
            totalCount = totalCount
        };

        return Result<PaginatedResponse<List<SubscriptionRequestDto>>>.Success(paged, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<SubscriptionRequestDto>> CancelSubscriptionRequestAsync(
        long teacherId, long actingUserId, long requestId)
    {
        var row = await _unitOfWork.SubscriptionRequestsRepo
            .GetByIdAndTeacherAsync(requestId, teacherId);

        if (row is null)
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequestNotFound, HttpStatusCode.NotFound);
        }

        if (row.Status != SubscriptionRequestStatus.Pending)
        {
            return Result<SubscriptionRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequestNotPending,
                HttpStatusCode.Conflict);
        }

        row.Status = SubscriptionRequestStatus.Cancelled;
        row.ResolvedAt = DateTime.UtcNow;
        row.ResolvedByUserId = actingUserId;
        _unitOfWork.SubscriptionRequestsRepo.UpdateRequest(row);
        await _unitOfWork.SaveChangesAsync();

        return Result<SubscriptionRequestDto>.Success(
            ToSubscriptionRequestDto(row),
            _localizer,
            SubscriptionConstants.Messages.SubscriptionRequestCancelled);
    }

    internal static SubscriptionRequestDto ToSubscriptionRequestDto(SubscriptionRequest row) => new()
    {
        Id = row.Id,
        PlanType = row.PlanType,
        RequestedStudents = row.RequestedStudents,
        RequestedLinkedStudents = row.RequestedLinkedStudents,
        ComputedAmountEGP = row.ComputedAmountEGP,
        Status = row.Status,
        Note = row.Note,
        RejectionReason = row.RejectionReason,
        RequestedAt = row.RequestedAt,
        ResolvedAt = row.ResolvedAt
    };

    // ════════════════════════════════════════════════
    // QUERY: PAYMENT HISTORY (FR-SUB-039)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<SubscriptionHistoryItemDto>>>> GetHistoryPagedAsync(
        long teacherId, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var allRows = await _unitOfWork.Users.GetAllSubscriptionsByTeacherIdAsync(teacherId);

        // The repo returns the unpaged list; paginate in memory because a teacher
        // typically has <50 historical rows — flagged in Phase 03 review.
        int totalCount = allRows.Count;
        var pageRows = allRows
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SubscriptionHistoryItemDto
            {
                Id = s.Id,
                StartDate = s.StartDate,
                EndDate = s.EndDate,
                AmountPaidEGP = s.AmountPaidEGP,
                PaymentMethod = s.PaymentMethod,
                PaymentChannel = s.PaymentChannel,
                MaskedTransactionReference = MaskReference(s.TransactionReference),
                PaymentConfirmedAt = s.PaymentConfirmedAt ,
            })
            .ToList();

        var paged = new PaginatedResponse<List<SubscriptionHistoryItemDto>>
        {
            data = pageRows,
            page = page,
            pageSize = pageSize,
            totalCount = totalCount
        };

        return Result<PaginatedResponse<List<SubscriptionHistoryItemDto>>>.Success(paged, _localizer);
    }

    // ════════════════════════════════════════════════
    // COMMAND: INITIATE RENEWAL (§6.4 / FR-SUB-030)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<RenewInitiateResponse>> InitiateRenewalAsync(
        long teacherId, RenewInitiateRequest request)
    {
        // ── Validation ──
        if (request.PaymentMethod == PaymentMethod.SuperAdminManual)
        {
            return Result<RenewInitiateResponse>.Failure(
                _localizer, SubscriptionConstants.Messages.SuperAdminMethodNotAllowed);
        }

        if (await _unitOfWork.SubscriptionPaymentsRepo.HasInFlightPaymentAsync(teacherId))
        {
            return Result<RenewInitiateResponse>.Failure(
                _localizer, SubscriptionConstants.Messages.PendingPaymentAlreadyInFlight,
                HttpStatusCode.Conflict);
        }

        // ── Resolve the price: LinkedStudentCapacity × per-student rate (BR-SUB-009) ──
        // "1 student app account = 2.5 EGP/month" — the student-APP-ACCOUNT limit determines
        // what the teacher pays (students-in-the-account is a free operational quota).
        // Snapshotted onto the pending row below, so later rate or capacity changes never alter
        // an in-flight payment. Kept in lockstep with ComputeRenewalPriceAsync: the amount shown
        // on the status/current screens must be the amount actually charged.
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
        {
            return Result<RenewInitiateResponse>.Failure(
                _localizer, SubscriptionConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);
        }
        // Center-owned teachers are covered by the center's subscription — no self-renewal.
        if (teacher.CenterId != null)
        {
            return Result<RenewInitiateResponse>.Failure(
                _localizer, "SubscriptionManagedByCenter", HttpStatusCode.Forbidden);
        }

        decimal? ratePerStudent = await _unitOfWork.SubscriptionPricingRepo.GetPricePerStudentAsync();
        if (ratePerStudent is null || ratePerStudent.Value <= 0m)
        {
            return Result<RenewInitiateResponse>.Failure(
                _localizer, SubscriptionConstants.Messages.PerStudentRateNotConfigured);
        }

        // Guard the decimal(10,2) money columns: capacity must be a sane positive number.
        // Legacy int.MaxValue "unlimited" capacities were concretized by migration, but
        // fail closed here rather than overflow if an out-of-range value ever reappears.
        if (teacher.LinkedStudentCapacity <= 0 || teacher.LinkedStudentCapacity > SubscriptionConstants.MaxStudentCapacity)
        {
            return Result<RenewInitiateResponse>.Failure(
                _localizer, SubscriptionConstants.Messages.StudentCapacityNotConfigured);
        }

        decimal amountEGP = teacher.LinkedStudentCapacity * ratePerStudent.Value;

        // ── Persist a fresh pending row in Status = Initiated ──
        var pending = new PendingSubscriptionPayment
        {
            TeacherId = teacherId,
            PaymentMethod = request.PaymentMethod,
            PaymentChannel = request.PaymentChannel,
            Status = PendingPaymentStatus.Initiated,
            AmountEGP = amountEGP,
            InitiatedAt = DateTime.UtcNow,
            CreateAt = DateTime.UtcNow
        };

        await _unitOfWork.SubscriptionPaymentsRepo.AddAsync(pending);
        await _unitOfWork.SaveChangesAsync();

        // ── Manual flow (the only channel) ──
        // The Paymob gateway path was removed 2026-07-17: it was disabled in every
        // environment (stub always failed → fell through to manual), so every channel —
        // including a request that still says Paymob — returns the manual payload,
        // exactly as before. RenewInitiateResponse keeps its Mode/Paymob/Manual shape
        // for wire-compat; Mode is always "manual" now.
        var manualPayload = BuildManualPayload(pending.Id, amountEGP, request.PaymentMethod);

        return Result<RenewInitiateResponse>.Success(
            new RenewInitiateResponse { Mode = "manual", Manual = manualPayload },
            _localizer,
            SubscriptionConstants.Messages.SubscriptionRenewalInitiated);
    }

    // ════════════════════════════════════════════════
    // COMMAND: SUBMIT MANUAL DETAILS (FR-SUB-033)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<RenewStatusDto>> SubmitManualAsync(
        long teacherId, ManualSubmitRequest request)
    {
        // ── Validation ──
        if (string.IsNullOrWhiteSpace(request.TransactionReference))
        {
            return Result<RenewStatusDto>.Failure(
                _localizer, SubscriptionConstants.Messages.TransactionReferenceRequired);
        }

        if (string.IsNullOrWhiteSpace(request.PaymentPhoneNumber))
        {
            return Result<RenewStatusDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PaymentPhoneRequired);
        }

        // ── Tenant-guarded fetch ──
        var pending = await _unitOfWork.SubscriptionPaymentsRepo
            .GetByIdAndTeacherAsync(request.PendingPaymentId, teacherId);

        if (pending is null)
        {
            return Result<RenewStatusDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PendingPaymentNotFound, HttpStatusCode.NotFound);
        }

        if (pending.Status != PendingPaymentStatus.Initiated)
        {
            return Result<RenewStatusDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PendingPaymentNotInInitiated,
                HttpStatusCode.Conflict);
        }

        // ── Encrypt the submitted details (REQ-SUB-NFR-004) ──
        var sensitiveBlob =
            $"phone:{request.PaymentPhoneNumber.Trim()};ref:{request.TransactionReference.Trim()}";

        pending.EncryptedSubmittedDetails = _encryption.Encrypt(sensitiveBlob);
        pending.SubmittedTransactionReference = request.TransactionReference.Trim();
        pending.Status = PendingPaymentStatus.AwaitingSuperAdminApproval;
        _unitOfWork.SubscriptionPaymentsRepo.UpdatePending(pending);
        await _unitOfWork.SaveChangesAsync();

        return Result<RenewStatusDto>.Success(
            ToRenewStatusDto(pending),
            _localizer,
            SubscriptionConstants.Messages.ManualSubmissionRecorded);
    }

    // ════════════════════════════════════════════════
    // QUERY: POLL RENEWAL STATUS
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<RenewStatusDto>> GetRenewalStatusAsync(
        long teacherId, long pendingPaymentId)
    {
        var pending = await _unitOfWork.SubscriptionPaymentsRepo
            .GetByIdAndTeacherAsync(pendingPaymentId, teacherId);

        if (pending is null)
        {
            return Result<RenewStatusDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PendingPaymentNotFound, HttpStatusCode.NotFound);
        }

        return Result<RenewStatusDto>.Success(ToRenewStatusDto(pending), _localizer);
    }

    // ════════════════════════════════════════════════
    // CAPACITY-INCREASE REQUESTS (teacher side)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<CapacityRequestDto>> SubmitCapacityRequestAsync(
        long teacherId, long actingUserId, CreateCapacityRequestRequest request)
    {
        // Which limit is being raised. Omitted / null = AccountStudents, so an app build that
        // predates the student-app-account limit behaves exactly as it always did.
        CapacityKind kind = request.CapacityKind ?? CapacityKind.AccountStudents;

        // ── Validation ──
        if (request.RequestedCapacity <= 0
            || request.RequestedCapacity > SubscriptionConstants.MaxStudentCapacity)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer,
                kind == CapacityKind.LinkedStudents
                    ? SubscriptionConstants.Messages.RequestedLinkedStudentsTooLarge
                    : SubscriptionConstants.Messages.RequestedCapacityTooLarge);
        }

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);
        }
        // Center-owned teachers' capacity is governed by the center's quota package — not self-requested.
        if (teacher.CenterId != null)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, "SubscriptionManagedByCenter", HttpStatusCode.Forbidden);
        }

        // The current value OF THE TARGETED LIMIT — never compare a linked-accounts request
        // against the students-in-the-account number.
        int currentCapacity = kind == CapacityKind.LinkedStudents
            ? teacher.LinkedStudentCapacity
            : teacher.StudentCapacity;

        // Increase-only: decreases stay an admin-side operation.
        if (request.RequestedCapacity <= currentCapacity)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer,
                kind == CapacityKind.LinkedStudents
                    ? SubscriptionConstants.Messages.LinkedCapacityIncreaseOnly
                    : SubscriptionConstants.Messages.RequestedCapacityMustExceedCurrent);
        }

        // Per-kind queue: a pending students-in-the-account request must not block a
        // student-app-account request (the filtered unique index allows one live row per kind).
        if (await _unitOfWork.CapacityRequestsRepo.HasPendingRequestAsync(teacherId, kind))
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.CapacityRequestAlreadyPending,
                HttpStatusCode.Conflict);
        }

        string? note = string.IsNullOrWhiteSpace(request.Note)
            ? null
            : request.Note.Trim();
        if (note is { Length: > 500 })
            note = note[..500];

        var row = new CapacityIncreaseRequest
        {
            TeacherId = teacherId,
            CapacityKind = kind,
            CapacityAtRequest = currentCapacity,
            RequestedCapacity = request.RequestedCapacity,
            Note = note,
            Status = CapacityRequestStatus.Pending,
            RequestedAt = DateTime.UtcNow,
            RequestedByUserId = actingUserId,
            CreateAt = DateTime.UtcNow
        };

        await _unitOfWork.CapacityRequestsRepo.AddAsync(row);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race-loss on UX_CapacityIncreaseRequests_Teacher_Kind_Pending — a concurrent
            // submit won. Same outcome as the pre-check above.
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.CapacityRequestAlreadyPending,
                HttpStatusCode.Conflict);
        }

        return Result<CapacityRequestDto>.Success(
            ToCapacityRequestDto(row),
            _localizer,
            SubscriptionConstants.Messages.CapacityRequestSubmitted);
    }

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<CapacityRequestDto>>>> GetCapacityRequestsPagedAsync(
        long teacherId, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var (items, totalCount) = await _unitOfWork.CapacityRequestsRepo
            .GetByTeacherPagedAsync(teacherId, page, pageSize);

        var paged = new PaginatedResponse<List<CapacityRequestDto>>
        {
            data = items.Select(ToCapacityRequestDto).ToList(),
            page = page,
            pageSize = pageSize,
            totalCount = totalCount
        };

        return Result<PaginatedResponse<List<CapacityRequestDto>>>.Success(paged, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<CapacityRequestDto>> CancelCapacityRequestAsync(
        long teacherId, long actingUserId, long requestId)
    {
        var row = await _unitOfWork.CapacityRequestsRepo
            .GetByIdAndTeacherAsync(requestId, teacherId);

        if (row is null)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.CapacityRequestNotFound, HttpStatusCode.NotFound);
        }

        if (row.Status != CapacityRequestStatus.Pending)
        {
            return Result<CapacityRequestDto>.Failure(
                _localizer, SubscriptionConstants.Messages.CapacityRequestNotPending,
                HttpStatusCode.Conflict);
        }

        row.Status = CapacityRequestStatus.Cancelled;
        row.ResolvedAt = DateTime.UtcNow;
        row.ResolvedByUserId = actingUserId;
        _unitOfWork.CapacityRequestsRepo.UpdateRequest(row);
        await _unitOfWork.SaveChangesAsync();

        return Result<CapacityRequestDto>.Success(
            ToCapacityRequestDto(row),
            _localizer,
            SubscriptionConstants.Messages.CapacityRequestCancelled);
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

    // ════════════════════════════════════════════════
    // CRITICAL: CONFIRM PAYMENT (§6.3 / §6.6)
    // ════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<ConfirmPaymentResultDto>> ConfirmPaymentAsync(
        long pendingPaymentId, long? resolvedByUserId)
    {
        // ── Pre-transaction load + idempotency short-circuit (EC-03) ──
        var pending = resolvedByUserId.HasValue
            ? await _unitOfWork.SubscriptionPaymentsRepo.GetByIdForAdminAsync(pendingPaymentId)
            : await _unitOfWork.SubscriptionPaymentsRepo.GetByIdForAdminAsync(pendingPaymentId);
        // Webhook path also uses GetByIdForAdminAsync — webhook trusts the gateway's
        // session id resolution upstream and does not have a teacher tenant context.

        if (pending is null)
        {
            return Result<ConfirmPaymentResultDto>.Failure(
                _localizer, SubscriptionConstants.Messages.PendingPaymentNotFound, HttpStatusCode.NotFound);
        }

        if (pending.Status == PendingPaymentStatus.Confirmed)
        {
            // Idempotent replay (EC-03). Return the existing subscription row info.
            var existingProjection = await _unitOfWork.Users
                .GetCurrentSubscriptionStatusAsync(pending.TeacherId);

            return Result<ConfirmPaymentResultDto>.Success(new ConfirmPaymentResultDto
            {
                SubscriptionId = existingProjection?.SubscriptionId ?? 0,
                StartDate = existingProjection?.StartDate ?? DateTime.MinValue,
                EndDate = existingProjection?.EndDate ?? DateTime.MinValue,
                WasIdempotentReplay = true
            }, _localizer, SubscriptionConstants.Messages.PaymentAlreadyConfirmed);
        }

        // ── §6.3 step 1: capture paymentConfirmedAt ONCE (BR-SUB-013) ──
        DateTime paymentConfirmedAt = DateTime.UtcNow;

        // ── Bounded retry on concurrency conflict (§6.6) ──
        for (int attempt = 1; attempt <= SubscriptionConstants.MaxConcurrencyRetries + 1; attempt++)
        {
            try
            {
                var result = await ConfirmPaymentOnceAsync(pending, paymentConfirmedAt, resolvedByUserId);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (attempt <= SubscriptionConstants.MaxConcurrencyRetries)
            {
                _logger.LogWarning(
                    "Concurrency conflict on pending {PendingId} attempt {Attempt} — retrying",
                    pendingPaymentId, attempt);

                // Reset the entity so the next attempt re-reads fresh state.
                _unitOfWork.SubscriptionPaymentsRepo.DetachPending(pending);
                pending = await _unitOfWork.SubscriptionPaymentsRepo.GetByIdForAdminAsync(pendingPaymentId)
                          ?? throw new InvalidOperationException("Pending row vanished mid-retry");
            }
            catch (DbUpdateException ex)
                when (IsUniqueViolation(ex) && attempt <= SubscriptionConstants.MaxConcurrencyRetries)
            {
                // Filtered unique index IX_TeacherSubscriptions_Current was violated —
                // a competing transaction beat us to flipping IsCurrent. Retry.
                _logger.LogWarning(
                    "Unique-violation on IsCurrent index for pending {PendingId} attempt {Attempt} — retrying",
                    pendingPaymentId, attempt);
                _unitOfWork.SubscriptionPaymentsRepo.DetachPending(pending);
           
                pending = await _unitOfWork.SubscriptionPaymentsRepo.GetByIdForAdminAsync(pendingPaymentId)
                          ?? throw new InvalidOperationException("Pending row vanished mid-retry");
            }
        }

        // Exhausted retries.
        _logger.LogError("ConfirmPaymentAsync exhausted retries for pending {PendingId}", pendingPaymentId);
        return Result<ConfirmPaymentResultDto>.Failure(
            _localizer, SubscriptionConstants.Messages.ConcurrentRenewalDetected,
            HttpStatusCode.Conflict);
    }

    // ════════════════════════════════════════════════
    // PRIVATE: SINGLE CONFIRM ATTEMPT (§6.3 steps 2–10)
    // ════════════════════════════════════════════════

    private async Task<Result<ConfirmPaymentResultDto>> ConfirmPaymentOnceAsync(
        PendingSubscriptionPayment pending,
        DateTime paymentConfirmedAt,
        long? resolvedByUserId)
    {
        // ── §6.3 step 2: Serializable transaction ──
        await _unitOfWork.BeginTransactionAsync(IsolationLevel.Serializable);

        try
        {
            // ── §6.3 step 3: pessimistic-lock the current subscription ──
            var currentSub = await _unitOfWork.Users
                .GetCurrentSubscriptionForUpdateAsync(pending.TeacherId);

            // ── §6.3 step 4: compute new StartDate per §6.2 ──
            DateTime newStart = ComputeRenewalStartDate(currentSub, paymentConfirmedAt);
            DateTime newEnd = newStart.AddDays(_defaults.PeriodDays);

            var newSubscription = new TeacherSubscription
            {
                TeacherId = pending.TeacherId,
                StartDate = newStart,
                EndDate = newEnd,
                IsCurrent = true,
                PaymentMethod = pending.PaymentMethod,
                PaymentChannel = pending.PaymentChannel,
                AmountPaidEGP = pending.AmountEGP,
                TransactionReference = pending.SubmittedTransactionReference,
                EncryptedPaymentDetails = pending.EncryptedSubmittedDetails,
                PaymentConfirmedAt = paymentConfirmedAt,
                CreatedByUserId = resolvedByUserId,
                CreateAt = DateTime.UtcNow
            };

            // ── §6.3 step 5: flip previous IsCurrent + insert new ──
            await _unitOfWork.Users.FlipCurrentAndInsertNewAsync(currentSub, newSubscription);
            pending.Status = PendingPaymentStatus.Confirmed;
            pending.ResolvedAt = paymentConfirmedAt;
            pending.ResolvedByUserId = resolvedByUserId;
            _unitOfWork.SubscriptionPaymentsRepo.UpdatePending(pending);

            // ── §6.3 step 7: SaveChanges — RowVersion check fires here ──
            await _unitOfWork.SaveChangesAsync();

            // ── §6.3 step 8: Commit ──
            await _unitOfWork.CommitAsync();

            // ── §6.3 step 9: synchronously invalidate the cache (BEFORE enqueue) ──
            // Order matters: invalidate first so any racing read after this point
            // hits the DB and sees the new row. Enqueue second — if the cache
            // invalidation fails, we still want the notification to fire.
            await _cache.InvalidateAsync(pending.TeacherId);

            // ── §6.3 step 10: fire-and-forget notification ──
            _backgroundJobs.Enqueue<IRenewalNotificationJob>(
                job => job.SendAsync(pending.TeacherId, newSubscription.Id));

            return Result<ConfirmPaymentResultDto>.Success(new ConfirmPaymentResultDto
            {
                SubscriptionId = newSubscription.Id,
                StartDate = newStart,
                EndDate = newEnd,
                WasIdempotentReplay = false
            }, _localizer, SubscriptionConstants.Messages.PaymentConfirmed);
        }
        catch
        {
            // Roll back; let the outer retry loop catch concurrency exceptions.
            await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    // ════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ════════════════════════════════════════════════

    /// <summary>
    /// §6.2 StartDate rule:
    ///   - Early renewal (currentSub still active): newStart = currentSub.EndDate.
    ///   - Late or first renewal (currentSub null/expired): newStart = paymentConfirmedAt.
    /// </summary>
    private static DateTime ComputeRenewalStartDate(
        TeacherSubscription? currentSub, DateTime paymentConfirmedAt)
    {
        if (currentSub is null) return paymentConfirmedAt;

        // If the current subscription's EndDate is in the future, we're an early
        // renewal — chain on top of it. Otherwise, start fresh from confirmation.
        return currentSub.EndDate > paymentConfirmedAt
            ? currentSub.EndDate
            : paymentConfirmedAt;
    }

    /// <summary>
    /// Read-time renewal price for display (GET current): LinkedStudentCapacity × per-student
    /// rate. The teacher pays for STUDENT APP ACCOUNTS, not for how many student records exist
    /// in the account (that quota is free). Returns 0 when unpriceable (teacher missing, capacity
    /// out of bounds, or the rate not configured) — same "0 when unpriceable" semantics the
    /// package-price resolver had, so the CurrentSubscriptionDto wire contract is unchanged.
    /// </summary>
    private async Task<decimal> ComputeRenewalPriceAsync(Teacher? teacher, SubscriptionPlanType? planType)
    {
        // The two managerial plans renew at their FLAT monthly price — capacity × rate is a
        // Full-plan formula only (it used to be applied to every plan, which showed a Managerial
        // teacher a per-student renewal figure they would never be charged).
        if (planType is SubscriptionPlanType.Managerial or SubscriptionPlanType.ManagerialPlus)
        {
            var setting = await _unitOfWork.SubscriptionPricingRepo.GetSettingAsync();
            return planType == SubscriptionPlanType.Managerial
                ? setting?.ManagerialMonthlyPriceEGP ?? 0m
                : setting?.ManagerialPlusMonthlyPriceEGP ?? 0m;
        }

        // The teacher is passed in, never re-fetched: both callers already hold the row (they
        // report LinkedStudentCapacity alongside the price), and GET /status is hit on every app
        // launch — loading it twice there was a needless extra round-trip on a hot path.
        if (teacher is null) return 0m;

        if (teacher.LinkedStudentCapacity <= 0 || teacher.LinkedStudentCapacity > SubscriptionConstants.MaxStudentCapacity)
            return 0m;

        decimal? ratePerStudent = await _unitOfWork.SubscriptionPricingRepo.GetPricePerStudentAsync();
        if (ratePerStudent is null || ratePerStudent.Value <= 0m) return 0m;

        return teacher.LinkedStudentCapacity * ratePerStudent.Value;
    }

    private static RenewStatusDto ToRenewStatusDto(PendingSubscriptionPayment pending) => new()
    {
        PendingPaymentId = pending.Id,
        Status = pending.Status,
        InitiatedAt = pending.InitiatedAt,
        ResolvedAt = pending.ResolvedAt,
        RejectionReason = pending.RejectionReason
    };

    /// <summary>
    /// Masks the trailing portion of a transaction reference for tutor-facing payment history (BR-SUB-011).
    /// "PMB123456789" → "PMB****6789".
    /// </summary>
    private static string? MaskReference(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        if (reference.Length <= 7) return reference; // too short to mask meaningfully

        return string.Concat(
            reference.AsSpan(0, 3),
            "****",
            reference.AsSpan(reference.Length - 4));
    }

    private string BuildManualPayLine(decimal amountEGP, PaymentMethod method)
    {
        string template = method switch
        {
            PaymentMethod.VodafoneCash => _localizer[
                SubscriptionConstants.Messages.ManualPayInstructionsVodafoneCash],
            PaymentMethod.InstaPay => _localizer[
                SubscriptionConstants.Messages.ManualPayInstructionsInstaPay],
            _ => string.Empty
        };

        string payTo = method switch
        {
            PaymentMethod.VodafoneCash => _defaults.ManualPayToVodafoneCash,
            PaymentMethod.InstaPay => _defaults.ManualPayToInstaPay,
            _ => string.Empty
        };

        return string.Format(template, amountEGP, payTo);
    }

    private ManualInitiatePayload BuildManualPayload(
        long pendingPaymentId, decimal amountEGP, PaymentMethod method)
    {
        string payTo = method switch
        {
            PaymentMethod.VodafoneCash => _defaults.ManualPayToVodafoneCash,
            PaymentMethod.InstaPay => _defaults.ManualPayToInstaPay,
            _ => string.Empty
        };

        return new ManualInitiatePayload
        {
            PendingPaymentId = pendingPaymentId,
            PayToNumber = payTo,
            Instructions = BuildManualPayLine(amountEGP, method),
            AmountEGP = amountEGP
        };
    }

    /// <summary>
    /// Detects SQL Server unique-key violation (errors 2601/2627) inside DbUpdateException.
    /// Specifically used to recognize a race-loss on the IX_TeacherSubscriptions_Current
    /// filtered unique index.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var sqlException = ex.InnerException as Microsoft.Data.SqlClient.SqlException
                           ?? ex.GetBaseException() as Microsoft.Data.SqlClient.SqlException;

        return sqlException is { Number: 2601 or 2627 };
    }
}