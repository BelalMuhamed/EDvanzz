using System.Net;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Exams;
using Edvanz.Application.Dtos.ExamHomework;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements the offline Exams module (<c>/api/exams</c>).
///
/// MODEL: an exam is an <see cref="AssignmentTemplate"/> of type <see cref="AssignmentType.Exam"/>
/// anchored to one or more sessions. Each anchored session produces exactly one
/// <see cref="AssignmentOccurrence"/> (carrying <c>SessionId</c>, and for DuringSession the linked
/// <c>SessionOccurrenceId</c>), and one <see cref="StudentAssignmentObligation"/> per student in
/// that session (or the supplied subset). This per-session shape is what lets the opened-exam
/// screen group by session and compute per-session statistics.
///
/// ARCHITECTURE: every DB operation goes through a named repo method on <see cref="IUnitOfWork"/>.
/// Student resolution reuses <c>IPaymentRepo.GetStudentIdsBySessionAsync</c> — the single source of
/// truth for "who's in a session".
/// </summary>
public class ExamService : IExamService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly ITimeZoneService _timeZoneService;
    private readonly IExamAttendanceSyncService _examAttendanceSync;
    private readonly IExamHomeworkService _examHomework;
    private readonly ISubscriptionGateService _subscriptionGate;

    public ExamService(
        IUnitOfWork unitOfWork,
        IStringLocalizer<Messages> localizer,
        ITimeZoneService timeZoneService,
        IExamAttendanceSyncService examAttendanceSync,
        IExamHomeworkService examHomework,
        ISubscriptionGateService subscriptionGate)
    {
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _timeZoneService = timeZoneService;
        _examAttendanceSync = examAttendanceSync;
        _examHomework = examHomework;
        _subscriptionGate = subscriptionGate;
    }

    /// <inheritdoc />
    public async Task<Result<ExamCreatedDto>> CreateExamAsync(
        long teacherId, long actingUserId, CreateExamDto dto)
    {
        // ── 0. Free-tier quota: paper exams (ModuleQuota table; subscribed teachers bypass) ──
        if (!await _subscriptionGate.CanCreateAsync(
                teacherId, Domain.Constants.ModuleQuotaKeys.Exams,
                () => _unitOfWork.ExamHomeworkRepo.CountExamTemplatesByTeacherAsync(teacherId)))
            return Result<ExamCreatedDto>.Failure(
                _localizer, Domain.Constants.SubscriptionConstants.Messages.SubscriptionRequired,
                HttpStatusCode.Forbidden);

        // ── 1. Scalar validation ─────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Trim().Length > 200)
            return Fail("ExamNameRequired");

        if (dto.Notes is not null && dto.Notes.Length > 2000)
            return Fail("AssignmentNotesTooLong");

        if (dto.MaxGrade <= 0m)
            return Fail("ExamRequiresMaxGrade");

        if (dto.SuccessScore < 0m)
            return Fail("PassingThresholdOutOfRange");

        // The core grade-range rule the tutor asked for: success can never exceed the max.
        if (dto.SuccessScore > dto.MaxGrade)
            return Fail("SuccessScoreExceedsMax");

        DateTime utcNow = DateTime.UtcNow;

        // ── 2. Dates + recipient + per-session plan (single pipeline shared with update) ──
        // DuringSession anchors EVERY resolved session to its own picked class occurrence
        // (dto.SessionOccurrences); SeparateTime applies the single dto.ExamDate to all.
        var build = await BuildSessionPlansAsync(
            teacherId, dto.DeliveryType, dto.ExamDate, dto.SessionOccurrences,
            dto.SessionIds, dto.GroupIds, dto.StudentIds);
        if (build.ErrorKey is not null)
            return Fail(build.ErrorKey, build.ErrorStatus, build.ErrorArgs);

        bool hasGroups = build.HasGroups;
        List<long> groupIds = build.GroupIds;
        List<SessionPlan> plans = build.Plans;

        // ── 3. Build the entity graph: template → session scopes → per-session occurrences → obligations ──
        var template = new AssignmentTemplate
        {
            TeacherId = teacherId,
            AssignmentType = AssignmentType.Exam,
            Name = dto.Name.Trim(),
            NameAr = null,
            Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
            IsRecurring = false,
            RecurrencePattern = RecurrencePattern.OneTime,
            IsRecurrenceStopped = false,
            TrackingMode = null,
            MaxGrade = dto.MaxGrade,
            PassingThreshold = dto.SuccessScore,
            ExamDeliveryType = dto.DeliveryType,
            CreatedByUserId = actingUserId,
            UpdatedAt = utcNow,
            CreateAt = utcNow,
        };

        // Persist the selection mode so the home screen can report "by sessions" vs "by groups":
        // group selection stores one SessionGroup scope per group; session selection stores one per session.
        var scopes = hasGroups
            ? groupIds.Select(gid => new AssignmentScope
            {
                TeacherId = teacherId,
                Template = template,
                ScopeType = AssignmentScopeType.SessionGroup,
                SessionGroupId = gid,
                CreateAt = utcNow,
            }).ToList()
            : plans.Select(p => new AssignmentScope
            {
                TeacherId = teacherId,
                Template = template,
                ScopeType = AssignmentScopeType.Session,
                SessionId = p.SessionId,
                CreateAt = utcNow,
            }).ToList();

        var occurrences = new List<AssignmentOccurrence>();
        var obligations = new List<StudentAssignmentObligation>();
        int occurrenceNumber = 1;
        foreach (var p in plans)
        {
            var occurrence = new AssignmentOccurrence
            {
                Template = template,
                TeacherId = teacherId,
                OccurrenceNumber = occurrenceNumber++,
                DueDate = p.DueDate,
                Status = AssignmentOccurrenceStatus.Pending,
                SessionId = p.SessionId,
                SessionOccurrenceId = p.SessionOccurrenceId,
                MaxGradeSnapshot = dto.MaxGrade,
                PassingThresholdSnapshot = dto.SuccessScore,
                TrackingModeSnapshot = null,
                TotalStudentCount = p.StudentIds.Count,
                CreateAt = utcNow,
            };
            p.Occurrence = occurrence;
            occurrences.Add(occurrence);

            foreach (var studentId in p.StudentIds)
            {
                obligations.Add(new StudentAssignmentObligation
                {
                    Occurrence = occurrence,
                    TeacherId = teacherId,
                    TeacherStudentId = studentId,
                    Status = ObligationStatus.Pending,
                    IsGradeEntered = false,
                    MarkedByScan = false,
                    UpdatedAt = utcNow,
                    CreateAt = utcNow,
                });
            }
        }

        // ── 4. Persist atomically ────────────────────────────────────────────
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            await _unitOfWork.ExamHomeworkRepo.AddTemplateAsync(template);
            await _unitOfWork.ExamHomeworkRepo.AddScopesRangeAsync(scopes);
            await _unitOfWork.ExamHomeworkRepo.AddOccurrencesRangeAsync(occurrences);
            await _unitOfWork.ExamHomeworkRepo.AddObligationsRangeAsync(obligations);

            await _unitOfWork.SaveChangesAsync();

            // For DuringSession exams, back-fill each occurrence's obligations from attendance already
            // recorded on its linked SessionOccurrence — INSIDE the transaction (strict) so the exam is
            // either fully in sync with the class or not created at all (no post-commit sticky half-state).
            // Live updates thereafter flow from AttendanceService via IExamAttendanceSyncService.
            if (dto.DeliveryType == ExamDeliveryType.DuringSession)
                foreach (var p in plans)
                    if (p.SessionOccurrenceId.HasValue)
                        await _examAttendanceSync.BackfillExamOccurrenceAsync(
                            teacherId, p.Occurrence!.Id, p.SessionOccurrenceId.Value, actingUserId);

            await _unitOfWork.CommitAsync();
        }
        catch (DbUpdateException)
        {
            await _unitOfWork.RollbackAsync();
            return Result<ExamCreatedDto>.Failure(_localizer, "DatabaseConflict", HttpStatusCode.Conflict);
        }
        catch
        {
            // A back-fill failure rolls the whole exam back rather than shipping a half-synced one.
            await _unitOfWork.RollbackAsync();
            throw;
        }

        var response = new ExamCreatedDto
        {
            ExamId = template.Id,
            Name = template.Name,
            DeliveryType = dto.DeliveryType,
            MaxGrade = dto.MaxGrade,
            SuccessScore = dto.SuccessScore,
            SessionsCount = occurrences.Count,
            StudentsAssigned = obligations.Count,
            Sessions = plans.Select(p => new ExamSessionCreatedDto
            {
                SessionId = p.SessionId,
                OccurrenceId = p.Occurrence!.Id,
                ExamDate = p.DueDate,
                StudentsAssigned = p.StudentIds.Count,
            }).ToList(),
        };

        return Result<ExamCreatedDto>.Success(response, _localizer, "ExamCreated", HttpStatusCode.Created);
    }

    /// <inheritdoc />
    public async Task<Result<ExamViewDto>> UpdateExamAsync(
        long teacherId, long actingUserId, long examId, UpdateExamDto dto)
    {
        // ── Guard: an EXISTING Exam-type template owned by this teacher (homework 404s here) ──
        var template = await _unitOfWork.ExamHomeworkRepo.GetTemplateByIdAndTeacherAsync(examId, teacherId);
        if (template is null || template.AssignmentType != AssignmentType.Exam)
            return Result<ExamViewDto>.Failure(_localizer, "ExamNotFound", HttpStatusCode.NotFound);

        // ── Scalar validation — the same grade rules as create ──
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Trim().Length > 200) return FailView("ExamNameRequired");
        if (dto.Notes is not null && dto.Notes.Length > 2000) return FailView("AssignmentNotesTooLong");
        if (dto.MaxGrade <= 0m) return FailView("ExamRequiresMaxGrade");
        if (dto.SuccessScore < 0m) return FailView("PassingThresholdOutOfRange");
        if (dto.SuccessScore > dto.MaxGrade) return FailView("SuccessScoreExceedsMax");
        DateTime utcNow = DateTime.UtcNow;

        // ── Dates + recipient + per-session plan (single pipeline shared with create) ──
        var build = await BuildSessionPlansAsync(
            teacherId, dto.DeliveryType, dto.ExamDate, dto.SessionOccurrences,
            dto.SessionIds, dto.GroupIds, dto.StudentIds);
        if (build.ErrorKey is not null)
            return FailView(build.ErrorKey, build.ErrorStatus, build.ErrorArgs);

        bool hasGroups = build.HasGroups;
        List<long> groupIds = build.GroupIds;
        List<SessionPlan> plans = build.Plans;

        var newSessionIds = plans.Select(p => p.SessionId).ToHashSet();

        // ── Load the CURRENT structure + whether any attendance/grade has been recorded ──
        var (currentOccurrences, _) = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrencesByTemplatePagedAsync(teacherId, examId, 1, 1000);
        var currentSessionIds = currentOccurrences.Where(o => o.SessionId.HasValue).Select(o => o.SessionId!.Value).ToHashSet();

        // Current per-session anchors — an exam holds exactly one occurrence per session by
        // construction, each with its own date (and linked class occurrence when DuringSession).
        var currentAnchors = new Dictionary<long, (DateTime Date, long? SessionOccurrenceId)>();
        foreach (var occ in currentOccurrences.Where(o => o.SessionId.HasValue))
            currentAnchors[occ.SessionId!.Value] = (occ.DueDate.Date, occ.SessionOccurrenceId);

        bool hasResults = false;
        foreach (var occ in currentOccurrences)
        {
            var obs = await _unitOfWork.ExamHomeworkRepo.GetObligationsByOccurrenceAsync(teacherId, occ.Id);
            if (obs.Any(ob => ob.Status != ObligationStatus.Pending || ob.IsGradeEntered))
            {
                hasResults = true;
                break;
            }
        }

        bool anchorsChanged = plans.Count != currentAnchors.Count
            || plans.Any(p => !currentAnchors.TryGetValue(p.SessionId, out var anchor)
                              || anchor.Date != p.DueDate.Date
                              || anchor.SessionOccurrenceId != p.SessionOccurrenceId);

        // ── A2: only the PROTECTED fields lock a results-bearing exam — the delivery type, the
        // per-session occurrence date picks / separate-time exam date, and the sessions/groups
        // assigned. Name, notes and grade bounds stay editable; the student roster is NOT protected
        // (it mirrors the class, so a student enrolling in / leaving the class never blocks an edit).
        bool structuralChange =
            dto.DeliveryType != template.ExamDeliveryType ||
            anchorsChanged ||
            !newSessionIds.SetEquals(currentSessionIds);

        // ── Policy: those protected fields can't change once attendance/grades exist ──
        if (structuralChange && hasResults)
            return FailView("ExamHasResultsCannotRestructure", HttpStatusCode.Conflict);

        if (structuralChange)
        {
            // Rebuild the graph IN PLACE (exam is result-free): update template scalars, purge the
            // old occurrences/obligations/scopes, and re-materialize from the new plan.
            template.Name = dto.Name.Trim();
            template.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
            template.MaxGrade = dto.MaxGrade;
            template.PassingThreshold = dto.SuccessScore;
            template.ExamDeliveryType = dto.DeliveryType;
            template.UpdatedAt = utcNow;

            var scopes = hasGroups
                ? groupIds.Select(gid => new AssignmentScope { TeacherId = teacherId, TemplateId = examId, ScopeType = AssignmentScopeType.SessionGroup, SessionGroupId = gid, CreateAt = utcNow }).ToList()
                : plans.Select(p => new AssignmentScope { TeacherId = teacherId, TemplateId = examId, ScopeType = AssignmentScopeType.Session, SessionId = p.SessionId, CreateAt = utcNow }).ToList();

            var occurrences = new List<AssignmentOccurrence>();
            var obligations = new List<StudentAssignmentObligation>();
            int occurrenceNumber = 1;
            foreach (var p in plans)
            {
                var occurrence = new AssignmentOccurrence
                {
                    TemplateId = examId,
                    TeacherId = teacherId,
                    OccurrenceNumber = occurrenceNumber++,
                    DueDate = p.DueDate,
                    Status = AssignmentOccurrenceStatus.Pending,
                    SessionId = p.SessionId,
                    SessionOccurrenceId = p.SessionOccurrenceId,
                    MaxGradeSnapshot = dto.MaxGrade,
                    PassingThresholdSnapshot = dto.SuccessScore,
                    TrackingModeSnapshot = null,
                    TotalStudentCount = p.StudentIds.Count,
                    CreateAt = utcNow,
                };
                p.Occurrence = occurrence;
                occurrences.Add(occurrence);
                foreach (var studentId in p.StudentIds)
                    obligations.Add(new StudentAssignmentObligation
                    {
                        Occurrence = occurrence,
                        TeacherId = teacherId,
                        TeacherStudentId = studentId,
                        Status = ObligationStatus.Pending,
                        IsGradeEntered = false,
                        MarkedByScan = false,
                        UpdatedAt = utcNow,
                        CreateAt = utcNow,
                    });
            }

            await _unitOfWork.BeginTransactionAsync();
            try
            {
                await _unitOfWork.ExamHomeworkRepo.UpdateTemplateAsync(template);
                await _unitOfWork.ExamHomeworkRepo.PurgeExamGraphAsync(examId);
                await _unitOfWork.ExamHomeworkRepo.AddScopesRangeAsync(scopes);
                await _unitOfWork.ExamHomeworkRepo.AddOccurrencesRangeAsync(occurrences);
                await _unitOfWork.ExamHomeworkRepo.AddObligationsRangeAsync(obligations);
                await _unitOfWork.SaveChangesAsync();

                // DuringSession: back-fill each new occurrence's obligations from attendance already
                // recorded on its linked SessionOccurrence — INSIDE the transaction (strict), same as create.
                if (dto.DeliveryType == ExamDeliveryType.DuringSession)
                    foreach (var p in plans)
                        if (p.SessionOccurrenceId.HasValue)
                            await _examAttendanceSync.BackfillExamOccurrenceAsync(teacherId, p.Occurrence!.Id, p.SessionOccurrenceId.Value, actingUserId);

                await _unitOfWork.CommitAsync();
            }
            catch (DbUpdateException)
            {
                await _unitOfWork.RollbackAsync();
                return FailView("DatabaseConflict", HttpStatusCode.Conflict);
            }
            catch
            {
                // A back-fill failure rolls the whole exam back rather than shipping a half-synced one.
                await _unitOfWork.RollbackAsync();
                throw;
            }
        }
        else
        {
            // Metadata-only (no structural/date change): reuse the shared, tested template edit.
            // AssignmentDate stays null — this branch is only reached when every per-session
            // anchor is unchanged, and sending a date would trip the legacy past-date guard on
            // exams whose (perfectly valid) dates have already passed.
            var templateEdit = new UpdateAssignmentTemplateDto
            {
                Name = dto.Name.Trim(),
                Notes = dto.Notes,
                AssignmentDate = null,
                MaxGrade = dto.MaxGrade,
                PassingThreshold = dto.SuccessScore,
                RowVersion = template.RowVersion
            };
            var update = await _examHomework.UpdateTemplateAsync(teacherId, actingUserId, examId, templateEdit);
            if (!update.IsSuccess)
                return FailView(update.Code ?? "ExamNotFound", update.StatusCode);

            // Keep each occurrence's grade snapshot in step with an edited max/pass score. Grade bounds
            // are always editable, but this metadata path — unlike a structural rebuild (:327-328) —
            // doesn't re-materialize the snapshots, so grade validation / pass-fail would otherwise keep
            // using the stale MaxGradeSnapshot/PassingThresholdSnapshot.
            await _unitOfWork.ExamHomeworkRepo.UpdateExamGradeSnapshotsAsync(
                teacherId, examId, dto.MaxGrade, dto.SuccessScore);
        }

        // ── Return the refreshed opened-exam view under an explicit "updated" code ──
        var view = await GetExamViewAsync(teacherId, examId);
        return view.IsSuccess
            ? Result<ExamViewDto>.Success(view.Data!, _localizer, "AssignmentUpdated")
            : view;
    }

    /// <inheritdoc />
    public async Task<Result<List<SessionExamDateDto>>> GetSessionExamDatesAsync(
        long teacherId, long sessionId, int year, int month)
    {
        if (month < 1 || month > 12 || year < 2000 || year > 2100)
            return Result<List<SessionExamDateDto>>.Failure(
                _localizer, "InvalidMonth", HttpStatusCode.BadRequest);

        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId, teacherId);
        if (session is null)
            return Result<List<SessionExamDateDto>>.Failure(
                _localizer, "SessionNotFoundOrForeign", HttpStatusCode.NotFound);

        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);

        var occurrences = await _unitOfWork.AttendanceRepo
            .GetOccurrencesBySessionAndDateRangeAsync(sessionId, start, end);

        var items = occurrences.Select(o => new SessionExamDateDto
        {
            SessionOccurrenceId = o.Id,
            Date = o.OccurrenceDate,
            Status = o.Status.ToString(),
        }).ToList();

        return Result<List<SessionExamDateDto>>.Success(items, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<ExamHomeDto>> GetExamHomeAsync(
        long teacherId, int upcomingPage, int pastPage, int pageSize)
    {
        int safeSize = pageSize is < 1 or > 100 ? 20 : pageSize;
        // Upper-clamp the page too: an unbounded page makes (page-1)*pageSize overflow int → a
        // negative SQL OFFSET → deterministic 500. 1,000,000 pages is far beyond any real use.
        int safeUpcoming = Math.Clamp(upcomingPage, 1, 1_000_000);
        int safePast = Math.Clamp(pastPage, 1, 1_000_000);

        DateTime today = _timeZoneService.GetTeacherLocalDate(teacherId);

        var (upcomingRows, upcomingTotal) = await _unitOfWork.ExamHomeworkRepo
            .GetExamOccurrencesForHomePagedAsync(teacherId, isPast: false, today, safeUpcoming, safeSize);
        var (pastRows, pastTotal) = await _unitOfWork.ExamHomeworkRepo
            .GetExamOccurrencesForHomePagedAsync(teacherId, isPast: true, today, safePast, safeSize);

        var allRows = upcomingRows.Concat(pastRows).ToList();
        var summaries = await _unitOfWork.ExamHomeworkRepo
            .GetCompletionSummariesByOccurrenceIdsAsync(allRows.Select(o => o.OccurrenceId));

        // Scope metadata (selection mode + assigned sessions/groups), batched for the page's exams.
        var templateIds = allRows.Select(o => o.TemplateId).Distinct().ToList();
        var scopes = await _unitOfWork.ExamHomeworkRepo.GetScopesByTemplateIdsAsync(templateIds);
        var scopesByTemplate = scopes.GroupBy(s => s.TemplateId).ToDictionary(g => g.Key, g => g.ToList());

        // Expand every referenced group to its member sessions in a single query.
        var groupIds = scopes
            .Where(s => s.ScopeType == AssignmentScopeType.SessionGroup && s.SessionGroupId.HasValue)
            .Select(s => s.SessionGroupId!.Value).Distinct().ToList();
        var groupSessions = await _unitOfWork.SessionsRepo.GetSessionsByGroupIdsAsync(teacherId, groupIds);
        var sessionsByGroup = groupSessions.GroupBy(s => s.GroupId).ToDictionary(
            g => g.Key,
            g => g.Select(x => new SessionRefDto { Id = x.Id, Name = x.SessionName }).ToList());

        ExamHomeCardDto BuildCard(ExamHomeOccurrenceRow o)
        {
            var s = summaries.GetValueOrDefault(o.OccurrenceId);
            var tplScopes = scopesByTemplate.GetValueOrDefault(o.TemplateId) ?? new List<AssignmentScope>();
            bool byGroups = tplScopes.Any(sc => sc.ScopeType == AssignmentScopeType.SessionGroup);

            var assignedSessions = tplScopes
                .Where(sc => sc.ScopeType == AssignmentScopeType.Session && sc.Session != null)
                .Select(sc => new SessionRefDto { Id = sc.SessionId!.Value, Name = sc.Session!.SessionName })
                .ToList();

            var assignedGroups = tplScopes
                .Where(sc => sc.ScopeType == AssignmentScopeType.SessionGroup && sc.SessionGroup != null)
                .Select(sc => new GroupRefDto
                {
                    Id = sc.SessionGroupId!.Value,
                    Name = sc.SessionGroup!.GroupName,
                    Sessions = sessionsByGroup.GetValueOrDefault(sc.SessionGroupId!.Value) ?? new List<SessionRefDto>(),
                })
                .ToList();

            return new ExamHomeCardDto
            {
                ExamId = o.TemplateId,
                OccurrenceId = o.OccurrenceId,
                Name = o.ExamName,
                DeliveryType = o.DeliveryType,
                SessionId = o.SessionId,
                SessionName = o.SessionName,
                Date = o.DueDate,
                AssignedCount = s?.TotalStudents ?? 0,
                TotalStudents = s?.TotalStudents ?? 0,
                AttendedCount = s?.DoneOrAttended ?? 0,
                MissedCount = s?.NotDoneOrAbsent ?? 0,
                PendingCount = s?.Pending ?? 0,
                IsPast = o.DueDate.Date < today,
                SelectionMode = byGroups ? "Groups" : "Sessions",
                AssignedSessions = assignedSessions,
                AssignedGroups = assignedGroups,
            };
        }

        var result = new ExamHomeDto
        {
            Upcoming = new PaginatedResponse<List<ExamHomeCardDto>>
            {
                data = upcomingRows.Select(BuildCard).ToList(),
                page = safeUpcoming,
                pageSize = safeSize,
                totalCount = upcomingTotal,
                totalPages = (int)Math.Ceiling(upcomingTotal / (double)safeSize),
            },
            Past = new PaginatedResponse<List<ExamHomeCardDto>>
            {
                data = pastRows.Select(BuildCard).ToList(),
                page = safePast,
                pageSize = safeSize,
                totalCount = pastTotal,
                totalPages = (int)Math.Ceiling(pastTotal / (double)safeSize),
            },
        };
        return Result<ExamHomeDto>.Success(result, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<ExamViewDto>> GetExamViewAsync(long teacherId, long examId)
    {
        var template = await _unitOfWork.ExamHomeworkRepo.GetTemplateByIdAndTeacherAsync(examId, teacherId);
        if (template is null || template.AssignmentType != AssignmentType.Exam)
            return Result<ExamViewDto>.Failure(_localizer, "ExamNotFound", HttpStatusCode.NotFound);

        var occurrences = await _unitOfWork.ExamHomeworkRepo.GetExamOccurrencesByTemplateAsync(teacherId, examId);
        var roster = await _unitOfWork.ExamHomeworkRepo.GetExamRosterByTemplateAsync(teacherId, examId);
        var rosterByOccurrence = roster.GroupBy(r => r.OccurrenceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var sessions = occurrences.Select(o =>
        {
            var rows = rosterByOccurrence.GetValueOrDefault(o.OccurrenceId) ?? new List<ExamRosterRow>();
            var stats = ComputeStats(rows, o.PassingThresholdSnapshot);
            return new ExamSessionViewDto
            {
                SessionId = o.SessionId ?? 0,
                SessionName = o.SessionName,
                OccurrenceId = o.OccurrenceId,
                Date = o.DueDate,
                Stats = stats,
                // UI "Take" vs "Edit" signals: attendance is "taken" once anyone is present/absent;
                // grades are "taken" once any grade has been entered.
                AttendanceTaken = stats.AttendedCount + stats.MissedCount > 0,
                GradesTaken = stats.GradedCount > 0,
                Students = rows.Select(r => MapRosterRow(r, o.PassingThresholdSnapshot)).ToList(),
            };
        }).ToList();

        var view = new ExamViewDto
        {
            ExamId = template.Id,
            Name = template.Name,
            DeliveryType = template.ExamDeliveryType,
            MaxGrade = template.MaxGrade,
            SuccessScore = template.PassingThreshold,
            GlobalStats = ComputeStats(roster, template.PassingThreshold),
            // Distinct headcount across the whole exam (a student sitting the exam in two sessions
            // counts once here, but twice in GlobalStats.TotalStudents which counts obligation rows).
            DistinctStudentCount = roster.Select(r => r.TeacherStudentId).Distinct().Count(),
            Sessions = sessions,
        };
        return Result<ExamViewDto>.Success(view, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<ExamSessionRosterDto>> GetExamSessionRosterAsync(
        long teacherId, long examId, long sessionId, int page, int pageSize, string? search,
        bool? graded)
    {
        // Exam-surface guard (mirror GetExamViewAsync): a Homework template must 404 here so this
        // exams-only endpoint can never surface homework obligations.
        var template = await _unitOfWork.ExamHomeworkRepo.GetTemplateByIdAndTeacherAsync(examId, teacherId);
        if (template is null || template.AssignmentType != AssignmentType.Exam)
            return Result<ExamSessionRosterDto>.Failure(_localizer, "ExamNotFound", HttpStatusCode.NotFound);

        var occurrences = await _unitOfWork.ExamHomeworkRepo.GetExamOccurrencesByTemplateAsync(teacherId, examId);
        if (occurrences.Count == 0)
            return Result<ExamSessionRosterDto>.Failure(_localizer, "ExamNotFound", HttpStatusCode.NotFound);

        var occ = occurrences.FirstOrDefault(o => o.SessionId == sessionId);
        if (occ is null)
            return Result<ExamSessionRosterDto>.Failure(_localizer, "SessionNotInExam", HttpStatusCode.NotFound);

        int safePage = Math.Clamp(page, 1, 1_000_000); // upper-clamp: avoid (page-1)*pageSize int overflow → 500
        int safeSize = pageSize is < 1 or > 200 ? 50 : pageSize;

        var (rows, totalCount) = await _unitOfWork.ExamHomeworkRepo.GetTrackingViewPagedAsync(
            teacherId, occ.OccurrenceId, search,
            statusFilter: null, missingEntries: null,
            gradeAboveThreshold: null, gradeBelowThreshold: null, belowPassingGrade: null,
            // All / Graded / Not graded must narrow the SERVER page — the grade screen
            // pages 30 at a time, so filtering what happens to be loaded would hide
            // every student past the first page behind a chip that claims a total.
            gradeEntered: graded,
            page: safePage, pageSize: safeSize);

        var students = rows.Select(r => new ExamStudentRowDto
        {
            ObligationId = r.ObligationId,
            TeacherStudentId = r.TeacherStudentId,
            StudentName = r.StudentName,
            StudentCode = r.StudentCode,
            Status = r.Status.ToString(),
            Attended = IsAttended(r.Status),
            Grade = r.GradeValue,
            IsGradeEntered = r.IsGradeEntered,
            IsBelowPassing = r.IsBelowPassing,
            RowVersion = Convert.ToBase64String(r.ObligationRowVersion),
        }).ToList();

        var dto = new ExamSessionRosterDto
        {
            ExamId = examId,
            SessionId = sessionId,
            SessionName = occ.SessionName,
            OccurrenceId = occ.OccurrenceId,
            Date = occ.DueDate,
            MaxGrade = occ.MaxGradeSnapshot,
            SuccessScore = occ.PassingThresholdSnapshot,
            Students = new PaginatedResponse<List<ExamStudentRowDto>>
            {
                data = students,
                page = safePage,
                pageSize = safeSize,
                totalCount = totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)safeSize),
            },
        };
        return Result<ExamSessionRosterDto>.Success(dto, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<BatchGradeResultDto>> SaveGradesAsync(
        long teacherId, long actingUserId, BatchGradeDto dto)
    {
        const int maxItems = 1000;
        if (dto.Items is null || dto.Items.Count == 0)
            return Result<BatchGradeResultDto>.Failure(_localizer, "BulkItemsEmpty", HttpStatusCode.BadRequest);
        if (dto.Items.Count > maxItems)
            return Result<BatchGradeResultDto>.Failure(_localizer, "BulkItemsTooMany", HttpStatusCode.BadRequest);

        // The exam these grades belong to; each student resolves to their single obligation within it.
        var template = await _unitOfWork.ExamHomeworkRepo.GetTemplateByIdAndTeacherAsync(dto.ExamId, teacherId);
        if (template is null || template.AssignmentType != AssignmentType.Exam)
            return Result<BatchGradeResultDto>.Failure(_localizer, "ExamNotFound", HttpStatusCode.NotFound);

        // De-duplicate by student id (last value wins) so a batch can't fight itself.
        var itemsByStudent = new Dictionary<long, GradeItemDto>();
        foreach (var it in dto.Items) itemsByStudent[it.TeacherStudentId] = it;

        var obligations = await _unitOfWork.ExamHomeworkRepo
            .GetObligationsForGradingByTemplateAndStudentsAsync(teacherId, dto.ExamId, itemsByStudent.Keys);
        var byStudent = obligations.GroupBy(o => o.TeacherStudentId).ToDictionary(g => g.Key, g => g.ToList());

        DateTime utcNow = DateTime.UtcNow;
        var results = new List<BatchGradeItemResultDto>();
        var toApply = new List<(StudentAssignmentObligation Obligation, GradeItemDto Item)>();

        foreach (var (studentId, item) in itemsByStudent)
        {
            if (!byStudent.TryGetValue(studentId, out var matches) || matches.Count == 0)
            { results.Add(FailItem(studentId, "ObligationNotFound")); continue; }
            if (matches.Count > 1)
            { results.Add(FailItem(studentId, "AmbiguousStudentInExam")); continue; }

            var o = matches[0];
            var occurrence = o.Occurrence;

            // A null grade means "clear this student's grade" — always allowed (a harmless no-op when
            // there is nothing to clear). The set-a-value guards below apply only when a grade is supplied.
            if (item.Grade.HasValue)
            {
                // Grade range — the core rule: 0 ≤ grade ≤ the exam's max.
                if (item.Grade.Value < 0m)
                { results.Add(FailItem(studentId, "GradeOutOfRange")); continue; }
                if (occurrence.MaxGradeSnapshot.HasValue && item.Grade.Value > occurrence.MaxGradeSnapshot.Value)
                { results.Add(FailItem(studentId, "GradeExceedsMax")); continue; }

                // You cannot grade a student who did not attend.
                if (o.Status == ObligationStatus.DidNotAttend)
                { results.Add(FailItem(studentId, "CannotGradeAbsentStudent")); continue; }

                // During-session grades-only guard: attendance comes from the session. A during-session
                // student not yet marked attended cannot be graded from the exam screen.
                if (occurrence.Template.ExamDeliveryType == ExamDeliveryType.DuringSession
                    && o.Status == ObligationStatus.Pending)
                { results.Add(FailItem(studentId, "AttendanceNotRecordedForExam")); continue; }
            }

            // Concurrency is judged PER ROW, before anything is written: a student whose row moved on
            // since the client read it is reported on their own line (with the current server values so
            // the client can show what it became and offer to re-apply), and the rest of the batch still
            // saves. Letting EF discover this at SaveChanges would throw once and roll back every other
            // student's grade — a whole class of entry lost to one stale row. Checked LAST so a clearer
            // reason (absent, out of range) wins over "someone else edited this".
            if (!o.RowVersion.AsSpan().SequenceEqual(item.RowVersion))
            {
                results.Add(ConflictItem(o));
                continue;
            }

            toApply.Add((o, item));
        }

        if (toApply.Count > 0)
        {
            await _unitOfWork.BeginTransactionAsync();
            try
            {
                foreach (var (o, item) in toApply)
                {
                    // A supplied grade sets AttendedWithGrade; a null grade CLEARS an existing grade and
                    // reverts an AttendedWithGrade row back to Attended (attendance is preserved). Clearing
                    // an already-ungraded/absent row leaves its status untouched.
                    bool clearing = !item.Grade.HasValue;
                    ObligationStatus newStatus = clearing
                        ? (o.Status == ObligationStatus.AttendedWithGrade ? ObligationStatus.Attended : o.Status)
                        : ObligationStatus.AttendedWithGrade;
                    decimal? newGrade = clearing ? null : item.Grade;

                    var audit = new StudentObligationAuditLog
                    {
                        StudentObligationId = o.Id,
                        TeacherId = o.TeacherId,
                        OldStatus = o.Status,
                        NewStatus = newStatus,
                        OldGradeValue = o.GradeValue,
                        NewGradeValue = newGrade,
                        MaxGradeSnapshot = o.Occurrence.MaxGradeSnapshot,
                        PassingThresholdSnapshot = o.Occurrence.PassingThresholdSnapshot,
                        ChangeReason = clearing ? "Grade cleared" : "Grade entry",
                        ChangedByUserId = actingUserId,
                        ChangedAt = utcNow,
                        CreateAt = utcNow,
                    };

                    o.Status = newStatus;
                    o.GradeValue = newGrade;
                    o.IsGradeEntered = !clearing;
                    o.LastUpdatedByUserId = actingUserId;
                    o.UpdatedAt = utcNow;

                    await _unitOfWork.ExamHomeworkRepo.UpdateObligationAsync(o);
                    await _unitOfWork.ExamHomeworkRepo.AddAuditLogAsync(audit);
                    // No SetObligationOriginalRowVersion: the client's token was matched against the
                    // row above, so EF's own original value (from that same read) IS the client's token.
                    // The check it still performs now covers only the read→write window.
                }

                await _unitOfWork.SaveChangesAsync();
                await _unitOfWork.CommitAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Only reachable when a row changed between the read above and this write — a window of
                // milliseconds now that stale tokens are rejected per row. Still a whole-batch 409, but
                // the client refreshes its tokens and can re-send, instead of looping on dead ones.
                await _unitOfWork.RollbackAsync();
                return Result<BatchGradeResultDto>.Failure(
                    _localizer, "ObligationConcurrencyConflict", HttpStatusCode.Conflict);
            }
            catch
            {
                await _unitOfWork.RollbackAsync();
                throw;
            }

            foreach (var (o, _) in toApply)
            {
                results.Add(new BatchGradeItemResultDto
                {
                    TeacherStudentId = o.TeacherStudentId,
                    ObligationId = o.Id,
                    Success = true,
                    Status = o.Status.ToString(),
                    Grade = o.GradeValue,
                    RowVersion = Convert.ToBase64String(o.RowVersion),
                });
            }
        }

        var payload = new BatchGradeResultDto
        {
            UpdatedCount = toApply.Count,
            AllSucceeded = results.All(r => r.Success),
            Items = results,
        };

        // OEX-1: the envelope must reflect the BATCH outcome instead of blindly claiming "Grades saved"
        // when every (or some) row was rejected. Mirrors ATT-5's bulk-attendance convention
        // (AttendanceService.BulkMarkAttendanceAsync): this stays a Result.Success — the `data` shape
        // (updatedCount/allSucceeded/items[]) is a fixed frontend contract and Result.Failure would drop
        // it — and only the message/code changes; the client reads the per-student Items[] for specifics.
        // Nothing saved → "none"; some saved & some rejected → "partial"; all saved → keep "GradesSaved".
        string batchMessageKey =
            payload.UpdatedCount == 0 ? "NoGradesSaved"
            : payload.AllSucceeded    ? "GradesSaved"
                                      : "GradesPartiallySaved";
        return Result<BatchGradeResultDto>.Success(payload, _localizer, batchMessageKey);
    }

    /// <inheritdoc />
    public async Task<Result<ExamAttendanceResultDto>> MarkExamAttendanceAsync(
        long teacherId, long actingUserId, ExamAttendanceDto dto)
    {
        const int maxItems = 1000;
        if (dto.Items is null || dto.Items.Count == 0)
            return Result<ExamAttendanceResultDto>.Failure(_localizer, "BulkItemsEmpty", HttpStatusCode.BadRequest);
        if (dto.Items.Count > maxItems)
            return Result<ExamAttendanceResultDto>.Failure(_localizer, "BulkItemsTooMany", HttpStatusCode.BadRequest);

        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceWithTemplateAsync(dto.OccurrenceId, teacherId);
        if (occurrence is null)
            return Result<ExamAttendanceResultDto>.Failure(_localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);
        if (occurrence.Template.AssignmentType != AssignmentType.Exam)
            return Result<ExamAttendanceResultDto>.Failure(_localizer, "NotAnExam", HttpStatusCode.BadRequest);
        if (occurrence.Template.ExamDeliveryType == ExamDeliveryType.DuringSession)
            return Result<ExamAttendanceResultDto>.Failure(
                _localizer, "AttendanceReadOnlyForDuringSession", HttpStatusCode.Conflict);

        var itemsByStudent = new Dictionary<long, ExamAttendanceItemDto>();
        foreach (var it in dto.Items) itemsByStudent[it.TeacherStudentId] = it;

        var obligations = await _unitOfWork.ExamHomeworkRepo
            .GetObligationsByOccurrenceAndStudentsAsync(teacherId, dto.OccurrenceId, itemsByStudent.Keys);
        var byStudent = obligations.ToDictionary(o => o.TeacherStudentId);

        DateTime utcNow = DateTime.UtcNow;
        var results = new List<ExamAttendanceItemResultDto>();
        var toApply = new List<(StudentAssignmentObligation Obligation, bool Present)>();

        foreach (var (studentId, item) in itemsByStudent)
        {
            if (!byStudent.TryGetValue(studentId, out var o))
            {
                results.Add(new ExamAttendanceItemResultDto
                { TeacherStudentId = studentId, Success = false, Code = "ObligationNotFound" });
                continue;
            }
            toApply.Add((o, item.Present));
        }

        if (toApply.Count > 0)
        {
            await _unitOfWork.BeginTransactionAsync();
            try
            {
                foreach (var (o, present) in toApply)
                {
                    ObligationStatus newStatus;
                    decimal? newGrade = o.GradeValue;
                    bool gradeEntered = o.IsGradeEntered;

                    if (present)
                    {
                        newStatus = (o.IsGradeEntered && o.GradeValue.HasValue)
                            ? ObligationStatus.AttendedWithGrade
                            : ObligationStatus.Attended;
                    }
                    else
                    {
                        newStatus = ObligationStatus.DidNotAttend;
                        newGrade = null;
                        gradeEntered = false;
                    }

                    var audit = new StudentObligationAuditLog
                    {
                        StudentObligationId = o.Id,
                        TeacherId = o.TeacherId,
                        OldStatus = o.Status,
                        NewStatus = newStatus,
                        OldGradeValue = o.GradeValue,
                        NewGradeValue = newGrade,
                        MaxGradeSnapshot = occurrence.MaxGradeSnapshot,
                        PassingThresholdSnapshot = occurrence.PassingThresholdSnapshot,
                        ChangeReason = "Exam attendance",
                        ChangedByUserId = actingUserId,
                        ChangedAt = utcNow,
                        CreateAt = utcNow,
                    };

                    o.Status = newStatus;
                    o.GradeValue = newGrade;
                    o.IsGradeEntered = gradeEntered;
                    o.LastUpdatedByUserId = actingUserId;
                    o.UpdatedAt = utcNow;

                    await _unitOfWork.ExamHomeworkRepo.UpdateObligationAsync(o);
                    await _unitOfWork.ExamHomeworkRepo.AddAuditLogAsync(audit);
                }

                await _unitOfWork.SaveChangesAsync();
                await _unitOfWork.CommitAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await _unitOfWork.RollbackAsync();
                return Result<ExamAttendanceResultDto>.Failure(
                    _localizer, "ObligationConcurrencyConflict", HttpStatusCode.Conflict);
            }
            catch
            {
                await _unitOfWork.RollbackAsync();
                throw;
            }

            foreach (var (o, _) in toApply)
            {
                results.Add(new ExamAttendanceItemResultDto
                {
                    TeacherStudentId = o.TeacherStudentId,
                    ObligationId = o.Id,
                    Success = true,
                    Status = o.Status.ToString(),
                    RowVersion = Convert.ToBase64String(o.RowVersion),
                });
            }
        }

        var payload = new ExamAttendanceResultDto
        {
            UpdatedCount = toApply.Count,
            AllSucceeded = results.All(r => r.Success),
            Items = results,
        };
        return Result<ExamAttendanceResultDto>.Success(payload, _localizer, "ExamAttendanceSaved");
    }

    /// <inheritdoc />
    public async Task<Result<ExamScanResultDto>> ScanExamAttendanceAsync(
        long teacherId, long actingUserId, ExamScanDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Code))
            return Result<ExamScanResultDto>.Failure(_localizer, "BarcodeRequired", HttpStatusCode.BadRequest);

        var occurrence = await _unitOfWork.ExamHomeworkRepo
            .GetOccurrenceWithTemplateAsync(dto.OccurrenceId, teacherId);
        if (occurrence is null)
            return Result<ExamScanResultDto>.Failure(_localizer, "OccurrenceNotFound", HttpStatusCode.NotFound);
        if (occurrence.Template.AssignmentType != AssignmentType.Exam)
            return Result<ExamScanResultDto>.Failure(_localizer, "NotAnExam", HttpStatusCode.BadRequest);
        if (occurrence.Template.ExamDeliveryType == ExamDeliveryType.DuringSession)
            return Result<ExamScanResultDto>.Failure(
                _localizer, "AttendanceReadOnlyForDuringSession", HttpStatusCode.Conflict);

        var student = await _unitOfWork.Students.GetActiveByCodeAndTeacherAsync(dto.Code.Trim(), teacherId);
        if (student is null)
            return Result<ExamScanResultDto>.Failure(_localizer, "StudentCodeNotFound", HttpStatusCode.NotFound);

        var obligation = await _unitOfWork.ExamHomeworkRepo
            .GetObligationByOccurrenceAndStudentAsync(dto.OccurrenceId, student.Id);
        if (obligation is null || obligation.TeacherId != teacherId)
            return Result<ExamScanResultDto>.Failure(_localizer, "ExamStudentNotFound", HttpStatusCode.NotFound);

        bool alreadyProcessed = obligation.Status is ObligationStatus.Attended or ObligationStatus.AttendedWithGrade;

        // Atomic, idempotent — marks Attended while preserving an already-entered grade.
        await _unitOfWork.ExamHomeworkRepo.SetExamAttendanceByOccurrenceAsync(
            teacherId, dto.OccurrenceId, new[] { student.Id },
            ObligationStatus.Attended, clearGrade: false, skipGraded: true, DateTime.UtcNow, actingUserId);

        var newStatus = obligation.Status == ObligationStatus.AttendedWithGrade
            ? ObligationStatus.AttendedWithGrade
            : ObligationStatus.Attended;

        return Result<ExamScanResultDto>.Success(new ExamScanResultDto
        {
            ObligationId = obligation.Id,
            TeacherStudentId = student.Id,
            StudentName = student.StudentName,
            StudentCode = student.StudentCode,
            Status = newStatus.ToString(),
            AlreadyProcessed = alreadyProcessed,
        }, _localizer, alreadyProcessed ? "ScanAlreadyProcessed" : "ScanRecorded");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Result<ExamCreatedDto> Fail(string key, HttpStatusCode status = HttpStatusCode.BadRequest, object?[]? args = null) =>
        args is null ? Result<ExamCreatedDto>.Failure(_localizer, key, status)
                     : Result<ExamCreatedDto>.Failure(_localizer, key, args, status);

    private Result<ExamViewDto> FailView(string key, HttpStatusCode status = HttpStatusCode.BadRequest, object?[]? args = null) =>
        args is null ? Result<ExamViewDto>.Failure(_localizer, key, status)
                     : Result<ExamViewDto>.Failure(_localizer, key, args, status);

    private static BatchGradeItemResultDto FailItem(long teacherStudentId, string code) =>
        new() { TeacherStudentId = teacherStudentId, Success = false, Code = code };

    /// <summary>
    /// A row whose concurrency token no longer matches. Carries the CURRENT server state — status,
    /// grade and a fresh token — so the client can show what the row became and re-apply the
    /// teacher's value without making her retype it or reload the screen.
    /// </summary>
    private static BatchGradeItemResultDto ConflictItem(StudentAssignmentObligation o) => new()
    {
        TeacherStudentId = o.TeacherStudentId,
        ObligationId = o.Id,
        Success = false,
        Code = "ObligationConcurrencyConflict",
        Status = o.Status.ToString(),
        Grade = o.GradeValue,
        RowVersion = Convert.ToBase64String(o.RowVersion),
    };

    private static bool IsAttended(ObligationStatus status) =>
        status == ObligationStatus.Attended || status == ObligationStatus.AttendedWithGrade;

    private static ExamStudentRowDto MapRosterRow(ExamRosterRow r, decimal? passingThreshold) => new()
    {
        ObligationId = r.ObligationId,
        TeacherStudentId = r.TeacherStudentId,
        StudentName = r.StudentName,
        StudentCode = r.StudentCode,
        Status = r.Status.ToString(),
        Attended = IsAttended(r.Status),
        Grade = r.GradeValue,
        IsGradeEntered = r.IsGradeEntered,
        IsBelowPassing = r.IsGradeEntered && r.GradeValue.HasValue && passingThreshold.HasValue
                         && r.GradeValue.Value < passingThreshold.Value,
        RowVersion = Convert.ToBase64String(r.ObligationRowVersion),
    };

    private static ExamStatsDto ComputeStats(IReadOnlyList<ExamRosterRow> rows, decimal? passingThreshold)
    {
        var graded = rows.Where(r => r.IsGradeEntered && r.GradeValue.HasValue)
            .Select(r => r.GradeValue!.Value).ToList();
        return new ExamStatsDto
        {
            TotalStudents = rows.Count,
            GradedCount = graded.Count,
            Average = graded.Count > 0 ? Math.Round(graded.Average(), 2) : null,
            Highest = graded.Count > 0 ? graded.Max() : null,
            Lowest = graded.Count > 0 ? graded.Min() : null,
            AttendedCount = rows.Count(r => IsAttended(r.Status)),
            MissedCount = rows.Count(r => r.Status == ObligationStatus.DidNotAttend),
            PendingCount = rows.Count(r => r.Status == ObligationStatus.Pending),
            BelowPassingCount = passingThreshold.HasValue
                ? graded.Count(g => g < passingThreshold.Value) : 0,
        };
    }

    /// <inheritdoc />
    public async Task<Result<bool>> DeleteExamAsync(
        long teacherId, long actingUserId, long examId, bool confirm)
    {
        if (!confirm)
            return Result<bool>.Failure(
                _localizer, "DeletionConfirmationRequired", HttpStatusCode.BadRequest);

        // Exam-surface guard: only templates of type Exam are reachable here. Homework
        // templates (still served by the legacy assignment endpoints) must 404 so this
        // endpoint can never be used to delete them.
        var template = await _unitOfWork.ExamHomeworkRepo
            .GetTemplateWithScopesAsync(examId, teacherId);
        if (template is null || template.AssignmentType != AssignmentType.Exam)
            return Result<bool>.Failure(_localizer, "ExamNotFound", HttpStatusCode.NotFound);

        // Delegate to the shared REQ-EXH-037 hard-delete flow (snapshot into
        // AssignmentDeletionLogs, audit-log purge, template/occurrences/obligations delete —
        // all in one transaction owned by that method).
        var deleted = await _examHomework.DeleteTemplateAsync(
            teacherId, actingUserId, examId, confirm: true);
        if (!deleted.IsSuccess)
            return deleted;

        // Re-envelope with the exams-surface code (200 + body: the frontend branches on
        // `code`, which a legacy-style 204 cannot carry).
        return Result<bool>.Success(true, _localizer, "ExamDeleted");
    }

    /// <summary>
    /// Shared create/update request pipeline (single-sourced so the two surfaces can never drift):
    /// validates the delivery-type date contract, resolves the recipient (sessions XOR groups,
    /// groups expanding to their member sessions) into owned target sessions, and materializes one
    /// <see cref="SessionPlan"/> per session that has students. DuringSession anchors EVERY
    /// resolved session — group members included — to its own picked class occurrence from
    /// <paramref name="sessionOccurrences"/> (the original per-session design, restored after the
    /// single-examDate regression); SeparateTime applies the single <paramref name="examDate"/>
    /// (today or future) to all. A non-null <c>ErrorKey</c> means validation failed and the caller
    /// returns it verbatim.
    /// </summary>
    private async Task<SessionPlanBuild> BuildSessionPlansAsync(
        long teacherId,
        ExamDeliveryType deliveryType,
        DateTime? examDate,
        List<ExamSessionOccurrenceDto>? sessionOccurrences,
        List<long>? sessionIds,
        List<long>? groupIds,
        List<long>? studentIds)
    {
        static SessionPlanBuild Invalid(string key, HttpStatusCode status = HttpStatusCode.BadRequest) =>
            new() { ErrorKey = key, ErrorStatus = status };

        // ── Delivery-type date contract ──────────────────────────────────────
        var occurrenceBySession = new Dictionary<long, long>();
        DateTime separateDate = default;
        if (deliveryType == ExamDeliveryType.DuringSession)
        {
            // Per-session anchors ONLY: each resolved session picks its own class occurrence.
            if (examDate is not null)
                return Invalid("ExamDateOnlyForSeparateTime");
            if (sessionOccurrences is not { Count: > 0 })
                return Invalid("SessionOccurrenceRequired");
            foreach (var entry in sessionOccurrences)
            {
                if (entry.SessionId is null || entry.SessionOccurrenceId is null)
                    return Invalid("SessionOccurrenceRequired");
                if (!occurrenceBySession.TryAdd(entry.SessionId.Value, entry.SessionOccurrenceId.Value))
                    return Invalid("DuplicateSessionInExam");
            }
        }
        else
        {
            // Single standalone date ONLY (today or future).
            if (sessionOccurrences is { Count: > 0 })
                return Invalid("SessionOccurrencesOnlyForDuringSession");
            if (examDate is null)
                return Invalid("ExamDateRequired");
            separateDate = examDate.Value.Date;
            // Teacher-local (Africa/Cairo) "today", consistent with the rest of the app (payments,
            // attendance, the exam home split) — UtcNow.Date would be off by up to a day near midnight.
            // OEX-2: use the exam-worded key ("Exam date must be today or a future date") — NOT the shared
            // AssignmentDateInPast, whose "Assignment date…" text is load-bearing for assignments/homework.
            // Create and update both flow through this one pipeline, so both surface the corrected wording.
            if (separateDate < _timeZoneService.GetTeacherLocalDate(teacherId))
                return Invalid("ExamDateInPast");
        }

        // ── Recipient: EITHER sessions OR groups (groups expand server-side) ──
        bool hasSessions = sessionIds is { Count: > 0 };
        bool hasGroups = groupIds is { Count: > 0 };
        if (hasSessions == hasGroups)
            return Invalid("SelectEitherSessionsOrGroups");

        var resolvedGroupIds = new List<long>();
        var sessionNames = new Dictionary<long, string>();   // A3: name a student-less session in the error
        List<long> targetSessionIds;
        if (hasGroups)
        {
            resolvedGroupIds = groupIds!.Distinct().ToList();
            foreach (var gid in resolvedGroupIds)
            {
                var group = await _unitOfWork.SessionsRepo.GetGroupByIdAndTeacherAsync(gid, teacherId);
                if (group is null)
                    return Invalid("GroupNotFoundOrForeign", HttpStatusCode.NotFound);
            }
            var groupSessions = await _unitOfWork.SessionsRepo
                .GetSessionsByGroupIdsAsync(teacherId, resolvedGroupIds);
            foreach (var s in groupSessions) sessionNames[s.Id] = s.SessionName;
            targetSessionIds = groupSessions.Select(s => s.Id).Distinct().ToList();
        }
        else
        {
            targetSessionIds = sessionIds!.Distinct().ToList();
            foreach (var sid in targetSessionIds)
            {
                var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sid, teacherId);
                if (session is null)
                    return Invalid("SessionNotFoundOrForeign", HttpStatusCode.NotFound);
                sessionNames[sid] = session.SessionName;
            }
        }

        if (targetSessionIds.Count == 0)
            return Invalid("ExamRequiresSession");

        // DuringSession: an anchor for a session that isn't part of the exam is a client error.
        // (Coverage is enforced per NON-EMPTY session inside the loop; a student-less session is
        //  rejected by name below and needs no anchor — A3.)
        if (deliveryType == ExamDeliveryType.DuringSession)
        {
            var targetSet = targetSessionIds.ToHashSet();
            foreach (var anchoredSessionId in occurrenceBySession.Keys)
                if (!targetSet.Contains(anchoredSessionId))
                    return Invalid("SessionOccurrenceForUnselectedSession");
        }

        // ── Per-session materialization plan ─────────────────────────────────
        // Optional global student subset, validated against the union of the target sessions.
        HashSet<long>? studentFilter = studentIds is { Count: > 0 }
            ? studentIds.Distinct().ToHashSet()
            : null;
        var matchedFilterStudents = new HashSet<long>();

        var plans = new List<SessionPlan>();
        var emptySessionNames = new List<string>();   // A3: sessions selected but with no assigned students

        foreach (var sessionId in targetSessionIds)
        {
            var sessionStudentSet = (await _unitOfWork.PaymentsRepo
                .GetStudentIdsBySessionAsync(teacherId, sessionId)).ToHashSet();

            // A3: a selected session (or a group member) with NO assigned students can't carry an exam —
            // collect its name and fail by name below rather than silently dropping it. It needs no anchor.
            if (sessionStudentSet.Count == 0)
            {
                emptySessionNames.Add(sessionNames.GetValueOrDefault(sessionId, $"#{sessionId}"));
                continue;
            }

            DateTime dueDate;
            long? sessionOccurrenceId = null;

            if (deliveryType == ExamDeliveryType.DuringSession)
            {
                // Only a non-empty session needs a picked occurrence: it must exist, be the teacher's,
                // and belong to THIS session. Its (possibly past) date becomes the exam date; the class's
                // recorded attendance back-fills the exam after commit.
                if (!occurrenceBySession.TryGetValue(sessionId, out var pickedOccurrenceId))
                    return Invalid("SessionOccurrenceRequired");
                var occ = await _unitOfWork.AttendanceRepo
                    .GetOccurrenceByIdAndTeacherAsync(pickedOccurrenceId, teacherId);
                if (occ is null || occ.SessionId != sessionId)
                    return Invalid("SessionOccurrenceNotFound", HttpStatusCode.NotFound);
                dueDate = occ.OccurrenceDate.Date;
                sessionOccurrenceId = occ.Id;
            }
            else
            {
                dueDate = separateDate;
            }

            List<long> planStudentIds;
            if (studentFilter is not null)
            {
                planStudentIds = sessionStudentSet.Where(studentFilter.Contains).ToList();
                foreach (var id in planStudentIds) matchedFilterStudents.Add(id);
            }
            else
            {
                planStudentIds = sessionStudentSet.ToList();
            }

            if (planStudentIds.Count == 0)
                continue;   // this session HAS students but the explicit subset excludes them all — skip it

            plans.Add(new SessionPlan
            {
                SessionId = sessionId,
                DueDate = dueDate,
                SessionOccurrenceId = sessionOccurrenceId,
                StudentIds = planStudentIds,
            });
        }

        // A3: any student-less selected session fails the whole create/update, named so the teacher
        // can remove it or assign students.
        if (emptySessionNames.Count > 0)
            return new SessionPlanBuild
            {
                ErrorKey = "SessionHasNoStudentsNamed",
                ErrorArgs = new object?[] { string.Join(", ", emptySessionNames) },
            };

        // Every explicitly-listed student must belong to one of the target sessions.
        if (studentFilter is not null && matchedFilterStudents.Count != studentFilter.Count)
            return Invalid("StudentsNotInSession");

        if (plans.Count == 0)
            return Invalid("SessionHasNoStudents");

        return new SessionPlanBuild { HasGroups = hasGroups, GroupIds = resolvedGroupIds, Plans = plans };
    }

    /// <summary>Outcome of <see cref="BuildSessionPlansAsync"/> — either an error key or the materialization plan.</summary>
    private sealed class SessionPlanBuild
    {
        public string? ErrorKey { get; init; }
        public HttpStatusCode ErrorStatus { get; init; } = HttpStatusCode.BadRequest;
        public object?[]? ErrorArgs { get; init; }   // formatting args for a named error (e.g. A3 session name)
        public bool HasGroups { get; init; }
        public List<long> GroupIds { get; init; } = new();
        public List<SessionPlan> Plans { get; init; } = new();
    }

    /// <summary>Internal per-session materialization plan (mutated with the built occurrence).</summary>
    private sealed class SessionPlan
    {
        public long SessionId { get; init; }
        public DateTime DueDate { get; init; }
        public long? SessionOccurrenceId { get; init; }
        public List<long> StudentIds { get; init; } = new();
        public AssignmentOccurrence? Occurrence { get; set; }
    }
}
