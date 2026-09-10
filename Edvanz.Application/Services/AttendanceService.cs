using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Attendance;
using Edvanz.Application.Extensions;
using Edvanz.Application.IservicesContract;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements all Attendance Module (Module 3) operations.
///
/// CHANGES FROM ORIGINAL — ALL STEPS CONSOLIDATED:
/// Step 1.1: OnStudentPermanentlyDeletedAsync now nullifies FK references on AttendanceRecords.
/// Step 2.1: BulkMarkAttendanceAsync includes cross-session duplicate detection.
/// Step 2.3: MarkAttendanceAsync/BulkMarkAttendanceAsync/AddAttendanceRecordAsync validate status.
/// Step 3.1: HoldStudentAsync and ReleaseHoldAsync fully implemented.
/// Step 3.2: ExportReportAsync and ExportTimelineAsync fully implemented.
/// Step 3.3: SyncOfflineRecordsAsync fully implemented.
/// Step 4.1: BulkMarkAttendanceAsync uses in-memory counter mutation + batch save.
/// Step 4.2: Cross-session date remapping handles null next occurrence explicitly.
/// Step 4.3: MarkAttendanceAsync returns redirect hint when no occurrence for today.
/// Step 7.2: All record creation populates denormalized StudentName/StudentCode.
/// Step 8.1: Uses AttendanceConstants.Messages for localization keys.
///
/// AUDIT FIX CHANGES:
/// - Step 1: OnStudentAssignedToSessionAsync populates denormalized StudentName/StudentCode.
/// - Step 2: BR-ATT-001 retroactive attendance guard in MarkAttendanceAsync/AddAttendanceRecordAsync.
/// - Step 3: BulkMarkAttendanceAsync fires messaging notifications for absent students.
/// - Step 4: BulkMarkAttendanceAsync validates student assignment exists before creating record.
/// - Step 5: SyncOfflineRecordsAsync no longer auto-confirms absence alerts.
/// - Step 6: MarkAttendanceAsync adds remapped-date duplicate check for cross-session.
/// - Step 7: MarkAttendanceResultDto populated with AssignedSessionId/name for cross-session warning.
/// - Step 8: ReleaseHoldAsync guards against deleted session while student on hold.
/// - Step 9: Counter updates wrapped with DbUpdateConcurrencyException retry.
/// - Step 11: RecordedByUserId defaults to TeacherId when not provided.
/// - Step 12: GetAbsenceOverviewAsync uses OccurrenceDate for date-specific absence view.
/// - Step 14: OnStudentAssignedToSessionAsync deactivates existing assignment first (idempotent).
/// - Step 15: DeleteAttendanceRecordAsync logs deletion before removing record.
/// - Step 17: GetUnmarkedCountAsync added for REQ-ATT-055 confirmation prompt.
/// - Step 19: MarkAttendanceAsync/HoldStudentAsync distinguish soft-deleted students.
///
/// TRANSACTION SAFETY: All transactional methods use the ownsTransaction pattern.
/// </summary>
public class AttendanceService : IAttendanceService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOccurrenceGeneratorService _occurrenceGenerator;
    private readonly IAttendanceReportExportService _exportService;
    private readonly IAttendanceNotifier _attendanceNotifier;      // ← Phase 3
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;
    private readonly ITimeZoneService _timeZoneService;
    private readonly ILogger<AttendanceService> _logger;
    private readonly IExamAttendanceSyncService _examAttendanceSync; // ← Exams: during-session sync

    public AttendanceService(
        IUnitOfWork unitOfWork,
        IOccurrenceGeneratorService occurrenceGenerator,
        IAttendanceReportExportService exportService,
        IAttendanceNotifier attendanceNotifier,                    // ← Phase 3
        IStringLocalizer<Domain.Resources.Messages> localizer,
        ITimeZoneService timeZoneService,
        ILogger<AttendanceService> logger,
        IExamAttendanceSyncService examAttendanceSync)
    {
        _unitOfWork = unitOfWork;
        _occurrenceGenerator = occurrenceGenerator;
        _exportService = exportService;
        _attendanceNotifier = attendanceNotifier;                  // ← Phase 3
        _localizer = localizer;
        _timeZoneService = timeZoneService;
        _logger = logger;
        _examAttendanceSync = examAttendanceSync;
    }

    // NOTE (rev 2, 2026-09-03): the first-attendance proration re-anchor hook was DELETED with the
    // enrollment-anchored redesign — attendance never re-prices the joining month. Do not reintroduce.

    // ══════════════════════════════════════════════
    // SESSION OCCURRENCE MANAGEMENT
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<int>> GenerateOccurrencesAsync(long teacherId, long sessionId)
    {
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<int>.Failure(_localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        // Compute each occurrence WITH its cross-session equivalence slot key (WeekStartDate +
        // DayPositionIndex). Additive: only insert dates not already materialized. The slot key is a
        // pure function of the date (+ SelectedDays), so existing rows never need renumbering.
        var keys = _occurrenceGenerator.ComputeOccurrenceKeys(session);
        var existingDates = await _unitOfWork.AttendanceRepo.GetExistingOccurrenceDatesAsync(sessionId);

        var newOccurrences = keys
            .Where(k => !existingDates.Contains(k.Date))
            .Select(k => new SessionOccurrence
            {
                TeacherId = teacherId,
                SessionId = sessionId,
                OccurrenceDate = k.Date,
                WeekStartDate = k.WeekStartDate,
                DayPositionIndex = k.DayPositionIndex,
                Status = OccurrenceStatus.Pending,
                CreateAt = DateTime.UtcNow
            })
            .ToList();

        if (newOccurrences.Count > 0)
        {
            await _unitOfWork.AttendanceRepo.AddOccurrencesRangeAsync(newOccurrences);
            await _unitOfWork.SaveChangesAsync();
        }

        return Result<int>.Success(newOccurrences.Count, _localizer, AttendanceConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<int>> RegenerateOccurrencesAsync(long teacherId, long sessionId)
    {
        // The session has already been mutated to the new recurrence by the caller (SessionService) —
        // read it back so ComputeOccurrenceKeys reflects the new pattern.
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<int>.Failure(_localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        var keys = _occurrenceGenerator.ComputeOccurrenceKeys(session);
        var existing = await _unitOfWork.AttendanceRepo.GetOccurrencesBySessionAsync(sessionId);

        // Fresh session (create path also flows here when nothing exists yet): plain additive insert.
        if (existing.Count == 0)
        {
            var fresh = keys.Select(k => BuildOccurrence(teacherId, sessionId, k)).ToList();
            if (fresh.Count > 0)
            {
                await _unitOfWork.AttendanceRepo.AddOccurrencesRangeAsync(fresh);
                await _unitOfWork.SaveChangesAsync();
            }
            return Result<int>.Success(fresh.Count, _localizer, AttendanceConstants.Messages.Success);
        }

        var existingIds = existing.Select(o => o.Id).ToList();

        // "Protected" = occurrences that carry real history and must survive a reschedule. Deleting them
        // would SET NULL the referencing row (attendance record / during-session exam anchor / per-session
        // payment), corrupting those modules — so they are kept exactly as-is (including their slot keys).
        var protectedIds = new HashSet<long>(
            (await _unitOfWork.AttendanceRepo.CountRecordsByOccurrenceBatchAsync(existingIds)).Keys);
        protectedIds.UnionWith(await _unitOfWork.ExamHomeworkRepo.GetReferencedSessionOccurrenceIdsAsync(existingIds));
        protectedIds.UnionWith(await _unitOfWork.PaymentsRepo.GetReferencedSessionOccurrenceIdsAsync(existingIds));

        // Phase 1 — drop every pure-placeholder occurrence so the schedule can be rebuilt from the new
        // pattern without the old (WeekStartDate, DayPositionIndex) slots colliding with the new ones.
        var deletableIds = existingIds.Where(id => !protectedIds.Contains(id)).ToList();
        if (deletableIds.Count > 0)
            await _unitOfWork.AttendanceRepo.DeleteOccurrencesByIdsAsync(deletableIds);

        // Phase 2 — re-materialize the new pattern, skipping any date or slot still held by a surviving
        // protected occurrence (recorded history keeps its slot; the unique index is never violated).
        var survivors = existing.Where(o => protectedIds.Contains(o.Id)).ToList();
        var occupiedDates = new HashSet<DateTime>(survivors.Select(o => o.OccurrenceDate));
        var occupiedSlots = new HashSet<(DateTime WeekStartDate, int DayPositionIndex)>(
            survivors.Select(o => (o.WeekStartDate, o.DayPositionIndex)));

        var toInsert = keys
            .Where(k => !occupiedDates.Contains(k.Date)
                     && !occupiedSlots.Contains((k.WeekStartDate, k.DayPositionIndex)))
            .Select(k => BuildOccurrence(teacherId, sessionId, k))
            .ToList();

        if (toInsert.Count > 0)
            await _unitOfWork.AttendanceRepo.AddOccurrencesRangeAsync(toInsert);

        // Single SaveChanges flushes the Phase-2 inserts (Phase-1 executed as a set-based DELETE already);
        // both run on the caller's transaction, so the whole rebuild commits or rolls back atomically.
        await _unitOfWork.SaveChangesAsync();

        return Result<int>.Success(toInsert.Count, _localizer, AttendanceConstants.Messages.Success);
    }

    /// <summary>
    /// Builds a Pending <see cref="SessionOccurrence"/> from a computed slot key. Shared by the
    /// generate and regenerate paths so the row shape stays identical.
    /// </summary>
    private static SessionOccurrence BuildOccurrence(
        long teacherId, long sessionId, (DateTime Date, DateTime WeekStartDate, int DayPositionIndex) key)
        => new SessionOccurrence
        {
            TeacherId = teacherId,
            SessionId = sessionId,
            OccurrenceDate = key.Date,
            WeekStartDate = key.WeekStartDate,
            DayPositionIndex = key.DayPositionIndex,
            Status = OccurrenceStatus.Pending,
            CreateAt = DateTime.UtcNow
        };

    // ══════════════════════════════════════════════
    // ATTENDANCE DASHBOARD
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<AttendanceDashboardDto>> GetDashboardAsync(
        long teacherId, AttendanceDashboardRequest request)
    {
        var date = request.Date?.Date ?? _timeZoneService.GetTeacherLocalDate(teacherId);

        var todayOccurrences = await _unitOfWork.AttendanceRepo
            .GetOccurrencesByTeacherAndDateAsync(teacherId, date);

        // FIX C1 (REQ-ATT-050): Batch-load MarkedCount and TotalStudents for all today's occurrences.
        // Previously hardcoded to 0 which violated REQ-ATT-050 (live counter "18 / 34").
        var occurrenceIds = todayOccurrences.Select(o => o.Id).ToList();
        var recordCounts = occurrenceIds.Count > 0
            ? await _unitOfWork.AttendanceRepo.CountRecordsByOccurrenceBatchAsync(occurrenceIds)
            : new Dictionary<long, int>();

        var sessionIds = todayOccurrences.Select(o => o.SessionId).Distinct().ToList();
        var studentCounts = sessionIds.Count > 0
            ? await _unitOfWork.AttendanceRepo.CountActiveAssignmentsBySessionBatchAsync(sessionIds)
            : new Dictionary<long, int>();

        var sessionCards = new List<AttendanceSessionCardDto>();
        foreach (var o in todayOccurrences)
        {
            var sess = o.Session;
            recordCounts.TryGetValue(o.Id, out int markedCount);
            studentCounts.TryGetValue(o.SessionId, out int totalStudents);

            sessionCards.Add(new AttendanceSessionCardDto
            {
                SessionId = o.SessionId,
                SessionName = sess?.SessionName ?? "Unknown",
                SessionGroupId = sess?.SessionGroupId,
                IsToday = true,
                TodayOccurrenceId = o.Id,
                Status = o.Status,
                MarkedCount = markedCount,
                TotalStudents = totalStudents,
                StartTime = sess?.StartTime ?? TimeSpan.Zero
            });
        }

        var dashboard = new AttendanceDashboardDto
        {
            Date = date,
            TotalSessionsToday = todayOccurrences.Count,
            CompletedSessions = todayOccurrences.Count(o => o.Status == OccurrenceStatus.Completed),
            PendingSessions = todayOccurrences.Count(o => o.Status == OccurrenceStatus.Pending),
            InProgressSessions = todayOccurrences.Count(o => o.Status == OccurrenceStatus.InProgress),
            SessionCards = sessionCards
        };

        // Exams: flag during-session exam sessions and list separate-time exams due today.
        // Best-effort — exam enrichment must never break the attendance dashboard.
        try
        {
            var examOccurrences = await _unitOfWork.ExamHomeworkRepo
                .GetExamOccurrencesForDateAsync(teacherId, date);
            if (examOccurrences.Count > 0)
            {
                var duringBySessionOcc = examOccurrences
                    .Where(e => e.SessionOccurrenceId.HasValue)
                    .GroupBy(e => e.SessionOccurrenceId!.Value)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var card in sessionCards)
                {
                    if (card.TodayOccurrenceId.HasValue
                        && duringBySessionOcc.TryGetValue(card.TodayOccurrenceId.Value, out var exam))
                    {
                        card.IsExamSession = true;
                        card.ExamId = exam.TemplateId;
                        card.ExamOccurrenceId = exam.OccurrenceId;
                        card.ExamName = exam.ExamName;
                    }
                }

                var separate = examOccurrences.Where(e => !e.SessionOccurrenceId.HasValue).ToList();
                if (separate.Count > 0)
                {
                    var summaries = await _unitOfWork.ExamHomeworkRepo
                        .GetCompletionSummariesByOccurrenceIdsAsync(separate.Select(e => e.OccurrenceId));
                    dashboard.ExamsToday = separate.Select(e =>
                    {
                        var s = summaries.GetValueOrDefault(e.OccurrenceId);
                        return new TodayExamCardDto
                        {
                            ExamId = e.TemplateId,
                            OccurrenceId = e.OccurrenceId,
                            Name = e.ExamName,
                            SessionId = e.SessionId,
                            SessionName = e.SessionName,
                            AssignedCount = s?.TotalStudents ?? 0,
                            AttendedCount = s?.DoneOrAttended ?? 0,
                            MissedCount = s?.NotDoneOrAbsent ?? 0,
                        };
                    }).ToList();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enrich attendance dashboard with exam data for teacher {TeacherId}", teacherId);
        }

        return Result<AttendanceDashboardDto>.Success(dashboard, _localizer, AttendanceConstants.Messages.Success);
    }

    // ══════════════════════════════════════════════
    // TAKE ATTENDANCE
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<AttendanceStudentListDto>> GetAttendanceStudentListAsync(
          long teacherId, long sessionId, DateTime? occurrenceDate,
          AttendanceStudentListRequest request)
    {
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<AttendanceStudentListDto>.Failure(
                _localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        var date = occurrenceDate?.Date ?? _timeZoneService.GetTeacherLocalDate(teacherId);

        var linkedSessions = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(sessionId);
        var linkedSessionIds = linkedSessions.Select(s => s.Id).ToList();

        var (items, totalCount, assignedCount, notAssignedCount, holdCount) =
            await _unitOfWork.AttendanceRepo.GetPagedAttendanceStudentListAsync(
                teacherId, sessionId, date, linkedSessionIds,
                request.Search, request.UnmarkedOnly,
                request.Page, request.PageSize);

        // Optional per-teacher enrichment (ShowPaymentInfoOnAttendanceScreen /
        // ShowAttendanceHistoryOnAttendanceScreen).
        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);
        bool showPaymentInfo = config?.ShowPaymentInfoOnAttendanceScreen ?? true;
        bool showAttendanceHistory = config?.ShowAttendanceHistoryOnAttendanceScreen ?? true;

        var teacherStudentIds = items.Select(r => r.TeacherStudentId).ToList();

        Dictionary<long, AttendanceScreenPaymentInfoRow>? paymentInfoMap = null;
        if (showPaymentInfo && teacherStudentIds.Count > 0)
        {
            var today = _timeZoneService.GetTeacherLocalDate(teacherId);
            var currentMonthStart = new DateTime(today.Year, today.Month, 1);
            var currentMonthEnd = currentMonthStart.AddMonths(1).AddDays(-1);
            var lastMonthStart = currentMonthStart.AddMonths(-1);
            var lastMonthEnd = currentMonthStart.AddDays(-1);

            paymentInfoMap = await _unitOfWork.PaymentsRepo.GetPaymentInfoForAttendanceBatchAsync(
                teacherId, teacherStudentIds, currentMonthEnd, lastMonthStart, lastMonthEnd);
        }
      

        var dtos = items.Select(row =>
        {
            var dto = new AttendanceStudentRowDto
            {
                TeacherStudentId = row.TeacherStudentId,
                StudentName = row.StudentName,
                StudentCode = row.StudentCode,
                Barcode = row.StudentCode,
                CurrentStatus = row.CurrentStatus,
                IsMarked = row.IsMarked,
                IsHeld = row.CurrentStatus == AttendanceStatus.Held,
                IsCrossSessionStudent = row.IsFromLinkedSession,
                SourceSessionName = row.SourceSessionName,
                ConsecutiveAbsences = row.ConsecutiveAbsences,
                TotalAbsences = row.TotalAbsences,
                WasAbsentLastSession = row.ConsecutiveAbsences > 0,
                LastAbsenceDate = row.LastAbsenceDate,
                LastAbsenceSessionName = row.LastAbsenceSessionName
            };

            if (showPaymentInfo)
            {
                var pay = paymentInfoMap != null && paymentInfoMap.TryGetValue(row.TeacherStudentId, out var p)
                    ? p : null;
                dto.PaymentInfo = new StudentPaymentInfoDto
                {
                    HasUnpaidLastMonth = pay?.HasUnpaidLastMonth ?? false,
                    HasUnpaidCurrentMonth = pay?.HasUnpaidCurrentMonth ?? false,
                    UnpaidMonthsCount = pay?.UnpaidMonthsCount ?? 0,
                    UnpaidAmount = pay?.UnpaidAmount ?? 0m,
                    UnpaidMonthLabels = pay?.UnpaidPeriods
                        .Select(PaymentLabelFormatter.FormatUnpaidPeriodLabel).ToList()
                        ?? new List<string>()
                };
            }
            int absenceThreshold = config?.ConsecutiveAbsenceThreshold ?? 3;

            if (showAttendanceHistory)
            {
                dto.HistoryInfo = new StudentAttendanceHistoryInfoDto
                {
                    WasAbsentLastSession = row.ConsecutiveAbsences > 0,
                    ConsecutiveAbsences = row.ConsecutiveAbsences,
                    TotalAbsences = row.TotalAbsences,
                    AbsenceConsequenceLabel = BuildAbsenceConsequenceLabel(row.ConsecutiveAbsences, absenceThreshold),
                    LastAbsenceDate = row.LastAbsenceDate,
                    LastAbsenceSessionName = row.LastAbsenceSessionName
                };
            }

            return dto;
        }).ToList();

        var response = new AttendanceStudentListDto
        {
            totalCount = totalCount,
            page = request.Page,
            pageSize = request.PageSize,
            totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
            data = dtos,
            AssignedCount = assignedCount,
            NotAssignedCount = notAssignedCount,
            HoldCount = holdCount,
            ShowAttendanceHistory = showAttendanceHistory,
            ShowPaymentInfo = showPaymentInfo
        };

        return Result<AttendanceStudentListDto>.Success(
            response, _localizer, AttendanceConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<SessionMonthAttendanceDto>> GetSessionMonthAttendanceAsync(
        long teacherId, long sessionId, SessionMonthAttendanceRequest request)
    {
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<SessionMonthAttendanceDto>.Failure(
                _localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        var monthStart = new DateTime(request.Year, request.Month, 1);
        var monthEndExclusive = monthStart.AddMonths(1);

        var (occurrences, occurrenceTotal) = await _unitOfWork.AttendanceRepo
            .GetPagedSessionMonthOccurrencesAsync(
                sessionId, monthStart, monthEndExclusive,
                request.OccurrencePage, request.OccurrencePageSize);

        var (roster, rosterTotal) = await _unitOfWork.AttendanceRepo
            .GetPagedSessionMonthRosterAsync(
                teacherId, sessionId, monthStart, monthEndExclusive,
                request.Search, request.Page, request.PageSize);

        var occurrenceIds = occurrences.Select(o => o.OccurrenceId).ToList();
        var studentIds = roster.Select(r => r.TeacherStudentId).ToList();

        // Cells for exactly this (student page × occurrence page); missing pair = unmarked.
        var cells = await _unitOfWork.AttendanceRepo
            .GetAttendanceStatusMatrixAsync(occurrenceIds, studentIds);
        var cellMap = cells.ToDictionary(c => (c.TeacherStudentId, c.OccurrenceId), c => c.Status);

        // Whole-month totals per student (independent of the occurrence page).
        var counts = await _unitOfWork.AttendanceRepo
            .GetSessionMonthAttendanceCountsAsync(sessionId, monthStart, monthEndExclusive, studentIds);
        var countMap = counts.ToDictionary(c => c.TeacherStudentId);

        var studentRows = roster.Select(r => new SessionMonthStudentRowDto
        {
            TeacherStudentId = r.TeacherStudentId,
            StudentName = r.StudentName,
            StudentCode = r.StudentCode,
            MonthPresentCount = countMap.TryGetValue(r.TeacherStudentId, out var c) ? c.PresentCount : 0,
            MonthAbsentCount = countMap.TryGetValue(r.TeacherStudentId, out var c2) ? c2.AbsentCount : 0,
            Cells = occurrences.Select(o =>
            {
                bool marked = cellMap.TryGetValue((r.TeacherStudentId, o.OccurrenceId), out var status);
                return new SessionMonthCellDto
                {
                    OccurrenceId = o.OccurrenceId,
                    IsMarked = marked,
                    Status = marked ? status : (AttendanceStatus?)null
                };
            }).ToList()
        }).ToList();

        var response = new SessionMonthAttendanceDto
        {
            SessionId = sessionId,
            SessionName = session.SessionName,
            Year = request.Year,
            Month = request.Month,
            Occurrences = new PaginatedResponse<List<SessionMonthOccurrenceDto>>
            {
                totalCount = occurrenceTotal,
                page = request.OccurrencePage,
                pageSize = request.OccurrencePageSize,
                totalPages = (int)Math.Ceiling(occurrenceTotal / (double)request.OccurrencePageSize),
                data = occurrences.Select(o => new SessionMonthOccurrenceDto
                {
                    OccurrenceId = o.OccurrenceId,
                    Date = o.Date
                }).ToList()
            },
            Students = new PaginatedResponse<List<SessionMonthStudentRowDto>>
            {
                totalCount = rosterTotal,
                page = request.Page,
                pageSize = request.PageSize,
                totalPages = (int)Math.Ceiling(rosterTotal / (double)request.PageSize),
                data = studentRows
            }
        };

        return Result<SessionMonthAttendanceDto>.Success(
            response, _localizer, AttendanceConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<MarkAttendanceResultDto>> MarkAttendanceAsync(MarkAttendanceDto dto)

    {
        // AUDIT FIX Step 11: Default RecordedByUserId to TeacherId for audit trail
        dto.RecordedByUserId ??= dto.TeacherId;

        // ATT-2 / ATT-3: status must be supplied (not omitted) and one of Present/Absent. A missing
        // status previously defaulted to Absent (0) and silently wrote a harmful Absent record.
        var statusError = ValidateCallerStatus(dto.Status);
        if (statusError is not null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, statusError, HttpStatusCode.BadRequest);
        var markStatus = dto.Status!.Value;

        // 1. Validate teacher
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(dto.TeacherId);
        if (teacher is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);

        // 2. Validate session
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(dto.SessionId, dto.TeacherId);
        if (session is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        // 3. Validate student exists
        var student = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(
            dto.TeacherStudentId, dto.TeacherId);
        // AUDIT FIX Step 19: Distinguish "not found" from "in recycle bin"
        if (student is null)
        {
            var deletedStudent = await _unitOfWork.Students.GetByIdAndTeacherIgnoreFiltersAsync(
                dto.TeacherStudentId, dto.TeacherId);
            if (deletedStudent is not null && deletedStudent.IsDeleted)
                return Result<MarkAttendanceResultDto>.Failure(
                    _localizer, AttendanceConstants.Messages.AttendanceStudentInRecycleBin,
                    HttpStatusCode.BadRequest);

            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.StudentNotFound, HttpStatusCode.NotFound);
        }

        var date = dto.OccurrenceDate?.Date ?? _timeZoneService.GetTeacherLocalDate(dto.TeacherId);

        // 4. Get or validate occurrence
        var occurrence = await _unitOfWork.AttendanceRepo
            .GetOccurrenceBySessionAndDateAsync(dto.SessionId, date);

        // Step 4.3: Return redirect hint when no occurrence for today
        if (occurrence is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.NoOccurrenceRedirectToEdit, HttpStatusCode.BadRequest);

        // 5. BR-ATT-002 / REQ-ATT-069: Check for duplicate attendance
        var existingRecord = await _unitOfWork.AttendanceRepo
            .GetExistingAttendanceAsync(dto.TeacherStudentId, occurrence.Id);
        if (existingRecord is not null)
        {
            // A system-written auto-absent (nightly sweep) is NOT a real teacher mark — a later mark on
            // the same occurrence OVERWRITES it (clears the inferred absence) instead of 409-ing, so the
            // teacher isn't forced through Edit Attendance. A teacher's own record still reads as a
            // duplicate (unchanged behaviour).
            if (existingRecord.IsAutoAbsent && existingRecord.Status == AttendanceStatus.Absent)
                return await ReconcileSingleMarkAsync(existingRecord, markStatus, session, dto,
                    AttendanceConstants.Messages.AutoAbsentOverwrittenByMark,
                    isCrossFlip: false, null, null, null, trackedOccurrence: occurrence);

            // A Held record is a DEFERRED, non-final state (created only via Hold, never a real
            // attendance decision). A later Present/Absent mark RESOLVES it in place — exactly like
            // ReleaseHold — instead of 409-ing as a duplicate. Without this the mark parks as an
            // unresolved conflict in the offline sync centre (the roster row stays Held, so the
            // offline reconcile never matches the Present/Absent it tried to write).
            if (existingRecord.Status == AttendanceStatus.Held)
                return await ReconcileSingleMarkAsync(existingRecord, markStatus, session, dto,
                    AttendanceConstants.Messages.HeldResolvedByMark,
                    isCrossFlip: false, null, null, null, trackedOccurrence: occurrence);

            return Result<MarkAttendanceResultDto>.Success(new MarkAttendanceResultDto
            {
                Record = null,
                IsDuplicate = true,
                DuplicateSessionName = existingRecord.SessionName,
                DuplicateRecordedAt = existingRecord.RecordedAt
            }, _localizer, AttendanceConstants.Messages.AttendanceDuplicateDetected);
        }

        // 7. Resolve the active assignment + cross-session landing target via the SHARED helper
        // (the same one bulk / add / hold use, so behaviour is identical everywhere).
        var linkedSessions = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(dto.SessionId);
        var linkedSessionIds = linkedSessions.Select(ls => ls.Id).ToList();
        var allLinkedSessionIds = linkedSessionIds.Append(dto.SessionId).ToList();

        var activeAssignment = await _unitOfWork.AttendanceRepo.GetActiveAssignmentAsync(dto.TeacherStudentId);
        if (activeAssignment is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceStudentNotAssigned, HttpStatusCode.BadRequest);

        var target = await ResolveMarkTargetAsync(occurrence, session, activeAssignment, linkedSessionIds, date);
        if (target is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceCrossSessionNotLinked, HttpStatusCode.BadRequest);
        var mt = target.Value;

        // Equivalence-aware duplicate guard: one attendance state per logical slot across the linked
        // group. Blocks a re-mark on ANY occurrence sharing this occurrence's (WeekStartDate,
        // DayPositionIndex) — whether stored on this session or a linked session's equivalent slot.
        var equivalentOccurrenceIds = await _unitOfWork.AttendanceRepo
            .GetEquivalentOccurrenceIdsAsync(dto.SessionId, date, allLinkedSessionIds);
        var crossDuplicate = await _unitOfWork.AttendanceRepo
            .GetExistingAttendanceOnEquivalentOccurrenceAsync(dto.TeacherStudentId, equivalentOccurrenceIds);
        if (crossDuplicate is not null)
        {
            // The student is being marked PRESENT on an EQUIVALENT occurrence while carrying an Absent
            // on another occurrence of the same logical slot (their home occurrence — typically written
            // by the nightly sweep, but a manual Absent flips too: physical attendance is ground truth).
            // Flip that Absent to CrossSessionPresent (attended THIS session) rather than 409-ing — same
            // class instance, so no "was absent last time" alert should survive.
            if (crossDuplicate.Status == AttendanceStatus.Absent && markStatus == AttendanceStatus.Present)
                return await ReconcileSingleMarkAsync(crossDuplicate, AttendanceStatus.CrossSessionPresent,
                    session, dto, AttendanceConstants.Messages.AbsentFlippedToCrossSessionPresent,
                    isCrossFlip: true, crossSessionId: dto.SessionId, crossSessionName: session.SessionName,
                    crossOccurrenceDate: date, trackedOccurrence: null);

            return Result<MarkAttendanceResultDto>.Success(new MarkAttendanceResultDto
            {
                Record = null,
                IsDuplicate = true,
                DuplicateSessionName = crossDuplicate.SessionName,
                DuplicateRecordedAt = crossDuplicate.RecordedAt
            }, _localizer, AttendanceConstants.Messages.AttendanceDuplicateDetected);
        }

        // 6. REQ-ATT-027/028/029: Check absence history for alert
        var absenceCounter = await _unitOfWork.AttendanceRepo
            .GetAbsenceCounterAsync(dto.TeacherId, dto.TeacherStudentId);

        var result = new MarkAttendanceResultDto();
        if (absenceCounter is not null && absenceCounter.ConsecutiveAbsences > 0)
        {
            result.HasAbsenceAlert = true;
            result.ConsecutiveAbsences = absenceCounter.ConsecutiveAbsences;
            result.LastAbsenceDate = absenceCounter.LastAbsenceDate;
            result.LastAbsenceSessionName = absenceCounter.LastAbsenceSessionName;
            result.LastAbsenceWasCrossSession = absenceCounter.LastAbsenceSessionId.HasValue
                && absenceCounter.LastAbsenceSessionId != dto.SessionId;

            if (!dto.AbsenceAlertConfirmed && markStatus == AttendanceStatus.Present)
            {
                result.Record = null;
                return Result<MarkAttendanceResultDto>.Success(
                    result, _localizer, AttendanceConstants.Messages.AttendanceAbsenceAlertPending);
            }
        }

        // AUDIT FIX Step 7 (REQ-ATT-013): Populate assigned session info for the cross-session warning
        if (mt.IsCrossSession)
        {
            result.AssignedSessionId = activeAssignment.SessionId;
            result.AssignedSessionName = activeAssignment.SessionName;
        }

        // 8. Cross-session marks are always CrossSessionPresent (the visitor attended).
        var attendanceStatus = mt.IsCrossSession ? AttendanceStatus.CrossSessionPresent : markStatus;
        var assignment = activeAssignment;

        // AUDIT FIX Step 2 (BR-ATT-001): No retroactive attendance before the (landed) occurrence's date
        if (mt.RecordOccurrenceDate.Date < assignment.AssignedAt.Date)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceBeforeAssignmentDate,
                HttpStatusCode.BadRequest);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            var record = new AttendanceRecord
            {
                TeacherId = dto.TeacherId,
                SessionOccurrenceId = mt.RecordOccurrenceId,
                TeacherStudentId = dto.TeacherStudentId,
                StudentSessionAssignmentId = assignment.Id,
                // Step 7.2: Denormalized student fields
                StudentName = student.StudentName,
                StudentCode = student.StudentCode,
                SessionId = mt.RecordSessionId,
                SessionName = mt.RecordSessionName,
                // FIX H3: Denormalized SessionGroupId survives session hard-delete (BR-ATT-005).
                // Enables Report Type 5 (SessionGroupAttendance) for deleted sessions.
                SessionGroupId = session.SessionGroupId,
                OccurrenceDate = mt.RecordOccurrenceDate,
                Status = attendanceStatus,
                AttendanceMethod = dto.AttendanceMethod,
                IsCrossSession = mt.IsCrossSession,
                CrossSessionId = mt.CrossSessionId,
                CrossSessionName = mt.CrossSessionName,
                CrossSessionOccurrenceDate = mt.CrossSessionOccurrenceDate,
                RecordedAt = DateTime.UtcNow,
                RecordedByUserId = dto.RecordedByUserId,
                IsEdited = false,
                CreateAt = DateTime.UtcNow
            };

            await _unitOfWork.AttendanceRepo.AddAttendanceRecordAsync(record);

            // 9. Update absence counter
            await UpdateAbsenceCounterForNewRecord(dto.TeacherId, dto.TeacherStudentId,
                attendanceStatus, date, session.SessionName, dto.SessionId);

            // Phase 3: Capture absence count while the counter entity is still in EF
            // tracking memory (after UpdateAbsenceCounterForNewRecord, before SaveChanges).
            // The value is used by the notifier after commit — not before — so that
            // Hangfire jobs are never enqueued for data that hasn't been committed.
            int consecutiveAbsencesForNotifier = 0;
            if (attendanceStatus == AttendanceStatus.Absent)
            {
                var counterSnapshot = await _unitOfWork.AttendanceRepo
                    .GetAbsenceCounterAsync(dto.TeacherId, dto.TeacherStudentId);
                consecutiveAbsencesForNotifier = counterSnapshot?.ConsecutiveAbsences ?? 1;
            }

            // FIX H8: SaveChanges BEFORE UpdateOccurrenceStatusAsync.
            // Previously the occurrence status query ran before SaveChanges,
            // so the just-added record wasn't yet in the database and the
            // AsNoTracking query couldn't see it. This caused the status
            // to always be "one mark behind".
            await _unitOfWork.SaveChangesAsync();

            // 10. Refresh the status of the occurrence the record LANDED on (the home-session
            // occurrence for a cross-session mark; the selected occurrence otherwise).
            await UpdateOccurrenceStatusAsync(mt.LandedOccurrence);

            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            // Phase 3: Notify messaging engine post-commit.
            // AttendanceNotifier is self-contained safe (try/catch + log internally),
            // so a messaging failure never propagates back to the attendance caller.
            // ownsTransaction guard: nested callers (offline sync) do not notify;
            // the outermost transaction owner is solely responsible for notification.
            if (ownsTransaction)
            {
                if (attendanceStatus == AttendanceStatus.Absent)
                    await _attendanceNotifier.OnStudentAbsentAsync(
                        dto.TeacherId, dto.TeacherStudentId,
                        dto.SessionId, session.SessionName, date,
                        consecutiveAbsencesForNotifier);
                else if (attendanceStatus is AttendanceStatus.Present
                                          or AttendanceStatus.CrossSessionPresent)
                {
                    await _attendanceNotifier.OnStudentAttendedAsync(
                        dto.TeacherId, dto.TeacherStudentId,
                        dto.SessionId, session.SessionName, date);
                }

                // Exams: reconcile any during-session exam on the occurrence the record LANDED on
                // (mt.RecordOccurrenceId — the student's home occurrence for a cross-session mark, ==
                // occurrence.Id for a same-session mark). Reconcile (not a blind push) so a student
                // assigned to the class after the exam was created auto-joins it on their first mark
                // (dynamic roster), consistent with the bulk/edit/add/delete/offline paths.
                await _examAttendanceSync.ReconcileExamsForSessionOccurrenceAsync(
                    dto.TeacherId, mt.RecordOccurrenceId, dto.RecordedByUserId ?? dto.TeacherId);
            }

            result.Record = MapToRecordDto(record, student.StudentName, student.StudentCode);

            return Result<MarkAttendanceResultDto>.Success(
                result, _localizer, AttendanceConstants.Messages.AttendanceMarkedSuccess);

        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<BulkMarkAttendanceResultDto>> BulkMarkAttendanceAsync(BulkMarkAttendanceDto dto)
    {
        // AUDIT FIX Step 11: Default RecordedByUserId to TeacherId for audit trail
        dto.RecordedByUserId ??= dto.TeacherId;

        // Normalize the two accepted shapes into ONE per-student (id → status) map.
        //   • NEW: dto.Items — each entry carries its own status (Present / Absent / Held). Held is
        //     accepted ONLY here (ValidateBulkItemStatus), never via the single/add/edit/legacy paths.
        //   • LEGACY: dto.TeacherStudentIds + a single top-level dto.Status (Present / Absent only).
        // Items wins when BOTH are supplied; neither → 400. ATT-4: repeated ids collapse (dictionary
        // key), so a duplicate id is a no-op instead of a unique-constraint 409.
        Dictionary<long, AttendanceStatus> itemStatuses;
        if (dto.Items is { Count: > 0 })
        {
            itemStatuses = new Dictionary<long, AttendanceStatus>();
            foreach (var item in dto.Items)
            {
                var itemStatusError = ValidateBulkItemStatus(item.Status);
                if (itemStatusError is not null)
                    return Result<BulkMarkAttendanceResultDto>.Failure(
                        _localizer, itemStatusError, HttpStatusCode.BadRequest);
                itemStatuses[item.TeacherStudentId] = item.Status!.Value; // last entry wins on repeats
            }
        }
        else if (dto.TeacherStudentIds is { Count: > 0 })
        {
            // ATT-2 / ATT-3: legacy status must be supplied and one of Present/Absent (see MarkAttendanceAsync).
            var statusError = ValidateCallerStatus(dto.Status);
            if (statusError is not null)
                return Result<BulkMarkAttendanceResultDto>.Failure(
                    _localizer, statusError, HttpStatusCode.BadRequest);
            itemStatuses = dto.TeacherStudentIds
                .Distinct()
                .ToDictionary(id => id, _ => dto.Status!.Value);
        }
        else
        {
            return Result<BulkMarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceBulkTargetsRequired, HttpStatusCode.BadRequest);
        }

        var studentIds = itemStatuses.Keys.ToList();

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(dto.TeacherId);
        if (teacher is null)
            return Result<BulkMarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);

        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(dto.SessionId, dto.TeacherId);
        if (session is null)
            return Result<BulkMarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        var date = dto.OccurrenceDate?.Date ?? _timeZoneService.GetTeacherLocalDate(dto.TeacherId);
        var occurrence = await _unitOfWork.AttendanceRepo
            .GetOccurrenceBySessionAndDateAsync(dto.SessionId, date);
        if (occurrence is null)
            return Result<BulkMarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceNoOccurrenceToday, HttpStatusCode.BadRequest);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            int successCount = 0;
            int skippedCount = 0;
            var absenceAlerts = new List<AbsenceAlertStudentDto>();

            // ATT-5: per-student outcome so the client can reconcile the multi-select instead of
            // trusting a top-level "all marked" message. One entry per DISTINCT id (ATT-4 dedup).
            var studentResults = new List<BulkMarkStudentResultDto>();
            void Skip(long id, string code)
            {
                skippedCount++;
                studentResults.Add(new BulkMarkStudentResultDto
                {
                    TeacherStudentId = id,
                    Success = false,
                    Code = code,
                    Reason = _localizer[code]
                });
            }

            // FIX 2.2: Batch-load all data before the loop (over the deduped id set — ATT-4)
            var allStudents = await _unitOfWork.Students
                .GetActiveByIdsAndTeacherAsync(dto.TeacherId, studentIds);
            var studentMap = allStudents.ToDictionary(s => s.Id);
            var existingStudentIds = await _unitOfWork.AttendanceRepo
                .GetExistingAttendanceBatchAsync(studentIds, occurrence.Id);
            var assignmentMap = await _unitOfWork.AttendanceRepo
                .GetActiveAssignmentsBatchAsync(studentIds);
            var counterMap = await _unitOfWork.AttendanceRepo
                .GetAbsenceCountersBatchAsync(dto.TeacherId, studentIds);

            // Step 2.1: Batch equivalence duplicate check across the linked group (by slot, not date).
            var linkedSessions = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(dto.SessionId);
            var linkedSessionIds = linkedSessions.Select(ls => ls.Id).ToList();
            var allLinkedSessionIds = linkedSessionIds.Append(dto.SessionId).ToList();
            var equivalentOccurrenceIds = await _unitOfWork.AttendanceRepo
                .GetEquivalentOccurrenceIdsAsync(dto.SessionId, date, allLinkedSessionIds);
            var crossSessionDuplicates = await _unitOfWork.AttendanceRepo
                .GetExistingAttendanceOnEquivalentOccurrenceBatchAsync(studentIds, equivalentOccurrenceIds);

            // Auto-absent reconciliation (pre-pass, before the normal insert loop). A system-written
            // auto-absent on this occurrence is OVERWRITTEN by the incoming mark; an Absent on an
            // EQUIVALENT occurrence FLIPS to CrossSessionPresent when marking Present (physical
            // attendance is ground truth). Handled up front so the loop's duplicate guards below simply
            // report these students as already-handled successes. Counters are recomputed from records
            // after the batch flush; occurrence statuses are refreshed alongside the fresh marks.
            var sameOccRecords = (await _unitOfWork.AttendanceRepo
                    .GetRecordsByOccurrenceForStudentsAsync(occurrence.Id, studentIds))
                .Where(r => r.TeacherStudentId.HasValue)
                .ToDictionary(r => r.TeacherStudentId!.Value);

            var reconciledStudentIds = new HashSet<long>();
            var reconciledOccurrenceIds = new HashSet<long>();
            foreach (var studentId in studentIds)
            {
                if (!studentMap.ContainsKey(studentId))
                    continue; // not an active student — the loop reports StudentNotFound

                var prePassStatus = itemStatuses[studentId];

                // Held mirrors HoldStudentAsync — it neither overwrites an auto-absent nor flips an
                // equivalent Absent (those are real-mark reconciliations). A Held whose student already
                // has a record is simply skipped by the main loop's existing-record guard.
                if (prePassStatus == AttendanceStatus.Held)
                    continue;

                if (sameOccRecords.TryGetValue(studentId, out var occRec))
                {
                    if (occRec.IsAutoAbsent && occRec.Status == AttendanceStatus.Absent)
                    {
                        await ApplyReconciliationAsync(occRec, prePassStatus,
                            AttendanceConstants.Messages.AutoAbsentOverwrittenByMark,
                            isCrossFlip: false, null, null, null, dto.RecordedByUserId);
                        reconciledStudentIds.Add(studentId);
                        reconciledOccurrenceIds.Add(occurrence.Id);
                    }
                    // A Held record is a deferred, non-final state — a Present/Absent scan RESOLVES it
                    // in place (mirrors the single-mark path + ReleaseHold), instead of being reported
                    // as a duplicate skip. prePassStatus is Present/Absent here (Held was skipped above).
                    else if (occRec.Status == AttendanceStatus.Held)
                    {
                        await ApplyReconciliationAsync(occRec, prePassStatus,
                            AttendanceConstants.Messages.HeldResolvedByMark,
                            isCrossFlip: false, null, null, null, dto.RecordedByUserId);
                        reconciledStudentIds.Add(studentId);
                        reconciledOccurrenceIds.Add(occurrence.Id);
                    }
                    continue; // any same-occ record → not eligible for the equivalent-flip below
                }

                if (prePassStatus == AttendanceStatus.Present
                    && crossSessionDuplicates.TryGetValue(studentId, out var crossRec)
                    && crossRec.Status == AttendanceStatus.Absent)
                {
                    await ApplyReconciliationAsync(crossRec, AttendanceStatus.CrossSessionPresent,
                        AttendanceConstants.Messages.AbsentFlippedToCrossSessionPresent,
                        isCrossFlip: true, dto.SessionId, session.SessionName, date, dto.RecordedByUserId);
                    reconciledStudentIds.Add(studentId);
                    if (crossRec.SessionOccurrenceId.HasValue)
                        reconciledOccurrenceIds.Add(crossRec.SessionOccurrenceId.Value);
                }
            }

            // Occurrences that received a mark — the selected occurrence for same-session marks, home
            // occurrences for cross-session marks. Their statuses are refreshed together after the loop.
            var affectedOccurrences = new HashSet<SessionOccurrence>();

            // Step 4.1: Track modified counters for batch save
            var modifiedCounters = new List<StudentAbsenceCounter>();

            foreach (var studentId in studentIds)
            {
                // Already handled by the auto-absent reconciliation pre-pass (overwrite / flip).
                if (reconciledStudentIds.Contains(studentId))
                {
                    successCount++;
                    studentResults.Add(new BulkMarkStudentResultDto { TeacherStudentId = studentId, Success = true });
                    continue;
                }

                if (!studentMap.TryGetValue(studentId, out var student))
                {
                    Skip(studentId, AttendanceConstants.Messages.StudentNotFound);
                    continue;
                }

                var itemStatus = itemStatuses[studentId];

                // Check same-occurrence duplicate. A Held whose student already has a record is reported
                // with the Hold-specific "already marked" message (mirrors HoldStudentAsync's 409 turned
                // into a graceful per-student skip); other statuses keep the duplicate message.
                if (existingStudentIds.Contains(studentId))
                {
                    Skip(studentId, itemStatus == AttendanceStatus.Held
                        ? AttendanceConstants.Messages.AttendanceAlreadyMarked
                        : AttendanceConstants.Messages.AttendanceDuplicateDetected);
                    continue;
                }

                // Held mirrors HoldStudentAsync: needs an active assignment, writes a Held record on THIS
                // occurrence (no cross-session remap, no equivalence dedup) and NEVER touches the absence
                // counter. Short-circuit before the cross-session / counter machinery below.
                if (itemStatus == AttendanceStatus.Held)
                {
                    if (!assignmentMap.TryGetValue(studentId, out var holdAssignment))
                    {
                        Skip(studentId, AttendanceConstants.Messages.AttendanceStudentNotAssigned);
                        continue;
                    }

                    var holdRecord = new AttendanceRecord
                    {
                        TeacherId = dto.TeacherId,
                        SessionOccurrenceId = occurrence.Id,
                        TeacherStudentId = studentId,
                        StudentSessionAssignmentId = holdAssignment.Id,
                        StudentName = student.StudentName,
                        StudentCode = student.StudentCode,
                        SessionId = dto.SessionId,
                        SessionName = session.SessionName,
                        SessionGroupId = session.SessionGroupId,
                        OccurrenceDate = date,
                        Status = AttendanceStatus.Held,
                        AttendanceMethod = dto.AttendanceMethod,
                        IsCrossSession = false,
                        RecordedAt = DateTime.UtcNow,
                        RecordedByUserId = dto.RecordedByUserId,
                        IsEdited = false,
                        CreateAt = DateTime.UtcNow
                    };

                    await _unitOfWork.AttendanceRepo.AddAttendanceRecordAsync(holdRecord);
                    // Refresh the selected occurrence's status (Held is excluded from the marked count,
                    // so this is a no-op for a pure-Held occurrence — added for consistency).
                    affectedOccurrences.Add(occurrence);

                    successCount++;
                    studentResults.Add(new BulkMarkStudentResultDto { TeacherStudentId = studentId, Success = true });
                    continue;
                }

                // Step 2.1: Check cross-session duplicate
                if (crossSessionDuplicates.ContainsKey(studentId))
                {
                    Skip(studentId, AttendanceConstants.Messages.AttendanceDuplicateDetected);
                    continue;
                }

                // AUDIT FIX Step 4 (BR-ATT-001): Skip students with no active assignment
                if (!assignmentMap.TryGetValue(studentId, out var assignment))
                {
                    Skip(studentId, AttendanceConstants.Messages.AttendanceStudentNotAssigned);
                    continue;
                }

                // Resolve the landing target via the SHARED cross-session helper — identical behaviour
                // to the single-mark path. A linked visitor is slot-remapped onto their home occurrence;
                // a same-session student lands on the selected occurrence.
                var target = await ResolveMarkTargetAsync(occurrence, session, assignment, linkedSessionIds, date);
                if (target is null)
                {
                    // Cross-session visitor whose home session is not linked — cannot mark here.
                    Skip(studentId, AttendanceConstants.Messages.AttendanceCrossSessionNotLinked);
                    continue;
                }
                var mt = target.Value;

                // A cross-session mark is always CrossSessionPresent (the visitor attended). Marking a
                // visitor ABSENT is meaningless in a session that isn't theirs, so skip those.
                if (mt.IsCrossSession && itemStatus == AttendanceStatus.Absent)
                {
                    Skip(studentId, AttendanceConstants.Messages.AttendanceCrossSessionCannotBeAbsent);
                    continue;
                }
                var effectiveStatus = mt.IsCrossSession ? AttendanceStatus.CrossSessionPresent : itemStatus;

                // FIX H6 (BR-ATT-001): No retroactive attendance before the assignment date.
                if (mt.RecordOccurrenceDate.Date < assignment.AssignedAt.Date)
                {
                    Skip(studentId, AttendanceConstants.Messages.AttendanceBeforeAssignmentDate);
                    continue;
                }

                // Check absence counter for alerts
                counterMap.TryGetValue(studentId, out var counter);
                if (counter is not null && counter.ConsecutiveAbsences > 0)
                {
                    absenceAlerts.Add(new AbsenceAlertStudentDto
                    {
                        TeacherStudentId = studentId,
                        StudentName = student.StudentName,
                        StudentCode = student.StudentCode,
                        ConsecutiveAbsences = counter.ConsecutiveAbsences,
                        LastAbsenceDate = counter.LastAbsenceDate,
                        LastAbsenceSessionName = counter.LastAbsenceSessionName,
                        WasCrossSession = counter.LastAbsenceSessionId.HasValue
                            && counter.LastAbsenceSessionId != dto.SessionId
                    });
                }

                var record = new AttendanceRecord
                {
                    TeacherId = dto.TeacherId,
                    SessionOccurrenceId = mt.RecordOccurrenceId,
                    TeacherStudentId = studentId,
                    StudentSessionAssignmentId = assignment.Id,
                    // Step 7.2: Denormalized student fields
                    StudentName = student.StudentName,
                    StudentCode = student.StudentCode,
                    SessionId = mt.RecordSessionId,
                    SessionName = mt.RecordSessionName,
                    // FIX H3: Denormalized SessionGroupId survives session hard-delete (BR-ATT-005).
                    SessionGroupId = session.SessionGroupId,
                    OccurrenceDate = mt.RecordOccurrenceDate,
                    Status = effectiveStatus,
                    AttendanceMethod = dto.AttendanceMethod,
                    IsCrossSession = mt.IsCrossSession,
                    CrossSessionId = mt.CrossSessionId,
                    CrossSessionName = mt.CrossSessionName,
                    CrossSessionOccurrenceDate = mt.CrossSessionOccurrenceDate,
                    RecordedAt = DateTime.UtcNow,
                    RecordedByUserId = dto.RecordedByUserId,
                    IsEdited = false,
                    CreateAt = DateTime.UtcNow
                };

                await _unitOfWork.AttendanceRepo.AddAttendanceRecordAsync(record);
                affectedOccurrences.Add(mt.LandedOccurrence);

                // Step 4.1: Update counter in memory instead of DB call
                if (counter is null)
                {
                    counter = new StudentAbsenceCounter
                    {
                        TeacherId = dto.TeacherId,
                        TeacherStudentId = studentId,
                        CreateAt = DateTime.UtcNow
                    };
                    await _unitOfWork.AttendanceRepo.AddAbsenceCounterAsync(counter);
                }

                counter.TotalOccurrences++;
                if (effectiveStatus == AttendanceStatus.Absent)
                {
                    counter.ConsecutiveAbsences++;
                    counter.TotalAbsences++;
                    counter.LastAbsenceDate = date;
                    counter.LastAbsenceSessionName = session.SessionName;
                    counter.LastAbsenceSessionId = dto.SessionId;
                }
                else
                {
                    counter.ConsecutiveAbsences = 0;
                    counter.TotalPresent++;
                    counter.LastAttendanceDate = date;
                }

                if (!modifiedCounters.Contains(counter))
                    modifiedCounters.Add(counter);

                successCount++;
                studentResults.Add(new BulkMarkStudentResultDto { TeacherStudentId = studentId, Success = true });
            }

            // Step 4.1: Batch save all modified counters
            if (modifiedCounters.Count > 0)
                await _unitOfWork.AttendanceRepo.UpdateAbsenceCountersRangeAsync(modifiedCounters);

            // FIX H8: SaveChanges BEFORE UpdateOccurrenceStatusAsync.
            // The occurrence status query uses AsNoTracking, so it needs the records
            // flushed to the database first to get an accurate count. This flush also persists the
            // reconciliation pre-pass's in-place status changes so the recompute below reads them.
            //
            // Resilient flush: a CONCURRENT scan (teacher + assistant scanning the same class at the
            // same time) may have recorded one of these students on this occurrence between the
            // pre-check snapshot and now. Rather than let that single unique-violation roll the WHOLE
            // batch back — which left every scanned student unmarked, and the night job then flipped
            // them to Absent — drop ONLY the already-recorded duplicate(s) and save the rest.
            var droppedDuplicateIds = await _unitOfWork.AttendanceRepo
                .FlushSkippingDuplicateRecordsAsync();

            // Report each dropped student as a graceful skip (they DO have a record — the competing
            // scan's — so this is a true duplicate, not a lost mark).
            foreach (var droppedId in droppedDuplicateIds.Distinct())
            {
                var res = studentResults.FirstOrDefault(r => r.TeacherStudentId == droppedId && r.Success);
                if (res is null) continue;
                res.Success = false;
                res.Code = AttendanceConstants.Messages.AttendanceDuplicateDetected;
                res.Reason = _localizer[AttendanceConstants.Messages.AttendanceDuplicateDetected];
                successCount--;
                skippedCount++;
            }

            // Reconciled students (overwrite/flip) bypassed the in-memory counter mutation — recompute
            // their counters from the now-flushed records (full recompute, so a flipped/overwritten
            // absence correctly drops the consecutive-absence streak that drives the "was absent" alert).
            // Dropped-duplicate students are recomputed too: their record was detached (never inserted),
            // so recomputing from records corrects the counter increment that was flushed for them.
            foreach (var reconciledId in reconciledStudentIds.Concat(droppedDuplicateIds).Distinct())
                await RecalculateAbsenceCounterFromRecordsAsync(dto.TeacherId, reconciledId);

            // Update the status of EVERY occurrence that received a mark (the selected occurrence for
            // same-session marks; home-session occurrences for cross-session marks) plus any occurrence
            // touched by the reconciliation pre-pass.
            foreach (var affected in affectedOccurrences)
                await UpdateOccurrenceStatusAsync(affected);

            var refreshedOccurrenceIds = affectedOccurrences.Select(o => o.Id).ToHashSet();
            foreach (var occId in reconciledOccurrenceIds)
            {
                if (!refreshedOccurrenceIds.Add(occId))
                    continue;
                // TRACKED get-by-id — returns the already-tracked instance (e.g. the selected occurrence,
                // or a home occurrence a fresh cross-session mark landed on) instead of a conflicting copy.
                var reconciledOcc = await _unitOfWork.AttendanceRepo
                    .GetOccurrenceByIdTrackedAsync(occId, dto.TeacherId);
                if (reconciledOcc is not null)
                    await UpdateOccurrenceStatusAsync(reconciledOcc);
            }

            await _unitOfWork.SaveChangesAsync();

            // Totals for THIS occurrence's own roster (denormalized SessionId) — excludes any
            // visitor/legacy cross-session record physically on this occurrence.
            var allRecords = await _unitOfWork.AttendanceRepo.GetRecordsByOccurrenceAsync(occurrence.Id);
            int totalPresent = allRecords.Count(r => (r.Status == AttendanceStatus.Present
                || r.Status == AttendanceStatus.CrossSessionPresent)
                && r.SessionId == occurrence.SessionId);
            int totalAbsent = allRecords.Count(r => r.Status == AttendanceStatus.Absent
                && r.SessionId == occurrence.SessionId);
            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            // Phase 3: Batch notify absent students post-commit.
            // One OnStudentsAbsentAsync call issues a single DispatchRequest that
            // the dispatcher fans out per student (BR-MSG-003) — replaces the old
            // per-alert loop that fired N individual stub calls pre-commit.
            // Per-item statuses: notify ONLY the alerted students actually marked Absent this batch
            // (Present/Held marks never fire an absence notification). Equivalent to the old
            // bulkStatus == Absent gate when every item shares one status.
            var absentAlertsToNotify = absenceAlerts
                .Where(a => itemStatuses[a.TeacherStudentId] == AttendanceStatus.Absent)
                .ToList();
            if (ownsTransaction && absentAlertsToNotify.Count > 0)
            {
                await _attendanceNotifier.OnStudentsAbsentAsync(
                    dto.TeacherId, dto.SessionId, session.SessionName, date,
                    absentAlertsToNotify
                        .Select(a => (a.TeacherStudentId, a.ConsecutiveAbsences))
                        .ToList());
            }

            // Exams: reconcile any during-session exam on the occurrences that ACTUALLY received a mark
            // (post-commit, best-effort). Reconciling from the persisted records — rather than pushing
            // the full requested id list — means students skipped this bulk (already marked, no active
            // assignment, cross-session, etc.) keep their real session status in the exam too.
            if (ownsTransaction)
            {
                foreach (var affectedOccurrenceId in affectedOccurrences.Select(o => o.Id)
                             .Concat(reconciledOccurrenceIds).Distinct())
                    await _examAttendanceSync.ReconcileExamsForSessionOccurrenceAsync(
                        dto.TeacherId, affectedOccurrenceId, dto.RecordedByUserId ?? dto.TeacherId);
            }

            var resultDto = new BulkMarkAttendanceResultDto
            {
                SuccessCount = successCount,
                SkippedCount = skippedCount,
                AbsenceAlertCount = absenceAlerts.Count,
                AbsenceAlerts = absenceAlerts,
                TotalPresent = totalPresent,
                TotalAbsent = totalAbsent,
                Results = studentResults
            };

            // ATT-5: don't claim "all selected students" when some were skipped — pick a truthful
            // message and let the client read the per-student Results for specifics.
            var bulkMessage = skippedCount > 0
                ? AttendanceConstants.Messages.AttendanceBulkMarkedPartial
                : AttendanceConstants.Messages.AttendanceBulkMarkedSuccess;

            return Result<BulkMarkAttendanceResultDto>.Success(
                resultDto, _localizer, bulkMessage);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    // ══════════════════════════════════════════════
    // UNMARKED COUNT (AUDIT FIX Step 17 — REQ-ATT-055)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<int>> GetUnmarkedCountAsync(
        long teacherId, long sessionId, DateTime? occurrenceDate)
    {
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<int>.Failure(
                _localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        var date = occurrenceDate?.Date ?? _timeZoneService.GetTeacherLocalDate(teacherId);

        var occurrence = await _unitOfWork.AttendanceRepo
            .GetOccurrenceBySessionAndDateAsync(sessionId, date);
        if (occurrence is null)
            return Result<int>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceNoOccurrenceToday, HttpStatusCode.BadRequest);

        var assignments = await _unitOfWork.AttendanceRepo
            .GetActiveAssignmentsBySessionAsync(sessionId);
        int totalStudents = assignments.Count;

        var records = await _unitOfWork.AttendanceRepo
            .GetRecordsByOccurrenceAsync(occurrence.Id);
        // Step 3.1: Exclude Held records; count only this occurrence's own session (denormalized
        // SessionId) so a cross-session visitor's fallback record doesn't inflate the marked count.
        int markedCount = records.Count(r =>
            r.Status != AttendanceStatus.Held && r.SessionId == occurrence.SessionId);

        int unmarkedCount = Math.Max(0, totalStudents - markedCount);
        return Result<int>.Success(unmarkedCount, _localizer, AttendanceConstants.Messages.Success);
    }

    // ══════════════════════════════════════════════
    // HOLD STATUS (Step 3.1)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<MarkAttendanceResultDto>> HoldStudentAsync(HoldStudentDto dto)
    {
        // AUDIT FIX Step 11: Default RecordedByUserId to TeacherId for audit trail
        dto.RecordedByUserId ??= dto.TeacherId;

        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(dto.TeacherId);
        if (teacher is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);

        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(dto.SessionId, dto.TeacherId);
        if (session is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        var student = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(
            dto.TeacherStudentId, dto.TeacherId);
        // AUDIT FIX Step 19: Distinguish "not found" from "in recycle bin"
        if (student is null)
        {
            var deletedStudent = await _unitOfWork.Students.GetByIdAndTeacherIgnoreFiltersAsync(
                dto.TeacherStudentId, dto.TeacherId);
            if (deletedStudent is not null && deletedStudent.IsDeleted)
                return Result<MarkAttendanceResultDto>.Failure(
                    _localizer, AttendanceConstants.Messages.AttendanceStudentInRecycleBin,
                    HttpStatusCode.BadRequest);

            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.StudentNotFound, HttpStatusCode.NotFound);
        }

        var date = dto.OccurrenceDate?.Date ?? _timeZoneService.GetTeacherLocalDate(dto.TeacherId);
        var occurrence = await _unitOfWork.AttendanceRepo
            .GetOccurrenceBySessionAndDateAsync(dto.SessionId, date);
        if (occurrence is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceNoOccurrenceToday, HttpStatusCode.BadRequest);

        // Check if student is already marked (any status including Held)
        var existing = await _unitOfWork.AttendanceRepo
            .GetExistingAttendanceAsync(dto.TeacherStudentId, occurrence.Id);
        if (existing is not null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceAlreadyMarked, HttpStatusCode.Conflict);

        var assignment = await _unitOfWork.AttendanceRepo.GetActiveAssignmentAsync(dto.TeacherStudentId);
        if (assignment is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceStudentNotAssigned, HttpStatusCode.BadRequest);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            var record = new AttendanceRecord
            {
                TeacherId = dto.TeacherId,
                SessionOccurrenceId = occurrence.Id,
                TeacherStudentId = dto.TeacherStudentId,
                StudentSessionAssignmentId = assignment.Id,
                StudentName = student.StudentName,
                StudentCode = student.StudentCode,
                SessionId = dto.SessionId,
                SessionName = session.SessionName,
                OccurrenceDate = date,
                Status = AttendanceStatus.Held,
                AttendanceMethod = AttendanceMethod.MultiSelect, // Holds come from UI interaction
                IsCrossSession = false,
                RecordedAt = DateTime.UtcNow,
                RecordedByUserId = dto.RecordedByUserId,
                IsEdited = false,
                CreateAt = DateTime.UtcNow
            };

            await _unitOfWork.AttendanceRepo.AddAttendanceRecordAsync(record);
            // NOTE: Do NOT update absence counter — Held is not a final state

            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            return Result<MarkAttendanceResultDto>.Success(new MarkAttendanceResultDto
            {
                Record = MapToRecordDto(record, student.StudentName, student.StudentCode)
            }, _localizer, AttendanceConstants.Messages.AttendanceHeldSuccess);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<MarkAttendanceResultDto>> ReleaseHoldAsync(ReleaseHoldDto dto)
    {
        var date = dto.OccurrenceDate?.Date ?? _timeZoneService.GetTeacherLocalDate(dto.TeacherId);
        var occurrence = await _unitOfWork.AttendanceRepo
            .GetOccurrenceBySessionAndDateAsync(dto.SessionId, date);
        if (occurrence is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceNoOccurrenceToday, HttpStatusCode.BadRequest);

        var heldRecord = await _unitOfWork.AttendanceRepo
            .GetHeldRecordAsync(dto.TeacherStudentId, occurrence.Id);
        if (heldRecord is null)
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceHoldNotFound, HttpStatusCode.NotFound);

        // AUDIT FIX Step 8: Guard against session deleted while student on hold.
        // If the session was deleted, the occurrence FK was set to null during cleanup.
        if (heldRecord.SessionOccurrenceId is null)
        {
            await _unitOfWork.AttendanceRepo.DeleteAttendanceRecordAsync(heldRecord);
            await _unitOfWork.SaveChangesAsync();
            return Result<MarkAttendanceResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceSessionDeletedWhileHeld,
                HttpStatusCode.Gone);
        }

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            if (dto.MarkAsPresent)
            {
                // Release as Present
                heldRecord.Status = AttendanceStatus.Present;
                heldRecord.IsEdited = true;
                heldRecord.LastEditedAt = DateTime.UtcNow;
                heldRecord.LastEditedByUserId = dto.RecordedByUserId;
                await _unitOfWork.AttendanceRepo.UpdateAttendanceRecordAsync(heldRecord);

                // Log the edit
                var editLog = new AttendanceEditLog
                {
                    AttendanceRecordId = heldRecord.Id,
                    PreviousStatus = AttendanceStatus.Held,
                    NewStatus = AttendanceStatus.Present,
                    PreviousAttendanceMethod = heldRecord.AttendanceMethod,
                    NewAttendanceMethod = heldRecord.AttendanceMethod,
                    EditedAt = DateTime.UtcNow,
                    EditedByUserId = dto.RecordedByUserId,
                    EditReason = "Released from hold — marked as present",
                    CreateAt = DateTime.UtcNow
                };
                await _unitOfWork.AttendanceRepo.AddEditLogAsync(editLog);

                // Update absence counter (Held → Present)
                var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(
                    dto.SessionId, dto.TeacherId);
                await UpdateAbsenceCounterForNewRecord(dto.TeacherId, dto.TeacherStudentId,
                    AttendanceStatus.Present, date,
                    session?.SessionName ?? heldRecord.SessionName, dto.SessionId);
            }
            else
            {
                // Discard hold — delete the record, return to unmarked
                await _unitOfWork.AttendanceRepo.DeleteAttendanceRecordAsync(heldRecord);
            }

            await _unitOfWork.SaveChangesAsync();

            // FIX H2: Recalculate occurrence status after hold release.
            // Previously, releasing a hold (mark as present or discard) did not
            // update the occurrence's Pending/InProgress/Completed status.
            await UpdateOccurrenceStatusAsync(occurrence);
            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            // Exams: reconcile any during-session exam on this occurrence (post-commit, best-effort).
            // Releasing a held student as Present must reach the exam obligation (was left Pending →
            // ungradable); a discard leaves them Pending, which reconcile confirms as a no-op.
            if (ownsTransaction)
                await _examAttendanceSync.ReconcileExamsForSessionOccurrenceAsync(
                    dto.TeacherId, occurrence.Id, dto.RecordedByUserId ?? dto.TeacherId);

            return Result<MarkAttendanceResultDto>.Success(new MarkAttendanceResultDto
            {
                Record = dto.MarkAsPresent
                    ? MapToRecordDto(heldRecord,
                        heldRecord.StudentName ?? "Unknown",
                        heldRecord.StudentCode ?? "")
                    : null
            }, _localizer, AttendanceConstants.Messages.AttendanceHoldReleasedSuccess);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    // ══════════════════════════════════════════════
    // EDIT ATTENDANCE
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<List<OccurrenceCalendarItemDto>>> GetOccurrenceCalendarAsync(
        long teacherId, long sessionId)
    {
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<List<OccurrenceCalendarItemDto>>.Failure(
                _localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        var occurrences = await _unitOfWork.AttendanceRepo.GetOccurrencesBySessionAsync(sessionId);
        var activeAssignments = await _unitOfWork.AttendanceRepo
            .GetActiveAssignmentsBySessionAsync(sessionId);
        int totalStudents = activeAssignments.Count;

        var occurrenceIds = occurrences.Select(o => o.Id).ToList();
        var recordCounts = await _unitOfWork.AttendanceRepo
            .CountRecordsByOccurrenceBatchAsync(occurrenceIds);

        var items = occurrences.Select(o =>
        {
            recordCounts.TryGetValue(o.Id, out int markedCount);

            return new OccurrenceCalendarItemDto
            {
                OccurrenceId = o.Id,
                OccurrenceDate = o.OccurrenceDate,
                Status = o.Status,
                MarkedCount = markedCount,
                TotalStudents = totalStudents
            };
        }).ToList();

        return Result<List<OccurrenceCalendarItemDto>>.Success(
            items, _localizer, AttendanceConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<List<AttendanceRecordDto>>> GetOccurrenceStudentsAsync(
          long teacherId, long sessionId, DateTime occurrenceDate)
    {
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<List<AttendanceRecordDto>>.Failure(
                _localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        var occurrence = await _unitOfWork.AttendanceRepo
            .GetOccurrenceBySessionAndDateAsync(sessionId, occurrenceDate);
        if (occurrence is null)
            return Result<List<AttendanceRecordDto>>.Success(
                new List<AttendanceRecordDto>(), _localizer, AttendanceConstants.Messages.Success);

        var records = await _unitOfWork.AttendanceRepo
            .GetRecordsForOccurrenceEditViewAsync(sessionId, occurrence.Id, occurrenceDate);
        var dtos = records.Select(r => MapToRecordDto(r,
            r.StudentName ?? r.TeacherStudent?.StudentName ?? "Unknown",
            r.StudentCode ?? r.TeacherStudent?.StudentCode ?? "")).ToList();

        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);
        bool showPaymentInfo = config?.ShowPaymentInfoOnAttendanceScreen ?? true;
        bool showAttendanceHistory = config?.ShowAttendanceHistoryOnAttendanceScreen ?? true;

        if ((showPaymentInfo || showAttendanceHistory) && dtos.Count > 0)
        {
            var teacherStudentIds = dtos.Select(d => d.TeacherStudentId).Distinct().ToList();
            var today = _timeZoneService.GetTeacherLocalDate(teacherId);
            var currentMonthStart = new DateTime(today.Year, today.Month, 1);

            Dictionary<long, AttendanceScreenPaymentInfoRow>? paymentInfoMap = null;
            if (showPaymentInfo)
            {
                var currentMonthEnd = currentMonthStart.AddMonths(1).AddDays(-1);
                var lastMonthStart = currentMonthStart.AddMonths(-1);
                var lastMonthEnd = currentMonthStart.AddDays(-1);

                paymentInfoMap = await _unitOfWork.PaymentsRepo.GetPaymentInfoForAttendanceBatchAsync(
                    teacherId, teacherStudentIds, currentMonthEnd, lastMonthStart, lastMonthEnd);
            }

            Dictionary<long, StudentAbsenceCounter>? countersMap = null;
            int absenceThreshold = config?.ConsecutiveAbsenceThreshold ?? 3;
            if (showAttendanceHistory)
            {
                countersMap = await _unitOfWork.AttendanceRepo
                    .GetAbsenceCountersBatchAsync(teacherId, teacherStudentIds);
            }

            foreach (var dto in dtos)
            {
                if (showPaymentInfo)
                {
                    var pay = paymentInfoMap != null && paymentInfoMap.TryGetValue(dto.TeacherStudentId, out var p)
                        ? p : null;
                    dto.PaymentInfo = new StudentPaymentInfoDto
                    {
                        HasUnpaidLastMonth = pay?.HasUnpaidLastMonth ?? false,
                        HasUnpaidCurrentMonth = pay?.HasUnpaidCurrentMonth ?? false,
                        UnpaidMonthsCount = pay?.UnpaidMonthsCount ?? 0,
                        UnpaidAmount = pay?.UnpaidAmount ?? 0m,
                        UnpaidMonthLabels = pay?.UnpaidPeriods
                            .Select(PaymentLabelFormatter.FormatUnpaidPeriodLabel).ToList()
                            ?? new List<string>()
                    };
                }

                if (showAttendanceHistory)
                {
                    var counter = countersMap != null && countersMap.TryGetValue(dto.TeacherStudentId, out var c)
                        ? c : null;
                    int consecutive = counter?.ConsecutiveAbsences ?? 0;
                    dto.HistoryInfo = new StudentAttendanceHistoryInfoDto
                    {
                        WasAbsentLastSession = consecutive > 0,
                        ConsecutiveAbsences = consecutive,
                        TotalAbsences = counter?.TotalAbsences ?? 0,
                        AbsenceConsequenceLabel = BuildAbsenceConsequenceLabel(consecutive, absenceThreshold),
                        LastAbsenceDate = counter?.LastAbsenceDate,
                        LastAbsenceSessionName = counter?.LastAbsenceSessionName
                    };
                }
            }
        }

        return Result<List<AttendanceRecordDto>>.Success(
            dtos, _localizer, AttendanceConstants.Messages.Success);
    }
    /// <inheritdoc />
    public async Task<Result<AttendanceRecordDto>> EditAttendanceAsync(EditAttendanceDto dto)
    {
        // ATT-3: NewStatus must be supplied and one of Present/Absent (Held / CrossSessionPresent /
        // out-of-range integer values are rejected — see ValidateCallerStatus).
        var statusError = ValidateCallerStatus(dto.NewStatus);
        if (statusError is not null)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, statusError, HttpStatusCode.BadRequest);
        var newStatus = dto.NewStatus!.Value;

        var record = await _unitOfWork.AttendanceRepo
            .GetAttendanceRecordByIdAsync(dto.AttendanceRecordId, dto.TeacherId);
        if (record is null)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceRecordNotFound, HttpStatusCode.NotFound);

        var previousStatus = record.Status;

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            // Log the edit
            var editLog = new AttendanceEditLog
            {
                AttendanceRecordId = record.Id,
                PreviousStatus = previousStatus,
                NewStatus = newStatus,
                PreviousAttendanceMethod = record.AttendanceMethod,
                NewAttendanceMethod = record.AttendanceMethod,
                EditedAt = DateTime.UtcNow,
                EditedByUserId = dto.EditedByUserId,
                EditReason = dto.EditReason,
                CreateAt = DateTime.UtcNow
            };
            await _unitOfWork.AttendanceRepo.AddEditLogAsync(editLog);

            // Update the record
            record.Status = newStatus;
            record.IsEdited = true;
            record.LastEditedAt = DateTime.UtcNow;
            record.LastEditedByUserId = dto.EditedByUserId;
            await _unitOfWork.AttendanceRepo.UpdateAttendanceRecordAsync(record);

            // ATT-9: flush the status change BEFORE recomputing the counter. RecalculateAbsenceCounterFromRecordsAsync
            // recomputes the totals + streak from DB queries that can't see un-flushed changes; without this the
            // counter would be computed from the record's OLD status and come back stale by one operation.
            await _unitOfWork.SaveChangesAsync();

            // Recalculate absence counter from records (now reads the just-persisted status).
            // Full recompute — not a ±1 adjustment — so the totals can't drift (see the method doc).
            if (record.TeacherStudentId.HasValue)
            {
                await RecalculateAbsenceCounterFromRecordsAsync(
                    dto.TeacherId, record.TeacherStudentId.Value);
            }

            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            // Exams: reconcile any during-session exam on this occurrence from the edited attendance
            // (post-commit, best-effort). A Present↔Absent edit now propagates to the exam obligation.
            if (ownsTransaction && record.SessionOccurrenceId.HasValue)
                await _examAttendanceSync.ReconcileExamsForSessionOccurrenceAsync(
                    dto.TeacherId, record.SessionOccurrenceId.Value, dto.EditedByUserId ?? dto.TeacherId);

            var resultDto = MapToRecordDto(record,
                record.StudentName ?? "Unknown",
                record.StudentCode ?? "");
            return Result<AttendanceRecordDto>.Success(
                resultDto, _localizer, AttendanceConstants.Messages.AttendanceEditedSuccess);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<AttendanceRecordDto>> AddAttendanceRecordAsync(AddAttendanceRecordDto dto)
    {
        // AUDIT FIX Step 11: Default RecordedByUserId to TeacherId for audit trail
        dto.RecordedByUserId ??= dto.TeacherId;

        // ATT-6: occurrence date is required. A non-nullable DateTime used to default to 0001-01-01,
        // which then surfaced as the misleading "not a scheduled occurrence day" (AttendanceNoOccurrenceToday).
        // The field is now nullable so an omission is a clean, correctly-attributed 400.
        if (dto.OccurrenceDate is null)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceOccurrenceDateRequired, HttpStatusCode.BadRequest);
        var occDate = dto.OccurrenceDate.Value.Date;

        // ATT-2 / ATT-3: status must be supplied and one of Present/Absent.
        var statusError = ValidateCallerStatus(dto.Status);
        if (statusError is not null)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, statusError, HttpStatusCode.BadRequest);
        var addStatusInput = dto.Status!.Value;

        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(dto.SessionId, dto.TeacherId);
        if (session is null)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        var student = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(
            dto.TeacherStudentId, dto.TeacherId);
        if (student is null)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, AttendanceConstants.Messages.StudentNotFound, HttpStatusCode.NotFound);

        var occurrence = await _unitOfWork.AttendanceRepo
            .GetOccurrenceBySessionAndDateAsync(dto.SessionId, occDate);
        if (occurrence is null)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceNoOccurrenceToday, HttpStatusCode.BadRequest);

        var assignment = await _unitOfWork.AttendanceRepo.GetActiveAssignmentAsync(dto.TeacherStudentId);
        if (assignment is null)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceStudentNotAssigned, HttpStatusCode.BadRequest);

        // Cross-session landing target via the SHARED helper (same design as mark / bulk).
        var linkedSessions = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(dto.SessionId);
        var linkedSessionIds = linkedSessions.Select(ls => ls.Id).ToList();
        var allLinkedSessionIds = linkedSessionIds.Append(dto.SessionId).ToList();

        var target = await ResolveMarkTargetAsync(occurrence, session, assignment, linkedSessionIds, occDate);
        if (target is null)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceCrossSessionNotLinked, HttpStatusCode.BadRequest);
        var mt = target.Value;
        var addStatus = mt.IsCrossSession ? AttendanceStatus.CrossSessionPresent : addStatusInput;

        // FIX H1 (BR-ATT-002): equivalence-aware duplicate guard before inserting (one state per slot).
        var equivalentOccurrenceIds = await _unitOfWork.AttendanceRepo
            .GetEquivalentOccurrenceIdsAsync(dto.SessionId, occDate, allLinkedSessionIds);
        var existingRecord = await _unitOfWork.AttendanceRepo
            .GetExistingAttendanceOnEquivalentOccurrenceAsync(dto.TeacherStudentId, equivalentOccurrenceIds);
        if (existingRecord is not null)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceDuplicateRecordExists,
                HttpStatusCode.Conflict);

        // AUDIT FIX Step 2 (BR-ATT-001): No retroactive attendance before the assignment date
        if (mt.RecordOccurrenceDate.Date < assignment.AssignedAt.Date)
            return Result<AttendanceRecordDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceBeforeAssignmentDate,
                HttpStatusCode.BadRequest);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            var record = new AttendanceRecord
            {
                TeacherId = dto.TeacherId,
                SessionOccurrenceId = mt.RecordOccurrenceId,
                TeacherStudentId = dto.TeacherStudentId,
                StudentSessionAssignmentId = assignment.Id,
                // Step 7.2: Denormalized student fields
                StudentName = student.StudentName,
                StudentCode = student.StudentCode,
                SessionId = mt.RecordSessionId,
                SessionName = mt.RecordSessionName,
                // FIX H3: Denormalized SessionGroupId survives session hard-delete (BR-ATT-005).
                SessionGroupId = session.SessionGroupId,
                OccurrenceDate = mt.RecordOccurrenceDate,
                Status = addStatus,
                AttendanceMethod = AttendanceMethod.MultiSelect, // Via Edit Attendance
                IsCrossSession = mt.IsCrossSession,
                CrossSessionId = mt.CrossSessionId,
                CrossSessionName = mt.CrossSessionName,
                CrossSessionOccurrenceDate = mt.CrossSessionOccurrenceDate,
                RecordedAt = DateTime.UtcNow,
                RecordedByUserId = dto.RecordedByUserId,
                IsEdited = false,
                CreateAt = DateTime.UtcNow
            };

            await _unitOfWork.AttendanceRepo.AddAttendanceRecordAsync(record);

            // ATT-9 (add path): flush the new record BEFORE updating the counter, so the streak
            // recompute inside UpdateAbsenceCounterForAddedRecord (RecalculateConsecutiveAbsencesAsync,
            // a DB query that can't see un-flushed changes) counts the record just added. Without this
            // flush, /add returned consecutiveAbsences one short — computed from the pre-insert state —
            // until the student's next counter write healed it. (The Total*/Last* maintenance in that
            // method is unaffected: it increments the counter in memory regardless of flush order.)
            await _unitOfWork.SaveChangesAsync();

            await UpdateAbsenceCounterForAddedRecord(dto.TeacherId, dto.TeacherStudentId,
                addStatus, mt.RecordOccurrenceDate.Date, session.SessionName, dto.SessionId);

            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            // Exams: reconcile any during-session exam on the occurrence this record landed on
            // (post-commit, best-effort), so a newly added present/absent mark reaches the exam.
            if (ownsTransaction)
                await _examAttendanceSync.ReconcileExamsForSessionOccurrenceAsync(
                    dto.TeacherId, mt.RecordOccurrenceId, dto.RecordedByUserId ?? dto.TeacherId);

            var resultDto = MapToRecordDto(record, student.StudentName, student.StudentCode);
            return Result<AttendanceRecordDto>.Success(
                resultDto, _localizer, AttendanceConstants.Messages.AttendanceAddedSuccess);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> DeleteAttendanceRecordAsync(DeleteAttendanceRecordDto dto)
    {
        var record = await _unitOfWork.AttendanceRepo
            .GetAttendanceRecordByIdAsync(dto.AttendanceRecordId, dto.TeacherId);
        if (record is null)
            return Result<bool>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceRecordNotFound, HttpStatusCode.NotFound);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        // Capture the record's key facts before removal — used by the counter recalc, the occurrence
        // status recompute, and the exam reconcile after the row is gone.
        long? occurrenceId = record.SessionOccurrenceId;
        long? deletedStudentId = record.TeacherStudentId;
        var deletedStatus = record.Status;

        try
        {
            // AUDIT FIX Step 15 (BR-ATT-006): Log deletion before removing the record.
            // With SetNull FK on AttendanceEditLog, the log survives with AttendanceRecordId = null.
            var deletionLog = new AttendanceEditLog
            {
                AttendanceRecordId = record.Id,
                PreviousStatus = deletedStatus,
                NewStatus = deletedStatus, // Same — this is a deletion, not a status change
                PreviousAttendanceMethod = record.AttendanceMethod,
                NewAttendanceMethod = record.AttendanceMethod,
                EditedAt = DateTime.UtcNow,
                EditedByUserId = dto.TeacherId,
                EditReason = "Record deleted via Edit Attendance (REQ-ATT-024)",
                CreateAt = DateTime.UtcNow
            };
            await _unitOfWork.AttendanceRepo.AddEditLogAsync(deletionLog);

            await _unitOfWork.AttendanceRepo.DeleteAttendanceRecordAsync(record);

            // ATT-9 + ATT-1: flush the deletion BEFORE the counter recalc and the occurrence recompute —
            // both run DB queries (RecalculateConsecutiveAbsencesAsync / GetRecordsByOccurrenceAsync) that
            // can't see an un-flushed removal, so without this they'd still count the deleted record.
            await _unitOfWork.SaveChangesAsync();

            // ATT-9: recompute the absence counter from the now-current records (was stale by one op —
            // consecutiveAbsences read the pre-delete state). Full records-based recompute also heals
            // any pre-existing Total* drift for this student.
            if (deletedStudentId.HasValue)
            {
                await RecalculateAbsenceCounterFromRecordsAsync(
                    dto.TeacherId, deletedStudentId.Value);
            }

            // ATT-1: recompute the occurrence's status now that a record was removed. Delete was the ONE
            // write path that never did this (mark / bulk / add / release-hold all do), so an occurrence
            // could stay InProgress/Completed with 0 marks — corrupting the Edit-Attendance calendar
            // colour and the daily dashboard's completedSessions count (DASH-2). UpdateOccurrenceStatusAsync
            // sends it back to Pending when markedCount returns to 0.
            if (occurrenceId.HasValue)
            {
                var occurrence = await _unitOfWork.AttendanceRepo
                    .GetOccurrenceByIdAndTeacherAsync(occurrenceId.Value, dto.TeacherId);
                if (occurrence is not null)
                    await UpdateOccurrenceStatusAsync(occurrence);
            }

            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            // Exams: reconcile any during-session exam on this occurrence (post-commit, best-effort).
            // The record is now gone, so a student whose only mark was deleted reverts to Pending.
            if (ownsTransaction && occurrenceId.HasValue)
                await _examAttendanceSync.ReconcileExamsForSessionOccurrenceAsync(
                    dto.TeacherId, occurrenceId.Value, dto.TeacherId);

            return Result<bool>.Success(
                true, _localizer, AttendanceConstants.Messages.AttendanceRecordDeletedSuccess);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<List<AttendanceEditLogDto>>> GetEditHistoryAsync(
        long teacherId, long attendanceRecordId)
    {
        var record = await _unitOfWork.AttendanceRepo
            .GetAttendanceRecordByIdAsync(attendanceRecordId, teacherId);
        if (record is null)
            return Result<List<AttendanceEditLogDto>>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceRecordNotFound, HttpStatusCode.NotFound);

        var logs = await _unitOfWork.AttendanceRepo.GetEditLogsByRecordAsync(attendanceRecordId);
        var dtos = logs.Select(l => new AttendanceEditLogDto
        {
            Id = l.Id,
            PreviousStatus = l.PreviousStatus,
            NewStatus = l.NewStatus,
            EditedAt = l.EditedAt,
            EditedByUserId = l.EditedByUserId,
            EditReason = l.EditReason
        }).ToList();

        return Result<List<AttendanceEditLogDto>>.Success(
            dtos, _localizer, AttendanceConstants.Messages.Success);
    }

    // ══════════════════════════════════════════════
    // ABSENCE OVERVIEW
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<AbsenceOverviewStudentDto>>>> GetAbsenceOverviewAsync(
        long teacherId, long sessionId, AbsenceOverviewRequest request)
    {
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<PaginatedResponse<List<AbsenceOverviewStudentDto>>>.Failure(
                _localizer, AttendanceConstants.Messages.SessionNotFound, HttpStatusCode.NotFound);

        // Include linked sessions for cross-session absence view (REQ-ATT-033)
        var linkedSessions = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(sessionId);
        var allSessionIds = linkedSessions.Select(s => s.Id).Append(sessionId).ToList();

        // AUDIT FIX Step 12 (REQ-ATT-035): If a specific date is requested,
        // query AttendanceRecords for that date instead of global counters
        if (request.OccurrenceDate.HasValue)
        {
            // FIX H4 (REQ-ATT-034): Pass phone filters to the date-specific path.
            // Previously MissingStudentPhone and MissingParentPhone were ignored here.
            var dateRecordCount = await _unitOfWork.AttendanceRepo.CountAbsentStudentsByDateAsync(
                teacherId, allSessionIds, request.OccurrenceDate.Value, request.Search,
                missingStudentPhone: request.MissingStudentPhone ? true : null,
                missingParentPhone: request.MissingParentPhone ? true : null);

            var dateRecords = await _unitOfWork.AttendanceRepo.GetAbsentStudentsByDateAsync(
                teacherId, allSessionIds, request.OccurrenceDate.Value,
                request.Search, request.Page, request.PageSize,
                missingStudentPhone: request.MissingStudentPhone ? true : null,
                missingParentPhone: request.MissingParentPhone ? true : null);

            var dateDtos = dateRecords.Select(r => new AbsenceOverviewStudentDto
            {
                TeacherStudentId = r.TeacherStudentId ?? 0,
                StudentName = r.StudentName ?? r.TeacherStudent?.StudentName ?? "Unknown",
                StudentCode = r.StudentCode ?? r.TeacherStudent?.StudentCode ?? "",
                SessionId = r.SessionId,
                SessionName = r.SessionName,
                ConsecutiveAbsences = 0, // Not available from a single-date query
                TotalAbsences = 0,
                LastAbsenceDate = r.OccurrenceDate,
                RecentStatuses = new List<AttendanceStatus> { AttendanceStatus.Absent }
            }).ToList();

            var dateResponse = new PaginatedResponse<List<AbsenceOverviewStudentDto>>
            {
                totalCount = dateRecordCount,
                page = request.Page,
                pageSize = request.PageSize,
                totalPages = (int)Math.Ceiling(dateRecordCount / (double)request.PageSize),
                data = dateDtos
            };

            return Result<PaginatedResponse<List<AbsenceOverviewStudentDto>>>.Success(
                dateResponse, _localizer, AttendanceConstants.Messages.Success);
        }

        // Default path: latest occurrence — uses global absence counters
        int totalCount = await _unitOfWork.AttendanceRepo.CountAbsenceOverviewAsync(
            teacherId,
            sessionId: request.SessionId ?? sessionId,
            search: request.Search,
            missingStudentPhone: request.MissingStudentPhone ? true : null,
            missingParentPhone: request.MissingParentPhone ? true : null,
            minConsecutiveAbsences: request.MinConsecutiveAbsences);

        var pagedCounters = await _unitOfWork.AttendanceRepo.GetPagedAbsenceOverviewAsync(
            teacherId,
            request.Page,
            request.PageSize,
            sessionId: request.SessionId ?? sessionId,
            search: request.Search,
            missingStudentPhone: request.MissingStudentPhone ? true : null,
            missingParentPhone: request.MissingParentPhone ? true : null,
            minConsecutiveAbsences: request.MinConsecutiveAbsences);

        // FIX M3: Batch-load recent statuses for all students in one query
        // instead of N+1 individual queries inside the loop.
        var studentIds = pagedCounters
            .Select(c => c.TeacherStudentId).ToList();
        var recentStatusesMap = studentIds.Count > 0
            ? await _unitOfWork.AttendanceRepo.GetRecentRecordsByStudentsBatchAsync(
                studentIds, AttendanceConstants.RecentStatusIndicatorCount)
            : new Dictionary<long, IReadOnlyList<AttendanceStatus>>();

        var dtos = new List<AbsenceOverviewStudentDto>();
        foreach (var counter in pagedCounters)
        {
            recentStatusesMap.TryGetValue(counter.TeacherStudentId, out var recentStatuses);

            dtos.Add(new AbsenceOverviewStudentDto
            {
                TeacherStudentId = counter.TeacherStudentId,
                StudentName = counter.TeacherStudent?.StudentName ?? "Unknown",
                StudentCode = counter.TeacherStudent?.StudentCode ?? "",
                SessionId = counter.TeacherStudent?.SessionId,
                // FIX M2: Populate SessionName — was always null in the default path.
                SessionName = counter.LastAbsenceSessionName,
                ConsecutiveAbsences = counter.ConsecutiveAbsences,
                TotalAbsences = counter.TotalAbsences,
                LastAbsenceDate = counter.LastAbsenceDate,
                RecentStatuses = recentStatuses?.ToList() ?? new List<AttendanceStatus>()
            });
        }

        var response = new PaginatedResponse<List<AbsenceOverviewStudentDto>>
        {
            totalCount = totalCount,
            page = request.Page,
            pageSize = request.PageSize,
            totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
            data = dtos
        };

        return Result<PaginatedResponse<List<AbsenceOverviewStudentDto>>>.Success(
            response, _localizer, AttendanceConstants.Messages.Success);
    }

    // ══════════════════════════════════════════════
    // STUDENT ATTENDANCE TIMELINE
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<StudentAttendanceSummaryDto>>>> GetTimelineStudentListAsync(
        long teacherId, AttendanceTimelineRequest request)
    {
        // FIX M4: Use DB-level pagination instead of loading ALL student IDs into memory.
        // Previously loaded all distinct IDs then paginated with LINQ Skip/Take in memory.
        // For a teacher with 50K students, this loaded 50K longs on every request.
        var (pagedIds, totalCount) = await _unitOfWork.AttendanceRepo.GetPagedTimelineStudentIdsAsync(
            teacherId,
            request.Page,
            request.PageSize,
            sessionId: request.SessionId,
            sessionGroupId: request.SessionGroupId,
            studentName: request.StudentName,
            studentCode: request.StudentCode);

        // FIX L8: Batch-load counters and assignments for the page to reduce N+1.
        // Previously BuildStudentSummary made 3 DB calls per student (student, counter, assignments).
        var counterMap = pagedIds.Count > 0
            ? await _unitOfWork.AttendanceRepo.GetAbsenceCountersBatchAsync(teacherId, pagedIds)
            : new Dictionary<long, StudentAbsenceCounter>();

        // FIX P5: Batch-load assignments for all paged students in one query.
        // Previously called GetAssignmentsByStudentAsync per student = N extra DB queries.
        var assignmentMap = pagedIds.Count > 0
            ? await _unitOfWork.AttendanceRepo.GetAssignmentsByStudentsBatchAsync(pagedIds)
            : new Dictionary<long, IReadOnlyList<StudentSessionAssignment>>();

        // Batch-load students
        var students = pagedIds.Count > 0
            ? await _unitOfWork.Students.GetActiveByIdsAndTeacherAsync(teacherId, pagedIds)
            : new List<TeacherStudent>();
        var studentMap = students.ToDictionary(s => s.Id);

        var summaries = new List<StudentAttendanceSummaryDto>();
        foreach (var studentId in pagedIds)
        {
            if (!studentMap.TryGetValue(studentId, out var student))
                continue;

            counterMap.TryGetValue(studentId, out var counter);
            assignmentMap.TryGetValue(studentId, out var assignments);

            var periods = (assignments ?? Array.Empty<StudentSessionAssignment>()).Select(a => new AssignmentPeriodDto
            {
                StudentSessionAssignmentId = a.Id,
                SessionId = a.SessionId,
                SessionName = a.SessionName,
                AssignedAt = a.AssignedAt,
                UnassignedAt = a.UnassignedAt,
                IsActive = a.IsActive
            }).ToList();

            int totalOcc = counter?.TotalOccurrences ?? 0;
            int totalPresent = counter?.TotalPresent ?? 0;

            summaries.Add(new StudentAttendanceSummaryDto
            {
                TeacherStudentId = studentId,
                StudentName = student.StudentName,
                StudentCode = student.StudentCode,
                TotalOccurrences = totalOcc,
                TotalAbsences = counter?.TotalAbsences ?? 0,
                AttendancePercentage = totalOcc > 0
                    ? Math.Round((decimal)totalPresent / totalOcc * 100, 1) : 0,
                ConsecutiveAbsences = counter?.ConsecutiveAbsences ?? 0,
                AssignmentPeriods = periods
            });
        }

        var response = new PaginatedResponse<List<StudentAttendanceSummaryDto>>
        {
            totalCount = totalCount,
            page = request.Page,
            pageSize = request.PageSize,
            totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
            data = summaries
        };

        return Result<PaginatedResponse<List<StudentAttendanceSummaryDto>>>.Success(
            response, _localizer, AttendanceConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<StudentAttendanceSummaryDto>> GetStudentAttendanceSummaryAsync(
        long teacherId, long teacherStudentId)
    {
        var summary = await BuildStudentSummary(teacherId, teacherStudentId);
        if (summary is null)
            return Result<StudentAttendanceSummaryDto>.Failure(
                _localizer, AttendanceConstants.Messages.StudentNotFound, HttpStatusCode.NotFound);

        return Result<StudentAttendanceSummaryDto>.Success(
            summary, _localizer, AttendanceConstants.Messages.Success);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Occurrence-overlay model (2026-07-13): the calendar is driven by the session's
    /// SCHEDULED occurrences for the month, not merely by rows in AttendanceRecord.
    /// This lets the student see every class day — including upcoming classes and days
    /// the teacher hasn't marked yet — with the recorded status overlaid where present.
    ///
    ///   1. Resolve the month window; default to the teacher's local current month when
    ///      the client omits year/month (ITimeZoneService, matches the payment module).
    ///   2. Take the student's assignment period(s) overlapping the month; for each, pull
    ///      that session's occurrences clipped to the enrollment window (BR-ATT-001: no
    ///      obligation before AssignedAt or after UnassignedAt).
    ///   3. Overlay AttendanceRecords onto the scheduled cells by SessionOccurrenceId; any
    ///      record with no matching cell (cross-session present against a linked session's
    ///      occurrence, or a record outside the window) is surfaced as its own date-driven
    ///      cell so nothing the student was marked for is dropped.
    ///   4. Stats count MARKED occurrences only; Held and unmarked/upcoming days are
    ///      excluded from the percentage denominator (present / (present + absent)).
    /// </remarks>
    public async Task<Result<MonthlyAttendanceSummaryDto>> GetStudentTimelineMonthAsync(
        long teacherId, long teacherStudentId, StudentTimelineMonthRequest request)
    {
        // ── 1. Month window — default to the teacher's local current month ──
        DateTime localToday = _timeZoneService.GetTeacherLocalDate(teacherId);
        int year, month;
        if (request.Year.HasValue && request.Month.HasValue
            && request.Year.Value is >= 1 and <= 9999)
        {
            year = request.Year.Value;
            month = request.Month.Value;
        }
        else
        {
            year = localToday.Year;
            month = localToday.Month;
        }
        var startDate = new DateTime(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);

        // ── 2. Assignment period(s) overlapping the month → the session(s) the student
        //       belonged to, and each session's scheduled occurrences within its window ──
        var assignments = await _unitOfWork.AttendanceRepo
            .GetAssignmentsByStudentAsync(teacherStudentId);
        var overlapping = assignments
            .Where(a => a.SessionId.HasValue
                && a.AssignedAt.Date <= endDate
                && (a.UnassignedAt == null || a.UnassignedAt.Value.Date >= startDate))
            .ToList();

        var dayCells = new List<StudentAttendanceDayDto>();
        var scheduledOccurrenceIds = new HashSet<long>();
        foreach (var a in overlapping)
        {
            var winStart = a.AssignedAt.Date > startDate ? a.AssignedAt.Date : startDate;
            var winEnd = (a.UnassignedAt.HasValue && a.UnassignedAt.Value.Date < endDate)
                ? a.UnassignedAt.Value.Date : endDate;

            var occurrences = await _unitOfWork.AttendanceRepo
                .GetOccurrencesBySessionAndDateRangeAsync(a.SessionId!.Value, winStart, winEnd);
            foreach (var o in occurrences)
            {
                if (!scheduledOccurrenceIds.Add(o.Id)) continue; // dedupe overlapping windows
                dayCells.Add(new StudentAttendanceDayDto
                {
                    Date = o.OccurrenceDate,
                    SessionOccurrenceId = o.Id,
                    SessionId = a.SessionId,
                    SessionName = a.SessionName,
                    Status = null, // scheduled but not yet marked
                    IsPast = o.OccurrenceDate.Date <= localToday.Date
                });
            }
        }

        // ── 3. Overlay the student's records onto the scheduled cells ──
        var records = await _unitOfWork.AttendanceRepo
            .GetRecordsByStudentAndDateRangeAsync(teacherStudentId, startDate, endDate);
        var recordDtos = records.Select(r => MapToRecordDto(r,
            r.StudentName ?? r.TeacherStudent?.StudentName ?? "Unknown",
            r.StudentCode ?? r.TeacherStudent?.StudentCode ?? "")).ToList();

        var recordByOccurrence = records
            .Where(r => r.SessionOccurrenceId.HasValue)
            .GroupBy(r => r.SessionOccurrenceId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var cell in dayCells)
        {
            if (cell.SessionOccurrenceId.HasValue
                && recordByOccurrence.TryGetValue(cell.SessionOccurrenceId.Value, out var matched))
                cell.Status = matched.Status;
        }

        // Records with no matching scheduled cell → surface as their own date-driven cells.
        foreach (var r in records)
        {
            if (r.SessionOccurrenceId.HasValue
                && scheduledOccurrenceIds.Contains(r.SessionOccurrenceId.Value))
                continue;
            dayCells.Add(new StudentAttendanceDayDto
            {
                Date = r.OccurrenceDate,
                SessionOccurrenceId = r.SessionOccurrenceId,
                SessionId = r.SessionId,
                SessionName = r.SessionName,
                Status = r.Status,
                IsPast = r.OccurrenceDate.Date <= localToday.Date
            });
        }

        dayCells = dayCells.OrderBy(c => c.Date).ToList();

        // ── 4. Month stats — marked occurrences only; Held excluded from the % ──
        int totalPresent = dayCells.Count(c =>
            c.Status == AttendanceStatus.Present || c.Status == AttendanceStatus.CrossSessionPresent);
        int totalAbsences = dayCells.Count(c => c.Status == AttendanceStatus.Absent);
        int markedOccurrences = dayCells.Count(c => c.Status.HasValue);
        int denominator = totalPresent + totalAbsences;

        // ── 5. Header session: the active/most-recent assignment overlapping the month,
        //       falling back to the latest record when no assignment overlaps ──
        long? headerSessionId = null;
        string? headerSessionName = null;
        var headerAssignment = overlapping.OrderByDescending(a => a.AssignedAt).FirstOrDefault();
        if (headerAssignment is not null)
        {
            headerSessionId = headerAssignment.SessionId;
            headerSessionName = headerAssignment.SessionName;
        }
        else if (records.Count > 0)
        {
            var latest = records.OrderByDescending(r => r.OccurrenceDate).First();
            headerSessionId = latest.SessionId;
            headerSessionName = latest.SessionName;
        }

        var monthSummary = new MonthlyAttendanceSummaryDto
        {
            Year = year,
            Month = month,
            SessionId = headerSessionId,
            SessionName = headerSessionName,
            TotalOccurrences = dayCells.Count,
            MarkedOccurrences = markedOccurrences,
            TotalPresent = totalPresent,
            TotalAbsences = totalAbsences,
            AttendancePercentage = denominator > 0
                ? Math.Round((decimal)totalPresent / denominator * 100, 1) : 0,
            Days = dayCells,
            Records = recordDtos
        };

        return Result<MonthlyAttendanceSummaryDto>.Success(
            monthSummary, _localizer, AttendanceConstants.Messages.Success);
    }

    // ══════════════════════════════════════════════
    // REPORTING
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<List<AttendanceRecordDto>>> GenerateReportAsync(
        long teacherId, AttendanceReportRequest request)
    {
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(teacherId);
        if (teacher is null)
            return Result<List<AttendanceRecordDto>>.Failure(
                _localizer, AttendanceConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);

        // ATT-7: each report type needs its scoping id. Without this a type that requires a sub-id
        // (e.g. SingleStudentAbsence with no teacherStudentId, or a session-scoped type with no
        // sessionId) silently returned an empty list — which reads to the client as "no absences"
        // rather than "you forgot a parameter". Validate up front and name the missing field.
        var reportParamError = request.ReportType switch
        {
            AttendanceReportType.SingleStudentAbsence when !request.TeacherStudentId.HasValue
                => AttendanceConstants.Messages.AttendanceReportStudentRequired,
            AttendanceReportType.SessionAbsence when !request.SessionId.HasValue
                => AttendanceConstants.Messages.AttendanceReportSessionRequired,
            AttendanceReportType.SessionAttendanceHistory when !request.SessionId.HasValue
                => AttendanceConstants.Messages.AttendanceReportSessionRequired,
            AttendanceReportType.LinkedSessionsAttendance when !request.SessionId.HasValue
                => AttendanceConstants.Messages.AttendanceReportSessionRequired,
            AttendanceReportType.SessionGroupAttendance when !request.SessionGroupId.HasValue
                => AttendanceConstants.Messages.AttendanceReportSessionGroupRequired,
            _ => null
        };
        if (reportParamError is not null)
            return Result<List<AttendanceRecordDto>>.Failure(
                _localizer, reportParamError, HttpStatusCode.BadRequest);

        long? studentFilter = null;
        if (request.ReportType == AttendanceReportType.SingleStudentAbsence
            && request.TeacherStudentId.HasValue)
            studentFilter = request.TeacherStudentId.Value;

        // Step 6.1: Handle all 6 report types
        IEnumerable<long>? linkedSessionIds = null;
        if (request.ReportType == AttendanceReportType.LinkedSessionsAttendance
            && request.SessionId.HasValue)
        {
            var linked = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(request.SessionId.Value);
            linkedSessionIds = linked.Select(s => s.Id).Append(request.SessionId.Value).ToList();
        }

        var records = await _unitOfWork.AttendanceRepo.ExecuteReportQueryAsync(
            teacherId,
            sessionId: request.SessionId,
            sessionGroupId: request.SessionGroupId,
            startDate: request.StartDate,
            endDate: request.EndDate,
            status: request.ReportType == AttendanceReportType.SingleStudentAbsence
                || request.ReportType == AttendanceReportType.SessionAbsence
                || request.ReportType == AttendanceReportType.AllSessionsAbsence
                    ? AttendanceStatus.Absent : null,
            teacherStudentId: studentFilter,
            sessionIds: linkedSessionIds);

        var dtos = records.Select(r => MapToRecordDto(r,
            r.StudentName ?? r.TeacherStudent?.StudentName ?? "Unknown",
            r.StudentCode ?? r.TeacherStudent?.StudentCode ?? "")).ToList();

        return Result<List<AttendanceRecordDto>>.Success(
            dtos, _localizer, AttendanceConstants.Messages.AttendanceReportGenerated);
    }

    // ══════════════════════════════════════════════
    // EXPORT (Step 3.2)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<byte[]>> ExportReportAsync(
        long teacherId, AttendanceReportRequest request, string format)
    {
        if (format.ToLower() != "xlsx" && format.ToLower() != "pdf")
            return Result<byte[]>.Failure(
                _localizer, AttendanceConstants.Messages.InvalidExportFormat, HttpStatusCode.BadRequest);

        var reportResult = await GenerateReportAsync(teacherId, request);
        if (!reportResult.IsSuccess)
            return Result<byte[]>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceReportGenerationFailed, reportResult.StatusCode);

        byte[] fileBytes = format.ToLower() == "xlsx"
            ? await _exportService.ExportReportToExcelAsync(reportResult.Data!, request.ReportType)
            : await _exportService.ExportReportToPdfAsync(reportResult.Data!, request.ReportType);

        return Result<byte[]>.Success(
            fileBytes, _localizer, AttendanceConstants.Messages.ExportCompleted);
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> ExportTimelineAsync(
        long teacherId, long teacherStudentId,
        DateTime? startDate, DateTime? endDate, string format)
    {
        if (format.ToLower() != "xlsx" && format.ToLower() != "pdf")
            return Result<byte[]>.Failure(
                _localizer, AttendanceConstants.Messages.InvalidExportFormat, HttpStatusCode.BadRequest);

        var summaryResult = await GetStudentAttendanceSummaryAsync(teacherId, teacherStudentId);
        if (!summaryResult.IsSuccess)
            return Result<byte[]>.Failure(
                _localizer, AttendanceConstants.Messages.StudentNotFound, summaryResult.StatusCode);

        // Determine date range
        // FIX R3: When no assignments exist, skip querying empty range entirely.
        var firstAssignment = summaryResult.Data!.AssignmentPeriods
            .OrderBy(p => p.AssignedAt).FirstOrDefault();
        if (firstAssignment is null && !startDate.HasValue)
        {
            // No assignments and no explicit start date — return empty export
            byte[] emptyBytes = format.ToLower() == "xlsx"
                ? await _exportService.ExportTimelineToExcelAsync(summaryResult.Data!, new List<MonthlyAttendanceSummaryDto>())
                : await _exportService.ExportTimelineToPdfAsync(summaryResult.Data!, new List<MonthlyAttendanceSummaryDto>());
            return Result<byte[]>.Success(emptyBytes, _localizer, AttendanceConstants.Messages.ExportCompleted);
        }

        var effectiveStart = startDate ?? firstAssignment!.AssignedAt;
        var effectiveEnd = endDate ?? DateTime.UtcNow;

        // FIX P4: Load ALL records in a single query then group by month in memory.
        // Previously made N sequential GetStudentTimelineMonthAsync calls (one per month),
        // each hitting the database. For 24 months = 24 DB round-trips.
        var allRecords = await _unitOfWork.AttendanceRepo
            .GetRecordsByStudentAndDateRangeAsync(teacherStudentId, effectiveStart, effectiveEnd);

        var recordDtos = allRecords.Select(r => MapToRecordDto(r,
            r.StudentName ?? r.TeacherStudent?.StudentName ?? "Unknown",
            r.StudentCode ?? r.TeacherStudent?.StudentCode ?? "")).ToList();

        // Group by Year-Month and build monthly summaries
        var months = recordDtos
            .GroupBy(r => new { r.OccurrenceDate.Year, r.OccurrenceDate.Month })
            .Select(g =>
            {
                int totalOcc = g.Count();
                int totalPresent = g.Count(r =>
                    r.Status == AttendanceStatus.Present || r.Status == AttendanceStatus.CrossSessionPresent);
                int totalAbsences = g.Count(r => r.Status == AttendanceStatus.Absent);
                return new MonthlyAttendanceSummaryDto
                {
                    Year = g.Key.Year,
                    Month = g.Key.Month,
                    TotalOccurrences = totalOcc,
                    TotalPresent = totalPresent,
                    TotalAbsences = totalAbsences,
                    AttendancePercentage = totalOcc > 0
                        ? Math.Round((decimal)totalPresent / totalOcc * 100, 1) : 0,
                    Records = g.ToList()
                };
            })
            .Where(m => m.TotalOccurrences > 0)
            .ToList();

        byte[] fileBytes = format.ToLower() == "xlsx"
            ? await _exportService.ExportTimelineToExcelAsync(summaryResult.Data!, months)
            : await _exportService.ExportTimelineToPdfAsync(summaryResult.Data!, months);

        return Result<byte[]>.Success(
            fileBytes, _localizer, AttendanceConstants.Messages.ExportCompleted);
    }

    // ══════════════════════════════════════════════
    // OFFLINE SYNC (Step 3.3)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<SyncResultDto>> SyncOfflineRecordsAsync(OfflineSyncRequestDto dto)
    {
        var teacher = await _unitOfWork.Users.GetActiveTeacherByIdAsync(dto.TeacherId);
        if (teacher is null)
            return Result<SyncResultDto>.Failure(
                _localizer, AttendanceConstants.Messages.TeacherNotFound, HttpStatusCode.NotFound);

        var result = new SyncResultDto
        {
            TotalSubmitted = dto.Entries.Count,
            EntryResults = new List<SyncEntryResultDto>()
        };

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            int successCount = 0;
            int conflictCount = 0;
            int failedCount = 0;

            // Occurrences that received a NEW mark this sync — reconciled into any during-session exam
            // after commit (the nested MarkAttendanceAsync calls below skip their own exam sync).
            var affectedOccurrenceIds = new HashSet<long>();

            foreach (var entry in dto.Entries)
            {
                // Get or validate occurrence
                var occurrence = await _unitOfWork.AttendanceRepo
                    .GetOccurrenceBySessionAndDateAsync(entry.SessionId, entry.OccurrenceDate.Date);

                if (occurrence is null)
                {
                    failedCount++;
                    result.EntryResults.Add(new SyncEntryResultDto
                    {
                        ClientEntryId = entry.ClientEntryId,
                        Success = false,
                        IsConflict = false,
                        ErrorMessage = "No occurrence found for this session on this date",
                        ErrorCode = "NoOccurrenceOnDate"
                    });
                    continue;
                }

                // Check for existing record
                var existing = await _unitOfWork.AttendanceRepo
                    .GetExistingAttendanceAsync(entry.TeacherStudentId, occurrence.Id);

                if (existing is not null)
                {
                    if (existing.Status == entry.Status)
                    {
                        // Already synced with same status — treat as success
                        successCount++;
                        result.EntryResults.Add(new SyncEntryResultDto
                        {
                            ClientEntryId = entry.ClientEntryId,
                            Success = true,
                            IsConflict = false
                        });
                        continue;
                    }

                    // Not every differing row is a HUMAN conflict. Two server rows are non-final
                    // states that MarkAttendanceAsync already overwrites/resolves in place on the
                    // interactive path (see its step 5):
                    //   • a system-written auto-absent from the nightly sweep — an inference, never
                    //     a tutor decision, and a real mark must beat it;
                    //   • an unresolved Held — a deferred state a later Present/Absent resolves.
                    // This batch path used to short-circuit BEFORE reaching that logic, so a class
                    // marked offline in the evening came back as a wall of unresolvable conflicts
                    // the morning after the sweep ran: nothing was recorded, the students stayed
                    // absent, and the ops parked in the sync centre forever. Fall through to the
                    // SHARED mark logic for exactly those two rows; a genuine tutor record with a
                    // different status stays a conflict for a human to resolve.
                    bool serverRowIsReplaceable =
                        (existing.IsAutoAbsent && existing.Status == AttendanceStatus.Absent)
                        || existing.Status == AttendanceStatus.Held;

                    if (!serverRowIsReplaceable)
                    {
                        // Conflict: server has a different status the tutor chose
                        conflictCount++;
                        var student = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(
                            entry.TeacherStudentId, dto.TeacherId);
                        result.EntryResults.Add(new SyncEntryResultDto
                        {
                            ClientEntryId = entry.ClientEntryId,
                            Success = false,
                            IsConflict = true,
                            ServerRecord = MapToRecordDto(existing,
                                existing.StudentName ?? student?.StudentName ?? "Unknown",
                                existing.StudentCode ?? student?.StudentCode ?? "")
                        });
                        continue;
                    }
                }

                // No existing record (or a replaceable auto-absent / Held one) — the shared mark
                // logic creates, overwrites or resolves as appropriate.
                affectedOccurrenceIds.Add(occurrence.Id);
                var markDto = new MarkAttendanceDto
                {
                    TeacherId = dto.TeacherId,
                    SessionId = entry.SessionId,
                    TeacherStudentId = entry.TeacherStudentId,
                    Status = entry.Status,
                    AttendanceMethod = entry.AttendanceMethod,
                    OccurrenceDate = entry.OccurrenceDate,
                    RecordedByUserId = dto.RecordedByUserId,
                    // A REPLAY IS NEVER WITHHELD FOR A PROMPT (2026-09-10).
                    //
                    // History: this used to pass `entry.AbsenceAlertConfirmed` through, so a mark
                    // for a student with a non-zero absence counter came back as
                    // RequiresAbsenceConfirmation with NOTHING written. That is fine when a human
                    // is watching — but a background drain has no one to ask, and the app cannot
                    // know in advance: it pre-confirms from a CACHED list row whose
                    // `wasAbsentLastSession` predates the nightly sweep, and a tutor who turned the
                    // absence pop-up off never pre-confirms at all. The result was a silent,
                    // self-reinforcing loss that hit EXACTLY the students who were absent last time
                    // — the mark vanished, so they stayed absent, so it happened again next week.
                    //
                    // Recording the mark and reporting the alert afterwards costs nothing the
                    // prompt was protecting: the alert exists to make the tutor follow up, which
                    // the app now does from the sync centre (AbsenceAlertRaised + AbsenceAlertInfo
                    // below). REQ-ATT-057/058 keep their teeth on the INTERACTIVE path
                    // (MarkAttendanceAsync via POST mark), which is untouched.
                    AbsenceAlertConfirmed = true
                };

                var markResult = await MarkAttendanceAsync(markDto);
                if (markResult.IsSuccess)
                {
                    var markData = markResult.Data!;

                    // Defensive only: with AbsenceAlertConfirmed forced true above, MarkAttendanceAsync
                    // can no longer return "alert pending, nothing written". If a future change ever
                    // reintroduces that shape, report it honestly (needs confirmation) rather than
                    // claiming a success that recorded nothing — never silently drop the mark.
                    if (markData.HasAbsenceAlert && markData.Record is null && !markData.IsDuplicate)
                    {
                        var pendingStudent = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(
                            entry.TeacherStudentId, dto.TeacherId);

                        result.RequiresConfirmationCount++;
                        result.EntryResults.Add(new SyncEntryResultDto
                        {
                            ClientEntryId = entry.ClientEntryId,
                            Success = false,
                            IsConflict = false,
                            RequiresAbsenceConfirmation = true,
                            AbsenceAlertInfo = BuildAbsenceAlertInfo(entry.TeacherStudentId, pendingStudent, markData)
                        });
                        continue;
                    }

                    successCount++;
                    var syncedEntry = new SyncEntryResultDto
                    {
                        ClientEntryId = entry.ClientEntryId,
                        Success = true,
                        IsConflict = false
                    };

                    // The mark IS recorded. When the student also carries an absence history, hand the
                    // detail back so the app can raise the same follow-up the interactive prompt would
                    // have — after the fact, rather than in place of the record.
                    if (markData.HasAbsenceAlert && markData.Record is not null)
                    {
                        var alertStudent = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(
                            entry.TeacherStudentId, dto.TeacherId);
                        syncedEntry.AbsenceAlertRaised = true;
                        syncedEntry.AbsenceAlertInfo =
                            BuildAbsenceAlertInfo(entry.TeacherStudentId, alertStudent, markData);
                    }

                    result.EntryResults.Add(syncedEntry);
                }
                else
                {
                    failedCount++;
                    result.EntryResults.Add(new SyncEntryResultDto
                    {
                        ClientEntryId = entry.ClientEntryId,
                        Success = false,
                        IsConflict = false,
                        ErrorMessage = markResult.Message ?? "Unknown error during sync",
                        ErrorCode = markResult.Code
                    });
                }
            }

            result.SuccessCount = successCount;
            result.ConflictCount = conflictCount;
            result.FailedCount = failedCount;

            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            // Exams: reconcile any during-session exam on the occurrences that received offline marks
            // (post-commit, best-effort) — closes the nested-transaction gap where the inner mark's own
            // exam sync was skipped.
            if (ownsTransaction)
                foreach (var affectedOccurrenceId in affectedOccurrenceIds)
                    await _examAttendanceSync.ReconcileExamsForSessionOccurrenceAsync(
                        dto.TeacherId, affectedOccurrenceId, dto.RecordedByUserId ?? dto.TeacherId);

            return Result<SyncResultDto>.Success(
                result, _localizer, AttendanceConstants.Messages.SyncCompleted);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Absence-alert payload for one synced entry. Shared by the informational path (the mark was
    /// recorded and the tutor should still follow up) and the defensive pending path, so both
    /// describe the student identically. FIX R1: the STUDENT's name, never
    /// <c>LastAbsenceSessionName</c>, which is a session.
    /// </summary>
    private static AbsenceAlertStudentDto BuildAbsenceAlertInfo(
        long teacherStudentId, TeacherStudent? student, MarkAttendanceResultDto markData) => new()
        {
            TeacherStudentId = teacherStudentId,
            StudentName = student?.StudentName ?? "Unknown",
            StudentCode = student?.StudentCode ?? "",
            ConsecutiveAbsences = markData.ConsecutiveAbsences,
            LastAbsenceDate = markData.LastAbsenceDate,
            LastAbsenceSessionName = markData.LastAbsenceSessionName,
            WasCrossSession = markData.LastAbsenceWasCrossSession
        };

    // ══════════════════════════════════════════════
    // STUDENT/PARENT VIEW ACCESS
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<MonthlyAttendanceSummaryDto>> GetStudentViewAttendanceAsync(
        long teacherId, long teacherStudentId, StudentTimelineMonthRequest request, AttendanceViewerType viewer)
    {
        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);
        if (!IsAttendanceVisibleTo(config, viewer))
            return Result<MonthlyAttendanceSummaryDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceVisibilityDisabled, HttpStatusCode.Forbidden);

        return await GetStudentTimelineMonthAsync(teacherId, teacherStudentId, request);
    }
    /// <inheritdoc />
    public async Task<Result<StudentAttendanceSummaryDto>> GetStudentViewAttendanceSummaryAsync(
        long teacherId, long teacherStudentId, AttendanceViewerType viewer)
    {
        var config = await _unitOfWork.Users.GetConfigurationByTeacherIdAsync(teacherId);
        if (!IsAttendanceVisibleTo(config, viewer))
            return Result<StudentAttendanceSummaryDto>.Failure(
                _localizer, AttendanceConstants.Messages.AttendanceVisibilityDisabled, HttpStatusCode.Forbidden);

        return await GetStudentAttendanceSummaryAsync(teacherId, teacherStudentId);
    }

    // ══════════════════════════════════════════════
    // INTEGRATION HOOKS
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<bool>> OnStudentAssignedToSessionAsync(
        long teacherId, long teacherStudentId, long sessionId, string sessionName)
    {
        // AUDIT FIX Step 14: Deactivate any existing active assignment first (idempotent).
        // Prevents dual active assignments even if caller forgot to unassign (REQ-ATT-048 safety).
        var existingAssignment = await _unitOfWork.AttendanceRepo
            .GetActiveAssignmentAsync(teacherStudentId);
        if (existingAssignment is not null)
        {
            existingAssignment.IsActive = false;
            existingAssignment.UnassignedAt = DateTime.UtcNow;
            await _unitOfWork.AttendanceRepo.UpdateAssignmentAsync(existingAssignment);
        }

        // AUDIT FIX Step 1: Fetch student to populate denormalized fields
        var student = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(teacherStudentId, teacherId);

        var assignment = new StudentSessionAssignment
        {
            TeacherId = teacherId,
            TeacherStudentId = teacherStudentId,
            SessionId = sessionId,
            SessionName = sessionName,
            // AUDIT FIX Step 1: Denormalized student fields — survive student permanent purge
            StudentName = student?.StudentName,
            StudentCode = student?.StudentCode,
            AssignedAt = DateTime.UtcNow,
            IsActive = true,
            CreateAt = DateTime.UtcNow
        };
        await _unitOfWork.AttendanceRepo.AddAssignmentAsync(assignment);

        var counter = await _unitOfWork.AttendanceRepo
            .GetAbsenceCounterAsync(teacherId, teacherStudentId);
        if (counter is null)
        {
            counter = new StudentAbsenceCounter
            {
                TeacherId = teacherId,
                TeacherStudentId = teacherStudentId,
                ConsecutiveAbsences = 0,
                TotalAbsences = 0,
                TotalPresent = 0,
                TotalOccurrences = 0,
                CreateAt = DateTime.UtcNow
            };
            await _unitOfWork.AttendanceRepo.AddAbsenceCounterAsync(counter);
        }

        return Result<bool>.Success(true, _localizer, AttendanceConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> OnStudentUnassignedFromSessionAsync(
        long teacherId, long teacherStudentId)
    {
        var activeAssignment = await _unitOfWork.AttendanceRepo
            .GetActiveAssignmentAsync(teacherStudentId);
        if (activeAssignment is not null)
        {
            activeAssignment.IsActive = false;
            activeAssignment.UnassignedAt = DateTime.UtcNow;
            await _unitOfWork.AttendanceRepo.UpdateAssignmentAsync(activeAssignment);
        }

        return Result<bool>.Success(true, _localizer, AttendanceConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> OnSessionDeletingAsync(long teacherId, long sessionId)
    {
        // Step 1.2: All these now use ExecuteUpdateAsync — no in-memory loading
        await _unitOfWork.AttendanceRepo.NullifyOccurrenceReferencesForSessionAsync(sessionId);
        await _unitOfWork.AttendanceRepo.NullifySessionIdOnRecordsForSessionAsync(sessionId);
        await _unitOfWork.AttendanceRepo.DeactivateAssignmentsBySessionAsync(sessionId);
        await _unitOfWork.AttendanceRepo.DeleteOccurrencesBySessionAsync(sessionId);

        return Result<bool>.Success(true, _localizer, AttendanceConstants.Messages.Success);
    }

    /// <inheritdoc />
    /// Step 1.1: Nullifies FK references on AttendanceRecords before student hard-delete.
    public async Task<Result<bool>> OnStudentPermanentlyDeletedAsync(long teacherStudentId)
    {
        await _unitOfWork.AttendanceRepo.DeleteAbsenceCountersByStudentAsync(teacherStudentId);
        await _unitOfWork.AttendanceRepo.DeactivateAssignmentsByStudentAsync(teacherStudentId);

        // Step 1.1: Nullify FK references on AttendanceRecords.
        // This prevents FK violation when TeacherStudent row is hard-deleted.
        // Denormalized StudentName/StudentCode remain intact for historical display.
        await _unitOfWork.AttendanceRepo.NullifyStudentReferencesOnRecordsAsync(teacherStudentId);

        return Result<bool>.Success(true, _localizer, AttendanceConstants.Messages.Success);
    }

    // ══════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════

    /// <summary>
    /// ATT-2 / ATT-3: Validates a caller-supplied attendance status for the take / edit / add paths.
    /// Returns a localized-message KEY when invalid, or null when valid:
    ///   • null   → the field was omitted (the DTO enum is nullable so omission is detectable — CC-6)
    ///              → <c>AttendanceStatusRequired</c> (previously an omitted status silently wrote Absent).
    ///   • Held / CrossSessionPresent / any out-of-range value (e.g. 99) → <c>InvalidAttendanceStatus</c>.
    /// Only Present and Absent may be supplied by a caller; Held comes from HoldStudentAsync and
    /// CrossSessionPresent is derived internally for cross-session marks.
    /// </summary>
    private static string? ValidateCallerStatus(AttendanceStatus? status)
    {
        if (status is null)
            return AttendanceConstants.Messages.AttendanceStatusRequired;
        if (status is not (AttendanceStatus.Present or AttendanceStatus.Absent))
            return AttendanceConstants.Messages.InvalidAttendanceStatus;
        return null;
    }

    /// <summary>
    /// Per-item status validation for the NEW <c>mark-bulk</c> <c>items</c> path ONLY. Unlike
    /// <see cref="ValidateCallerStatus"/> (single mark / add / edit / legacy bulk — Present/Absent only),
    /// this ALSO accepts <c>Held</c>, since Hold-in-bulk is driven per item here. <c>CrossSessionPresent</c>
    /// and any out-of-range value stay rejected. Returns a message key on failure, null when valid:
    ///   • null → <c>AttendanceStatusRequired</c> (omission stays detectable — CC-6).
    ///   • not Present/Absent/Held → <c>InvalidAttendanceStatus</c>.
    /// </summary>
    private static string? ValidateBulkItemStatus(AttendanceStatus? status)
    {
        if (status is null)
            return AttendanceConstants.Messages.AttendanceStatusRequired;
        if (status is not (AttendanceStatus.Present or AttendanceStatus.Absent or AttendanceStatus.Held))
            return AttendanceConstants.Messages.InvalidAttendanceStatus;
        return null;
    }

    private async Task UpdateAbsenceCounterForNewRecord(
        long teacherId, long teacherStudentId,
        AttendanceStatus status, DateTime date, string sessionName, long sessionId)
    {
        // FIX H7: Retry loop for optimistic concurrency on RowVersion.
        // StudentAbsenceCounter uses [Timestamp] RowVersion but previously had no retry logic.
        // Two concurrent requests (e.g., two assistants on linked sessions) would cause
        // DbUpdateConcurrencyException to propagate unhandled.
        for (int attempt = 0; attempt < AttendanceConstants.MaxConcurrencyRetries; attempt++)
        {
            try
            {
                // FIX R2: Use AsNoTracking fetch then re-attach to avoid stale RowVersion.
                // On retry, the previous attempt left the stale entity in the change tracker.
                // GetAbsenceCounterFreshAsync bypasses the tracker to get the latest RowVersion.
                var counter = await _unitOfWork.AttendanceRepo
                    .GetAbsenceCounterFreshAsync(teacherId, teacherStudentId);

                if (counter is null)
                {
                    counter = new StudentAbsenceCounter
                    {
                        TeacherId = teacherId,
                        TeacherStudentId = teacherStudentId,
                        CreateAt = DateTime.UtcNow
                    };
                    await _unitOfWork.AttendanceRepo.AddAbsenceCounterAsync(counter);
                }

                counter.TotalOccurrences++;

                if (status == AttendanceStatus.Absent)
                {
                    counter.ConsecutiveAbsences++;
                    counter.TotalAbsences++;
                    counter.LastAbsenceDate = date;
                    counter.LastAbsenceSessionName = sessionName;
                    counter.LastAbsenceSessionId = sessionId;
                }
                else // Present or CrossSessionPresent
                {
                    counter.ConsecutiveAbsences = 0;
                    counter.TotalPresent++;
                    counter.LastAttendanceDate = date;
                }

                await _unitOfWork.AttendanceRepo.UpdateAbsenceCounterAsync(counter);
                return; // Success — exit retry loop
            }
            catch (Exception ex) when (IsConcurrencyException(ex))
            {
                if (attempt >= AttendanceConstants.MaxConcurrencyRetries - 1)
                {
                    _logger.LogError(ex,
                        "Concurrency conflict on absence counter for student {StudentId} exceeded max retries ({Max})",
                        teacherStudentId, AttendanceConstants.MaxConcurrencyRetries);
                    throw; // Final attempt — propagate to caller
                }

                _logger.LogWarning(
                    "Concurrency conflict updating absence counter for student {StudentId}, retry {Attempt}",
                    teacherStudentId, attempt + 1);
                await Task.Delay(50 * (attempt + 1)); // Brief backoff
            }
        }
    }

    private async Task UpdateAbsenceCounterForAddedRecord(
        long teacherId, long teacherStudentId,
        AttendanceStatus status, DateTime date, string sessionName, long sessionId)
    {
        var counter = await _unitOfWork.AttendanceRepo
            .GetAbsenceCounterAsync(teacherId, teacherStudentId);

        if (counter is null)
        {
            counter = new StudentAbsenceCounter
            {
                TeacherId = teacherId,
                TeacherStudentId = teacherStudentId,
                CreateAt = DateTime.UtcNow
            };
            await _unitOfWork.AttendanceRepo.AddAbsenceCounterAsync(counter);
        }

        counter.TotalOccurrences++;
        if (status == AttendanceStatus.Absent)
        {
            counter.TotalAbsences++;
            if (!counter.LastAbsenceDate.HasValue || date > counter.LastAbsenceDate.Value)
            {
                counter.LastAbsenceDate = date;
                counter.LastAbsenceSessionName = sessionName;
                counter.LastAbsenceSessionId = sessionId;
            }
        }
        else
        {
            counter.TotalPresent++;
            if (!counter.LastAttendanceDate.HasValue || date > counter.LastAttendanceDate.Value)
                counter.LastAttendanceDate = date;
        }

        // Recalculate consecutive from actual records (out-of-order safe)
        counter.ConsecutiveAbsences = await _unitOfWork.AttendanceRepo
            .RecalculateConsecutiveAbsencesAsync(teacherStudentId);

        await _unitOfWork.AttendanceRepo.UpdateAbsenceCounterAsync(counter);
    }

    /// <summary>
    /// Recomputes the student's absence-counter totals (TotalOccurrences / TotalPresent /
    /// TotalAbsences) and the consecutive-absence streak as a pure aggregate of their non-Held
    /// attendance records — the single source of truth. Called by the edit and delete paths AFTER the
    /// record change is flushed (ATT-9), so the counter can never drift from the records.
    ///
    /// Replaces the previous ±1 transition maintenance, which silently skewed the totals for the
    /// transitions it did not enumerate — e.g. a CrossSessionPresent record edited to Absent (left
    /// TotalPresent too high), or a Held record deleted (wrongly decremented TotalOccurrences). That
    /// was the pre-existing StudentAbsenceCounter drift (a student reading &gt;100% attendance).
    ///
    /// The append paths (mark / bulk / add) increment correctly for a fresh record, so recomputing
    /// here on every non-append write keeps the whole counter consistent AND self-heals any historical
    /// skew on the student's next edit/delete. The Last* fields (LastAbsenceDate/SessionName/SessionId
    /// and LastAttendanceDate) are ALSO recomputed from records here, so editing or deleting the
    /// most-recent absence/attendance no longer leaves them pointing at a now-changed or removed
    /// occurrence — the "was absent last session" warning stays truthful. No-op when the counter row
    /// is absent.
    /// </summary>
    private async Task RecalculateAbsenceCounterFromRecordsAsync(long teacherId, long teacherStudentId)
    {
        var counter = await _unitOfWork.AttendanceRepo
            .GetAbsenceCounterAsync(teacherId, teacherStudentId);
        if (counter is null) return;

        var (totalOccurrences, totalPresent, totalAbsences) = await _unitOfWork.AttendanceRepo
            .GetCounterAggregatesAsync(teacherStudentId);

        counter.TotalOccurrences = totalOccurrences;
        counter.TotalPresent = totalPresent;
        counter.TotalAbsences = totalAbsences;
        counter.ConsecutiveAbsences = await _unitOfWork.AttendanceRepo
            .RecalculateConsecutiveAbsencesAsync(teacherStudentId);

        // Recompute the Last* fields from records too, so editing/deleting the most-recent
        // absence updates the "last absent on … in …" shown on the take-attendance row, the
        // absence-overview list and the mark-absence alert (previously left stale, pointing at a
        // now-changed or deleted occurrence).
        var last = await _unitOfWork.AttendanceRepo
            .GetLastAbsenceAndAttendanceAsync(teacherStudentId);
        counter.LastAbsenceDate = last.LastAbsenceDate;
        counter.LastAbsenceSessionName = last.LastAbsenceSessionName;
        counter.LastAbsenceSessionId = last.LastAbsenceSessionId;
        counter.LastAttendanceDate = last.LastAttendanceDate;

        await _unitOfWork.AttendanceRepo.UpdateAbsenceCounterAsync(counter);
    }

    /// <summary>
    /// Reconciles an EXISTING attendance record for an incoming present/absent mark, in place, instead
    /// of rejecting it as a duplicate. Two cases feed this (see the take paths):
    ///   • OVERWRITE — a system-written <c>IsAutoAbsent</c> record on the SAME occurrence becomes the
    ///     freshly-marked status, so a teacher re-marking the morning after the nightly sweep isn't
    ///     forced through Edit Attendance.
    ///   • FLIP — an <see cref="AttendanceStatus.Absent"/> record on an EQUIVALENT occurrence becomes
    ///     <see cref="AttendanceStatus.CrossSessionPresent"/> (with the attended session/date) because
    ///     the student was scanned present on a linked session's equivalent occurrence — the same
    ///     logical class instance, so it must not read as a fresh absence.
    /// Writes an <see cref="AttendanceEditLog"/>, clears <c>IsAutoAbsent</c>, and flags the record
    /// edited. Mutates the tracked record only — the caller owns flush / counter recompute / occurrence
    /// refresh / commit (so the counter recompute drops the stale consecutive-absence streak and the
    /// "was absent last time" alert clears).
    /// </summary>
    private async Task ApplyReconciliationAsync(
        AttendanceRecord record, AttendanceStatus newStatus, string editReason, bool isCrossFlip,
        long? crossSessionId, string? crossSessionName, DateTime? crossOccurrenceDate, long? editedByUserId)
    {
        var now = DateTime.UtcNow;
        await _unitOfWork.AttendanceRepo.AddEditLogAsync(new AttendanceEditLog
        {
            AttendanceRecordId = record.Id,
            PreviousStatus = record.Status,
            NewStatus = newStatus,
            PreviousAttendanceMethod = record.AttendanceMethod,
            NewAttendanceMethod = record.AttendanceMethod,
            EditedAt = now,
            EditedByUserId = editedByUserId,
            EditReason = editReason,
            CreateAt = now
        });

        record.Status = newStatus;
        record.IsAutoAbsent = false;
        if (isCrossFlip)
        {
            record.IsCrossSession = true;
            record.CrossSessionId = crossSessionId;
            record.CrossSessionName = crossSessionName;
            record.CrossSessionOccurrenceDate = crossOccurrenceDate;
        }
        record.IsEdited = true;
        record.LastEditedAt = now;
        record.LastEditedByUserId = editedByUserId;
        await _unitOfWork.AttendanceRepo.UpdateAttendanceRecordAsync(record);
    }

    /// <summary>
    /// Single-mark wrapper around <see cref="ApplyReconciliationAsync"/>: applies the in-place
    /// overwrite/flip, recomputes the counter + occurrence status, commits, then (post-commit) fires the
    /// matching attended/absent notification and exam reconcile — mirroring MarkAttendanceAsync's tail so
    /// the reconciled mark behaves exactly like a fresh one.
    /// </summary>
    private async Task<Result<MarkAttendanceResultDto>> ReconcileSingleMarkAsync(
        AttendanceRecord record, AttendanceStatus newStatus, Session session, MarkAttendanceDto dto,
        string editReason, bool isCrossFlip, long? crossSessionId, string? crossSessionName,
        DateTime? crossOccurrenceDate, SessionOccurrence? trackedOccurrence)
    {
        long? teacherStudentId = record.TeacherStudentId;

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction)
            await _unitOfWork.BeginTransactionAsync();

        try
        {
            await ApplyReconciliationAsync(record, newStatus, editReason, isCrossFlip,
                crossSessionId, crossSessionName, crossOccurrenceDate, dto.RecordedByUserId);

            // Flush BEFORE the counter/occurrence recompute — both read back from the DB (ATT-9).
            await _unitOfWork.SaveChangesAsync();

            int consecutive = 0;
            if (teacherStudentId.HasValue)
            {
                await RecalculateAbsenceCounterFromRecordsAsync(dto.TeacherId, teacherStudentId.Value);
                var counter = await _unitOfWork.AttendanceRepo
                    .GetAbsenceCounterAsync(dto.TeacherId, teacherStudentId.Value);
                consecutive = counter?.ConsecutiveAbsences ?? 0;
            }

            // Refresh the occurrence status. Prefer the caller's already-TRACKED instance (the selected
            // occurrence in the overwrite case) — reloading it AsNoTracking would collide with the
            // tracked copy. The flip case passes null and the record's (distinct, untracked) home
            // occurrence is loaded by id.
            var occToRefresh = trackedOccurrence;
            if (occToRefresh is null && record.SessionOccurrenceId.HasValue)
                occToRefresh = await _unitOfWork.AttendanceRepo
                    .GetOccurrenceByIdTrackedAsync(record.SessionOccurrenceId.Value, dto.TeacherId);
            if (occToRefresh is not null)
                await UpdateOccurrenceStatusAsync(occToRefresh);

            await _unitOfWork.SaveChangesAsync();

            if (ownsTransaction)
                await _unitOfWork.CommitAsync();

            if (ownsTransaction && teacherStudentId.HasValue)
            {
                if (newStatus == AttendanceStatus.Absent)
                    await _attendanceNotifier.OnStudentAbsentAsync(
                        dto.TeacherId, teacherStudentId.Value, dto.SessionId, session.SessionName,
                        record.OccurrenceDate, consecutive);
                else
                    await _attendanceNotifier.OnStudentAttendedAsync(
                        dto.TeacherId, teacherStudentId.Value, dto.SessionId, session.SessionName,
                        record.OccurrenceDate);

                if (record.SessionOccurrenceId.HasValue)
                    await _examAttendanceSync.ReconcileExamsForSessionOccurrenceAsync(
                        dto.TeacherId, record.SessionOccurrenceId.Value, dto.RecordedByUserId ?? dto.TeacherId);
            }

            return Result<MarkAttendanceResultDto>.Success(new MarkAttendanceResultDto
            {
                Record = MapToRecordDto(record, record.StudentName ?? "Unknown", record.StudentCode ?? "")
            }, _localizer, AttendanceConstants.Messages.AttendanceMarkedSuccess);
        }
        catch
        {
            if (ownsTransaction)
                await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// The resolved landing target for a single attendance mark. Shared by EVERY attendance-taking
    /// path (single mark, bulk, add-record, hold) so cross-session behaviour is identical everywhere.
    /// A cross-session (linked) student's record lands on their HOME session's equivalent-slot
    /// occurrence; a same-session student lands on the selected occurrence.
    /// </summary>
    private readonly record struct MarkTarget(
        bool IsCrossSession,
        long RecordOccurrenceId,
        DateTime RecordOccurrenceDate,
        SessionOccurrence LandedOccurrence,
        long? RecordSessionId,
        string? RecordSessionName,
        long? CrossSessionId,
        string? CrossSessionName,
        DateTime? CrossSessionOccurrenceDate);

    /// <summary>
    /// Resolves where a mark lands and its cross-session field values, applying the home-equivalent
    /// slot remap (same WeekStartDate + DayPositionIndex as the attended occurrence). Returns null when
    /// the student is a cross-session visitor whose home session is NOT linked to the selected session
    /// (BR-ATT-003) — the caller rejects (single/add) or skips (bulk). Callers own the Status: a
    /// cross-session PRESENCE mark becomes CrossSessionPresent; a hold stays Held.
    /// </summary>
    private async Task<MarkTarget?> ResolveMarkTargetAsync(
        SessionOccurrence selectedOccurrence, Session selectedSession,
        StudentSessionAssignment activeAssignment, IReadOnlyCollection<long> linkedSessionIds,
        DateTime physicalDate)
    {
        bool isCrossSession = activeAssignment.SessionId.HasValue
            && activeAssignment.SessionId.Value != selectedSession.Id;

        if (!isCrossSession)
        {
            return new MarkTarget(false, selectedOccurrence.Id, physicalDate, selectedOccurrence,
                selectedSession.Id, selectedSession.SessionName, null, null, null);
        }

        // BR-ATT-003: a cross-session mark is only valid when the student's home session is linked.
        if (!linkedSessionIds.Contains(activeAssignment.SessionId!.Value))
            return null;

        // Slot remap onto the HOME session's equivalent occurrence so home-keyed reads stay correct.
        // Fallback to the attended occurrence when the home has no occurrence for the slot yet.
        long recordOccurrenceId = selectedOccurrence.Id;
        DateTime recordOccurrenceDate = physicalDate;
        SessionOccurrence landedOccurrence = selectedOccurrence;

        var homeOccurrence = await _unitOfWork.AttendanceRepo.GetOccurrenceBySessionAndSlotAsync(
            activeAssignment.SessionId.Value, selectedOccurrence.WeekStartDate, selectedOccurrence.DayPositionIndex);
        if (homeOccurrence is not null)
        {
            recordOccurrenceId = homeOccurrence.Id;
            recordOccurrenceDate = homeOccurrence.OccurrenceDate;
            landedOccurrence = homeOccurrence;
        }

        return new MarkTarget(true, recordOccurrenceId, recordOccurrenceDate, landedOccurrence,
            activeAssignment.SessionId, activeAssignment.SessionName,
            selectedSession.Id, selectedSession.SessionName, physicalDate);
    }

    private async Task UpdateOccurrenceStatusAsync(SessionOccurrence occurrence)
    {
        var records = await _unitOfWork.AttendanceRepo.GetRecordsByOccurrenceAsync(occurrence.Id);
        var assignments = await _unitOfWork.AttendanceRepo.GetActiveAssignmentsBySessionAsync(occurrence.SessionId);

        // Step 3.1: Exclude Held records. Also count only records belonging to THIS occurrence's own
        // session (denormalized SessionId) so a cross-session visitor's record stored on the attended
        // occurrence (partial-week fallback) — which carries its HOME SessionId — never inflates this
        // occurrence's marked count against its own (home) roster.
        int markedCount = records.Count(r =>
            r.Status != AttendanceStatus.Held && r.SessionId == occurrence.SessionId);
        int totalStudents = assignments.Count;

        if (markedCount == 0)
            occurrence.Status = OccurrenceStatus.Pending;
        else if (markedCount >= totalStudents && totalStudents > 0)
            occurrence.Status = OccurrenceStatus.Completed;
        else
            occurrence.Status = OccurrenceStatus.InProgress;

        await _unitOfWork.AttendanceRepo.UpdateOccurrenceAsync(occurrence);
    }

    private async Task<StudentAttendanceSummaryDto?> BuildStudentSummary(
        long teacherId, long teacherStudentId)
    {
        var student = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(teacherStudentId, teacherId);
        if (student is null) return null;

        var counter = await _unitOfWork.AttendanceRepo
            .GetAbsenceCounterAsync(teacherId, teacherStudentId);

        var assignments = await _unitOfWork.AttendanceRepo
            .GetAssignmentsByStudentAsync(teacherStudentId);

        var periods = assignments.Select(a => new AssignmentPeriodDto
        {
            StudentSessionAssignmentId = a.Id,
            SessionId = a.SessionId,
            SessionName = a.SessionName,
            AssignedAt = a.AssignedAt,
            UnassignedAt = a.UnassignedAt,
            IsActive = a.IsActive
        }).ToList();

        int totalOcc = counter?.TotalOccurrences ?? 0;
        int totalPresent = counter?.TotalPresent ?? 0;

        return new StudentAttendanceSummaryDto
        {
            TeacherStudentId = teacherStudentId,
            StudentName = student.StudentName,
            StudentCode = student.StudentCode,
            TotalOccurrences = totalOcc,
            TotalAbsences = counter?.TotalAbsences ?? 0,
            AttendancePercentage = totalOcc > 0
                ? Math.Round((decimal)totalPresent / totalOcc * 100, 1) : 0,
            ConsecutiveAbsences = counter?.ConsecutiveAbsences ?? 0,
            AssignmentPeriods = periods
        };
    }

    /// <summary>
    /// Maps an AttendanceRecord entity to its output DTO.
    /// Step 7.2: Prefers denormalized fields, falls back to navigation property.
    /// </summary>
    private static AttendanceRecordDto MapToRecordDto(
        AttendanceRecord record, string studentName, string studentCode)
    {
        return new AttendanceRecordDto
        {
            Id = record.Id,
            TeacherStudentId = record.TeacherStudentId ?? 0,
            StudentName = record.StudentName ?? studentName,
            StudentCode = record.StudentCode ?? studentCode,
            SessionOccurrenceId = record.SessionOccurrenceId,
            SessionId = record.SessionId,
            SessionName = record.SessionName,
            OccurrenceDate = record.OccurrenceDate,
            Status = record.Status,
            AttendanceMethod = record.AttendanceMethod,
            IsCrossSession = record.IsCrossSession,
            CrossSessionId = record.CrossSessionId,
            CrossSessionName = record.CrossSessionName,
            CrossSessionOccurrenceDate = record.CrossSessionOccurrenceDate,
            RecordedAt = record.RecordedAt,
            IsEdited = record.IsEdited,
            LastEditedAt = record.LastEditedAt
        };
    }

    

    /// <summary>
    /// FIX H7: Architecture-safe concurrency exception detection.
    /// The Application layer cannot reference Microsoft.EntityFrameworkCore directly
    /// (that would violate Onion Architecture — EF Core is an Infrastructure concern).
    /// Instead we check the exception type name to detect DbUpdateConcurrencyException
    /// without taking a compile-time dependency on the EF Core assembly.
    /// </summary>
    private static bool IsConcurrencyException(Exception ex)
    {
        // Walk the exception chain — EF Core may wrap the concurrency exception
        var current = ex;
        while (current is not null)
        {
            if (current.GetType().Name == "DbUpdateConcurrencyException")
                return true;
            current = current.InnerException;
        }
        return false;
    }
    /// <summary>
    /// Per-viewer visibility gate (AAM-FR-04.8 vs AAM-FR-04.9 — independent toggles).
    /// Each caller is checked against only its own flag. Fail-closed when no
    /// configuration row exists (preserves the previous deny-on-null behavior).
    /// </summary>
    private static bool IsAttendanceVisibleTo(TeacherConfiguration? config, AttendanceViewerType viewer)
    {
        if (config is null) return false;
        return viewer switch
        {
            AttendanceViewerType.Student => config.StudentVisibilityAttendance,
            AttendanceViewerType.Parent => config.ParentVisibilityAttendance,
            _ => false
        };
    }
    private string? BuildAbsenceConsequenceLabel(int consecutiveAbsences, int threshold)
    {
        if (consecutiveAbsences <= 0)
            return null;

        return consecutiveAbsences >= threshold
            ? _localizer[AttendanceConstants.Messages.AttendanceConsecutiveAbsenceAlertReached, consecutiveAbsences]
            : _localizer[AttendanceConstants.Messages.AttendanceConsecutiveAbsenceApproachingAlert, consecutiveAbsences, threshold];
    }
}