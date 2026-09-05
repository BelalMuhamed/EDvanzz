using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.TeacherLinks;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Localization;
using System.Net;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements the teacher-side half of the link request/approval flow.
/// All data access goes through IUnitOfWork.Users named methods (no raw
/// predicates), results follow the Result pattern, and student notifications
/// run post-commit as best-effort side effects via IStudentLinkNotifier.
/// </summary>
public class TeacherStudentLinkService : ITeacherStudentLinkService
{
    private const int MaxPageSize = 100;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IStudentLinkNotifier _linkNotifier;
    private readonly ISubscriptionGateService _subscriptionGate;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;

    public TeacherStudentLinkService(
        IUnitOfWork unitOfWork,
        IStudentLinkNotifier linkNotifier,
        ISubscriptionGateService subscriptionGate,
        IStringLocalizer<Domain.Resources.Messages> localizer)
    {
        _unitOfWork = unitOfWork;
        _linkNotifier = linkNotifier;
        _subscriptionGate = subscriptionGate;
        _localizer = localizer;
    }

    /// <inheritdoc />
    public async Task<Result<TeacherCodeDto>> GetMyTeacherCodeAsync(long teacherId)
    {
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(teacherId);
        if (teacher is null)
            return Result<TeacherCodeDto>.Failure(_localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        return Result<TeacherCodeDto>.Success(
            new TeacherCodeDto { TeacherCode = teacher.TeacherCode }, _localizer, "Success");
    }

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<TeacherLinkRequestListItemDto>>>> GetPendingLinkRequestsAsync(
        long teacherId, int page, int pageSize)
    {
        (page, pageSize) = NormalizePaging(page, pageSize);

        var (rows, totalCount) = await _unitOfWork.Users
            .GetPendingLinkRequestsForTeacherPagedAsync(teacherId, page, pageSize);

        // Suggested roster matches: one batch query for all typed codes on this
        // page, plus one batch query for their claim state — never per-row.
        var codes = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.RequestedStudentCode))
            .Select(r => r.RequestedStudentCode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matchesByCode = new Dictionary<string, TeacherStudent>(StringComparer.OrdinalIgnoreCase);
        var claimedIds = new HashSet<long>();

        if (codes.Count > 0)
        {
            var rosterMatches = await _unitOfWork.Users.GetActiveTeacherStudentsByCodesAsync(teacherId, codes);
            foreach (var ts in rosterMatches)
                matchesByCode[ts.StudentCode] = ts;

            if (rosterMatches.Count > 0)
            {
                var claimed = await _unitOfWork.Users.GetActivelyLinkedTeacherStudentIdsAsync(
                    rosterMatches.Select(ts => ts.Id).ToList());
                claimedIds = claimed.ToHashSet();
            }
        }

        var items = rows.Select(r =>
        {
            RosterStudentSuggestionDto? suggestion = null;
            if (!string.IsNullOrWhiteSpace(r.RequestedStudentCode) &&
                matchesByCode.TryGetValue(r.RequestedStudentCode!, out var ts))
            {
                suggestion = new RosterStudentSuggestionDto
                {
                    TeacherStudentId = ts.Id,
                    StudentName = ts.StudentName,
                    StudentCode = ts.StudentCode,
                    IsAlreadyLinked = claimedIds.Contains(ts.Id)
                };
            }

            return new TeacherLinkRequestListItemDto
            {
                LinkId = r.LinkId,
                RequestedStudentName = r.RequestedStudentName,
                RequestedStudentCode = r.RequestedStudentCode,
                RequestedAt = r.RequestedAt,
                StudentAccountCode = r.StudentAccountCode,
                StudentFullName = r.StudentFullName,
                StudentPhoneNumber = r.StudentPhoneNumber,
                SuggestedMatch = suggestion
            };
        }).ToList();

        var response = new PaginatedResponse<List<TeacherLinkRequestListItemDto>>
        {
            data = items,
            page = page,
            pageSize = pageSize,
            totalCount = totalCount,
            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
        };

        return Result<PaginatedResponse<List<TeacherLinkRequestListItemDto>>>.Success(response, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<LinkedStudentListItemDto>> AcceptLinkRequestAsync(
        long teacherId, long linkId, long actingUserId, AcceptLinkRequestDto dto)
    {
        var link = await _unitOfWork.Users.GetStudentTeacherLinkByIdForTeacherAsync(linkId, teacherId);
        if (link is null)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "LinkRequestNotFound", HttpStatusCode.NotFound);

        if (link.LinkStatus != LinkStatus.Pending)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "LinkRequestAlreadyResolved", HttpStatusCode.Conflict);

        // A plan without student accounts (Managerial / ManagerialPlus) forbids connecting any
        // student account to the teacher.
        var acceptEntitlements = await _subscriptionGate.GetPlanEntitlementsAsync(teacherId);
        if (!acceptEntitlements.StudentAccountsAllowed)
            return Result<LinkedStudentListItemDto>.Failure(
                _localizer, SubscriptionConstants.StudentAccountsBlockedMessageKey(acceptEntitlements.PlanType),
                HttpStatusCode.Forbidden);

        // ── Accept CONNECTS the account (Active); binding it to a student record is
        // a SEPARATE step (BindStudentLinkAsync). TeacherStudentId or StudentCode is
        // an optional "Accept & link" shortcut — when both are omitted the link is
        // accepted UNBOUND (connected but Not linked, no data access) and can be
        // linked later. A supplied target that does not resolve FAILS the accept —
        // never silently downgrade an "Accept & link" into a plain accept. ──
        var (rosterStudent, resolveFailure) =
            await ResolveRosterTargetAsync(teacherId, link, dto.TeacherStudentId, dto.StudentCode);
        if (resolveFailure is not null)
            return resolveFailure;

        if (rosterStudent is not null)
        {
            // One student account per student record.
            bool alreadyClaimed = await _unitOfWork.Users.IsTeacherStudentActivelyLinkedAsync(rosterStudent.Id);
            if (alreadyClaimed)
                return Result<LinkedStudentListItemDto>.Failure(_localizer, "RosterStudentAlreadyClaimed", HttpStatusCode.Conflict);

            // Paid seat check — ONLY on the "Accept & link" path, because only a BOUND link
            // consumes a student-app-account seat. Accepting UNBOUND stays free, and per the
            // Connection-vs-Link rule this must FAIL the accept rather than silently downgrade
            // it to an unbound accept (the caller asked to link).
            var seatFailure = await CheckLinkedStudentCapacityAsync(teacherId);
            if (seatFailure is not null) return seatFailure;
        }

        var now = DateTime.UtcNow;
        link.TeacherStudentId = rosterStudent?.Id;   // null = accepted but Not linked
        link.LinkStatus = LinkStatus.Active;
        link.LinkedAt = now;
        link.RespondedAt = now;
        link.RespondedByUserId = actingUserId;

        await _unitOfWork.Users.UpdateStudentTeacherLinkAsync(link);
        await _unitOfWork.SaveChangesAsync();

        // ── Post-commit, best-effort: tell the student the request was accepted ──
        try
        {
            await _linkNotifier.NotifyRequestResolvedAsync(link.StudentUserId, teacherId, accepted: true);
        }
        catch { /* notification failure must not fail the accept */ }

        var item = await BuildLinkedStudentItemAsync(link, rosterStudent);
        // Accurate copy per path: bound ("Accept & link") vs unbound (accepted, Not linked yet).
        string acceptMsg = rosterStudent is not null ? "LinkRequestAccepted" : "LinkRequestAcceptedUnlinked";
        return Result<LinkedStudentListItemDto>.Success(item, _localizer, acceptMsg);
    }

