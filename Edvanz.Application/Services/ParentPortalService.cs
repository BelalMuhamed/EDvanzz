using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Edvanz.Application.Common;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.ParentPortal;
using Edvanz.Application.Dtos.ParentUser;
using Edvanz.Application.Options;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Edvanz.Application.Services;

/// <inheritdoc cref="IParentPortalService"/>
public sealed class ParentPortalService : IParentPortalService
{
    /// <summary>Rolling window both abuse caps are measured over.</summary>
    private static readonly TimeSpan AbuseWindow = TimeSpan.FromHours(1);

    /// <summary>A teacher gets at most ONE "parents are waiting" notification per this window.</summary>
    private static readonly TimeSpan NotificationBatchWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// After a teacher rejects a request, further requests on the same (student, device) or
    /// (student, phone) are silently discarded for this long. Without it a rejected parent can
    /// re-submit immediately and keep repopulating the inbox — <c>Rejected</c> is a terminal
    /// status, so the live-row unique index does not stop them.
    /// </summary>
    private static readonly TimeSpan RejectionCooldown = TimeSpan.FromHours(24);

    /// <summary>Teacher codes are fixed-width 8 digits.</summary>
    private const int TeacherCodeLength = 8;

    /// <summary>Grades page size used by the dashboard's embedded grades section.</summary>
    private const int DashboardGradesPageSize = 20;

    private const int MaxGradesPageSize = 100;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IParentSectionComposer _sections;
    private readonly ISubscriptionGateService _subscriptionGate;
    private readonly ITimeZoneService _timeZoneService;
    private readonly IParentPortalNotifier _notifier;
    private readonly ParentPortalOptions _options;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;
    private readonly ILogger<ParentPortalService> _logger;

