using Edvanz.Application.Common;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.StudentUser;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Localization;
using System.Net;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements all Student User module operations.
/// Follows the Result pattern for operation outcomes.
/// All database access goes through IUnitOfWork.Users (IUserRepo) — no direct
/// GetRepository calls with raw expression predicates.
/// 
/// ARCHITECTURAL NOTE:
/// All query logic is encapsulated in IUserRepo named methods.
/// If a query needs to change, you edit the repo method — not this service.
/// 
/// TRANSACTION SAFETY:
/// Methods that write multiple rows check _unitOfWork.HasActiveTransaction.
/// When called from User module registration (outer transaction active),
/// they participate in that transaction. When called standalone, they
/// manage their own. See ownsTransaction pattern in each write method.
/// 
/// FIX DB-1: Dashboard methods use batch loading via GetTeacherDashboardDataAsync
/// to eliminate N+1 query patterns. Previously each linked teacher caused 4 individual
/// DB round-trips; now all data is loaded in 4 total queries regardless of teacher count.
/// </summary>
public class StudentUserService : IStudentUserService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStudentAccountCodeGenerator _codeGenerator;
    private readonly IStudentLinkNotifier _linkNotifier;
    private readonly IQrCodeRenderer _qrCodeRenderer;
    private readonly ISubscriptionGateService _subscriptionGate;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;
    private readonly IStudentDeviceLockService _deviceLock;

    public StudentUserService(
        IUnitOfWork unitOfWork,
        IStudentAccountCodeGenerator codeGenerator,
        IStudentLinkNotifier linkNotifier,
        IQrCodeRenderer qrCodeRenderer,
        ISubscriptionGateService subscriptionGate,
        IStringLocalizer<Domain.Resources.Messages> localizer,
        IStudentDeviceLockService deviceLock)
    {
        _unitOfWork = unitOfWork;
        _codeGenerator = codeGenerator;
        _linkNotifier = linkNotifier;
        _qrCodeRenderer = qrCodeRenderer;
        _subscriptionGate = subscriptionGate;
        _localizer = localizer;
        _deviceLock = deviceLock;
    }

    /// <inheritdoc />
    public async Task<Result<StudentUserProfileDto>> InitializeStudentUserAsync(CreateStudentUserDto dto)
    {
        // Validate the user exists and is of type Student
        var user = await _unitOfWork.Users.GetByIdAndTypeAsync(dto.UserId, UserType.Student);
        if (user is null)
            return Result<StudentUserProfileDto>.Failure(_localizer, "UserNotFound", HttpStatusCode.NotFound);

        // Ensure no duplicate StudentUser record for this user
        bool alreadyExists = await _unitOfWork.Users.StudentUserExistsByUserIdAsync(dto.UserId);
        if (alreadyExists)
            return Result<StudentUserProfileDto>.Failure(_localizer, "StudentUserAlreadyInitialized", HttpStatusCode.Conflict);

        // If caller already started a transaction (User module registration),
        // we participate in it. Otherwise we manage our own.
        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;

        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            // Generate unique student account code (AAM-FR-05.3 / AAM-NFR-03)
            string accountCode = await _codeGenerator.GenerateUniqueCodeAsync();

            // Create the StudentUser record
            var studentUser = new StudentUser
            {
                UserId = dto.UserId,
                StudentAccountCode = accountCode,
                LanguagePreference = dto.LanguagePreference,
                AccountStatus = AccountStatus.Active,
                IsFirstLogin = true,
                CreateAt = DateTime.UtcNow
            };

            await _unitOfWork.Users.AddStudentUserAsync(studentUser);
            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            var profileDto = BuildProfileDto(studentUser, user, linkedTeacherCount: 0);
            return Result<StudentUserProfileDto>.Success(profileDto, _localizer, "StudentUserInitializedSuccess", HttpStatusCode.Created);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<StudentUserProfileDto>> GetStudentUserProfileAsync(long studentUserId)
    {
        var studentUser = await _unitOfWork.Users.GetActiveStudentUserByIdAsync(studentUserId);
        if (studentUser is null)
            return Result<StudentUserProfileDto>.Failure(_localizer, "StudentUserNotFound", HttpStatusCode.NotFound);

        var user = await _unitOfWork.Users.GetUserByIdAsync(studentUser.UserId);
        if (user is null)
            return Result<StudentUserProfileDto>.Failure(_localizer, "UserNotFound", HttpStatusCode.NotFound);

        int linkedCount = await _unitOfWork.Users.CountActiveStudentTeacherLinksAsync(studentUserId);

        var profileDto = BuildProfileDto(studentUser, user, linkedCount);

        return Result<StudentUserProfileDto>.Success(profileDto, _localizer, "Success", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<StudentUserProfileDto>> UpdateStudentUserProfileAsync(
        long studentUserId, UpdateStudentUserProfileDto dto)
    {
        var studentUser = await _unitOfWork.Users.GetActiveStudentUserByIdAsync(studentUserId);
        if (studentUser is null)
            return Result<StudentUserProfileDto>.Failure(_localizer, "StudentUserNotFound", HttpStatusCode.NotFound);

        if (dto.LanguagePreference is not null &&
            dto.LanguagePreference != "en" && dto.LanguagePreference != "ar")
        {
            return Result<StudentUserProfileDto>.Failure(_localizer, "InvalidLanguagePreference", HttpStatusCode.BadRequest);
        }

        // Update language preference
        if (dto.LanguagePreference is not null)
            studentUser.LanguagePreference = dto.LanguagePreference;

        await _unitOfWork.Users.UpdateStudentUserAsync(studentUser);
        await _unitOfWork.SaveChangesAsync();

        // Reload user for profile DTO
        var user = await _unitOfWork.Users.GetUserByIdAsync(studentUser.UserId);
        int linkedCount = await _unitOfWork.Users.CountActiveStudentTeacherLinksAsync(studentUserId);

        var profileDto = BuildProfileDto(studentUser, user!, linkedCount);

        return Result<StudentUserProfileDto>.Success(profileDto, _localizer, "StudentUserProfileUpdated", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<StudentDashboardDto>> GetDashboardAsync(long studentUserId)
    {
        var studentUser = await _unitOfWork.Users.GetActiveStudentUserByIdAsync(studentUserId);
        if (studentUser is null)
            return Result<StudentDashboardDto>.Failure(_localizer, "StudentUserNotFound", HttpStatusCode.NotFound);

        // Get the student's teachers (including pending/rejected requests) for the dashboard
        var linkedTeachersResult = await GetMyTeachersAsync(studentUserId);

        var dashboardDto = new StudentDashboardDto
        {
            IsFirstLogin = studentUser.IsFirstLogin,
            StudentAccountCode = studentUser.StudentAccountCode,
            LinkedTeachers = linkedTeachersResult.IsSuccess && linkedTeachersResult.Data is not null
                ? linkedTeachersResult.Data
                : new List<StudentDashboardTeacherDto>()
        };

        return Result<StudentDashboardDto>.Success(dashboardDto, _localizer, "Success", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<StudentDashboardTeacherDto>> CreateLinkRequestAsync(long studentUserId, CreateLinkRequestDto dto)
    {
        // ── 1. Validate student user exists ──
        var studentUser = await _unitOfWork.Users.GetActiveStudentUserByIdAsync(studentUserId);
        if (studentUser is null)
            return Result<StudentDashboardTeacherDto>.Failure(_localizer, "StudentUserNotFound", HttpStatusCode.NotFound);

        // ── 2. Resolve the teacher from the public 8-digit code ──
        if (string.IsNullOrWhiteSpace(dto.TeacherCode) || dto.TeacherCode.Trim().Length != 8)
            return Result<StudentDashboardTeacherDto>.Failure(_localizer, "InvalidTeacherCode", HttpStatusCode.BadRequest);

        var teacher = await _unitOfWork.Users.GetActiveTeacherByCodeAsync(dto.TeacherCode.Trim());
        if (teacher is null)
            return Result<StudentDashboardTeacherDto>.Failure(_localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        // ── 2b. A plan without student accounts (Managerial / ManagerialPlus) does not accept student links ──
        var planEntitlements = await _subscriptionGate.GetPlanEntitlementsAsync(teacher.Id);
        if (!planEntitlements.StudentAccountsAllowed)
            return Result<StudentDashboardTeacherDto>.Failure(
                _localizer, SubscriptionConstants.StudentAccountsBlockedMessageKey(planEntitlements.PlanType),
                HttpStatusCode.Forbidden);

        // ── 3. Validate the student-typed name (the teacher identifies the request by it) ──
        if (string.IsNullOrWhiteSpace(dto.StudentName))
            return Result<StudentDashboardTeacherDto>.Failure(_localizer, "StudentLinkNameRequired", HttpStatusCode.BadRequest);

        // ── 4. One live row per (student, teacher): reject if already pending/linked ──
        var liveLink = await _unitOfWork.Users.GetLiveStudentTeacherLinkAsync(studentUserId, teacher.Id);
        if (liveLink is not null)
        {
            return liveLink.LinkStatus == LinkStatus.Active
                ? Result<StudentDashboardTeacherDto>.Failure(_localizer, "TeacherAlreadyLinked", HttpStatusCode.Conflict)
                : Result<StudentDashboardTeacherDto>.Failure(_localizer, "LinkRequestAlreadyPending", HttpStatusCode.Conflict);
        }

        // ── 5. Create the Pending request (transaction-safe) ──
        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            var now = DateTime.UtcNow;
            var link = new StudentTeacherLink
            {
                StudentUserId = studentUserId,
                TeacherId = teacher.Id,
                TeacherStudentId = null, // bound by the teacher at accept time
                LinkStatus = LinkStatus.Pending,
                RequestedStudentName = dto.StudentName.Trim(),
                RequestedStudentCode = string.IsNullOrWhiteSpace(dto.StudentCode)
                    ? null
                    : dto.StudentCode.Trim().ToUpperInvariant(),
                RequestedAt = now,
                // LinkedAt is non-nullable; it is overwritten with the accept time
                // when the teacher approves. Until then it mirrors RequestedAt.
                LinkedAt = now,
                CreateAt = now
            };

            await _unitOfWork.Users.AddStudentTeacherLinkAsync(link);

            // The dashboard is no longer "empty" once a request exists (AAM-FR-05.4)
            if (studentUser.IsFirstLogin)
            {
                studentUser.IsFirstLogin = false;
                await _unitOfWork.Users.UpdateStudentUserAsync(studentUser);
            }

            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            // ── 6. Post-commit, best-effort: tell the teacher a request arrived ──
            try
            {
                await _linkNotifier.NotifyRequestReceivedAsync(teacher.Id, link.RequestedStudentName!);
            }
            catch { /* notification failure must not fail the request */ }

            var batchData = await _unitOfWork.Users.GetTeacherDashboardDataAsync(new List<long> { teacher.Id });
            var dashboardTeacher = BuildDashboardTeacherDtoFromBatch(link, teacher.Id, studentUser.LanguagePreference, batchData);

            return Result<StudentDashboardTeacherDto>.Success(dashboardTeacher, _localizer, "LinkRequestCreated", HttpStatusCode.Created);
        }
        catch (Exception ex) when (IsUniqueLinkViolation(ex))
        {
            // Race: two concurrent requests for the same pair — the filtered unique
            // index (LinkStatus IN Active,Pending) is the tiebreaker.
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            return Result<StudentDashboardTeacherDto>.Failure(_localizer, "LinkRequestAlreadyPending", HttpStatusCode.Conflict);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> UnlinkTeacherAsync(long studentUserId, long teacherId, long actingUserId)
    {
        // The single live row is either Pending (cancel) or Active (unlink)
        var link = await _unitOfWork.Users.GetLiveStudentTeacherLinkAsync(studentUserId, teacherId);

        if (link is null)
            return Result<bool>.Failure(_localizer, "LinkNotFound", HttpStatusCode.NotFound);

        bool wasPending = link.LinkStatus == LinkStatus.Pending;

        // Soft transition: preserve the record for audit
        link.LinkStatus = wasPending ? LinkStatus.CancelledByStudent : LinkStatus.Unlinked;
        link.UnlinkedAt = DateTime.UtcNow;
        link.RemovedByUserId = actingUserId;

        await _unitOfWork.Users.UpdateStudentTeacherLinkAsync(link);
        await _unitOfWork.SaveChangesAsync();

        return Result<bool>.Success(true, _localizer,
            wasPending ? "LinkRequestCancelled" : "TeacherUnlinkSuccess", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    /// <summary>
    /// "Delete" a teacher from the student's side — the harder cousin of Unlink.
    /// It ends any live link (Active → Unlinked, Pending → CancelledByStudent) AND
    /// dismisses the card so it disappears from the student's home entirely, and it
    /// tells the teacher a linked student left (awareness only). Unlike Unlink, the
    /// card is NOT kept as a re-requestable "Unlinked" entry. Operates on the LATEST
    /// row of any status, so a dead card (Rejected/RemovedByTeacher/Unlinked) can be
    /// cleared too. Never touches the teacher's roster / attendance / payment data.
    /// </summary>
    public async Task<Result<bool>> DeleteTeacherAsync(long studentUserId, long teacherId, long actingUserId)
    {
        var link = await _unitOfWork.Users.GetLatestStudentTeacherLinkAsync(studentUserId, teacherId);
        if (link is null)
            return Result<bool>.Failure(_localizer, "LinkNotFound", HttpStatusCode.NotFound);

        // Idempotent: a double-tap (already deleted) is a success, not a 404/500.
        if (link.StudentDismissed)
            return Result<bool>.Success(true, _localizer, "TeacherDeleteSuccess", HttpStatusCode.OK);

        // Only a real, still-Active connection warrants telling the teacher "the student left"
        // (and only then does the student drop off the teacher's ACTIVE linked list). A dead
        // card being cleared is silent.
        bool wasConnected = link.LinkStatus == LinkStatus.Active;
        var now = DateTime.UtcNow;

        // End a live row first so we never leave a live row that would block a future
        // re-request (the filtered unique index only allows one live row per pair).
        if (link.LinkStatus == LinkStatus.Active)
        {
            link.LinkStatus = LinkStatus.Unlinked;
            link.UnlinkedAt = now;
            link.RemovedByUserId = actingUserId;
        }
        else if (link.LinkStatus == LinkStatus.Pending)
        {
            link.LinkStatus = LinkStatus.CancelledByStudent;
            link.UnlinkedAt = now;
            link.RemovedByUserId = actingUserId;
        }
        // else: already terminal (Rejected / Unlinked / CancelledByStudent) — just hide it.

        link.StudentDismissed = true;
        link.StudentDismissedAt = now;

        await _unitOfWork.Users.UpdateStudentTeacherLinkAsync(link);
        await _unitOfWork.SaveChangesAsync();

        // Post-commit, best-effort: a notification failure must not fail the delete.
        if (wasConnected)
        {
            try
            {
                var studentName = string.IsNullOrWhiteSpace(link.RequestedStudentName)
                    ? string.Empty
                    : link.RequestedStudentName!.Trim();
                await _linkNotifier.NotifyLinkDeletedByStudentAsync(teacherId, studentName);
            }
            catch { /* awareness ping only — never fail the operation */ }
        }

        return Result<bool>.Success(true, _localizer, "TeacherDeleteSuccess", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    /// <summary>
    /// FIX DB-1: batch loading — 1 query for links + 4 queries for all teacher data.
    /// Request/approval flow: returns the LATEST link row per teacher across every
    /// status, so the student sees Pending requests and accepted/rejected outcomes
    /// on the same list. Pending/Active entries sort before historical ones.
    /// </summary>
    public async Task<Result<List<StudentDashboardTeacherDto>>> GetMyTeachersAsync(long studentUserId)
    {
        // Validate student user exists
        var studentUser = await _unitOfWork.Users.GetActiveStudentUserByIdAsync(studentUserId);
        if (studentUser is null)
            return Result<List<StudentDashboardTeacherDto>>.Failure(_localizer, "StudentUserNotFound", HttpStatusCode.NotFound);

        // All rows, newest first — reduce to the latest row per teacher so each
        // teacher appears exactly once with its current state (AAM-FR-05.7).
        var allLinks = await _unitOfWork.Users.GetAllStudentTeacherLinksAsync(studentUserId);
        var latestPerTeacher = allLinks
            .GroupBy(l => l.TeacherId)
            .Select(g => g.First()) // rows are ordered by Id DESC in the repo
            // "Delete" (dismiss) hides the teacher from the student's home entirely: drop
            // any teacher whose NEWEST row was student-dismissed. A later re-request adds a
            // fresh (non-dismissed) row that becomes newest, so the teacher reappears.
            .Where(l => !l.StudentDismissed)
            .ToList();

        if (!latestPerTeacher.Any())
        {
            return Result<List<StudentDashboardTeacherDto>>.Success(
                new List<StudentDashboardTeacherDto>(), _localizer, "Success", HttpStatusCode.OK);
        }

        // FIX DB-1: Batch load ALL teacher data in one call (4 queries total, not 4×N)
        var teacherIds = latestPerTeacher.Select(l => l.TeacherId).Distinct().ToList();
        var batchData = await _unitOfWork.Users.GetTeacherDashboardDataAsync(teacherIds);

        // Build dashboard DTOs from pre-loaded data — zero additional DB calls
        var dashboardTeachers = new List<StudentDashboardTeacherDto>();

        foreach (var link in latestPerTeacher)
        {
            if (!batchData.Teachers.ContainsKey(link.TeacherId))
                continue; // Teacher account deleted — skip

            var dashboardTeacher = BuildDashboardTeacherDtoFromBatch(
                link, link.TeacherId, studentUser.LanguagePreference, batchData);

            dashboardTeachers.Add(dashboardTeacher);
        }

        // Live entries first (Pending, then Active), then historical states,
        // newest activity first within each group.
        var ordered = dashboardTeachers
            .OrderBy(t => t.Status switch
            {
                nameof(LinkStatus.Pending) => 0,
                nameof(LinkStatus.Active) => 1,
                _ => 2
            })
            .ThenByDescending(t => t.RespondedAt ?? t.RequestedAt ?? t.LinkedAt)
            .ToList();

        return Result<List<StudentDashboardTeacherDto>>.Success(
            ordered, _localizer, "Success", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<StudentUserProfileDto>> GetStudentUserByAccountCodeAsync(string accountCode)
    {
        if (string.IsNullOrWhiteSpace(accountCode))
            return Result<StudentUserProfileDto>.Failure(_localizer, "StudentAccountCodeRequired", HttpStatusCode.BadRequest);

        var studentUser = await _unitOfWork.Users.GetStudentUserByAccountCodeAsync(accountCode);

        if (studentUser is null)
            return Result<StudentUserProfileDto>.Failure(_localizer, "StudentUserNotFound", HttpStatusCode.NotFound);

        var user = await _unitOfWork.Users.GetUserByIdAsync(studentUser.UserId);
        if (user is null)
            return Result<StudentUserProfileDto>.Failure(_localizer, "UserNotFound", HttpStatusCode.NotFound);

        int linkedCount = await _unitOfWork.Users.CountActiveStudentTeacherLinksAsync(studentUser.Id);

        var profileDto = BuildProfileDto(studentUser, user, linkedCount);

        return Result<StudentUserProfileDto>.Success(profileDto, _localizer, "Success", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<StudentTeacherBarcodeDto>> GetTeacherBarcodeForStudentAsync(
        long studentUserId, long teacherId, string? deviceId, string? previousDeviceId = null)
    {
        // ── 1. Validate the calling student account exists (defensive — the controller
        //       already resolved it from the JWT). ──
        var studentUser = await _unitOfWork.Users.GetActiveStudentUserByIdAsync(studentUserId);
        if (studentUser is null)
            return Result<StudentTeacherBarcodeDto>.Failure(_localizer, "StudentUserNotFound", HttpStatusCode.NotFound);

        // ── 2. The student must be ACTIVELY linked to this teacher. The route teacherId is
        //       untrusted (§3.3): a student can only ever obtain a code under a teacher they
        //       actually belong to (mirrors the attendance/videos student gates). ──
        var link = await _unitOfWork.Users.GetActiveStudentTeacherLinkAsync(studentUserId, teacherId);
        if (link is null)
            return Result<StudentTeacherBarcodeDto>.Failure(_localizer, "StudentNotLinkedToTeacher", HttpStatusCode.Forbidden);

        // ── 3. The Active link must be BOUND to a roster record — the per-teacher code lives
        //       on that record. Active-but-unbound = connected, no code issued yet. ──
        if (link.TeacherStudentId is null)
            return Result<StudentTeacherBarcodeDto>.Failure(_localizer, "StudentNotBoundToRoster", HttpStatusCode.Forbidden);

        // ── 4. Honour the teacher's barcode display mode (AAM-FR-04.7). HardCopyOnly means the
        //       teacher issues printed cards only — the app must not show an in-app QR. The
        //       stable code lets the frontend hide the "Show QR Code" button. ──
        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);

        // ── Device lock (per teacher): the QR is teacher-scoped content — gate it too. Blocking it
        //    also stops the student being scanned for attendance, so a wrong block here is costly:
        //    see StudentDeviceLockPolicy for why the id is now stable per DEVICE, not per install. ──
        var deviceOutcome = await _deviceLock.EvaluateAsync(link, teacherId, config, deviceId, previousDeviceId);
        if (deviceOutcome.IsBlocked)
            return Result<StudentTeacherBarcodeDto>.Failure(_localizer, deviceOutcome.FailureCode!, deviceOutcome.Status!.Value);

        if (config?.BarcodeDisplayMode == BarcodeDisplayMode.HardCopyOnly)
            return Result<StudentTeacherBarcodeDto>.Failure(_localizer, "BarcodeNotAvailableInApp", HttpStatusCode.Forbidden);

        // ── 5. Load the bound roster record (tenant-scoped to the teacher). Null means the
        //       teacher deleted it after binding — treat as unbound. ──
        var roster = await _unitOfWork.Users.GetActiveTeacherStudentByIdAsync(teacherId, link.TeacherStudentId.Value);
        if (roster is null)
            return Result<StudentTeacherBarcodeDto>.Failure(_localizer, "StudentNotBoundToRoster", HttpStatusCode.Forbidden);

        // ── 6. Encode the value the teacher's scan resolves. GetActiveByCodeAndTeacherAsync
        //       (the exam/attendance/payment scan path) matches TeacherStudent.StudentCode, so
        //       the QR MUST encode StudentCode for the loop to close. (Barcode is set ==
        //       StudentCode at creation but is immutable per REQ-STU-048, so it can drift from
        //       StudentCode after a manual-mode code change — StudentCode is the authoritative
        //       scan key.) ──
        string code = roster.StudentCode;

        // ── 7. Resolve the teacher's display name for the screen header (lean projection). ──
        string teacherName = await _unitOfWork.Users.GetTeacherDisplayNameAsync(teacherId) ?? string.Empty;

        var dto = new StudentTeacherBarcodeDto
        {
            TeacherId = teacherId,
            TeacherName = teacherName,
            Code = code,
            QrSvg = _qrCodeRenderer.RenderQrCodeSvg(code)
        };

        return Result<StudentTeacherBarcodeDto>.Success(dto, _localizer, "Success", HttpStatusCode.OK);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> RegisterDeviceForTeacherAsync(
        long studentUserId, long teacherId, string? deviceId, string? previousDeviceId = null)
    {
        // Normalize exactly as the gate does, so a value that OPENS content can never be rejected
        // here (or stored in a shape the gate would then fail to match).
        var trimmed = StudentDeviceLockPolicy.Normalize(deviceId);
        if (trimmed is null)
            return Result<bool>.Failure(_localizer, "DeviceIdMissing", HttpStatusCode.BadRequest);

        var previous = StudentDeviceLockPolicy.Normalize(previousDeviceId);

        var studentUser = await _unitOfWork.Users.GetActiveStudentUserByIdAsync(studentUserId);
        if (studentUser is null)
            return Result<bool>.Failure(_localizer, "StudentUserNotFound", HttpStatusCode.NotFound);

        // Same gate as every teacher-scoped read: must be ACTIVE and bound under this teacher.
        var link = await _unitOfWork.Users.GetActiveStudentTeacherLinkAsync(studentUserId, teacherId);
        if (link is null || link.LinkStatus != LinkStatus.Active)
            return Result<bool>.Failure(_localizer, "TeacherLinkNotFound", HttpStatusCode.Forbidden);
        if (link.TeacherStudentId is null)
            return Result<bool>.Failure(_localizer, "StudentEnrollmentRemoved", HttpStatusCode.Forbidden);

        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);
        // Lock off → nothing to register; report success so the client can just proceed.
        if (config is null || !config.IsDeviceLockEnabled)
            return Result<bool>.Success(true, _localizer, "DeviceRegistered", HttpStatusCode.OK);

        // Already bound: succeed if it's this device, otherwise the teacher/assistant must reset first.
        if (!string.IsNullOrWhiteSpace(link.LockedDeviceId))
        {
            if (StudentDeviceLockPolicy.SameDevice(link.LockedDeviceId, trimmed))
                return Result<bool>.Success(true, _localizer, "DeviceRegistered", HttpStatusCode.OK);

            // Same phone, former id: re-point the binding onto the stable id (conditional write —
            // it can never take over a binding held by a genuinely different device).
            if (previous is not null && StudentDeviceLockPolicy.SameDevice(link.LockedDeviceId, previous))
            {
                await _unitOfWork.Users.TryMigrateStudentTeacherLinkDeviceAsync(
                    link.Id, previous, trimmed, DateTime.UtcNow);
                return Result<bool>.Success(true, _localizer, "DeviceRegistered", HttpStatusCode.OK);
            }

            return Result<bool>.Failure(_localizer, StudentDeviceLockPolicy.MismatchCode, HttpStatusCode.Forbidden);
        }

        // Not bound yet: bind atomically (first device wins if two register at once).
        bool bound = await _unitOfWork.Users.TryBindStudentTeacherLinkDeviceAsync(link.Id, trimmed, DateTime.UtcNow);
        if (bound)
            return Result<bool>.Success(true, _localizer, "DeviceRegistered", HttpStatusCode.OK);

        // Lost the race — accept if the winner was this same device, else it's a mismatch.
        var fresh = await _unitOfWork.Users.GetActiveStudentTeacherLinkAsync(studentUserId, teacherId);
        if (fresh?.LockedDeviceId is not null &&
            StudentDeviceLockPolicy.SameDevice(fresh.LockedDeviceId, trimmed))
            return Result<bool>.Success(true, _localizer, "DeviceRegistered", HttpStatusCode.OK);

        return Result<bool>.Failure(_localizer, StudentDeviceLockPolicy.MismatchCode, HttpStatusCode.Forbidden);
    }
    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<StudentAccountListItemDto>>>> GetStudentAccountsAsync(
        StudentAccountListRequest request)
    {
        var (items, totalCount) = await _unitOfWork.Users.GetStudentAccountsPagedAsync(
            request.Search, request.TeacherId, request.Page, request.PageSize);

        // Batch-load linked teachers for exactly this page's accounts — one extra
        // query total, regardless of page size (same convention as
        // GetMyTeachersAsync's GetTeacherDashboardDataAsync batch load above).
        var studentAccountIds = items.Select(i => i.StudentAccountId).ToList();
        var teacherRows = await _unitOfWork.Users.GetActiveLinkedTeachersForStudentUsersAsync(studentAccountIds);

        var teachersByAccount = teacherRows
            .GroupBy(r => r.StudentUserId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new StudentAccountTeacherDto
                {
                    TeacherId = r.TeacherId,
                    TeacherCode = r.TeacherCode,
                    StudentCode = r.StudentCode,
                    TeacherName = r.TeacherName,
                    
                }).ToList());

        var dtos = items.Select(i => new StudentAccountListItemDto
        {
            StudentAccountId = i.StudentAccountId,
            FullName = i.FullName,
            UserName = i.UserName,
            PhoneNumber = i.PhoneNumber,
            AccountCode=i.StudentAccountCode,
            LastLoginAt = i.LastLoginAt,
            Teachers = teachersByAccount.TryGetValue(i.StudentAccountId, out var teachers)
                ? teachers
                : new List<StudentAccountTeacherDto>()
        }).ToList();

        var response = new PaginatedResponse<List<StudentAccountListItemDto>>
        {
            totalCount = totalCount,
            page = request.Page,
            pageSize = request.PageSize,
            totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)request.PageSize),
            data = dtos
        };

        return Result<PaginatedResponse<List<StudentAccountListItemDto>>>.Success(response, _localizer);
    }

    // ══════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════

    /// <summary>
    /// Builds a StudentUserProfileDto from the entity and user data.
    /// Centralizes the mapping to avoid duplication across methods.
    /// </summary>
    private static StudentUserProfileDto BuildProfileDto(StudentUser studentUser, User user, int linkedTeacherCount)
    {
        return new StudentUserProfileDto
        {
            Id = studentUser.Id,
            UserId = studentUser.UserId,
            StudentAccountCode = studentUser.StudentAccountCode,
            FullName = user.FullName,
            Email = user.Email,
            PhoneNumber = user.PhoneNumber,
            LanguagePreference = studentUser.LanguagePreference,
            AccountStatus = studentUser.AccountStatus.ToString(),
            IsFirstLogin = studentUser.IsFirstLogin,
            CreatedAt = studentUser.CreateAt,
            LinkedTeacherCount = linkedTeacherCount
        };
    }

    /// <summary>
    /// FIX DB-1 + FIX BUG-3: Builds a StudentDashboardTeacherDto from pre-loaded batch data.
    /// Zero database calls — all data resolved from in-memory dictionaries.
    /// Subject name respects the student's language preference (AAM-FR-02.2).
    /// </summary>
    private static StudentDashboardTeacherDto BuildDashboardTeacherDtoFromBatch(
        StudentTeacherLink link,
        long teacherId,
        string? languagePreference,
        TeacherDashboardBatchData batchData)
    {
        var teacher = batchData.Teachers.GetValueOrDefault(teacherId);
        string teacherFullName = string.Empty;
        if (teacher is not null && batchData.Users.TryGetValue(teacher.UserId, out var teacherUser))
            teacherFullName = teacherUser.FullName;

        // Resolve subject name with language preference
        string subjectName = teacher?.CustomSubject ?? string.Empty;
        if (teacher is not null &&
            batchData.TeacherSubjects.TryGetValue(teacherId, out var teacherSubjects) &&
            teacherSubjects.Any())
        {
            var firstSubjectId = teacherSubjects.First().SubjectId;
            if (batchData.Subjects.TryGetValue(firstSubjectId, out var subject))
            {
                // FIX BUG-3: Respect user's language preference for subject name (AAM-FR-02.2)
                subjectName = languagePreference == "ar" ? subject.NameAr : subject.NameEn;
            }
        }

        // Load STUDENT visibility settings (AAM-FR-04.8 / AAM-FR-05.8)
        batchData.Configurations.TryGetValue(teacherId, out var config);

        return new StudentDashboardTeacherDto
        {
            TeacherId = teacherId,
            LinkId = link.Id,
            Status = ComputeDisplayStatus(link),
            RequestedAt = link.RequestedAt,
            RespondedAt = link.RespondedAt,
            TeacherCode = teacher?.TeacherCode ?? string.Empty,
            TeacherFullName = teacherFullName,
            SubjectName = subjectName,
            LinkedAt = link.LinkedAt,
            IsEnrollmentActive = link.LinkStatus == LinkStatus.Active && link.TeacherStudentId.HasValue,
            IsLinked = link.LinkStatus == LinkStatus.Active && link.TeacherStudentId.HasValue,
            VisibilityAttendance = config?.StudentVisibilityAttendance ?? true,
            VisibilityPayment = config?.StudentVisibilityPayment ?? true,
            VisibilityHomework = config?.StudentVisibilityHomework ?? true,
            VisibilityExamDefault = config?.StudentVisibilityExamDefault ?? false,
            VisibilityVideo = config?.StudentVisibilityVideo ?? true,
            VisibilityOnlineExamDefault = config?.StudentVisibilityOnlineExamDefault ?? true
        };
    }

    /// <summary>
    /// Resolves the single AUTHORITATIVE status string the student app renders,
    /// folding the enrollment binding into the request lifecycle so the client
    /// never combines fields. The stored <see cref="LinkStatus"/> deliberately
    /// stays Active when a teacher unbinds (so the student can be re-bound without
    /// a new request), so an Active row is reported as:
    ///   • "Active" — only when still bound to a roster record (real access);
    ///   • "RemovedByTeacher" — bound then unbound by the teacher (RemovedByUserId stamped);
    ///   • "AwaitingLink" — accepted but never bound yet (connected, no access).
    /// Every other state passes through its stored <see cref="LinkStatus"/> name.
    /// </summary>
    private static string ComputeDisplayStatus(StudentTeacherLink link)
    {
        if (link.LinkStatus == LinkStatus.Active && !link.TeacherStudentId.HasValue)
        {
            // Either marker means the binding was removed after the student was
            // linked (RemovedByUserId = who, UnlinkedAt = when / backfilled); with
            // neither, the link was accepted but never bound yet ("AwaitingLink").
            var wasRemoved = link.RemovedByUserId.HasValue || link.UnlinkedAt.HasValue;
            return wasRemoved
                ? nameof(LinkStatus.RemovedByTeacher)
                : DashboardLinkStatus.AwaitingLink;
        }

        return link.LinkStatus.ToString();
    }

    /// <summary>
    /// Architecture-safe unique-index violation detection (same rationale as
    /// AttendanceService.IsConcurrencyException / FIX H7): the Application layer
    /// cannot reference EF Core or SqlClient types, so we walk the exception
    /// chain by type name and match the filtered unique index name.
    /// </summary>
    private static bool IsUniqueLinkViolation(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if ((current.GetType().Name == "SqlException" || current.GetType().Name == "DbUpdateException") &&
                current.Message.Contains("IX_StudentTeacherLinks_StudentUserId_TeacherId"))
                return true;
            current = current.InnerException;
        }
        return false;
    }
}