    /// <inheritdoc />
    public async Task<Result<LinkedStudentListItemDto>> BindStudentLinkForAdminAsync(
          long linkId, long actingUserId, BindStudentLinkDto dto)
    {
        // BUG-LINKID-01 guard — see UnbindStudentLinkForAdminAsync.
        if (linkId <= 0)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "InvalidLinkId", HttpStatusCode.BadRequest);

        var link = await _unitOfWork.Users.GetStudentTeacherLinkByIdAsync(linkId);
        if (link is null)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "LinkNotFound", HttpStatusCode.NotFound);

        // Delegate to the teacher-scoped method now that the owning TeacherId
        // is known — reuses the exact bind/re-point/claim-check logic, zero
        // duplication.
        return await BindStudentLinkAsync(link.TeacherId, linkId, actingUserId, dto);
    }

    /// <inheritdoc />
    public async Task<Result<LinkedStudentListItemDto>> BindStudentLinkAsync(
          long teacherId, long linkId, long actingUserId, BindStudentLinkDto dto)
    {
        // BUG-LINKID-01 guard — see UnbindStudentLinkAsync.
        if (linkId <= 0)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "InvalidLinkId", HttpStatusCode.BadRequest);

        var link = await _unitOfWork.Users.GetStudentTeacherLinkByIdForTeacherAsync(linkId, teacherId);
        if (link is null)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "LinkNotFound", HttpStatusCode.NotFound);

        // Only an ACCEPTED (Active) connection can be linked

        // Only an ACCEPTED (Active) connection can be linked — Pending must be
        // accepted first, terminal states are not linkable.
        if (link.LinkStatus != LinkStatus.Active)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "LinkNotActive", HttpStatusCode.Conflict);

        // A plan without student accounts (Managerial / ManagerialPlus) forbids binding a student
        // to the teacher's roster.
        var bindEntitlements = await _subscriptionGate.GetPlanEntitlementsAsync(teacherId);
        if (!bindEntitlements.StudentAccountsAllowed)
            return Result<LinkedStudentListItemDto>.Failure(
                _localizer, SubscriptionConstants.StudentAccountsBlockedMessageKey(bindEntitlements.PlanType),
                HttpStatusCode.Forbidden);

        // ── Resolve the target student record: explicit id wins, else by code ──
        var (rosterStudent, resolveFailure) =
            await ResolveRosterTargetAsync(teacherId, link, dto.TeacherStudentId, dto.StudentCode);
        if (resolveFailure is not null)
            return resolveFailure;

        // Unlike accept, bind without a target is meaningless — reject it.
        if (rosterStudent is null)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "BindTargetRequired", HttpStatusCode.BadRequest);

        // Already pointed at this record → idempotent success (no notification).
        if (link.TeacherStudentId == rosterStudent.Id)
        {
            var same = await BuildLinkedStudentItemAsync(link, rosterStudent);
            return Result<LinkedStudentListItemDto>.Success(same, _localizer, "LinkBound");
        }

        // One student account per record. This link holds a different (or no) record,
        // so any Active holder of the target is necessarily a DIFFERENT account.
        bool alreadyClaimed = await _unitOfWork.Users.IsTeacherStudentActivelyLinkedAsync(rosterStudent.Id);
        if (alreadyClaimed)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "RosterStudentAlreadyClaimed", HttpStatusCode.Conflict);

        // Paid seat check — ONLY for a first bind (unbound -> bound), which is a net +1.
        // RE-POINTING an already-bound link is net zero and must ALWAYS be allowed, even at
        // (or over) the limit: blocking "Change linked student" would be a UX regression and
        // would trap a teacher who is exactly at their limit.
        if (link.TeacherStudentId is null)
        {
            var seatFailure = await CheckLinkedStudentCapacityAsync(teacherId);
            if (seatFailure is not null) return seatFailure;
        }

        link.TeacherStudentId = rosterStudent.Id;   // first bind, or re-point ("Change")
        link.RemovedByUserId = null;                // (re)bound → clear any stale removal marker
        link.UnlinkedAt = null;
        await _unitOfWork.Users.UpdateStudentTeacherLinkAsync(link);
        await _unitOfWork.SaveChangesAsync();

        // ── Post-commit, best-effort: the student just gained access ──
        try
        {
            await _linkNotifier.NotifyLinkBindingChangedAsync(link.StudentUserId, teacherId, linked: true);
        }
        catch { /* notification failure must not fail the bind */ }

        var item = await BuildLinkedStudentItemAsync(link, rosterStudent);
        return Result<LinkedStudentListItemDto>.Success(item, _localizer, "LinkBound");
    }

    /// <inheritdoc />
    public async Task<Result<LinkedStudentListItemDto>> UnbindStudentLinkForAdminAsync(
            long linkId, long actingUserId)
    {
        // BUG-LINKID-01 guard — checked here too since this method resolves TeacherId via its
        // own lookup before ever reaching UnbindStudentLinkAsync's guard.
        if (linkId <= 0)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "InvalidLinkId", HttpStatusCode.BadRequest);

        var link = await _unitOfWork.Users.GetStudentTeacherLinkByIdAsync(linkId);
        if (link is null)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "LinkNotFound", HttpStatusCode.NotFound);

        // Delegate to the teacher-scoped method now that the owning TeacherId
        // is known — reuses the exact unbind logic, zero duplication.
        return await UnbindStudentLinkAsync(link.TeacherId, linkId, actingUserId);
    }
    /// <inheritdoc />
    public async Task<Result<LinkedStudentListItemDto>> UnbindStudentLinkAsync(
            long teacherId, long linkId, long actingUserId)
    {
        // BUG-LINKID-01 guard: a null/zero LinkId (stale DTO, dropped enrichment upstream) must
        // fail loudly and distinctly from a genuinely missing link — not surface as LinkNotFound.
        if (linkId <= 0)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "InvalidLinkId", HttpStatusCode.BadRequest);

        var link = await _unitOfWork.Users.GetStudentTeacherLinkByIdForTeacherAsync(linkId, teacherId);
        if (link is null)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "LinkNotFound", HttpStatusCode.NotFound);

        if (link.LinkStatus != LinkStatus.Active)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "UnbindLinkNotActive", HttpStatusCode.Conflict);

        bool wasLinked = link.TeacherStudentId.HasValue;
        link.TeacherStudentId = null;               // stays Active (connected), loses access
        if (wasLinked)
        {
            // Mark the removal (who + when) so the dashboard reports a concrete
            // "RemovedByTeacher" for this Active-but-unbound row — vs an accepted-
            // but-never-bound "AwaitingLink". UnlinkedAt doubles as the backfill
            // marker for links unbound before this code existed (see migration
            // BackfillUnbindRemovalMarker). Read only by the status projection.
            link.RemovedByUserId = actingUserId;
            link.UnlinkedAt = DateTime.UtcNow;
        }
        await _unitOfWork.Users.UpdateStudentTeacherLinkAsync(link);
        await _unitOfWork.SaveChangesAsync();

        if (wasLinked)
        {
            // ── Post-commit, best-effort: the student just lost access ──
            try
            {
                await _linkNotifier.NotifyLinkBindingChangedAsync(link.StudentUserId, teacherId, linked: false);
            }
            catch { /* notification failure must not fail the unbind */ }
        }

        var item = await BuildLinkedStudentItemAsync(link, rosterStudent: null);
        return Result<LinkedStudentListItemDto>.Success(item, _localizer, "LinkUnbound");
    }

    /// <inheritdoc />
    public async Task<Result<LinkedStudentListItemDto>> ResetStudentDeviceAsync(
        long teacherId, long linkId, long actingUserId)
    {
        if (linkId <= 0)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "InvalidLinkId", HttpStatusCode.BadRequest);

        var link = await _unitOfWork.Users.GetStudentTeacherLinkByIdForTeacherAsync(linkId, teacherId);
        if (link is null)
            return Result<LinkedStudentListItemDto>.Failure(_localizer, "LinkNotFound", HttpStatusCode.NotFound);

        // Idempotent: clearing an already-empty device binding is fine. The student re-registers
        // (with consent) the next time they open the teacher. The link's connect/bind state is untouched.
        link.LockedDeviceId = null;
        link.DeviceBoundAt = null;
        link.DeviceResetAt = DateTime.UtcNow;
        link.DeviceResetByUserId = actingUserId;
        await _unitOfWork.Users.UpdateStudentTeacherLinkAsync(link);
        await _unitOfWork.SaveChangesAsync();

        // Keep the row's Linked / Not linked state by re-loading the bound roster record, if any.
        TeacherStudent? roster = link.TeacherStudentId is null
            ? null
            : await _unitOfWork.Users.GetActiveTeacherStudentByIdAsync(teacherId, link.TeacherStudentId.Value);

        var item = await BuildLinkedStudentItemAsync(link, roster);
        return Result<LinkedStudentListItemDto>.Success(item, _localizer, "DeviceReset");
    }

    /// <inheritdoc />
    public async Task<Result<bool>> RejectLinkRequestAsync(long teacherId, long linkId, long actingUserId)
    {
        var link = await _unitOfWork.Users.GetStudentTeacherLinkByIdForTeacherAsync(linkId, teacherId);
        if (link is null)
            return Result<bool>.Failure(_localizer, "LinkRequestNotFound", HttpStatusCode.NotFound);

        if (link.LinkStatus != LinkStatus.Pending)
            return Result<bool>.Failure(_localizer, "LinkRequestAlreadyResolved", HttpStatusCode.Conflict);

        link.LinkStatus = LinkStatus.Rejected;
        link.RespondedAt = DateTime.UtcNow;
        link.RespondedByUserId = actingUserId;

        await _unitOfWork.Users.UpdateStudentTeacherLinkAsync(link);
        await _unitOfWork.SaveChangesAsync();

        // ── Post-commit, best-effort: tell the student the request was rejected ──
        try
        {
            await _linkNotifier.NotifyRequestResolvedAsync(link.StudentUserId, teacherId, accepted: false);
        }
        catch { /* notification failure must not fail the reject */ }

        return Result<bool>.Success(true, _localizer, "LinkRequestRejected");
    }

    /// <inheritdoc />
    public async Task<Result<LinkedStudentsPageResponse>> GetLinkedStudentsAsync(
        long teacherId, int page, int pageSize, string? search = null,
        LinkedStudentFilter filter = LinkedStudentFilter.All)
    {
        (page, pageSize) = NormalizePaging(page, pageSize);

        var (rows, allCount, linkedCount, filteredCount) = await _unitOfWork.Users
            .GetActiveLinkedStudentsForTeacherPagedAsync(teacherId, page, pageSize, search, filter);

        var items = rows.Select(r => new LinkedStudentListItemDto
        {
            LinkId = r.LinkId,
            LinkedAt = r.LinkedAt,
            StudentAccountCode = r.StudentAccountCode,
            StudentFullName = r.StudentFullName,
            StudentPhoneNumber = r.StudentPhoneNumber,
            TeacherStudentId = r.TeacherStudentId,
            RosterStudentName = r.RosterStudentName,
            RosterStudentCode = r.RosterStudentCode,
            IsLinked = r.TeacherStudentId.HasValue,
            IsDeviceRegistered = r.IsDeviceRegistered,
            DeviceBoundAt = r.DeviceBoundAt
        }).ToList();

        // Page-level flag: whether the teacher has the device lock on (drives the app's device UI).
        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);

        // Seat usage is counted over ALL of the teacher's links, NOT off `linkedCount` above —
        // that one is narrowed by the page's search term and would misreport what's left.
        var capacityTeacher = await _unitOfWork.Users.GetTeacherByIdAsync(teacherId);
        int linkedCapacity = capacityTeacher?.LinkedStudentCapacity ?? 0;
        int seatsUsed = await _unitOfWork.studentTeacherLinkRepo.CountBoundLinksAsync(teacherId);

        var response = new LinkedStudentsPageResponse
        {
            data = items,
            page = page,
            pageSize = pageSize,
            // totalCount/totalPages describe the FILTERED slice because they are the paging
            // contract — the client stops fetching at page * pageSize >= totalCount, so reporting
            // the unfiltered size here would send it chasing empty pages. With the default
            // All filter these equal allCount, so existing clients see byte-identical numbers.
            totalCount = filteredCount,
            totalPages = (int)Math.Ceiling(filteredCount / (double)pageSize),
            // The chip counts, by contrast, describe the whole searched set — they have to keep
            // answering "how many are on the OTHER tab" while a filter is applied. Note
            // unlinkedCount is derived from allCount, never from totalCount, which is now filtered.
            allCount = allCount,
            linkedCount = linkedCount,
            unlinkedCount = allCount - linkedCount,
            deviceLockEnabled = config?.IsDeviceLockEnabled ?? false,
            linkedStudentCapacity = linkedCapacity,
            // Clamped: a limit lowered below current usage reads as "0 left", never negative.
            linkedStudentsRemaining = Math.Max(0, linkedCapacity - seatsUsed),
        };

        return Result<LinkedStudentsPageResponse>.Success(response, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<List<LinkedStudentListItemDto>>> GetUnboundActiveLinksForAdminAsync(
        long teacherId)
    {
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
            return Result<List<LinkedStudentListItemDto>>.Failure(
                _localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        var rows = await _unitOfWork.Users.GetUnboundActiveLinksForTeacherAsync(teacherId);

        var items = rows.Select(r => new LinkedStudentListItemDto
        {
            LinkId = r.LinkId,
            LinkedAt = r.LinkedAt,
            StudentAccountCode = r.StudentAccountCode,
            StudentFullName = r.StudentFullName,
            StudentPhoneNumber = r.StudentPhoneNumber,
            TeacherStudentId = null,
            RosterStudentName = null,
            RosterStudentCode = null,
            IsLinked = false
        }).ToList();

        return Result<List<LinkedStudentListItemDto>>.Success(items, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<RemoveLinkedStudentsResultDto>> RemoveLinkedStudentsAsync(
        long teacherId, long actingUserId, RemoveLinkedStudentsDto dto)
    {
        var requestedIds = dto.LinkIds.Distinct().ToList();
        if (requestedIds.Count == 0)
            return Result<RemoveLinkedStudentsResultDto>.Failure(_localizer, "NoLinksSelected", HttpStatusCode.BadRequest);

        var links = await _unitOfWork.Users.GetActiveLinksByIdsForTeacherAsync(teacherId, requestedIds);
        if (links.Count == 0)
            return Result<RemoveLinkedStudentsResultDto>.Failure(_localizer, "LinkNotFound", HttpStatusCode.NotFound);

        var now = DateTime.UtcNow;
        foreach (var link in links)
        {
            link.LinkStatus = LinkStatus.RemovedByTeacher;
            link.UnlinkedAt = now;
            link.RemovedByUserId = actingUserId;
            await _unitOfWork.Users.UpdateStudentTeacherLinkAsync(link);
        }

        await _unitOfWork.SaveChangesAsync();

        // ── Post-commit, best-effort: tell each affected student ──
        foreach (var link in links)
        {
            try
            {
                await _linkNotifier.NotifyRemovedByTeacherAsync(link.StudentUserId, teacherId);
            }
            catch { /* notification failure must not fail the removal */ }
        }

        var removedIds = links.Select(l => l.Id).ToHashSet();
        var result = new RemoveLinkedStudentsResultDto
        {
            RemovedCount = links.Count,
            SkippedLinkIds = requestedIds.Where(id => !removedIds.Contains(id)).ToList()
        };

        return Result<RemoveLinkedStudentsResultDto>.Success(result, _localizer, "LinkedStudentsRemoved");
    }

    // ══════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════

    private static (int page, int pageSize) NormalizePaging(int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;
        return (page, pageSize);
    }

    /// <summary>
    /// Seat guard for the student-APP-ACCOUNT limit (<c>Teacher.LinkedStudentCapacity</c> — the
    /// limit the subscription price is based on, distinct from the students-in-the-account quota
    /// enforced in TeacherStudentService). A seat is consumed ONLY by a link that is Active AND
    /// bound to a student record, so this is called exactly on the two paths that create a new
    /// binding: "Accept &amp; link" and a FIRST bind. Returns null when there is room, otherwise a
    /// 403 failure naming the limit.
    ///
    /// Check-then-write with no lock — deliberately the same shape as the existing
    /// StudentCapacity check in TeacherStudentService (a concurrent double-bind can overshoot by
    /// one; lowering the limit never breaks existing linked students, it only stops new binds).
    /// </summary>
    private async Task<Result<LinkedStudentListItemDto>?> CheckLinkedStudentCapacityAsync(long teacherId)
    {
        var teacher = await _unitOfWork.Users.GetTeacherByIdAsync(teacherId);
        if (teacher is null)
            return Result<LinkedStudentListItemDto>.Failure(
                _localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        int seatsUsed = await _unitOfWork.studentTeacherLinkRepo.CountBoundLinksAsync(teacherId);
        if (seatsUsed >= teacher.LinkedStudentCapacity)
        {
            // A center-managed teacher cannot act on the generic advice ("upgrade your
            // subscription") — the center owns their limit, and the center's own screens
            // expose a single "student capacity" field, so they must be pointed at the
            // center instead of at a plan they are not allowed to buy.
            string messageKey = teacher.CenterId is null
                ? SubscriptionConstants.Messages.LinkedStudentCapacityReached
                : SubscriptionConstants.Messages.LinkedStudentCapacityReachedCenterManaged;

            return Result<LinkedStudentListItemDto>.Failure(
                _localizer,
                messageKey,
                new object?[] { teacher.LinkedStudentCapacity },
                HttpStatusCode.Forbidden);
        }

        return null;
    }

    /// <summary>
    /// Resolves the "Accept &amp; link" / bind target student record from an explicit
    /// id or a TEACHER-assigned roster code — shared by AcceptLinkRequestAsync and
    /// BindStudentLinkAsync so the two contracts cannot drift. Returns:
    /// (student, null) on success; (null, failure) when a target was supplied but
    /// does not resolve; (null, null) when NEITHER id nor code was supplied — the
    /// caller decides whether that is allowed (accept: unbound accept; bind: 400).
    /// A code that instead matches the linked account's globally-unique
    /// StudentAccountCode gets its own message — pasting the account code shown on
    /// the link row where the roster code belongs is the known client mixup, and
    /// the generic not-found reads as nonsense for a code the teacher can see.
    /// </summary>
    private async Task<(TeacherStudent? Student, Result<LinkedStudentListItemDto>? Failure)> ResolveRosterTargetAsync(
        long teacherId, StudentTeacherLink link, long? teacherStudentId, string? studentCode)
    {
        TeacherStudent? rosterStudent;
        if (teacherStudentId.HasValue)
        {
            rosterStudent = await _unitOfWork.Users.GetActiveTeacherStudentByIdAsync(teacherId, teacherStudentId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(studentCode))
        {
            string normalized = studentCode.Trim().ToUpperInvariant();
            rosterStudent = await _unitOfWork.Users.GetActiveTeacherStudentByCodeAsync(teacherId, normalized);
            if (rosterStudent is null)
            {
                // Account-code mixup check — one extra lookup, failure path only.
                var studentUser = await _unitOfWork.Users.GetStudentUserByIdAsync(link.StudentUserId);
                if (string.Equals(studentUser?.StudentAccountCode, normalized, StringComparison.OrdinalIgnoreCase))
                    return (null, Result<LinkedStudentListItemDto>.Failure(
                        _localizer, "StudentAccountCodeNotRosterCode", HttpStatusCode.BadRequest));

                // Wrong CODE gets its own wording; the generic "selected student not
                // found" below is for the id path, where the caller sent a picker id.
                return (null, Result<LinkedStudentListItemDto>.Failure(
                    _localizer, "TeacherStudentCodeNotFound", HttpStatusCode.NotFound));
            }
        }
        else
        {
            return (null, null);
        }

        if (rosterStudent is null)
            return (null, Result<LinkedStudentListItemDto>.Failure(_localizer, "RosterStudentNotFound", HttpStatusCode.NotFound));

        return (rosterStudent, null);
    }

    /// <summary>
    /// Builds the linked-student list item returned by a successful accept so the
    /// teacher UI can insert the row without refetching the whole list.
    /// </summary>
    private async Task<LinkedStudentListItemDto> BuildLinkedStudentItemAsync(
        StudentTeacherLink link, TeacherStudent? rosterStudent)
    {
        var studentUser = await _unitOfWork.Users.GetStudentUserByIdAsync(link.StudentUserId);
        User? accountUser = studentUser is null
            ? null
            : await _unitOfWork.Users.GetUserByIdAsync(studentUser.UserId);

        return new LinkedStudentListItemDto
        {
            LinkId = link.Id,
            LinkedAt = link.LinkedAt,
            StudentAccountCode = studentUser?.StudentAccountCode ?? string.Empty,
            StudentFullName = accountUser?.FullName ?? string.Empty,
            StudentPhoneNumber = accountUser?.PhoneNumber,
            TeacherStudentId = rosterStudent?.Id,
            RosterStudentName = rosterStudent?.StudentName,
            RosterStudentCode = rosterStudent?.StudentCode,
            IsLinked = rosterStudent is not null,
            IsDeviceRegistered = !string.IsNullOrEmpty(link.LockedDeviceId),
            DeviceBoundAt = link.DeviceBoundAt
        };
    }
}