    public ParentPortalService(
        IUnitOfWork unitOfWork,
        IParentSectionComposer sections,
        ISubscriptionGateService subscriptionGate,
        ITimeZoneService timeZoneService,
        IParentPortalNotifier notifier,
        IOptions<ParentPortalOptions> options,
        IStringLocalizer<Domain.Resources.Messages> localizer,
        ILogger<ParentPortalService> logger)
    {
        _unitOfWork = unitOfWork;
        _sections = sections;
        _subscriptionGate = subscriptionGate;
        _timeZoneService = timeZoneService;
        _notifier = notifier;
        _options = options.Value;
        _localizer = localizer;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // PUBLIC — ONBOARDING
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<ParentPortalTeacherPreviewDto>> GetTeacherPreviewAsync(
        string teacherCode, string? language)
    {
        string code = (teacherCode ?? string.Empty).Trim();
        if (code.Length != TeacherCodeLength)
            return Result<ParentPortalTeacherPreviewDto>.Failure(
                _localizer, "ParentPortalCodeLength", HttpStatusCode.BadRequest);

        var teacher = await _unitOfWork.Users.GetActiveTeacherByCodeAsync(code);
        if (teacher is null)
            return Result<ParentPortalTeacherPreviewDto>.Failure(
                _localizer, "ParentPortalTeacherNotFound", HttpStatusCode.NotFound);

        var (teacherName, subjectName, config) = await ResolveTeacherHeaderAsync(teacher.Id, language);
        bool eligible = await IsPortalEligibleAsync(teacher.Id, config);

        var dto = new ParentPortalTeacherPreviewDto
        {
            TeacherName = teacherName,
            SubjectName = subjectName,
            PortalEnabled = eligible
        };

        // The message follows the flag so the portal can render the hint without its own copy deck.
        return Result<ParentPortalTeacherPreviewDto>.Success(
            dto, _localizer, eligible ? "Success" : "ParentPortalDisabled", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<ParentPortalAccessRequestResultDto>> RequestAccessAsync(
        ParentPortalAccessRequestDto dto, string? clientIp, string? userAgent)
    {
        // ── 1. Shape validation (cheap, leaks nothing — these are format rules) ──
        string teacherCode = (dto.TeacherCode ?? string.Empty).Trim();
        if (teacherCode.Length != TeacherCodeLength)
            return Result<ParentPortalAccessRequestResultDto>.Failure(
                _localizer, "ParentPortalCodeLength", HttpStatusCode.BadRequest);

        string studentCode = (dto.StudentCode ?? string.Empty).Trim();
        if (studentCode.Length == 0)
            return Result<ParentPortalAccessRequestResultDto>.Failure(
                _localizer, "ParentPortalStudentCodeRequired", HttpStatusCode.BadRequest);

        string? deviceHash = ParentPortalHash.Compute(dto.DeviceId);
        if (deviceHash is null)
            return Result<ParentPortalAccessRequestResultDto>.Failure(
                _localizer, "ParentPortalSessionExpired", HttpStatusCode.BadRequest);

        // Phone is optional, but a SUPPLIED one must be a real Egyptian mobile — otherwise the
        // parent silently loses auto-approval and never learns why.
        string? claimedPhone = null;
        if (!string.IsNullOrWhiteSpace(dto.PhoneNumber))
        {
            claimedPhone = EgyptianPhoneNumber.Normalize(dto.PhoneNumber);
            if (claimedPhone is null)
                return Result<ParentPortalAccessRequestResultDto>.Failure(
                    _localizer, "ParentPortalPhoneFormat", HttpStatusCode.BadRequest);
        }

        // The parent's self-declared name. REQUIRED: the teacher approves by recognising a person,
        // and a request that arrives as a bare phone number gives them nothing to decide on.
        //
        // This is validated HERE, in the shape-validation block, deliberately: it runs before the
        // student code is ever resolved, so a missing name can never become a probe that
        // distinguishes a real student from an invented one.
        //
        // No normalization — nothing ever compares this value, unlike ClaimedPhone.
        // Gated by ParentPortal__RequireParentName (default false) so this API can ship BEFORE
        // the portal build that collects the name — an older portal that omits it keeps working
        // rather than 400-ing every parent. Flip the setting once that portal drop is live.
        if (string.IsNullOrWhiteSpace(dto.ParentName))
        {
            if (_options.RequireParentName)
                return Result<ParentPortalAccessRequestResultDto>.Failure(
                    _localizer, "ParentPortalNameRequired", HttpStatusCode.BadRequest);
        }
        else
        {
            string suppliedName = dto.ParentName.Trim();
            if (suppliedName.Length < ParentPortalConstants.ParentNameMinLength
                || suppliedName.Length > ParentPortalConstants.ParentNameMaxLength)
                return Result<ParentPortalAccessRequestResultDto>.Failure(
                    _localizer, "ParentPortalNameLength", HttpStatusCode.BadRequest);
        }

        // NULL (not "") when absent, so the column keeps its "no name recorded" meaning and
        // the fill-only backfill below stays a genuine fill rather than writing blanks.
        string? parentName = string.IsNullOrWhiteSpace(dto.ParentName)
            ? null
            : dto.ParentName.Trim();

        var now = DateTime.UtcNow;
        var windowStart = now - AbuseWindow;

        // ── 2. Per-device abuse cap (before any lookup — a scanner burns its budget here).
        //       A non-positive configured limit means "no cap", never "block everything". ──
        if (_options.RequestsPerDevicePerHour > 0)
        {
            int deviceRequests = await _unitOfWork.ParentPortalAccesses
                .CountPendingByDeviceSinceAsync(deviceHash, windowStart);
            if (deviceRequests >= _options.RequestsPerDevicePerHour)
                return TooManyRequests();
        }

        // ── 3. Teacher resolution ──
        // The teacher code is PUBLIC (printed on share cards) and the preview endpoint already
        // reports whether a code resolves, so a not-found here leaks nothing new.
        var teacher = await _unitOfWork.Users.GetActiveTeacherByCodeAsync(teacherCode);
        if (teacher is null)
            return Result<ParentPortalAccessRequestResultDto>.Failure(
                _localizer, "ParentPortalTeacherNotFound", HttpStatusCode.NotFound);

        // ── 4. Per-teacher abuse cap. Evaluated for EVERY request, valid or not, so it can never
        //       be used to tell a real student code from a fake one. ──
        if (_options.RequestsPerTeacherPerHour > 0)
        {
            int teacherRequests = await _unitOfWork.ParentPortalAccesses
                .CountPendingForTeacherSinceAsync(teacher.Id, windowStart);
            if (teacherRequests >= _options.RequestsPerTeacherPerHour)
                return TooManyRequests();
        }

        var (teacherName, _, config) = await ResolveTeacherHeaderAsync(teacher.Id, dto.Language);
        bool eligible = await IsPortalEligibleAsync(teacher.Id, config);

        var student = await _unitOfWork.Users.GetActiveTeacherStudentByCodeAsync(teacher.Id, studentCode);

        // ══════════════════════════════════════════════════════════════════
        // SECURITY — TWO AXES, AND THEY ARE NOT THE SAME. DO NOT MERGE THEM.
        //
        // TEACHER axis (eligibility) — MAY diverge, and deliberately does.
        //   Whether a teacher accepts portal followers is ALREADY PUBLIC: anyone can read it from
        //   GET /teachers/{teacherCode}/preview, which returns `portalEnabled` for any teacher
        //   code. Hiding it here would therefore buy exactly zero security while stranding a real
        //   parent of a not-yet-enabled teacher on a "waiting for approval" screen that can NEVER
        //   resolve — nothing was written, so no teacher will ever see a request to approve.
        //   So this returns an honest, actionable 403 telling them to ask the teacher to switch
        //   it on.
        //
        // STUDENT axis (does this roster code exist?) — MUST NEVER diverge.
        //   TeacherStudent.StudentCode is a SEQUENTIAL counter (A1, A2 … Z999) and
        //   Teacher.TeacherCode is public, so anyone could walk a teacher's entire roster by
        //   submitting codes. The ONLY thing stopping that is that a request for a code that does
        //   NOT exist is answered with the byte-identical payload a genuine pending request gets:
        //   200, state "pending", the same message, and NO student fields — writing nothing. Any
        //   divergence on THIS branch (a 404, a different code, an extra field, a different
        //   message) turns the endpoint into a roster-enumeration oracle. A REAL pending request
        //   below withholds the student's name/code/id for the same reason; student details are
        //   only ever returned on an "active" (phone-verified) result.
        //
        //   THREE separate situations all funnel into that one identical pending payload, and they
        //   must stay indistinguishable: the student code does not exist; a genuine new request was
        //   just queued; and a request suppressed by the post-rejection cooldown (step 5b).
        // ══════════════════════════════════════════════════════════════════
        if (!eligible)
            return Result<ParentPortalAccessRequestResultDto>.Failure(
                _localizer, "ParentPortalDisabled", HttpStatusCode.Forbidden);

        if (student is null)
            return PendingResult(teacherName);

        // ── 5. Already have a live grant on this device? Re-surface it instead of duplicating. ──
        var existing = await _unitOfWork.ParentPortalAccesses
            .GetLiveByStudentAndDeviceAsync(student.Id, deviceHash);
        if (existing is not null)
        {
            // Backfill the name onto a grant that predates this field. A parent who was approved
            // before names existed lands here every time their portal session lapses and they
            // re-enter the codes; without this their teacher would never see a name for them.
            // Fill-only — a stored name is never overwritten from an unauthenticated request, so
            // this cannot be used to rewrite an approved follower's identity.
            if (parentName is not null && string.IsNullOrWhiteSpace(existing.ParentName))
            {
                existing.ParentName = parentName;
                try
                {
                    await _unitOfWork.ParentPortalAccesses.UpdateAsync(existing);
                    await _unitOfWork.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    // Cosmetic enrichment: never fail the parent's sign-in over it.
                    _logger.LogWarning(ex,
                        "Parent portal: could not backfill the parent name on grant {GrantId}", existing.Id);
                }
            }

            return existing.Status == ParentPortalAccessStatus.Active
                ? ActiveResult(teacherName, student)
                : PendingResult(teacherName);
        }

        // ── 5b. POST-REJECTION COOLDOWN ──────────────────────────────────────────────────
        // Rejected is TERMINAL, so it does not occupy the live-row unique index and a rejected
        // parent could otherwise re-submit straight away and keep reappearing in the inbox
        // (bounded only by the hourly caps). Keyed on the NEWEST row across both axes, not "was
        // there ever a rejection": someone rejected yesterday and approved today must not be held.
        //
        // It returns the SAME PendingResult as everything else and writes nothing — a distinct
        // code or status here would re-open the enumeration oracle closed above.
        if (await IsInRejectionCooldownAsync(student.Id, deviceHash, claimedPhone, now))
            return PendingResult(teacherName);

        // ── 6. Decide whether this request is already trusted. TWO independent rules. ──
        //
        // (a) ROSTER PHONE — the teacher wrote this number on the student's record themselves.
        //     Compared IN MEMORY on the already-loaded row: roster phones are only Trim()-ed on
        //     write, so stored formats vary and only a normalize-both-sides comparison is correct.
        bool rosterPhoneMatches =
            EgyptianPhoneNumber.AreSameNumber(claimedPhone, student.ParentPhoneNumber);

        // (b) TRUSTED PHONE — this number already holds an ACTIVE grant on this student, so a
        //     teacher vetted it before. This is what makes access follow the PHONE instead of the
        //     browser: clearing cookies or moving to a new phone no longer re-queues an approved
        //     parent. Compared in SQL against the always-normalized ClaimedPhone column.
        bool trustedPhone = !rosterPhoneMatches
            && claimedPhone is not null
            && await _unitOfWork.ParentPortalAccesses
                .HasActiveGrantWithPhoneAsync(student.Id, claimedPhone);

        bool grantActive = rosterPhoneMatches || trustedPhone;

        // AutoApproved stays honest: TRUE only for the roster-phone rule. A trusted-phone grant is
        // not "the app let them in on its own" — a teacher approved that number once. Origin
        // carries the full reason so the teacher UI can explain the difference.
        ParentPortalAccessOrigin? origin =
            rosterPhoneMatches ? ParentPortalAccessOrigin.RosterPhone
            : trustedPhone ? ParentPortalAccessOrigin.TrustedPhone
            : null;

        // Read BEFORE the insert so the batching decision is not confused by our own new row.
        DateTime? newestPendingBefore = grantActive
            ? null
            : await _unitOfWork.ParentPortalAccesses.GetNewestPendingRequestedAtAsync(teacher.Id);

        var grant = new ParentPortalAccess
        {
            TeacherId = teacher.Id,
            TeacherStudentId = student.Id,
            DeviceHash = deviceHash,
            Status = grantActive ? ParentPortalAccessStatus.Active : ParentPortalAccessStatus.Pending,
            ClaimedPhone = claimedPhone,
            ParentName = parentName,
            AutoApproved = rosterPhoneMatches,
            Origin = origin,
            RequestedAt = now,
            RespondedAt = grantActive ? now : null,
            RequestIpHash = ParentPortalHash.Compute(clientIp),
            UserAgent = Truncate(userAgent, 256),
            CreateAt = now
        };

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction) await _unitOfWork.BeginTransactionAsync();
        try
        {
            await _unitOfWork.ParentPortalAccesses.AddAsync(grant);
            await _unitOfWork.SaveChangesAsync();
            if (ownsTransaction) await _unitOfWork.CommitAsync();
        }
        catch (Exception ex)
        {
            if (ownsTransaction) await _unitOfWork.RollbackAsync();
            _logger.LogError(ex,
                "Parent portal: could not record an access request for teacher {TeacherId}", teacher.Id);
            return Result<ParentPortalAccessRequestResultDto>.Failure(
                _localizer, "ParentPortalUnavailable", HttpStatusCode.InternalServerError);
        }

        // ── 7. Post-commit, best-effort notification (§5.1 ordering) with hourly batching. ──
        if (!grantActive)
            await NotifyTeacherAsync(teacher.Id, student.StudentName, parentName, newestPendingBefore, now);

        return grantActive ? ActiveResult(teacherName, student) : PendingResult(teacherName);
    }

    /// <summary>
    /// True when the newest grant on either axis is a rejection inside
    /// <see cref="RejectionCooldown"/>. Uses <c>RespondedAt</c> (when the teacher actually said no)
    /// and falls back to <c>RequestedAt</c> for any row missing it.
    /// </summary>
    private async Task<bool> IsInRejectionCooldownAsync(
        long teacherStudentId, string deviceHash, string? claimedPhone, DateTime nowUtc)
    {
        var newest = await _unitOfWork.ParentPortalAccesses
            .GetNewestForStudentByDeviceOrPhoneAsync(teacherStudentId, deviceHash, claimedPhone);

        if (newest is null || newest.Status != ParentPortalAccessStatus.Rejected)
            return false;

        DateTime rejectedAt = newest.RespondedAt ?? newest.RequestedAt;
        return nowUtc - rejectedAt < RejectionCooldown;
    }

    // ══════════════════════════════════════════════════════════════════════
    // PUBLIC — STATE
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<ParentPortalAccessStateDto>> GetAccessStateAsync(string deviceHash)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            return NoAccessState();

        var grant = await _unitOfWork.ParentPortalAccesses.GetLatestByDeviceAsync(deviceHash);
        if (grant is null)
            return NoAccessState();

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(grant.TeacherId);
        var (teacherName, subjectName, config) = teacher is null
            ? (string.Empty, string.Empty, (TeacherConfiguration?)null)
            : await ResolveTeacherHeaderAsync(teacher.Id, null);

        var dto = new ParentPortalAccessStateDto
        {
            TeacherName = teacherName,
            SubjectName = subjectName,
            Visibility = BuildVisibility(config)
        };

        switch (grant.Status)
        {
            case ParentPortalAccessStatus.Rejected:
                dto.State = ParentPortalConstants.States.Rejected;
                dto.Visibility = new ParentPortalVisibilityDto();
                return Result<ParentPortalAccessStateDto>.Success(
                    dto, _localizer, "ParentPortalRequestRejected", HttpStatusCode.OK);

            case ParentPortalAccessStatus.Revoked:
                // RespondedByUserId distinguishes a teacher revocation from the parent's own
                // "stop following" — the latter is simply "no access on this device" again.
                dto.Visibility = new ParentPortalVisibilityDto();
                if (grant.RespondedByUserId is null)
                {
                    dto.State = ParentPortalConstants.States.None;
                    return Result<ParentPortalAccessStateDto>.Success(
                        dto, _localizer, "ParentPortalSessionExpired", HttpStatusCode.OK);
                }
                dto.State = ParentPortalConstants.States.Revoked;
                return Result<ParentPortalAccessStateDto>.Success(
                    dto, _localizer, "ParentPortalAccessRevoked", HttpStatusCode.OK);

            case ParentPortalAccessStatus.Pending:
                // No student fields on a pending row — see the enumeration note in RequestAccessAsync.
                dto.State = ParentPortalConstants.States.Pending;
                dto.Visibility = new ParentPortalVisibilityDto();
                return Result<ParentPortalAccessStateDto>.Success(
                    dto, _localizer, "ParentPortalRequestPending", new object?[] { teacherName }, HttpStatusCode.OK);
        }

        // ── Active: everything is re-validated LIVE on every call ──
        if (teacher is null)
        {
            dto.State = ParentPortalConstants.States.Disabled;
            dto.Visibility = new ParentPortalVisibilityDto();
            return Result<ParentPortalAccessStateDto>.Success(
                dto, _localizer, "ParentPortalUnavailable", HttpStatusCode.OK);
        }

        if (!await IsPortalEligibleAsync(teacher.Id, config))
        {
            dto.State = ParentPortalConstants.States.Disabled;
            dto.Visibility = new ParentPortalVisibilityDto();
            return Result<ParentPortalAccessStateDto>.Success(
                dto, _localizer, "ParentPortalDisabled", HttpStatusCode.OK);
        }

        // Null navigation = the roster row was soft-deleted (its global filter removed it).
        if (grant.TeacherStudent is null)
        {
            dto.State = ParentPortalConstants.States.StudentRemoved;
            dto.Visibility = new ParentPortalVisibilityDto();
            return Result<ParentPortalAccessStateDto>.Success(
                dto, _localizer, "ParentPortalStudentRemoved", HttpStatusCode.OK);
        }

        dto.State = ParentPortalConstants.States.Active;
        dto.StudentName = grant.TeacherStudent.StudentName;
        dto.StudentCode = grant.TeacherStudent.StudentCode;
        dto.RosterId = grant.TeacherStudentId;
        dto.SessionName = await ResolveSessionNameAsync(teacher.Id, grant.TeacherStudent.SessionId);

        await TouchAsync(grant.Id);

        return Result<ParentPortalAccessStateDto>.Success(dto, _localizer, "Success", HttpStatusCode.OK);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PUBLIC — READS
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<ParentPortalDashboardDto>> GetDashboardAsync(string deviceHash, long rosterId)
    {
        var (context, failureKey, status) = await ResolveContextAsync(deviceHash, rosterId);
        if (context is null)
            return Result<ParentPortalDashboardDto>.Failure(_localizer, failureKey, status);

        DateTime localToday = _timeZoneService.GetTeacherLocalDate(context.Teacher.Id);

        var dto = new ParentPortalDashboardDto
        {
            Header = new ParentPortalHeaderDto
            {
                StudentName = context.Student.StudentName,
                StudentCode = context.Student.StudentCode,
                TeacherName = context.TeacherName,
                SubjectName = context.SubjectName,
                SessionName = await ResolveSessionNameAsync(context.Teacher.Id, context.Student.SessionId),
                Month = $"{localToday.Year:D4}-{localToday.Month:D2}",
                MonthLabel = MonthName(localToday.Year, localToday.Month)
            },
            Attendance = ToAttendanceSection(
                await _sections.BuildAttendanceAsync(context.Teacher.Id, context.Student.Id)),
            Payments = ToPaymentsSection(
                await _sections.BuildPaymentsAsync(context.Teacher.Id, context.Student.Id)),
            Grades = await BuildGradesSectionAsync(context, page: 1, pageSize: DashboardGradesPageSize)
        };

        return Result<ParentPortalDashboardDto>.Success(dto, _localizer, "Success", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<ParentPortalAttendanceSectionDto>> GetAttendanceAsync(
        string deviceHash, long rosterId, int? year, int? month)
    {
        var (context, failureKey, status) = await ResolveContextAsync(deviceHash, rosterId);
        if (context is null)
            return Result<ParentPortalAttendanceSectionDto>.Failure(_localizer, failureKey, status);

        var section = ToAttendanceSection(
            await _sections.BuildAttendanceAsync(context.Teacher.Id, context.Student.Id, year, month));

        return Result<ParentPortalAttendanceSectionDto>.Success(
            section, _localizer, section.Visible ? "Success" : "ParentPortalNothingShared", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<ParentPortalPaymentsSectionDto>> GetPaymentsAsync(string deviceHash, long rosterId)
    {
        var (context, failureKey, status) = await ResolveContextAsync(deviceHash, rosterId);
        if (context is null)
            return Result<ParentPortalPaymentsSectionDto>.Failure(_localizer, failureKey, status);

        var section = ToPaymentsSection(
            await _sections.BuildPaymentsAsync(context.Teacher.Id, context.Student.Id));

        return Result<ParentPortalPaymentsSectionDto>.Success(
            section, _localizer, section.Visible ? "Success" : "ParentPortalNothingShared", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<ParentPortalGradesSectionDto>> GetGradesAsync(
        string deviceHash, long rosterId, int page, int pageSize)
    {
        var (context, failureKey, status) = await ResolveContextAsync(deviceHash, rosterId);
        if (context is null)
            return Result<ParentPortalGradesSectionDto>.Failure(_localizer, failureKey, status);

        var section = await BuildGradesSectionAsync(context, page, pageSize);

        return Result<ParentPortalGradesSectionDto>.Success(
            section, _localizer, section.Visible ? "Success" : "ParentPortalNothingShared", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> RevokeOwnAccessAsync(string deviceHash)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            return Result<bool>.Success(true, _localizer, "ParentPortalSessionExpired", HttpStatusCode.OK);

        // Latest, not "active": a parent may also withdraw a request that is still Pending.
        var grant = await _unitOfWork.ParentPortalAccesses.GetLatestByDeviceAsync(deviceHash);
        if (grant is null ||
            (grant.Status != ParentPortalAccessStatus.Active && grant.Status != ParentPortalAccessStatus.Pending))
            // Idempotent: nothing to remove is a successful removal from the parent's point of view.
            return Result<bool>.Success(true, _localizer, "ParentPortalSessionExpired", HttpStatusCode.OK);

        // GetLatestByDeviceAsync is AsNoTracking; re-fetch the tracked row before mutating it.
        var tracked = await _unitOfWork.ParentPortalAccesses
            .GetLiveByStudentAndDeviceAsync(grant.TeacherStudentId, deviceHash);
        if (tracked is null)
            return Result<bool>.Success(true, _localizer, "ParentPortalSessionExpired", HttpStatusCode.OK);

        tracked.Status = ParentPortalAccessStatus.Revoked;
        tracked.RespondedAt = DateTime.UtcNow;
        // RespondedByUserId stays NULL — that is how GetAccessStateAsync tells a parent's own
        // "stop following" apart from a teacher revocation.
        tracked.RespondedByUserId = null;

        await _unitOfWork.ParentPortalAccesses.UpdateAsync(tracked);
        await _unitOfWork.SaveChangesAsync();

        return Result<bool>.Success(true, _localizer, "ParentPortalSessionExpired", HttpStatusCode.OK);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE — CALLER RESOLUTION
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Everything a read endpoint needs once the device has been authorized.</summary>
    private sealed record PortalContext(
        ParentPortalAccess Grant,
        Teacher Teacher,
        TeacherConfiguration? Config,
        TeacherStudent Student,
        string TeacherName,
        string SubjectName);

    /// <summary>
    /// Authorizes a portal read and re-validates the whole chain LIVE.
    ///
    /// THE ROUTE'S <paramref name="rosterId"/> IS NEVER TRUSTED (CLAUDE.md §3.3, generalized by
    /// BUG-12 to every identity id): the grant is resolved from the DEVICE first, and the supplied
    /// roster id must then equal the one that grant names — otherwise 404, indistinguishable from
    /// "no grant at all", so it cannot be used to probe which roster ids exist.
    /// </summary>
    private async Task<(PortalContext? Context, string FailureKey, HttpStatusCode Status)> ResolveContextAsync(
        string deviceHash, long rosterId)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            return (null, "ParentPortalSessionExpired", HttpStatusCode.Unauthorized);

        var grant = await _unitOfWork.ParentPortalAccesses.GetActiveByDeviceAsync(deviceHash);
        if (grant is null)
            return (null, "ParentPortalSessionExpired", HttpStatusCode.Unauthorized);

        if (grant.TeacherStudentId != rosterId)
            return (null, "ParentPortalSessionExpired", HttpStatusCode.NotFound);

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(grant.TeacherId);
        if (teacher is null)
            return (null, "ParentPortalUnavailable", HttpStatusCode.Forbidden);

        var (teacherName, subjectName, config) = await ResolveTeacherHeaderAsync(teacher.Id, null);

        if (!await IsPortalEligibleAsync(teacher.Id, config))
            return (null, "ParentPortalDisabled", HttpStatusCode.Forbidden);

        if (grant.TeacherStudent is null)
            return (null, "ParentPortalStudentRemoved", HttpStatusCode.NotFound);

        await TouchAsync(grant.Id);

        return (new PortalContext(grant, teacher, config, grant.TeacherStudent, teacherName, subjectName),
            string.Empty, HttpStatusCode.OK);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE — SHARED HELPERS
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether the teacher currently accepts portal followers: the per-teacher opt-in must be on,
    /// AND the plan must include parent follow-up. Full and ManagerialPlus do; plain Managerial
    /// does not (SubscriptionPlanCapabilities is the single plan → feature map). This is the
    /// read-time chokepoint, so a plan downgrade closes the portal immediately without touching
    /// the teacher's stored toggle — an upgrade back re-opens it just as automatically.
    /// </summary>
    private async Task<bool> IsPortalEligibleAsync(long teacherId, TeacherConfiguration? config)
    {
        // Fail-closed on a missing config row: the portal is opt-in, never opt-out.
        if (config is null || !config.ParentPortalEnabled)
            return false;

        var entitlements = await _subscriptionGate.GetPlanEntitlementsAsync(teacherId);
        return entitlements.ParentFollowUpAllowed;
    }

    /// <summary>Teacher display name + subject label (in the reader's language) + the configuration row, in one batch call.</summary>
    private async Task<(string TeacherName, string SubjectName, TeacherConfiguration? Config)>
        ResolveTeacherHeaderAsync(long teacherId, string? language)
    {
        var batch = await _unitOfWork.Users.GetTeacherDashboardDataAsync(new List<long> { teacherId });
        batch.Teachers.TryGetValue(teacherId, out var teacher);
        batch.Configurations.TryGetValue(teacherId, out var config);

        string teacherName = string.Empty;
        if (teacher is not null && batch.Users.TryGetValue(teacher.UserId, out var teacherUser))
            teacherName = teacherUser.FullName;

        bool arabic = ResolveIsArabic(language);
        string subjectName = teacher?.CustomSubject ?? string.Empty;
        if (teacher is not null &&
            batch.TeacherSubjects.TryGetValue(teacherId, out var teacherSubjects) &&
            teacherSubjects.Any() &&
            batch.Subjects.TryGetValue(teacherSubjects.First().SubjectId, out var subject))
        {
            subjectName = arabic ? subject.NameAr : subject.NameEn;
        }

        return (teacherName, subjectName, config);
    }

    /// <summary>Explicit request language wins; otherwise fall back to the negotiated Accept-Language culture.</summary>
    private static bool ResolveIsArabic(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language))
            return language.Trim().StartsWith("ar", StringComparison.OrdinalIgnoreCase);

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("ar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The reader's language ("ar" / "en") from the negotiated Accept-Language culture — the portal's visitor is a parent, not the teacher.</summary>
    private static string CurrentLanguage() => ResolveIsArabic(null) ? "ar" : "en";

    private async Task<string?> ResolveSessionNameAsync(long teacherId, long? sessionId)
    {
        if (sessionId is null) return null;
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId.Value, teacherId);
        return session?.SessionName;
    }

    /// <summary>Live parent-visibility flags. Fail-closed on a missing config row (the entity defaults are opt-in only for attendance/payment, and grades default to hidden).</summary>
    private static ParentPortalVisibilityDto BuildVisibility(TeacherConfiguration? config) => new()
    {
        Attendance = config?.ParentVisibilityAttendance ?? false,
        Payments = config?.ParentVisibilityPayment ?? false,
        Grades = (config?.ParentVisibilityExamDefault ?? false)
                 || (config?.ParentVisibilityOnlineExamDefault ?? false)
    };

    /// <summary>Best-effort stamp of the last portal read. Never allowed to fail a read.</summary>
    private async Task TouchAsync(long grantId)
    {
        try
        {
            await _unitOfWork.ParentPortalAccesses.TouchLastSeenAsync(grantId, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parent portal: could not stamp LastSeenAt for grant {GrantId}", grantId);
        }
    }

    /// <summary>
    /// Fires the teacher notification at most ONCE per hour: if a pending request already existed
    /// inside the window the teacher was already told, so this burst stays silent.
    /// </summary>
    private async Task NotifyTeacherAsync(
        long teacherId, string studentName, string? parentName, DateTime? newestPendingBefore, DateTime now)
    {
        try
        {
            if (newestPendingBefore is not null &&
                now - newestPendingBefore.Value < NotificationBatchWindow)
                return;

            int pendingCount = await _unitOfWork.ParentPortalAccesses.CountPendingForTeacherAsync(teacherId);
            await _notifier.NotifyPendingRequestsAsync(teacherId, studentName, pendingCount, parentName);
        }
        catch (Exception ex)
        {
            // Post-commit side effect — a notification failure must never fail the parent's request.
            _logger.LogWarning(ex, "Parent portal: pending-request notification failed for teacher {TeacherId}", teacherId);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE — PROJECTIONS
    // ══════════════════════════════════════════════════════════════════════

    private async Task<ParentPortalGradesSectionDto> BuildGradesSectionAsync(
        PortalContext context, int page, int pageSize)
    {
        bool offlineVisible = context.Config?.ParentVisibilityExamDefault ?? false;
        bool onlineVisible = context.Config?.ParentVisibilityOnlineExamDefault ?? false;

        var section = new ParentPortalGradesSectionDto { Visible = offlineVisible || onlineVisible };
        if (!section.Visible)
            return section;

        // The READER is the parent, so exam subject labels follow the portal's negotiated
        // Accept-Language, not the teacher's own preference.
        string language = CurrentLanguage();

        var offline = await _sections.BuildOfflineGradesAsync(
            context.Teacher.Id, context.Student.Id, language, offlineVisible);
        var online = await _sections.BuildOnlineGradesAsync(
            context.Teacher.Id, context.Student.Id, language, onlineVisible);

        // Merged newest-first; ExamId breaks a same-day tie deterministically so paging is stable.
        var rows = offline.Rows
            .Concat(online.Rows)
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.ExamId)
            .ToList();

        var graded = rows.Where(r => r.ScorePercentage.HasValue)
            .Select(r => r.ScorePercentage!.Value)
            .ToList();

        int safePage = page < 1 ? 1 : page;
        int safePageSize = pageSize < 1 ? DashboardGradesPageSize
            : pageSize > MaxGradesPageSize ? MaxGradesPageSize : pageSize;

        section.Data = new ParentPortalGradesDto
        {
            // Summary spans the WHOLE history, not the page — the tiles must not change as the
            // parent pages through the list.
            Summary = new ParentPortalGradesSummaryDto
            {
                CompletedCount = graded.Count,
                UngradedCount = rows.Count - graded.Count,
                AveragePercentage = graded.Count == 0 ? null : Math.Round(graded.Average(), 2),
                HighestPercentage = graded.Count == 0 ? null : graded.Max(),
                LowestPercentage = graded.Count == 0 ? null : graded.Min()
            },
            Items = rows.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToList(),
            Page = safePage,
            PageSize = safePageSize,
            TotalCount = rows.Count,
            TotalPages = (int)Math.Ceiling(rows.Count / (double)safePageSize)
        };

        return section;
    }

    private static ParentPortalAttendanceSectionDto ToAttendanceSection(ParentDashboardAttendanceDto source) =>
        new() { Visible = source.Visible, Data = source.Visible ? source.Data : null };

    private static ParentPortalPaymentsSectionDto ToPaymentsSection(ParentDashboardPaymentDto source) =>
        new() { Visible = source.Visible, Data = source.Visible ? source.Data : null };

    /// <summary>
    /// The ONE pending payload — used for a freshly-queued request, an already-pending one, a
    /// nonexistent student code, and a cooldown-suppressed re-request alike. It deliberately
    /// reuses <c>ParentPortalRequestSent</c> in every case: answering "still waiting" only for a
    /// code that really exists would let an attacker probe the roster by submitting the same code
    /// twice. Student fields stay null for the same reason.
    /// </summary>
    private Result<ParentPortalAccessRequestResultDto> PendingResult(string teacherName) =>
        Result<ParentPortalAccessRequestResultDto>.Success(
            new ParentPortalAccessRequestResultDto
            {
                State = ParentPortalConstants.States.Pending,
                TeacherName = teacherName
                // Student fields deliberately left null — see the enumeration note above.
            },
            _localizer, "ParentPortalRequestSent", new object?[] { teacherName }, HttpStatusCode.OK);

    private Result<ParentPortalAccessRequestResultDto> ActiveResult(string teacherName, TeacherStudent student) =>
        Result<ParentPortalAccessRequestResultDto>.Success(
            new ParentPortalAccessRequestResultDto
            {
                State = ParentPortalConstants.States.Active,
                TeacherName = teacherName,
                StudentName = student.StudentName,
                StudentCode = student.StudentCode,
                RosterId = student.Id
            },
            _localizer, "Success", HttpStatusCode.OK);

    private Result<ParentPortalAccessRequestResultDto> TooManyRequests() =>
        Result<ParentPortalAccessRequestResultDto>.Failure(
            _localizer, "ParentPortalTooManyRequests",
            new object?[] { (int)AbuseWindow.TotalMinutes },
            HttpStatusCode.TooManyRequests);

    private Result<ParentPortalAccessStateDto> NoAccessState() =>
        Result<ParentPortalAccessStateDto>.Success(
            new ParentPortalAccessStateDto { State = ParentPortalConstants.States.None },
            _localizer, "ParentPortalSessionExpired", HttpStatusCode.OK);

    /// <summary>Full month name in the invariant culture (e.g. "March") — same convention as the student home aggregate.</summary>
    private static string MonthName(int year, int month) =>
        new DateTime(year, month, 1).ToString("MMMM", CultureInfo.InvariantCulture);

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= maxLength ? value
        : value[..maxLength];
}
