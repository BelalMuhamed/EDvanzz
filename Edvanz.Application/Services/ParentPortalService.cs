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
using Microsoft.Extensions.Caching.Distributed;
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
    private readonly IDistributedCache _cache;
    private readonly ParentPortalOptions _options;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;
    private readonly ILogger<ParentPortalService> _logger;

    public ParentPortalService(
        IUnitOfWork unitOfWork,
        IParentSectionComposer sections,
        ISubscriptionGateService subscriptionGate,
        ITimeZoneService timeZoneService,
        IParentPortalNotifier notifier,
        IDistributedCache cache,
        IOptions<ParentPortalOptions> options,
        IStringLocalizer<Domain.Resources.Messages> localizer,
        ILogger<ParentPortalService> logger)
    {
        _unitOfWork = unitOfWork;
        _sections = sections;
        _subscriptionGate = subscriptionGate;
        _timeZoneService = timeZoneService;
        _notifier = notifier;
        _cache = cache;
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

        // The parent's mobile number. A SUPPLIED one must be a real Egyptian mobile — otherwise
        // the parent silently loses auto-approval and never learns why.
        //
        // REQUIRED once ParentPortal__RequirePhone is on (see ParentPortalOptions.RequirePhone for
        // the deploy-ordering reason it starts false). The phone is not a formality: it is what
        // lets the roster-phone rule admit a parent with no teacher involvement at all, what lets
        // an approved parent back in from a new handset or a browser that lost its cookie, and the
        // one thing a teacher can act on when they are deciding whether to approve a stranger.
        string? claimedPhone = null;
        if (!string.IsNullOrWhiteSpace(dto.PhoneNumber))
        {
            claimedPhone = EgyptianPhoneNumber.Normalize(dto.PhoneNumber);
            if (claimedPhone is null)
                return Result<ParentPortalAccessRequestResultDto>.Failure(
                    _localizer, "ParentPortalPhoneFormat", HttpStatusCode.BadRequest);
        }
        else if (_options.RequirePhone)
        {
            return Result<ParentPortalAccessRequestResultDto>.Failure(
                _localizer, "ParentPortalPhoneRequired", HttpStatusCode.BadRequest);
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
        // SECURITY — WHAT WE TELL THE PARENT, AND WHY IT CHANGED (2026-09-11).
        //
        // TEACHER axis (eligibility) — honest, and always was.
        //   Whether a teacher accepts portal followers is ALREADY PUBLIC: anyone can read it from
        //   GET /teachers/{teacherCode}/preview, which returns `portalEnabled` for any teacher
        //   code. Hiding it here would therefore buy exactly zero security while stranding a real
        //   parent of a not-yet-enabled teacher on a "waiting for approval" screen that can NEVER
        //   resolve. So this returns an honest, actionable 403.
        //
        // STUDENT axis (does this student code exist?) — DELIBERATE REVERSAL. DO NOT "FIX" BACK.
        //   This branch used to return the byte-identical pending payload a genuine request gets,
        //   writing nothing, so that nobody could walk a teacher's students by submitting codes
        //   (StudentCode is a sequential counter A1..Z999 and TeacherCode is public). That
        //   protected the roster and destroyed the product: the teacher's own share message asks
        //   parents for the TEACHER code only, so parents arrive not knowing the second code,
        //   guess, are told "request sent", and wait forever on a screen no one can resolve —
        //   nothing was written, so no teacher ever sees anything to approve. Reproduced on prod
        //   2026-09-11: a fake code and a real code produced the same screen; only one reached the
        //   teacher. Owner's decision: a parent must be told, and corrected, exactly as they are
        //   for a wrong TEACHER code.
        //
        //   The enumeration defence did not go away — it became a BUDGET instead of a blanket.
        //   Honest answers are metered per device and per teacher (see the options); once a caller
        //   burns through them inside the hour we revert to the old neutral pending payload, which
        //   writes nothing and reveals nothing. A parent fixing a typo needs two or three; a
        //   script walking A1, A2, A3… goes dark almost immediately. The portal adds its own,
        //   tighter cap (10 distinct codes per browser per 30 minutes) in front of this.
        //
        //   A REAL pending request still withholds the student's name/code/id — those are only
        //   ever returned on an "active" (phone-verified) result. Knowing that a code EXISTS is
        //   now answerable; knowing WHO it belongs to is still not.
        // ══════════════════════════════════════════════════════════════════
        if (!eligible)
            return Result<ParentPortalAccessRequestResultDto>.Failure(
                _localizer, "ParentPortalDisabled", HttpStatusCode.Forbidden);

        if (student is null)
        {
            // Over budget = this caller has been told "not found" too many times this hour, so
            // they are treated as a scanner and get the old silent answer. Under budget = a
            // human who mistyped, and they get told so.
            return await IsUnknownCodeBudgetExhaustedAsync(teacher.Id, deviceHash)
                ? PendingResult(teacherName)
                : Result<ParentPortalAccessRequestResultDto>.Failure(
                    _localizer, "ParentPortalStudentCodeNotFound", HttpStatusCode.NotFound);
        }

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

            if (existing.Status == ParentPortalAccessStatus.Active)
                return ActiveResult(teacherName, student);

            // ── PENDING: RE-EVALUATE TRUST, do not just report "still waiting". ──────────
            // This branch used to return PendingResult outright, which quietly stranded the
            // two most likely ways a waiting parent gets un-stuck:
            //   * they first submitted with no phone (it was optional) and now supply one;
            //   * the TEACHER adds their number to the student's record — the remedy this
            //     product advertises everywhere, including on the waiting screen itself and
            //     in the post-rejection message.
            // In both cases the resubmit hit this short-circuit and answered "still waiting"
            // forever, so the fix a teacher had just performed did nothing and only a manual
            // approval could ever release them. The repository loads this row TRACKED
            // precisely so the request path can promote it — nothing ever did.
            var (rosterMatch, trusted) = await EvaluateTrustAsync(student, claimedPhone);
            if (!rosterMatch && !trusted)
                return PendingResult(teacherName);

            existing.Status = ParentPortalAccessStatus.Active;
            existing.RespondedAt = now;
            // RespondedByUserId stays NULL — no human approved this; the number did.
            existing.AutoApproved = rosterMatch;
            existing.Origin = rosterMatch
                ? ParentPortalAccessOrigin.RosterPhone
                : ParentPortalAccessOrigin.TrustedPhone;
            // Record the number that earned the promotion, so a later device change is
            // re-admitted by the trusted-phone rule. Safe to store even over a previous
            // value: it only reaches here when the teacher already knows this number.
            if (claimedPhone is not null)
                existing.ClaimedPhone = claimedPhone;

            try
            {
                await _unitOfWork.ParentPortalAccesses.UpdateAsync(existing);
                await _unitOfWork.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Unlike the name backfill above, this one must NOT be swallowed: telling a
                // parent they are in when the row did not change would strand them again.
                _logger.LogError(ex,
                    "Parent portal: could not promote pending grant {GrantId} to active", existing.Id);
                return PendingResult(teacherName);
            }

            return ActiveResult(teacherName, student);
        }

        // ── 5b. Decide whether this request is already trusted. TWO independent rules. ──
        //
        // ORDER MATTERS: this runs BEFORE the post-rejection cooldown below, and that is the whole
        // point. It used to run after, which meant a teacher had NO working remedy for a rejection
        // they regretted — they could add the parent's number to the student's record (the one fix
        // the product tells everyone about, including the portal's own waiting screen) and the
        // parent would STILL be silently held for 24 hours. Trust earned from the teacher must
        // beat a cooldown that exists only to stop untrusted re-submits.
        //
        var (rosterPhoneMatches, trustedPhone) = await EvaluateTrustAsync(student, claimedPhone);
        bool grantActive = rosterPhoneMatches || trustedPhone;

        // ── 5c. POST-REJECTION COOLDOWN — untrusted re-submits only ──────────────────────
        // Rejected is TERMINAL, so it does not occupy the live-row unique index and a rejected
        // parent could otherwise re-submit straight away and keep reappearing in the inbox
        // (bounded only by the hourly caps). Keyed on the NEWEST row across both axes, not "was
        // there ever a rejection": someone rejected yesterday and approved today must not be held.
        //
        // It now answers HONESTLY rather than returning a silent pending, for the same reason the
        // unknown-code branch does: the old behaviour left a rejected parent on a waiting screen
        // that could never resolve, with nothing written and nobody able to help them. The message
        // names the remedy — ask the teacher to put your number on the child's record — which the
        // trust rules above now honour immediately.
        //
        // This leaks only to someone holding a phone number that was already rejected for that
        // student, i.e. the rejected parent themselves; their own device is already told "rejected"
        // by GET /access.
        if (!grantActive &&
            await IsInRejectionCooldownAsync(student.Id, deviceHash, claimedPhone, now))
        {
            return Result<ParentPortalAccessRequestResultDto>.Failure(
                _localizer, "ParentPortalRequestPreviouslyRejected", HttpStatusCode.Conflict);
        }

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
    /// The TWO independent rules that admit a request without a teacher touching it. Shared by the
    /// new-request path and the promote-a-waiting-row path so the two can never drift — a parent
    /// must not be admitted on a first submit but left waiting on a second, or vice versa.
    ///
    /// (a) ROSTER PHONE — the teacher wrote this number on the student's record themselves.
    ///     Compared IN MEMORY on the already-loaded row: roster phones are only Trim()-ed on write,
    ///     so stored formats vary and only a normalize-both-sides comparison is correct.
    ///
    /// (b) TRUSTED PHONE — this number already holds an ACTIVE grant on this student, so a teacher
    ///     vetted it before. This is what makes access follow the PHONE instead of the browser:
    ///     clearing cookies or moving to a new handset no longer re-queues an approved parent.
    ///     Compared in SQL against the always-normalized ClaimedPhone column.
    /// </summary>
    private async Task<(bool RosterPhoneMatches, bool TrustedPhone)> EvaluateTrustAsync(
        TeacherStudent student, string? claimedPhone)
    {
        bool rosterPhoneMatches =
            EgyptianPhoneNumber.AreSameNumber(claimedPhone, student.ParentPhoneNumber);

        bool trustedPhone = !rosterPhoneMatches
            && claimedPhone is not null
            && await _unitOfWork.ParentPortalAccesses
                .HasActiveGrantWithPhoneAsync(student.Id, claimedPhone);

        return (rosterPhoneMatches, trustedPhone);
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
    public async Task<Result<ParentPortalAccessStateDto>> GetAccessStateAsync(
        string deviceHash, long? rosterId = null)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            return NoAccessState();

        // SELECTION. A device may follow several children; the caller names which one it wants and
        // we fall back to the newest — which is exactly what this method always returned, so a
        // one-child parent (and any client that never sends rosterId) sees no change at all.
        // A rosterId this device holds no ACTIVE grant for is IGNORED rather than refused: the
        // selection is a UI preference, and a child the teacher has since revoked must degrade to
        // "here is your other child", never to an error page.
        ParentPortalAccess? grant = null;
        if (rosterId is > 0)
            grant = await _unitOfWork.ParentPortalAccesses
                .GetActiveByDeviceAndStudentAsync(deviceHash, rosterId.Value);

        grant ??= await _unitOfWork.ParentPortalAccesses.GetLatestByDeviceAsync(deviceHash);
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
        dto.Students = await BuildFollowedStudentsAsync(deviceHash, grant.TeacherStudentId);

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

    /// <summary>
    /// The children this browser follows, for the switcher. One query plus one batched teacher
    /// lookup — the two children may sit with DIFFERENT teachers, so each entry carries its own
    /// teacher name and the switcher can label them apart.
    ///
    /// Rows whose roster record was soft-deleted are dropped: a name-less chip that leads to a
    /// "student removed" page is worse than not offering the child at all.
    /// </summary>
    private async Task<List<ParentPortalFollowedStudentDto>> BuildFollowedStudentsAsync(
        string deviceHash, long selectedRosterId)
    {
        var grants = await _unitOfWork.ParentPortalAccesses.GetActiveGrantsByDeviceAsync(deviceHash);

        var live = grants.Where(g => g.TeacherStudent is not null).ToList();
        if (live.Count == 0)
            return new List<ParentPortalFollowedStudentDto>();

        // ONE batch for every teacher involved, so a two-child parent costs one extra query, not
        // one per child.
        var teacherIds = live.Select(g => g.TeacherId).Distinct().ToList();
        var batch = await _unitOfWork.Users.GetTeacherDashboardDataAsync(teacherIds);

        return live.Select(g =>
        {
            string teacherName = string.Empty;
            if (batch.Teachers.TryGetValue(g.TeacherId, out var t) &&
                batch.Users.TryGetValue(t.UserId, out var u))
                teacherName = u.FullName;

            return new ParentPortalFollowedStudentDto
            {
                RosterId = g.TeacherStudentId,
                StudentName = g.TeacherStudent!.StudentName,
                StudentCode = g.TeacherStudent.StudentCode,
                TeacherName = teacherName,
                IsSelected = g.TeacherStudentId == selectedRosterId
            };
        }).ToList();
    }

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
    /// BUG-12 to every identity id): it is looked up as a grant THIS DEVICE HOLDS, and anything
    /// else 404s, indistinguishable from "no grant at all", so it cannot be used to probe which
    /// roster ids exist.
    ///
    /// It used to resolve the device's NEWEST active grant and then demand the route id equal it,
    /// which was the same guarantee for one child and a dead end for two: a parent of siblings
    /// could not reach the older child at all. Authorizing the id against the device's OWN grants
    /// is exactly as strict and finally correct for a parent with more than one.
    /// </summary>
    private async Task<(PortalContext? Context, string FailureKey, HttpStatusCode Status)> ResolveContextAsync(
        string deviceHash, long rosterId)
    {
        if (string.IsNullOrWhiteSpace(deviceHash))
            return (null, "ParentPortalSessionExpired", HttpStatusCode.Unauthorized);

        if (rosterId <= 0)
            return (null, "ParentPortalSessionExpired", HttpStatusCode.NotFound);

        var grant = await _unitOfWork.ParentPortalAccesses
            .GetActiveByDeviceAndStudentAsync(deviceHash, rosterId);
        if (grant is null)
            return (null, "ParentPortalSessionExpired", HttpStatusCode.Unauthorized);

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

    /// <summary>
    /// Teacher display name + subject label (in the reader's language) + the live configuration row.
    ///
    /// ONE query, via the portal's own named repo method. It used to go through
    /// <c>GetTeacherDashboardDataAsync</c>, a bulk dashboard loader that fires FIVE round-trips to
    /// produce one name and one label — on every access request and, far worse, on every poll of
    /// the waiting screen. Do not route this back through the dashboard batch.
    /// </summary>
    private async Task<(string TeacherName, string SubjectName, TeacherConfiguration? Config)>
        ResolveTeacherHeaderAsync(long teacherId, string? language)
    {
        var header = await _unitOfWork.ParentPortalAccesses.GetPortalTeacherHeaderAsync(teacherId);
        if (header is null)
            return (string.Empty, string.Empty, null);

        bool arabic = ResolveIsArabic(language);
        string? subjectLabel = arabic ? header.SubjectNameAr : header.SubjectNameEn;

        // A teacher with no linked subject falls back to their free-text one, exactly as before.
        string subjectName = string.IsNullOrWhiteSpace(subjectLabel)
            ? header.CustomSubject ?? string.Empty
            : subjectLabel;

        return (header.TeacherName, subjectName, header.Configuration);
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
    /// Tells the teacher a parent is waiting. The INBOX ROW is written for every request; only the
    /// PUSH is batched to at most one per hour, so a burst produces one buzz but never loses a
    /// parent.
    ///
    /// Changed 2026-09-11: this used to return early inside the batching window, writing nothing at
    /// all, so the 2nd..Nth parent in an hour left no trace anywhere the teacher could find — and
    /// since FCM is unconfigured in production, the row it was skipping was the ONLY signal that
    /// existed. Do not restore the early return.
    /// </summary>
    private async Task NotifyTeacherAsync(
        long teacherId, string studentName, string? parentName, DateTime? newestPendingBefore, DateTime now)
    {
        try
        {
            bool alreadyPushedThisWindow = newestPendingBefore is not null &&
                now - newestPendingBefore.Value < NotificationBatchWindow;

            int pendingCount = await _unitOfWork.ParentPortalAccesses.CountPendingForTeacherAsync(teacherId);
            await _notifier.NotifyPendingRequestsAsync(
                teacherId, studentName, pendingCount, parentName, suppressPush: alreadyPushedThisWindow);
        }
        catch (Exception ex)
        {
            // Post-commit side effect — a notification failure must never fail the parent's request.
            _logger.LogWarning(ex, "Parent portal: pending-request notification failed for teacher {TeacherId}", teacherId);
        }
    }

    /// <summary>
    /// Meters how many times we may answer "that student code does not exist" honestly, per device
    /// and per teacher, inside <see cref="AbuseWindow"/>.
    ///
    /// This is what replaced the blanket silence on the unknown-code branch (see the SECURITY block
    /// in <see cref="RequestAccessAsync"/>). Real parents mistype once or twice; a script walking
    /// A1, A2, A3… exhausts the budget almost immediately and gets the old, uninformative pending
    /// payload from then on. Counting happens on the ANSWER, not the attempt, so a parent whose
    /// codes all resolve never touches it.
    ///
    /// Both counters are bumped on every unknown code, so rotating device ids still burns the
    /// teacher's budget.
    /// </summary>
    private async Task<bool> IsUnknownCodeBudgetExhaustedAsync(long teacherId, string deviceHash)
    {
        bool deviceExhausted = await BumpAndCheckAsync(
            $"pp:unknown-code:dev:{deviceHash}", _options.UnknownStudentCodeRepliesPerDevicePerHour);
        bool teacherExhausted = await BumpAndCheckAsync(
            $"pp:unknown-code:teacher:{teacherId}", _options.UnknownStudentCodeRepliesPerTeacherPerHour);

        return deviceExhausted || teacherExhausted;
    }

    /// <summary>
    /// Counts one use against <paramref name="key"/> and reports whether the budget is now spent.
    /// A non-positive limit disables the budget (always answer honestly).
    /// </summary>
    private async Task<bool> BumpAndCheckAsync(string key, int limit)
    {
        if (limit <= 0) return false;

        try
        {
            string? stored = await _cache.GetStringAsync(key);
            int used = int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;

            used++;

            await _cache.SetStringAsync(
                key,
                used.ToString(CultureInfo.InvariantCulture),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = AbuseWindow });

            return used > limit;
        }
        catch (Exception ex)
        {
            // Fail OPEN, deliberately. The cache is a convenience; the portal's own per-browser cap
            // (10 distinct codes per 30 minutes) is the primary guard, and a cache blip must never
            // start telling real parents "request sent" for a code that does not exist — that is
            // the exact failure this whole change exists to remove.
            _logger.LogWarning(ex, "Parent portal: unknown-code budget check failed for {CacheKey}", key);
            return false;
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
