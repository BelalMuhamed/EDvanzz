using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Session;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Localization;
using System.Net;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements all Session Module (Module 2) operations.
/// Manages teacher-scoped sessions: CRUD, groups, membership links,
/// student assignment, search, filter, and session name generation.
/// 
/// All database access goes through IUnitOfWork.SessionsRepo (ISessionRepo)
/// and IUnitOfWork.Users (IUserRepo for teacher validation) — no direct
/// GetRepository calls with raw expression predicates.
/// 
/// ARCHITECTURAL NOTE:
/// All query logic is encapsulated in ISessionRepo named methods.
/// If a query changes, you edit the repo method — not this service.
/// 
/// TRANSACTION SAFETY:
/// All transactional methods use the ownsTransaction pattern:
///   bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
/// This makes them safe for both standalone calls and nested calls.
/// 
/// HARD DELETE:
/// REQ-SES-041: Sessions use hard delete — no soft-delete or recycle bin.
/// 
/// ATTENDANCE MODULE INTEGRATION (Module 3):
/// Session lifecycle events are forwarded to IAttendanceService:
/// - CreateSession → GenerateOccurrencesAsync (populate occurrence schedule)
/// - UpdateSession → GenerateOccurrencesAsync (regenerate if dates changed)
/// - DeleteSession → OnSessionDeletingAsync (preserve attendance records before hard delete)
/// - DuplicateSession → GenerateOccurrencesAsync (populate for the duplicate)
/// - AssignStudents → OnStudentAssignedToSessionAsync (create assignment + init counter)
/// - ConfirmReassign → OnStudentUnassigned + OnStudentAssigned (close old, open new period)
/// - UnassignStudent → OnStudentUnassignedFromSessionAsync (close assignment period)
///
/// PAYMENT MODULE INTEGRATION (Module 4):
/// Session lifecycle events are forwarded to IPaymentService:
/// - AssignStudents → OnStudentAssignedToSessionAsync (generate payment periods + init counter)
/// - ConfirmReassign → OnStudentMovedBetweenSessionsAsync (carry billing A→B re-priced; paid months
///   stay as history) — or a plain assign when the student had no prior session
/// - UnassignStudent → OnStudentUnassignedFromSessionAsync (preserve payment history)
/// - DeleteSession → OnSessionDeletingAsync (keep paid history, void future-unpaid, carry unpaid
///   arrears as pending debt, then nullify SessionId on surviving records before hard delete)
/// </summary>
public class SessionService : ISessionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISessionNameGenerator _nameGenerator;
    private readonly IAttendanceService _attendanceService;
    private readonly IPaymentService _paymentService;
    private readonly ISubscriptionGateService _subscriptionGate;
    private readonly ITimeZoneService _timeZoneService;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;

    /// <summary>
    /// Maximum number of days selectable for Weekly/BiWeekly occurrence types.
    /// REQ-SES-008: Minimum 1, maximum 7 days per week.
    /// </summary>
    private const int MaxSelectedDays = 7;

    /// <summary>
    /// Valid day-of-week indices. 0=Saturday through 6=Friday.
    /// REQ-SES-008: Day-of-week selector values.
    /// </summary>
    private static readonly HashSet<int> ValidDayIndices = new() { 0, 1, 2, 3, 4, 5, 6 };

    public SessionService(
        IUnitOfWork unitOfWork,
        ISessionNameGenerator nameGenerator,
        IAttendanceService attendanceService,
        IPaymentService paymentService,
        ISubscriptionGateService subscriptionGate,
        IStringLocalizer<Domain.Resources.Messages> localizer,
        ITimeZoneService timeZoneService)
    {
        _unitOfWork = unitOfWork;
        _nameGenerator = nameGenerator;
        _attendanceService = attendanceService;
        _paymentService = paymentService;
        _subscriptionGate = subscriptionGate;
        _localizer = localizer;
        _timeZoneService = timeZoneService;
    }

    // ══════════════════════════════════════════════
    // SESSION CRUD
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<SessionDto>> CreateSessionAsync(CreateSessionDto dto)
    {
        // 1. Validate teacher exists
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(dto.TeacherId);
        if (teacher is null)
            return Result<SessionDto>.Failure(_localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        // 1b. Free-tier quota: unsubscribed teachers may keep at most the configured session count.
        if (!await _subscriptionGate.CanCreateAsync(
                dto.TeacherId, ModuleQuotaKeys.Sessions,
                () => _unitOfWork.SessionsRepo.CountSessionsByTeacherAsync(dto.TeacherId)))
            return Result<SessionDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequired, HttpStatusCode.Forbidden);

        // 2. Resolve session name: auto-generate or use provided
        string sessionName;
        if (string.IsNullOrWhiteSpace(dto.SessionName))
        {
            // Auto-generate: check teacher config for session name settings
            var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(dto.TeacherId);
            var language = config?.SessionNameLanguage ?? GenerationLanguage.English;
            sessionName = await _nameGenerator.GenerateNextNameAsync(dto.TeacherId, language);
        }
        else
        {
            sessionName = dto.SessionName.Trim();
        }

        // 3. Validate unique session name per teacher (BR-SES-001)
        bool nameExists = await _unitOfWork.SessionsRepo.SessionNameExistsAsync(dto.TeacherId, sessionName);
        if (nameExists)
            return Result<SessionDto>.Failure(_localizer, "SessionNameDuplicate", HttpStatusCode.Conflict);

        // 4. Validate occurrence type configuration
        var occurrenceErrorKey = ValidateOccurrenceConfiguration(dto.OccurrenceType, dto.SelectedDays, dto.MonthlyDayOfMonth);
        if (occurrenceErrorKey is not null)
            return Result<SessionDto>.Failure(_localizer, occurrenceErrorKey, HttpStatusCode.BadRequest);

        // 5. Validate date range (REQ-SES-014)
        if (dto.EndDate <= dto.StartDate)
            return Result<SessionDto>.Failure(_localizer, "SessionEndDateMustBeAfterStartDate", HttpStatusCode.BadRequest);

        // 6. Validate group exists if provided
        if (dto.SessionGroupId.HasValue)
        {
            var group = await _unitOfWork.SessionsRepo.GetGroupByIdAndTeacherAsync(dto.SessionGroupId.Value, dto.TeacherId);
            if (group is null)
                return Result<SessionDto>.Failure(_localizer, "SessionGroupNotFound", HttpStatusCode.NotFound);
        }

        // 7. Create the session entity
        var session = new Session
        {
            TeacherId = dto.TeacherId,
            SessionName = sessionName,
            OccurrenceType = dto.OccurrenceType,
            SelectedDays = FormatSelectedDays(dto.SelectedDays),
            MonthlyDayOfMonth = dto.OccurrenceType == OccurrenceType.Monthly ? dto.MonthlyDayOfMonth : null,
            PaymentType = dto.PaymentType,
            SessionAmount = dto.SessionAmount,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate,
            StartTime = dto.StartTime,
            DurationMinutes = dto.DurationMinutes,
            SessionGroupId = dto.SessionGroupId,
            CreateAt = DateTime.UtcNow
        };

        await _unitOfWork.SessionsRepo.AddAsync(session);
        await _unitOfWork.SaveChangesAsync();

        // ── ATTENDANCE INTEGRATION: Generate session occurrences (REQ-ATT-001/002) ──
        // Populates SessionOccurrences table from the session's recurrence rules.
        // This enables the attendance dashboard, Take Attendance, and all date-based queries.
        await _attendanceService.GenerateOccurrencesAsync(dto.TeacherId, session.Id);

        var resultDto = await BuildSessionDtoAsync(session);
        return Result<SessionDto>.Success(resultDto, _localizer, "SessionCreatedSuccess", HttpStatusCode.Created);
    }

    /// <inheritdoc />
    public async Task<Result<SessionDto>> GetSessionByIdAsync(long teacherId, long sessionId)
    {
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<SessionDto>.Failure(_localizer, "SessionNotFound", HttpStatusCode.NotFound);

        var dto = await BuildSessionDtoAsync(session);
        return Result<SessionDto>.Success(dto, _localizer, "Success");
    }

    /// <inheritdoc />
    public async Task<Result<SessionDto>> UpdateSessionAsync(long teacherId, long sessionId, UpdateSessionDto dto)
    {
        // 1. Validate session exists and belongs to teacher
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<SessionDto>.Failure(_localizer, "SessionNotFound", HttpStatusCode.NotFound);

        // 2. Validate unique name (excluding self) — BR-SES-001
        string trimmedName = dto.SessionName.Trim();
        bool nameExists = await _unitOfWork.SessionsRepo.SessionNameExistsExcludingAsync(teacherId, trimmedName, sessionId);
        if (nameExists)
            return Result<SessionDto>.Failure(_localizer, "SessionNameDuplicate", HttpStatusCode.Conflict);

        // 3. Detect a schedule-pattern change (occurrence type, selected days, or monthly day) — the part
        //    of the recurrence that REMAPS occurrence dates. REQ-SES-009.
        byte? newMonthlyDay = dto.OccurrenceType == OccurrenceType.Monthly ? dto.MonthlyDayOfMonth : null;
        bool schedulePatternChanged = session.OccurrenceType != dto.OccurrenceType
                                   || session.SelectedDays != FormatSelectedDays(dto.SelectedDays)
                                   || session.MonthlyDayOfMonth != newMonthlyDay;

        // SES-1: the schedule pattern may be changed even when the session has enrolled students — the
        // occurrences are rebuilt transactionally below. It is blocked ONLY while a membership link
        // exists: BR-SES-003 requires linked sessions to keep the same occurrence type and day count, and
        // they share a cross-session slot alignment that a remap would desync. Tell the tutor which
        // session(s) to unlink first (clear, actionable message — replaces the old students-or-links lock).
        if (schedulePatternChanged)
        {
            var linkedSessions = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(sessionId);
            if (linkedSessions.Count > 0)
            {
                string linkedNames = string.Join(", ", linkedSessions.Select(s => s.SessionName));
                return Result<SessionDto>.Failure(
                    _localizer, "SessionScheduleLockedByLink", new object?[] { linkedNames }, HttpStatusCode.BadRequest);
            }
        }

        // 4. Validate occurrence configuration (SES-5: returns the stable message key)
        var occurrenceErrorKey = ValidateOccurrenceConfiguration(dto.OccurrenceType, dto.SelectedDays, dto.MonthlyDayOfMonth);
        if (occurrenceErrorKey is not null)
            return Result<SessionDto>.Failure(_localizer, occurrenceErrorKey, HttpStatusCode.BadRequest);

        // 5. Validate date range (REQ-SES-014)
        if (dto.EndDate <= dto.StartDate)
            return Result<SessionDto>.Failure(_localizer, "SessionEndDateMustBeAfterStartDate", HttpStatusCode.BadRequest);

        // 6. Validate group if provided
        if (dto.SessionGroupId.HasValue)
        {
            var group = await _unitOfWork.SessionsRepo.GetGroupByIdAndTeacherAsync(dto.SessionGroupId.Value, teacherId);
            if (group is null)
                return Result<SessionDto>.Failure(_localizer, "SessionGroupNotFound", HttpStatusCode.NotFound);
        }

        // ── Track whether the recurrence or its date window changed → occurrences must be rebuilt ──
        bool datesOrRecurrenceChanged = schedulePatternChanged
            || session.StartDate != dto.StartDate
            || session.EndDate != dto.EndDate;

        // ── Track a price change so the new amount flows to upcoming (future-month) unpaid periods. ──
        // A PaymentType switch changes period STRUCTURE (monthly rows vs per-occurrence), not just the
        // price, so propagation is limited to a pure amount change on the SAME payment type.
        bool amountChanged = session.SessionAmount != dto.SessionAmount
            && session.PaymentType == dto.PaymentType;

        // ── Track an end-date change so the payment module can backfill the billing months the
        // original assignment never generated (it only ran to the end date as it was back then). ──
        bool endDateChanged = session.EndDate != dto.EndDate;

        // SES-1: the session mutation AND the occurrence rebuild must be one atomic unit. Previously the
        // session row was committed first and occurrence regeneration ran un-transacted afterwards, so a
        // slot-key collision left the session's day pattern out of sync with its generated occurrences
        // (a 409 that had already persisted the change).
        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        // Set only when the price actually changed; rides back on the response so the app reports
        // what the re-pricing did instead of leaving the teacher to guess.
        Dtos.Payment.SessionRepriceSummary? priceReconcile = null;

        try
        {
            // 7. Apply updates
            session.SessionName = trimmedName;
            session.OccurrenceType = dto.OccurrenceType;
            session.SelectedDays = FormatSelectedDays(dto.SelectedDays);
            session.MonthlyDayOfMonth = newMonthlyDay;
            session.PaymentType = dto.PaymentType;
            session.SessionAmount = dto.SessionAmount;
            session.StartDate = dto.StartDate;
            session.EndDate = dto.EndDate;
            session.StartTime = dto.StartTime;
            session.DurationMinutes = dto.DurationMinutes;
            session.SessionGroupId = dto.SessionGroupId;

            await _unitOfWork.SessionsRepo.UpdateAsync(session);
            await _unitOfWork.SaveChangesAsync();

            // ── ATTENDANCE INTEGRATION: Rebuild occurrences to match the new pattern ──
            // RegenerateOccurrencesAsync reconciles (drops pure-placeholder occurrences, re-materializes
            // the new pattern) while preserving every occurrence that carries attendance/exam/payment
            // history — and never violates the slot-key unique index. Runs on THIS transaction.
            if (datesOrRecurrenceChanged)
                await _attendanceService.RegenerateOccurrencesAsync(teacherId, sessionId);

            // ── PAYMENT INTEGRATION: re-price the session's still-owed periods to the new amount
            // (session-default students only; custom-priced students keep their price — BR-PAY-003).
            // Monthly bills re-price from the teacher-local CURRENT month, per-class bills from next
            // month. Runs on THIS transaction so the price + re-pricing commit atomically. The summary
            // rides back on the response so the app can report the recalculation.
            if (amountChanged)
            {
                var repriceResult = await _paymentService
                    .OnSessionAmountChangedAsync(teacherId, sessionId, dto.SessionAmount);
                priceReconcile = repriceResult.Data;
            }

            // ── PAYMENT INTEGRATION: billing periods are generated ONCE, at assignment, and only as
            // far as the end date AT THAT TIME. Extending the window afterwards used to leave the
            // already-assigned students with NO obligation for the new months, so the session dropped
            // off the payment screens for those months and its students read as "paid" with nothing
            // due. Backfill the gap.
            //
            // Gated on an end-date change: the backfill walks every assigned student, so running it on
            // an unrelated edit (rename, price) would cost hundreds of queries for nothing. It is
            // idempotent and additive (only missing months, never past the end date) and it resumes
            // from each student's own latest period, so it ALSO repairs sessions whose window was
            // extended before this fix shipped — re-save the session with its end date changed.
            if (endDateChanged)
                await _paymentService.BackfillSessionPeriodsThroughEndDateAsync(teacherId, sessionId);

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }

        var resultDto = await BuildSessionDtoAsync(session);
        resultDto.PriceReconcile = priceReconcile;
        return Result<SessionDto>.Success(resultDto, _localizer, "SessionUpdatedSuccess");
    }

    /// <inheritdoc />
    public async Task<Result<SessionDeleteConfirmationDto>> GetDeleteConfirmationAsync(long teacherId, long sessionId)
    {
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<SessionDeleteConfirmationDto>.Failure(_localizer, "SessionNotFound", HttpStatusCode.NotFound);

        // REQ-SES-040: Student count
        int studentCount = await _unitOfWork.SessionsRepo.CountStudentsBySessionAsync(sessionId);

        // REQ-SES-047: Linked session names
        var linkedSessions = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(sessionId);

        var confirmation = new SessionDeleteConfirmationDto
        {
            SessionId = session.Id,
            SessionName = session.SessionName,
            AssignedStudentCount = studentCount,
            AffectedLinkedSessions = linkedSessions.Select(ls => new LinkedSessionInfo
            {
                Id = ls.Id,
                SessionName = ls.SessionName
            }).ToList()
        };

        return Result<SessionDeleteConfirmationDto>.Success(confirmation, _localizer, "Success");
    }

    /// <inheritdoc />
    public async Task<Result<bool>> DeleteSessionAsync(long teacherId, long sessionId)
    {
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<bool>.Failure(_localizer, "SessionNotFound", HttpStatusCode.NotFound);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            // ── ATTENDANCE INTEGRATION: Preserve attendance records before hard delete ──
            // BR-ATT-005: Attendance records must be retained even after session deletion.
            // This nullifies SessionOccurrenceId on AttendanceRecords (denormalized data preserved),
            // deactivates all StudentSessionAssignments for this session,
            // and deletes the SessionOccurrence rows (which are now unreferenced).
            await _attendanceService.OnSessionDeletingAsync(teacherId, sessionId);

            // ── PAYMENT INTEGRATION: apply the session-delete money lifecycle before hard delete ──
            // Per student: keep PAID/past periods as history, VOID future-unpaid, and collapse unpaid
            // arrears-through-current-month into ONE pending carry-forward debt per month (SessionId null,
            // IsCarriedForward) that follows the student into their next session (re-priced) on reassign.
            // SessionId is then nulled on the surviving PAID/history rows + transactions + departures, and
            // each affected counter is resynced. Denormalized SessionName preserved on all records.
            await _paymentService.OnSessionDeletingAsync(teacherId, sessionId);

            // ── VIDEO / ONLINE-EXAM SCOPE CLEANUP: remove targeting rules before hard delete ──
            // VideoScope, VideoUnitScope, and OnlineExamScope reference this session via a
            // NoAction FK, so any surviving row would block the delete with a 409
            // ("conflicts with existing data"). Unlike attendance/payment history, a scope
            // row is a live access/assignment rule with no meaning once its session is gone,
            // and its CHECK constraint forbids nulling the FK — so the rows are deleted.
            await _unitOfWork.VideoAssetsRepo.DeleteScopesBySessionAsync(sessionId);
            await _unitOfWork.VideoUnitsRepo.DeleteUnitScopesBySessionAsync(sessionId);
            await _unitOfWork.OnlineExamsRepo.DeleteScopesBySessionAsync(sessionId);

            await _unitOfWork.SaveChangesAsync();

            // REQ-SES-043: Remove all links where this session is on the Restrict side
            // (the NoAction side is handled by the DB, but the Restrict side needs manual cleanup)
            var linksAsTarget = await _unitOfWork.SessionsRepo.GetLinksBySessionAsync(sessionId);
            foreach (var link in linksAsTarget)
            {
                await _unitOfWork.SessionsRepo.DeleteLinkAsync(link);
            }

            // REQ-SES-041: Hard delete the session
            // REQ-SES-042: DB NoAction SetNull clears TeacherStudents.SessionId automatically
            await _unitOfWork.SessionsRepo.DeleteAsync(session);
            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            return Result<bool>.Success(true, _localizer, "SessionDeletedSuccess");
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<SessionDto>> DuplicateSessionAsync(long teacherId, long sessionId)
    {
        // 1. Load source session
        var source = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (source is null)
            return Result<SessionDto>.Failure(_localizer, "SessionNotFound", HttpStatusCode.NotFound);

        // 1b. Free-tier quota: duplication creates a new session, so it counts toward the cap.
        if (!await _subscriptionGate.CanCreateAsync(
                teacherId, ModuleQuotaKeys.Sessions,
                () => _unitOfWork.SessionsRepo.CountSessionsByTeacherAsync(teacherId)))
            return Result<SessionDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequired, HttpStatusCode.Forbidden);

        // 2. Generate a new name for the duplicate
        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);
        var language = config?.SessionNameLanguage ?? GenerationLanguage.English;
        string newName = await _nameGenerator.GenerateNextNameAsync(teacherId, language);

        // 3. REQ-SES-046: Copy configuration fields, blank start/end dates
        var teacherLocalToday = _timeZoneService.GetTeacherLocalDate(teacherId);
        var duplicate = new Session
        {
            TeacherId = teacherId,
            SessionName = newName,
            OccurrenceType = source.OccurrenceType,
            SelectedDays = source.SelectedDays,
            MonthlyDayOfMonth = source.MonthlyDayOfMonth,
            PaymentType = source.PaymentType,
            SessionAmount = source.SessionAmount,
            // REQ-SES-046: Blank start and end dates — use today as placeholder
            // The tutor is expected to set the actual dates after duplicating.
            // The tutor's today, not UTC's: between midnight and 2-3 AM Cairo the UTC date is
            // still yesterday, and the duplicate would open dated a day before it was made.
            StartDate = teacherLocalToday,
            EndDate = teacherLocalToday.AddMonths(1),
            StartTime = source.StartTime,
            DurationMinutes = source.DurationMinutes,
            SessionGroupId = source.SessionGroupId,
            CreateAt = DateTime.UtcNow
        };

        await _unitOfWork.SessionsRepo.AddAsync(duplicate);
        await _unitOfWork.SaveChangesAsync();

        // ── ATTENDANCE INTEGRATION: Generate occurrences for the duplicate ──
        await _attendanceService.GenerateOccurrencesAsync(teacherId, duplicate.Id);

        var dto = await BuildSessionDtoAsync(duplicate);
        return Result<SessionDto>.Success(dto, _localizer, "SessionDuplicatedSuccess", HttpStatusCode.Created);
    }

    // ══════════════════════════════════════════════
    // SESSION LIST (SEARCH + FILTER + PAGINATION)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<SessionDto>>>> GetSessionListAsync(
        long teacherId, SessionListRequest request)
    {
        // Validate teacher exists
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
            return Result<PaginatedResponse<List<SessionDto>>>.Failure(
                _localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        // Build the filtered, sorted query via repo
        var query = _unitOfWork.SessionsRepo.BuildSessionListQuery(
            teacherId,
            _timeZoneService.GetTeacherLocalDate(teacherId),
            request.Search,
            request.GroupId,
            request.OccurrenceType,
            request.ActiveOnly,
            request.ExpiredOnly,
            request.SortBy,
            request.SortDirection);

        // Get total count AFTER filters
        int totalCount = await _unitOfWork.SessionsRepo.CountAsync(query);

        // Get the paginated results
        var sessions = await _unitOfWork.SessionsRepo.GetPagedAsync(query, request.Page, request.PageSize);

        // Build DTOs with student counts and linked session info
        var dtos = new List<SessionDto>();
        foreach (var session in sessions)
        {
            var dto = await BuildSessionDtoAsync(session);
            dtos.Add(dto);
        }

        var response = new PaginatedResponse<List<SessionDto>>
        {
            totalCount = totalCount,
            page = request.Page,
            pageSize = request.PageSize,
            totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
            data = dtos
        };

        return Result<PaginatedResponse<List<SessionDto>>>.Success(response, _localizer, "Success");
    }

    // ══════════════════════════════════════════════
    // SESSION GROUPS
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<SessionGroupDto>> CreateGroupAsync(CreateSessionGroupDto dto)
    {
        // Validate teacher
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(dto.TeacherId);
        if (teacher is null)
            return Result<SessionGroupDto>.Failure(_localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        // Free-tier quota: session groups (default 0 → subscriber-only).
        if (!await _subscriptionGate.CanCreateAsync(
                dto.TeacherId, ModuleQuotaKeys.Groups,
                () => _unitOfWork.SessionsRepo.CountGroupsByTeacherAsync(dto.TeacherId)))
            return Result<SessionGroupDto>.Failure(
                _localizer, SubscriptionConstants.Messages.SubscriptionRequired, HttpStatusCode.Forbidden);

        // Validate unique group name
        string trimmedName = dto.GroupName.Trim();
        bool nameExists = await _unitOfWork.SessionsRepo.GroupNameExistsAsync(dto.TeacherId, trimmedName);
        if (nameExists)
            return Result<SessionGroupDto>.Failure(_localizer, "SessionGroupNameDuplicate", HttpStatusCode.Conflict);

        var group = new SessionGroup
        {
            TeacherId = dto.TeacherId,
            GroupName = trimmedName,
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            CreateAt = DateTime.UtcNow
        };

        await _unitOfWork.SessionsRepo.AddGroupAsync(group);
        await _unitOfWork.SaveChangesAsync();

        var resultDto = new SessionGroupDto
        {
            Id = group.Id,
            TeacherId = group.TeacherId,
            GroupName = group.GroupName,
            Description = group.Description,
            SessionCount = 0,
            CreatedAt = group.CreateAt
        };

        return Result<SessionGroupDto>.Success(resultDto, _localizer, "SessionGroupCreatedSuccess", HttpStatusCode.Created);
    }

    /// <inheritdoc />
    public async Task<Result<List<SessionGroupDto>>> GetGroupsAsync(long teacherId)
    {
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
            return Result<List<SessionGroupDto>>.Failure(_localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        var groups = await _unitOfWork.SessionsRepo.GetGroupsByTeacherAsync(teacherId);

        // Build DTOs with session counts
        var dtos = new List<SessionGroupDto>();
        foreach (var group in groups)
        {
            // Count sessions in each group via the repo's session list query
            var sessionQuery = _unitOfWork.SessionsRepo.BuildSessionListQuery(
                teacherId, _timeZoneService.GetTeacherLocalDate(teacherId), groupId: group.Id);
            int sessionCount = await _unitOfWork.SessionsRepo.CountAsync(sessionQuery);

            // Distinct active students across the group's sessions (a TeacherStudent has at
            // most one SessionId, so the target count is inherently deduplicated).
            int studentCount = await _unitOfWork.SessionsRepo.CountStudentsInTargetsAsync(
                teacherId, Array.Empty<long>(), new[] { group.Id });

            dtos.Add(new SessionGroupDto
            {
                Id = group.Id,
                TeacherId = group.TeacherId,
                GroupName = group.GroupName,
                Description = group.Description,
                SessionCount = sessionCount,
                StudentsNumber = studentCount,
                CreatedAt = group.CreateAt
            });
        }

        return Result<List<SessionGroupDto>>.Success(dtos, _localizer, "Success");
    }

    /// <inheritdoc />
    public async Task<Result<SessionGroupDto>> RenameGroupAsync(long teacherId, long groupId, RenameSessionGroupDto dto)
    {
        var group = await _unitOfWork.SessionsRepo.GetGroupByIdAndTeacherAsync(groupId, teacherId);
        if (group is null)
            return Result<SessionGroupDto>.Failure(_localizer, "SessionGroupNotFound", HttpStatusCode.NotFound);

        string trimmedName = dto.GroupName.Trim();
        bool nameExists = await _unitOfWork.SessionsRepo.GroupNameExistsExcludingAsync(teacherId, trimmedName, groupId);
        if (nameExists)
            return Result<SessionGroupDto>.Failure(_localizer, "SessionGroupNameDuplicate", HttpStatusCode.Conflict);

        group.GroupName = trimmedName;
        // Description is opt-in on rename: null leaves it unchanged; an empty/whitespace
        // string clears it; any other value updates it.
        if (dto.Description != null)
            group.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        await _unitOfWork.SessionsRepo.UpdateGroupAsync(group);
        await _unitOfWork.SaveChangesAsync();

        // Count sessions for DTO
        var sessionQuery = _unitOfWork.SessionsRepo.BuildSessionListQuery(
            teacherId, _timeZoneService.GetTeacherLocalDate(teacherId), groupId: groupId);
        int sessionCount = await _unitOfWork.SessionsRepo.CountAsync(sessionQuery);

        var resultDto = new SessionGroupDto
        {
            Id = group.Id,
            TeacherId = group.TeacherId,
            GroupName = group.GroupName,
            Description = group.Description,
            SessionCount = sessionCount,
            CreatedAt = group.CreateAt
        };

        return Result<SessionGroupDto>.Success(resultDto, _localizer, "SessionGroupRenamedSuccess");
    }

    /// <inheritdoc />
    public async Task<Result<bool>> DeleteGroupAsync(long teacherId, long groupId)
    {
        var group = await _unitOfWork.SessionsRepo.GetGroupByIdAndTeacherAsync(groupId, teacherId);
        if (group is null)
            return Result<bool>.Failure(_localizer, "SessionGroupNotFound", HttpStatusCode.NotFound);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            // ── VIDEO / ONLINE-EXAM SCOPE CLEANUP: remove group-targeting rules before delete ──
            // VideoScope, VideoUnitScope, and OnlineExamScope reference this group via a
            // NoAction FK (Session's own SessionGroupId FK is SetNull, but these scope tables
            // are not), so a surviving group-scope row would block the delete with a 409.
            // Scope rows are live rules with a CHECK constraint that forbids nulling the FK —
            // so the group-targeting rows are removed.
            await _unitOfWork.VideoAssetsRepo.DeleteScopesByGroupAsync(groupId);
            await _unitOfWork.VideoUnitsRepo.DeleteUnitScopesByGroupAsync(groupId);
            await _unitOfWork.OnlineExamsRepo.DeleteScopesByGroupAsync(groupId);

            // REQ-SES-031: Deleting group does NOT delete sessions.
            // Sessions become ungrouped via DB SetNull on the Session.SessionGroupId FK.
            await _unitOfWork.SessionsRepo.DeleteGroupAsync(group);
            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            return Result<bool>.Success(true, _localizer, "SessionGroupDeletedSuccess");
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    // ══════════════════════════════════════════════
    // SESSION LINKING (MEMBERSHIP)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<bool>> CreateLinkAsync(CreateSessionLinkDto dto)
    {
        // SES-4: [Required] is a no-op on a non-nullable long, so an omitted sessionIdA/sessionIdB binds
        // to 0. Reject the missing id up front with a field-named 400 instead of looking up session 0 and
        // returning a misleading 404 "Session not found".
        if (dto.SessionIdA <= 0 || dto.SessionIdB <= 0)
            return Result<bool>.Failure(_localizer, "SessionIdRequired", HttpStatusCode.BadRequest);

        // 1. Validate both sessions exist and belong to the teacher
        if (dto.SessionIdA == dto.SessionIdB)
            return Result<bool>.Failure(_localizer, "SessionLinkSameSession", HttpStatusCode.BadRequest);

        var sessionA = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(dto.SessionIdA, dto.TeacherId);
        if (sessionA is null)
            return Result<bool>.Failure(_localizer, "SessionNotFound", HttpStatusCode.NotFound);

        var sessionB = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(dto.SessionIdB, dto.TeacherId);
        if (sessionB is null)
            return Result<bool>.Failure(_localizer, "SessionNotFound", HttpStatusCode.NotFound);

        // 2. BR-SES-003: Sessions can be linked when they share the same occurrence
        //    type AND the same *number* of days per week — the specific days need not
        //    match, only the count (e.g. two Weekly sessions each running 2 days/week).
        if (sessionA.OccurrenceType != sessionB.OccurrenceType)
            return Result<bool>.Failure(_localizer, "SessionLinkOccurrenceMismatch", HttpStatusCode.BadRequest);

        static int CountSelectedDays(string? selectedDays) =>
            string.IsNullOrWhiteSpace(selectedDays)
                ? 0
                : selectedDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        if (CountSelectedDays(sessionA.SelectedDays) != CountSelectedDays(sessionB.SelectedDays))
            return Result<bool>.Failure(_localizer, "SessionLinkDaysMismatch", HttpStatusCode.BadRequest);

        // 3. Check link doesn't already exist
        bool linkExists = await _unitOfWork.SessionsRepo.LinkExistsAsync(dto.SessionIdA, dto.SessionIdB);
        if (linkExists)
            return Result<bool>.Failure(_localizer, "SessionLinkAlreadyExists", HttpStatusCode.Conflict);

        // 4. Create the link with canonical ordering (lower Id first)
        long lower = Math.Min(dto.SessionIdA, dto.SessionIdB);
        long upper = Math.Max(dto.SessionIdA, dto.SessionIdB);

        var link = new SessionLink
        {
            SessionId = lower,
            LinkedSessionId = upper,
            CreateAt = DateTime.UtcNow
        };

        await _unitOfWork.SessionsRepo.AddLinkAsync(link);
        await _unitOfWork.SaveChangesAsync();

        return Result<bool>.Success(true, _localizer, "SessionLinkCreatedSuccess", HttpStatusCode.Created);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> RemoveLinkAsync(long teacherId, long sessionIdA, long sessionIdB)
    {
        // Validate at least one session belongs to the teacher (security check)
        var sessionA = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionIdA, teacherId);
        if (sessionA is null)
            return Result<bool>.Failure(_localizer, "SessionNotFound", HttpStatusCode.NotFound);

        // Find the link
        var link = await _unitOfWork.SessionsRepo.GetLinkAsync(sessionIdA, sessionIdB);
        if (link is null)
            return Result<bool>.Failure(_localizer, "SessionLinkNotFound", HttpStatusCode.NotFound);

        // REQ-SES-037: Remove link — does not affect sessions or student assignments
        await _unitOfWork.SessionsRepo.DeleteLinkAsync(link);
        await _unitOfWork.SaveChangesAsync();

        return Result<bool>.Success(true, _localizer, "SessionLinkRemovedSuccess");
    }

    // ══════════════════════════════════════════════
    // STUDENT ASSIGNMENT
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<AssignStudentsResultDto>> AssignStudentsAsync(AssignStudentsToSessionDto dto)
    {
        // SES-4: reject an omitted/zero sessionId (a non-nullable long defaults to 0) with a clear 400
        // instead of looking up session 0 and returning a misleading 404 "Session not found".
        if (dto.SessionId <= 0)
            return Result<AssignStudentsResultDto>.Failure(_localizer, "SessionIdRequired", HttpStatusCode.BadRequest);

        // 1. Validate session exists and belongs to teacher
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(dto.SessionId, dto.TeacherId);
        if (session is null)
            return Result<AssignStudentsResultDto>.Failure(_localizer, "SessionNotFound", HttpStatusCode.NotFound);

        // 2. Load all requested students
        var students = await _unitOfWork.Students.GetActiveByIdsAndTeacherAsync(dto.TeacherId, dto.StudentIds);
        if (students.Count == 0)
            return Result<AssignStudentsResultDto>.Failure(_localizer, "NoValidStudentsFound", HttpStatusCode.BadRequest);

        var result = new AssignStudentsResultDto();
        int assignedCount = 0;

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            foreach (var student in students)
            {
                // REQ-SES-018: Check if student is already assigned to another session
                if (student.SessionId.HasValue && student.SessionId.Value != dto.SessionId)
                {
                    // Load the current session to get its name for the warning
                    var currentSession = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(student.SessionId.Value, dto.TeacherId);
                    string currentSessionName = currentSession?.SessionName ?? "Unknown";

                    result.Warnings.Add(new StudentReassignmentWarning
                    {
                        StudentId = student.Id,
                        StudentName = student.StudentName,
                        StudentCode = student.StudentCode,
                        CurrentSessionId = student.SessionId.Value,
                        CurrentSessionName = currentSessionName,
                        NewSessionId = dto.SessionId,
                        NewSessionName = session.SessionName
                    });
                }
                else if (student.SessionId.HasValue && student.SessionId.Value == dto.SessionId)
                {
                    // SES-2: the student is already assigned to THIS exact session → idempotent no-op.
                    // Re-running the attendance/payment materialization hooks would regenerate downstream
                    // periods for a student that never moved, so skip them and do not count the student.
                    continue;
                }
                else
                {
                    // Assign directly — student has no current session
                    student.SessionId = dto.SessionId;
                    await _unitOfWork.Students.UpdateAsync(student);

                    // ── ATTENDANCE INTEGRATION: Create assignment record + init counter ──
                    // REQ-ATT-019: Attendance timeline starts from this assignment date.
                    // Creates StudentSessionAssignment and initializes StudentAbsenceCounter.
                    await _attendanceService.OnStudentAssignedToSessionAsync(
                        dto.TeacherId, student.Id, dto.SessionId, session.SessionName);

                    // ── PAYMENT INTEGRATION: Generate payment periods + init payment counter ──
                    // REQ-PAY-018/019: Payment periods created from assignment date.
                    // REQ-PAY-016: StudentPaymentCounter initialized for custom amount tracking.
                    await _paymentService.OnStudentAssignedToSessionAsync(
                        dto.TeacherId, student.Id, dto.SessionId, session.SessionName, DateTime.UtcNow);

                    assignedCount++;
                }
            }

            if (assignedCount > 0)
            {
                await _unitOfWork.SaveChangesAsync();
            }

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            result.AssignedCount = assignedCount;
            return Result<AssignStudentsResultDto>.Success(result, _localizer, "StudentsAssignedSuccess");
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<int>> ConfirmReassignStudentsAsync(long teacherId, long sessionId, List<long> studentIds)
    {
        // Validate session
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<int>.Failure(_localizer, "SessionNotFound", HttpStatusCode.NotFound);

        // Load students
        var students = await _unitOfWork.Students.GetActiveByIdsAndTeacherAsync(teacherId, studentIds);
        if (students.Count == 0)
            return Result<int>.Failure(_localizer, "NoValidStudentsFound", HttpStatusCode.BadRequest);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            // REQ-SES-019: Override previous session assignment
            foreach (var student in students)
            {
                // Capture the CURRENT session BEFORE repointing. A confirm-reassign is a genuine
                // move (A → B), so the billing must be CARRIED OVER (unpaid arrears + current month
                // move to B re-priced to B's amount, paid months stay in A as history, a partial is
                // split, unpaid future in A is cancelled) — the SAME OnStudentMovedBetweenSessionsAsync
                // path the student-edit move uses. The old unassign(A)+assign(B) pair STRANDED past
                // arrears in A and re-prorated B's first month as if it were a brand-new enrollment
                // (proration is only for genuinely new students — §7.4). Attendance is unchanged.
                long? previousSessionId = student.SessionId;

                // ── ATTENDANCE INTEGRATION: Close old assignment period ──
                // REQ-ATT-020: Preserve complete attendance history under previous session.
                // Sets UnassignedAt on the current active StudentSessionAssignment.
                await _attendanceService.OnStudentUnassignedFromSessionAsync(teacherId, student.Id);

                // Update the student's current session pointer
                student.SessionId = sessionId;
                await _unitOfWork.Students.UpdateAsync(student);

                // ── ATTENDANCE INTEGRATION: Open new assignment period ──
                // REQ-ATT-045: New attendance history starts from reassignment date.
                // REQ-ATT-048: Even if re-assigned to a previously attended session,
                //              this creates a NEW assignment period.
                await _attendanceService.OnStudentAssignedToSessionAsync(
                    teacherId, student.Id, sessionId, session.SessionName);

                // ── PAYMENT INTEGRATION: carry the billing over (A → B) or, for a student who had no
                // prior session, generate the new session's periods. REQ-PAY-085/086.
                if (previousSessionId is not null && previousSessionId.Value != sessionId)
                {
                    var prevSession = await _unitOfWork.SessionsRepo
                        .GetByIdAndTeacherAsync(previousSessionId.Value, teacherId);
                    await _paymentService.OnStudentMovedBetweenSessionsAsync(
                        teacherId, student.Id,
                        previousSessionId.Value, prevSession?.SessionName ?? string.Empty,
                        sessionId, session.SessionName, DateTime.UtcNow);
                }
                else
                {
                    await _paymentService.OnStudentAssignedToSessionAsync(
                        teacherId, student.Id, sessionId, session.SessionName, DateTime.UtcNow);
                }
            }

            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            return Result<int>.Success(students.Count, _localizer, "StudentsReassignedSuccess");
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> UnassignStudentAsync(long teacherId, long sessionId, long studentId)
    {
        var student = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(studentId, teacherId);
        if (student is null)
            return Result<bool>.Failure(_localizer, "StudentNotFound", HttpStatusCode.NotFound);

        if (student.SessionId != sessionId)
            return Result<bool>.Failure(_localizer, "StudentNotInSession", HttpStatusCode.BadRequest);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            // ── ATTENDANCE INTEGRATION: Close the active assignment period ──
            // REQ-ATT-020: Preserve complete attendance history under this session.
            await _attendanceService.OnStudentUnassignedFromSessionAsync(teacherId, studentId);

            // ── PAYMENT INTEGRATION: Preserve payment history ──
            await _paymentService.OnStudentUnassignedFromSessionAsync(teacherId, studentId);

            student.SessionId = null;
            await _unitOfWork.Students.UpdateAsync(student);
            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            return Result<bool>.Success(true, _localizer, "StudentUnassignedSuccess");
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    // ══════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════

    /// <summary>
    /// Validates the occurrence type configuration (selected days for Weekly/BiWeekly,
    /// monthly day for Monthly). Returns the stable message KEY of the first violation, or null if valid.
    /// SES-5: returning the key (not a pre-localized string) lets the caller use the
    /// <c>Result.Failure(localizer, key)</c> overload so both <c>Code</c> and <c>Message</c> are populated
    /// (the string overload leaves <c>Code</c> null).
    /// REQ-SES-007/008: Occurrence type rules.
    /// </summary>
    private static string? ValidateOccurrenceConfiguration(
        OccurrenceType occurrenceType,
        List<int>? selectedDays,
        byte? monthlyDayOfMonth)
    {
        switch (occurrenceType)
        {
            case OccurrenceType.Weekly:
            case OccurrenceType.BiWeekly:
                // REQ-SES-008: At least one day required, max 7
                if (selectedDays is null || selectedDays.Count == 0)
                    return "SessionSelectedDaysRequired";

                if (selectedDays.Count > MaxSelectedDays)
                    return "SessionSelectedDaysTooMany";

                // Validate each day index is 0-6
                if (selectedDays.Any(d => !ValidDayIndices.Contains(d)))
                    return "SessionSelectedDaysInvalid";

                // Check for duplicate day indices
                if (selectedDays.Distinct().Count() != selectedDays.Count)
                    return "SessionSelectedDaysDuplicate";

                break;

            case OccurrenceType.Monthly:
                // Monthly requires a day-of-month
                if (!monthlyDayOfMonth.HasValue || monthlyDayOfMonth.Value < 1 || monthlyDayOfMonth.Value > 31)
                    return "SessionMonthlyDayRequired";

                break;

            default:
                return "SessionOccurrenceTypeInvalid";
        }

        return null;
    }

    /// <summary>
    /// Formats a list of day indices into a comma-separated string for database storage.
    /// Returns null if the list is null or empty (for Monthly occurrence type).
    /// Sorts the indices to ensure consistent storage and comparison for BR-SES-003.
    /// </summary>
    private static string? FormatSelectedDays(List<int>? days)
    {
        if (days is null || days.Count == 0)
            return null;

        // Sort for consistent storage — critical for BR-SES-003 membership matching
        return string.Join(",", days.OrderBy(d => d));
    }

    /// <summary>
    /// Parses a comma-separated day string back into a list of integers.
    /// Returns null if the input is null or empty.
    /// </summary>
    private static List<int>? ParseSelectedDays(string? daysString)
    {
        if (string.IsNullOrWhiteSpace(daysString))
            return null;

        return daysString.Split(',')
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
            .ToList();
    }

    /// <summary>
    /// Builds a complete SessionDto from a Session entity, including:
    /// - Student count (REQ-SES-NFR-009)
    /// - Group name (for display)
    /// - Linked sessions (REQ-SES-034)
    /// - Expired status (REQ-SES-015)
    /// </summary>
    private async Task<SessionDto> BuildSessionDtoAsync(Session session)
    {
        int studentCount = await _unitOfWork.SessionsRepo.CountStudentsBySessionAsync(session.Id);
        var linkedSessions = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(session.Id);

        // Load group name if grouped
        string? groupName = null;
        if (session.SessionGroupId.HasValue)
        {
            var group = await _unitOfWork.SessionsRepo.GetGroupByIdAndTeacherAsync(
                session.SessionGroupId.Value, session.TeacherId);
            groupName = group?.GroupName;
        }

        return new SessionDto
        {
            Id = session.Id,
            TeacherId = session.TeacherId,
            SessionName = session.SessionName,
            OccurrenceType = session.OccurrenceType,
            SelectedDays = ParseSelectedDays(session.SelectedDays),
            MonthlyDayOfMonth = session.MonthlyDayOfMonth,
            PaymentType = session.PaymentType,
            SessionAmount = session.SessionAmount,
            StartDate = session.StartDate,
            EndDate = session.EndDate,
            StartTime = session.StartTime,
            DurationMinutes = session.DurationMinutes,
            SessionGroupId = session.SessionGroupId,
            SessionGroupName = groupName,
            StudentCount = studentCount,
            // EndDate is a calendar day, so it has to be compared with the TEACHER's today:
            // UtcNow.Date is still yesterday between midnight and 2-3 AM Cairo, which flipped
            // the Expired chip on a session for the first hours of the day it actually ended.
            IsExpired = session.EndDate < _timeZoneService.GetTeacherLocalDate(session.TeacherId),
            LinkedSessions = linkedSessions.Select(ls => new LinkedSessionInfo
            {
                Id = ls.Id,
                SessionName = ls.SessionName
            }).ToList(),
            CreatedAt = session.CreateAt
        };
    }
    /// <inheritdoc />
    public async Task<Result<List<SessionLookupItemDto>>> GetSessionLookupAsync(long teacherId)
    {
        // Validate teacher exists — same guard as every other teacher-scoped read in this service.
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
            return Result<List<SessionLookupItemDto>>.Failure(_localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        // Reuses the existing repo projection — already used internally by GetSessionAssignmentChipsAsync.
        var sessions = await _unitOfWork.SessionsRepo.GetTeacherSessionNamesAsync(teacherId);

        var dto = sessions
            .Select(s => new SessionLookupItemDto { Id = s.Id, SessionName = s.SessionName })
            .ToList();

        return Result<List<SessionLookupItemDto>>.Success(dto, _localizer);
    }
}