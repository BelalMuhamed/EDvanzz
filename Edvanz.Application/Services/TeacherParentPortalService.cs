using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Edvanz.Application.Common;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.ParentPortal;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Localization;

namespace Edvanz.Application.Services;

/// <inheritdoc cref="ITeacherParentPortalService"/>
public sealed class TeacherParentPortalService : ITeacherParentPortalService
{
    private const string ApproveAction = "approve";
    private const string RejectAction = "reject";

    private const int MaxPageSize = 100;
    private const int MaxBulkIds = 200;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;
    private readonly ISubscriptionGateService _subscriptionGate;

    public TeacherParentPortalService(
        IUnitOfWork unitOfWork,
        IStringLocalizer<Domain.Resources.Messages> localizer,
        ISubscriptionGateService subscriptionGate)
    {
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _subscriptionGate = subscriptionGate;
    }

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<ParentPortalRequestListItemDto>>>> GetPendingRequestsAsync(
        long teacherId, int page, int pageSize)
    {
        int safePage = page < 1 ? 1 : page;
        int safePageSize = pageSize < 1 ? 20 : pageSize > MaxPageSize ? MaxPageSize : pageSize;

        // §3.8: total is counted separately, before the page is fetched.
        int total = await _unitOfWork.ParentPortalAccesses.CountPendingForTeacherAsync(teacherId);
        var rows = await _unitOfWork.ParentPortalAccesses
            .GetPendingForTeacherPagedAsync(teacherId, safePage, safePageSize);

        var payload = new PaginatedResponse<List<ParentPortalRequestListItemDto>>
        {
            totalCount = total,
            page = safePage,
            pageSize = safePageSize,
            totalPages = (int)Math.Ceiling(total / (double)safePageSize),
            data = rows.Select(ToRequestItem).ToList()
        };

        return Result<PaginatedResponse<List<ParentPortalRequestListItemDto>>>.Success(
            payload, _localizer, "Success", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<ParentPortalApproveResultDto>> ApproveRequestAsync(
        long teacherId, long requestId, long actingUserId, ParentPortalApproveRequestDto? dto)
    {
        var grant = await _unitOfWork.ParentPortalAccesses.GetByIdForTeacherAsync(requestId, teacherId);
        if (grant is null)
            return Result<ParentPortalApproveResultDto>.Failure(
                _localizer, "ParentPortalRequestNotFound", HttpStatusCode.NotFound);

        // Approving an already-Active grant is a no-op success (double-tap / retry safe). The
        // optional phone save is skipped too — it belongs to the act of approving.
        if (grant.Status == ParentPortalAccessStatus.Active)
            return Result<ParentPortalApproveResultDto>.Success(
                new ParentPortalApproveResultDto { Follower = ToFollowerItem(grant, grant.TeacherStudent) },
                _localizer, "ParentPortalAccessGranted", HttpStatusCode.OK);

        if (grant.Status != ParentPortalAccessStatus.Pending)
            return Result<ParentPortalApproveResultDto>.Failure(
                _localizer, "ParentPortalRequestNotFound", HttpStatusCode.NotFound);

        if (grant.TeacherStudent is null)
            return Result<ParentPortalApproveResultDto>.Failure(
                _localizer, "ParentPortalStudentRemoved", HttpStatusCode.NotFound);

        var result = new ParentPortalApproveResultDto();

        Apply(grant, ParentPortalAccessStatus.Active, actingUserId, DateTime.UtcNow);
        await _unitOfWork.ParentPortalAccesses.UpdateAsync(grant);

        // ── Optional: promote the approved number onto the student's roster record. ──
        // grant.TeacherStudent is TRACKED (GetByIdForTeacherAsync does not AsNoTracking), so the
        // roster write joins the SAME SaveChanges as the approval — one atomic unit, no partial
        // "approved but phone lost" state. ParentPhoneNumber is deliberately non-unique (siblings
        // share a parent), so this can never trip a unique violation.
        if (dto?.SavePhoneToStudent == true)
            (result.PhoneSavedToStudent, result.PhoneSaveSkippedReason) = SavePhoneToStudent(grant);

        await _unitOfWork.SaveChangesAsync();

        result.Follower = ToFollowerItem(grant, grant.TeacherStudent);
        return Result<ParentPortalApproveResultDto>.Success(
            result, _localizer, "ParentPortalAccessGranted", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<ParentPortalFollowerListItemDto>> RejectRequestAsync(
        long teacherId, long requestId, long actingUserId) =>
        await ResolveSingleAsync(teacherId, requestId, actingUserId,
            ParentPortalAccessStatus.Rejected, "ParentPortalAccessRejected");

    /// <inheritdoc />
    public async Task<Result<ParentPortalBulkResultDto>> BulkResolveAsync(
        long teacherId, ParentPortalBulkActionDto dto, long actingUserId)
    {
        string action = (dto.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action != ApproveAction && action != RejectAction)
            return Result<ParentPortalBulkResultDto>.Failure(
                _localizer, "BadRequest", HttpStatusCode.BadRequest);

        var ids = (dto.Ids ?? new List<long>()).Where(id => id > 0).Distinct().Take(MaxBulkIds).ToList();
        if (ids.Count == 0)
            return Result<ParentPortalBulkResultDto>.Failure(
                _localizer, "BadRequest", HttpStatusCode.BadRequest);

        // Tenant-scoped fetch: ids outside this teacher simply do not come back.
        var rows = await _unitOfWork.ParentPortalAccesses.GetByIdsForTeacherAsync(ids, teacherId);
        var byId = rows.ToDictionary(r => r.Id);

        var target = action == ApproveAction
            ? ParentPortalAccessStatus.Active
            : ParentPortalAccessStatus.Rejected;

        var now = DateTime.UtcNow;
        var result = new ParentPortalBulkResultDto();

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction) await _unitOfWork.BeginTransactionAsync();
        try
        {
            foreach (long id in ids)
            {
                if (!byId.TryGetValue(id, out var grant) ||
                    grant.Status != ParentPortalAccessStatus.Pending ||
                    // Approving a follower for a roster record that has since been deleted would
                    // grant access to nothing — skip it rather than create a dead grant.
                    (target == ParentPortalAccessStatus.Active && grant.TeacherStudent is null))
                {
                    result.SkippedIds.Add(id);
                    continue;
                }

                Apply(grant, target, actingUserId, now);
                await _unitOfWork.ParentPortalAccesses.UpdateAsync(grant);
                result.ProcessedIds.Add(id);
                result.Affected++;
            }

            await _unitOfWork.SaveChangesAsync();
            if (ownsTransaction) await _unitOfWork.CommitAsync();
        }
        catch
        {
            if (ownsTransaction) await _unitOfWork.RollbackAsync();
            throw;
        }

        return Result<ParentPortalBulkResultDto>.Success(
            result, _localizer,
            target == ParentPortalAccessStatus.Active ? "ParentPortalAccessGranted" : "ParentPortalAccessRejected",
            HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<List<ParentPortalFollowerListItemDto>>> GetFollowersAsync(
        long teacherId, long teacherStudentId)
    {
        // Tenant guard on the STUDENT too, so a foreign roster id 404s instead of returning [].
        var student = await _unitOfWork.Users.GetActiveTeacherStudentByIdAsync(teacherId, teacherStudentId);
        if (student is null)
            return Result<List<ParentPortalFollowerListItemDto>>.Failure(
                _localizer, "RosterStudentNotFound", HttpStatusCode.NotFound);

        var rows = await _unitOfWork.ParentPortalAccesses
            .GetFollowersForStudentAsync(teacherId, teacherStudentId);

        // GetFollowersForStudentAsync does not load the navigation (one known student), so the
        // name/code are filled from the already-resolved roster record.
        var items = rows.Select(r => ToFollowerItem(r, student)).ToList();

        return Result<List<ParentPortalFollowerListItemDto>>.Success(items, _localizer, "Success", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<ParentPortalRevokeResultDto>> RevokeFollowerAsync(
        long teacherId, long accessId, long actingUserId)
    {
        var grant = await _unitOfWork.ParentPortalAccesses.GetByIdForTeacherAsync(accessId, teacherId);
        if (grant is null ||
            (grant.Status != ParentPortalAccessStatus.Active && grant.Status != ParentPortalAccessStatus.Pending))
            return Result<ParentPortalRevokeResultDto>.Failure(
                _localizer, "ParentPortalRequestNotFound", HttpStatusCode.NotFound);

        var now = DateTime.UtcNow;

        // ── Device-only grant (the parent left the phone blank): nothing to revoke phone-wide. ──
        if (string.IsNullOrWhiteSpace(grant.ClaimedPhone))
        {
            Apply(grant, ParentPortalAccessStatus.Revoked, actingUserId, now);
            await _unitOfWork.ParentPortalAccesses.UpdateAsync(grant);
            await _unitOfWork.SaveChangesAsync();

            return Result<ParentPortalRevokeResultDto>.Success(
                new ParentPortalRevokeResultDto
                {
                    RevokedCount = 1,
                    RevokedPhone = null,
                    TeacherStudentId = grant.TeacherStudentId
                },
                _localizer, "ParentPortalAccessRevoked", HttpStatusCode.OK);
        }

        // ── REFUSE when the number is the student's ROSTER parent phone. ───────────────────
        // Revoking it could not hold: the roster-phone rule would auto-approve them again on their
        // very next submit, so the teacher would think they had removed someone who is still in.
        // Refuse with an actionable message rather than silently editing the student's record from
        // a revoke button — clearing roster data must be a deliberate act on the student screen.
        if (EgyptianPhoneNumber.AreSameNumber(grant.ClaimedPhone, grant.TeacherStudent?.ParentPhoneNumber))
            return Result<ParentPortalRevokeResultDto>.Failure(
                _localizer, "ParentPortalRevokeBlockedRosterPhone", HttpStatusCode.Conflict);

        // ── PHONE-WIDE revocation, one atomic UPDATE. ─────────────────────────────────────
        // Revoking only the tapped row would be a revoke that does NOTHING: the trusted-phone rule
        // would re-admit the parent through any surviving Active sibling row on their next submit.
        int revoked = await _unitOfWork.ParentPortalAccesses.RevokeByStudentAndPhoneAsync(
            teacherId, grant.TeacherStudentId, grant.ClaimedPhone, actingUserId, now);

        // NOTE: RevokeByStudentAndPhoneAsync is an ExecuteUpdate — it bypasses the change tracker
        // and has already hit the database. `grant` is now a stale in-memory copy; it is not
        // mutated and no SaveChanges follows, so nothing can write those stale values back.
        return Result<ParentPortalRevokeResultDto>.Success(
            new ParentPortalRevokeResultDto
            {
                RevokedCount = revoked,
                RevokedPhone = grant.ClaimedPhone,
                TeacherStudentId = grant.TeacherStudentId
            },
            _localizer, "ParentPortalAccessRevoked", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<ParentPortalSummaryDto>> GetSummaryAsync(long? teacherId, bool canManage)
    {
        // No acting teacher → the EMPTY summary, never an error. This endpoint is polled in the
        // background by the teacher drawer, and any 4xx it emits gets read by the app's
        // ActingTeacherUnavailableInterceptor as "acting teacher gone" and ejects the operator
        // from the acting-as shell. See TeacherParentPortalController.GetSummary.
        // CanManage stays false here: with no teacher there is nothing to manage.
        if (teacherId is null)
            return Result<ParentPortalSummaryDto>.Success(
                new ParentPortalSummaryDto(), _localizer, "Success", HttpStatusCode.OK);

        long id = teacherId.Value;
        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(id);
        var entitlements = await _subscriptionGate.GetPlanEntitlementsAsync(id);

        var dto = new ParentPortalSummaryDto
        {
            PendingCount = await _unitOfWork.ParentPortalAccesses.CountPendingForTeacherAsync(id),
            FollowedStudentsCount = await _unitOfWork.ParentPortalAccesses
                .CountFollowedStudentsForTeacherAsync(id),
            StudentsMissingParentPhone = await _unitOfWork.Users
                .CountStudentsMissingParentPhoneAsync(id),
            PortalEnabled = config?.ParentPortalEnabled ?? false,
            // Decided by the caller (the API layer owns authorization) using the very same
            // PermissionRequirement the [ModulePermission] attribute evaluates — never re-derived
            // here, so there is exactly one source of truth for "may this caller manage requests".
            CanManage = canManage,
            // Plan half of the portal eligibility (the app locks the whole section on false).
            PortalAllowed = entitlements.ParentFollowUpAllowed
        };

        return Result<ParentPortalSummaryDto>.Success(dto, _localizer, "Success", HttpStatusCode.OK);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE
    // ══════════════════════════════════════════════════════════════════════

    private async Task<Result<ParentPortalFollowerListItemDto>> ResolveSingleAsync(
        long teacherId, long requestId, long actingUserId,
        ParentPortalAccessStatus target, string successKey)
    {
        var grant = await _unitOfWork.ParentPortalAccesses.GetByIdForTeacherAsync(requestId, teacherId);
        if (grant is null)
            return Result<ParentPortalFollowerListItemDto>.Failure(
                _localizer, "ParentPortalRequestNotFound", HttpStatusCode.NotFound);

        // Approving an already-Active grant is a no-op success (double-tap / retry safe).
        if (grant.Status == target)
            return Result<ParentPortalFollowerListItemDto>.Success(
                ToFollowerItem(grant, grant.TeacherStudent), _localizer, successKey, HttpStatusCode.OK);

        if (grant.Status != ParentPortalAccessStatus.Pending)
            return Result<ParentPortalFollowerListItemDto>.Failure(
                _localizer, "ParentPortalRequestNotFound", HttpStatusCode.NotFound);

        if (target == ParentPortalAccessStatus.Active && grant.TeacherStudent is null)
            return Result<ParentPortalFollowerListItemDto>.Failure(
                _localizer, "ParentPortalStudentRemoved", HttpStatusCode.NotFound);

        Apply(grant, target, actingUserId, DateTime.UtcNow);
        await _unitOfWork.ParentPortalAccesses.UpdateAsync(grant);
        await _unitOfWork.SaveChangesAsync();

        return Result<ParentPortalFollowerListItemDto>.Success(
            ToFollowerItem(grant, grant.TeacherStudent), _localizer, successKey, HttpStatusCode.OK);
    }

    /// <summary>
    /// Single mutation point for a teacher-side decision, so the audit columns can never drift
    /// apart. A transition to Active stamps <see cref="ParentPortalAccessOrigin.TeacherApproved"/>
    /// — this path is BY DEFINITION a human approval, never the roster-phone or trusted-phone rule
    /// (both of those create the row Active in the first place and never come through here).
    /// </summary>
    private static void Apply(
        ParentPortalAccess grant, ParentPortalAccessStatus target, long actingUserId, DateTime nowUtc)
    {
        grant.Status = target;
        grant.RespondedAt = nowUtc;
        grant.RespondedByUserId = actingUserId;

        if (target == ParentPortalAccessStatus.Active)
            grant.Origin = ParentPortalAccessOrigin.TeacherApproved;
    }

    /// <summary>
    /// Copies the approved parent's number onto the student's roster record when — and only
    /// when — the record has none. Mutates the TRACKED student so the write joins the caller's
    /// SaveChanges. Returns (saved, skipReason).
    ///
    /// An existing number is NEVER overwritten, same or different: the roster is the teacher's own
    /// data and a portal approval must not quietly rewrite it. The reason literals come from
    /// <see cref="ParentPortalConstants.PhoneSaveSkipReasons"/> and are part of the wire contract.
    /// </summary>
    private static (bool Saved, string? SkipReason) SavePhoneToStudent(ParentPortalAccess grant)
    {
        if (string.IsNullOrWhiteSpace(grant.ClaimedPhone))
            return (false, ParentPortalConstants.PhoneSaveSkipReasons.NoPhoneOnRequest);

        var student = grant.TeacherStudent;
        if (student is null)
            return (false, ParentPortalConstants.PhoneSaveSkipReasons.NoPhoneOnRequest);

        if (!string.IsNullOrWhiteSpace(student.ParentPhoneNumber))
        {
            return EgyptianPhoneNumber.AreSameNumber(grant.ClaimedPhone, student.ParentPhoneNumber)
                ? (false, ParentPortalConstants.PhoneSaveSkipReasons.AlreadySaved)
                : (false, ParentPortalConstants.PhoneSaveSkipReasons.StudentHasDifferentPhone);
        }

        // Stored in the canonical normalized shape, matching what the roster write paths produce.
        student.ParentPhoneNumber = grant.ClaimedPhone;
        return (true, null);
    }

    private static ParentPortalRequestListItemDto ToRequestItem(ParentPortalAccess grant) => new()
    {
        Id = grant.Id,
        TeacherStudentId = grant.TeacherStudentId,
        StudentName = grant.TeacherStudent?.StudentName,
        StudentCode = grant.TeacherStudent?.StudentCode,
        ClaimedPhone = grant.ClaimedPhone,
        ParentName = grant.ParentName,
        // Both flags read the SAME already-materialized roster row that supplies StudentName /
        // StudentCode above (GetPendingForTeacherPagedAsync Includes it), so this is pure
        // projection — no extra query, no N+1.
        PhoneMatchesRoster = EgyptianPhoneNumber.AreSameNumber(
            grant.ClaimedPhone, grant.TeacherStudent?.ParentPhoneNumber),
        StudentHasParentPhone = !string.IsNullOrWhiteSpace(grant.TeacherStudent?.ParentPhoneNumber),
        RequestedAt = grant.RequestedAt,
        Status = grant.Status
    };

    private static ParentPortalFollowerListItemDto ToFollowerItem(
        ParentPortalAccess grant, TeacherStudent? student) => new()
    {
        Id = grant.Id,
        TeacherStudentId = grant.TeacherStudentId,
        StudentName = student?.StudentName,
        StudentCode = student?.StudentCode,
        ClaimedPhone = grant.ClaimedPhone,
        ParentName = grant.ParentName,
        Status = grant.Status,
        AutoApproved = grant.AutoApproved,
        Origin = grant.Origin,
        RequestedAt = grant.RequestedAt,
        RespondedAt = grant.RespondedAt,
        LastSeenAt = grant.LastSeenAt
    };
}
