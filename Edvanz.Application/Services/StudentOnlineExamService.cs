using Edvanz.Application.Dtos;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Net;

namespace Edvanz.Application.Services;

public class StudentOnlineExamService : IStudentOnlineExamService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Messages> _localizer;
    private readonly IOnlineExamGradingService _grading;
    private readonly IFileAccessService _fileAccess;
    private readonly ITimeZoneService _timeZoneService;

    public StudentOnlineExamService(
        IUnitOfWork unitOfWork, IStringLocalizer<Messages> localizer, IOnlineExamGradingService grading,
        IFileAccessService fileAccess, ITimeZoneService timeZoneService)
    {
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _grading = grading;
        _fileAccess = fileAccess;
        _timeZoneService = timeZoneService;
    }

    // ══════════════════════════════════════════════════════════════════════
    // S1 — MY EXAMS (upcoming/past split, QD)
    // ══════════════════════════════════════════════════════════════════════
    public async Task<Result<StudentOnlineExamListDto>> GetMyExamsAsync(
        long teacherId, long teacherStudentId, string? studentLanguage)
    {
        var (sessionId, groupId) = await _unitOfWork.OnlineExamsRepo.GetStudentSessionContextAsync(teacherStudentId);
        var exams = await _unitOfWork.OnlineExamsRepo.GetExamsAssignedToStudentAsync(teacherId, sessionId, groupId);

        if (exams.Count == 0)
            return Result<StudentOnlineExamListDto>.Success(new StudentOnlineExamListDto(), _localizer);

        // One grouped fetch for this student's reports across the candidate exam ids.
        var examIds = exams.Select(e => e.Id).ToList();
        var reports = await _unitOfWork.GetRepository<Domain.Entities.StudentOnlineExamReport, long>()
            .GetAsync(r => examIds.Contains(r.OnlineExamId) && r.TeacherStudentId == teacherStudentId);
        var reportByExam = reports.ToDictionary(r => r.OnlineExamId);

        // Subject label — resolved ONCE for this teacher (never per-exam → no N+1). Canonical
        // StudentUserService.BuildDashboardTeacherDtoFromBatch pattern (AAM-FR-02.2/BUG-3): default
        // to the teacher's free-text CustomSubject, then override with the linked ministry subject
        // localized to the STUDENT's stored LanguagePreference (null/empty → English). This matches
        // the offline-exam + dashboard modules; it is NOT the request UI-culture (the prior
        // CurrentUICulture approach diverged from the canonical pattern despite claiming otherwise).
        var subjectRow = await _unitOfWork.OnlineExamsRepo.GetTeacherSubjectNameAsync(teacherId);
        string subjectName = subjectRow?.CustomSubject ?? string.Empty;
        if (subjectRow?.SubjectNameEn is not null)
        {
            bool isArabic = string.Equals(studentLanguage?.Trim(), "ar", StringComparison.OrdinalIgnoreCase);
            subjectName = isArabic ? (subjectRow.SubjectNameAr ?? subjectRow.SubjectNameEn) : subjectRow.SubjectNameEn;
        }

        var now = DateTime.UtcNow;
        var result = new StudentOnlineExamListDto();

        foreach (var exam in exams)
        {
            reportByExam.TryGetValue(exam.Id, out var report);
            bool finalized = report?.SubmittedAt is not null;
            // A Blocked report is terminal too (online exams have no retake) yet never carries
            // SubmittedAt — without this the list re-offered a blocked exam as attemptable
            // (`upcoming`, StudentStatus null) and the student only found out at submit-409.
            bool terminal = finalized || report?.Status == StudentOnlineExamStatus.Blocked;
            // Assigned (every row here is in-scope) + never attempted + window CLOSED ⇒ virtual Missed,
            // so the app renders a status/score instead of null. Not persisted.
            bool missed = report is null && exam.EndDateTime < now;

            // exam.StartDateTime is a UTC instant. The student list shows the exam's local
            // wall-clock date/time, so convert UTC → Cairo BEFORE truncating to DateOnly/TimeOnly.
            // Truncating the raw UTC instant showed times ~2-3h early (the UTC+2/+3 offset bug).
            var localStart = _timeZoneService.ConvertUtcToLocal(exam.StartDateTime, "Africa/Cairo");

            var row = new OnlineExamStudentListItemDto
            {
                ExamId = exam.Id,
                ExamName = exam.Title,
                Subject = subjectName,
                ExamDate = DateOnly.FromDateTime(localStart),
                ExamTime = TimeOnly.FromDateTime(localStart),
                // The real instant, NOT the truncated local pair — the client's entry gate has
                // to be judged against this or it drifts by the device's offset from Cairo.
                // Same value the take screen already sends, so the two screens agree.
                StartDateTime = exam.StartDateTime,
                Duration = exam.EndDateTime - exam.StartDateTime,
                QuestionsCount = exam.Questions.Count,
                ExamDegree = exam.Questions.Sum(q => q.Degree),
                StudentDegree = terminal ? report!.Score : (missed ? 0m : (decimal?)null),
                StudentStatus = terminal ? report!.Status.ToString()
                    : (missed ? StudentOnlineExamStatus.Missed.ToString() : null),
            };

            // QD split: past = terminal (finalized ∨ blocked) ∨ End<now; upcoming = Published ∧ not terminal ∧ window open.
            if (terminal || exam.EndDateTime < now)
                result.Past.Add(row);
            else if (exam.Status == OnlineExamStatus.Published)
                result.Upcoming.Add(row);
            // else: Closed-but-never-finalized falls into neither bucket (§4 note — intentional per plan wording).
        }

        return Result<StudentOnlineExamListDto>.Success(result, _localizer);
    }

    // ══════════════════════════════════════════════════════════════════════
    // S2 — TAKE SCREEN (no IsCorrect)
    // ══════════════════════════════════════════════════════════════════════
    public async Task<Result<OnlineExamTakeScreenDto>> GetTakeScreenAsync(
        long teacherId, long teacherStudentId, long onlineExamId)
    {
        var exam = await _unitOfWork.OnlineExamsRepo.GetByIdAndTeacherAsync(onlineExamId, teacherId);
        if (exam is null || exam.Status != OnlineExamStatus.Published)
            return Result<OnlineExamTakeScreenDto>.Failure(_localizer, OnlineExamConstants.Messages.NotFound, HttpStatusCode.NotFound);

        bool isAssigned = await _unitOfWork.OnlineExamsRepo
            .BuildAssignedStudentIdsQuery(onlineExamId, teacherId)
            .AnyAsync(id => id == teacherStudentId);
        if (!isAssigned)
            return Result<OnlineExamTakeScreenDto>.Failure(_localizer, OnlineExamConstants.Messages.NotInScope, HttpStatusCode.Forbidden);

        var questions = await _unitOfWork.OnlineExamsRepo.GetQuestionsForStudentAsync(onlineExamId);
        // PERF: batch-resolve every question-image gated URL in ONE query (was an N+1).
        var imageUrls = await _fileAccess.TryBuildGatedUrlsAsync(
            questions.Where(q => q.ImageFileInternalId is not null).Select(q => q.ImageFileInternalId!.Value));
        foreach (var q in questions)
            q.ImageUrl = q.ImageFileInternalId is long fid ? imageUrls.GetValueOrDefault(fid) : null;

        // Attempt state so the app can render blocked/finalized on re-entry (writes already
        // enforce it; this makes the read surface tell the same story).
        var report = await _unitOfWork.StudentOnlineExamReportsRepo.GetByExamAndStudentAsync(onlineExamId, teacherStudentId);

        var dto = new OnlineExamTakeScreenDto
        {
            ExamId = exam.Id,
            ExamName = exam.Title,
            Description = exam.Description,
            Instructions = exam.Instructions,
            StartDateTime = exam.StartDateTime,
            EndDateTime = exam.EndDateTime,
            ExamDegree = await _unitOfWork.OnlineExamsRepo.GetTotalDegreeAsync(onlineExamId),
            StudentStatus = report?.Status.ToString(),
            BlockOnViolation = exam.BlockOnViolation,
            MaxViolations = exam.MaxViolations,
            ViolationCount = report?.ViolationCount ?? 0,
            Questions = questions.ToList(),
        };

        return Result<OnlineExamTakeScreenDto>.Success(dto, _localizer);
    }

    // ══════════════════════════════════════════════════════════════════════
    // S6-read — REVIEW SKELETON (correct hidden until finalize; nothing to
    // reveal yet since S3/S4 land in Phase 5 — this is the read path only)
    // ══════════════════════════════════════════════════════════════════════
    public async Task<Result<OnlineExamReviewDto>> GetReviewAsync(
        long teacherId, long teacherStudentId, long onlineExamId)
    {
        var exam = await _unitOfWork.OnlineExamsRepo.GetByIdAndTeacherAsync(onlineExamId, teacherId);
        if (exam is null || exam.Status == OnlineExamStatus.Draft)
            return Result<OnlineExamReviewDto>.Failure(_localizer, OnlineExamConstants.Messages.NotFound, HttpStatusCode.NotFound);

        var report = await _unitOfWork.StudentOnlineExamReportsRepo.GetReportWithAnswersAsync(onlineExamId, teacherStudentId);

        // Allow review if currently assigned, OR if they have a report (case-3: since
        // unassigned but already attempted — they can still see their own review).
        if (report is null)
        {
            bool isAssigned = await _unitOfWork.OnlineExamsRepo
                .BuildAssignedStudentIdsQuery(onlineExamId, teacherId)
                .AnyAsync(id => id == teacherStudentId);
            if (!isAssigned)
                return Result<OnlineExamReviewDto>.Failure(_localizer, OnlineExamConstants.Messages.NotInScope, HttpStatusCode.Forbidden);
        }
        if (report is not null)
            await _grading.TryAutoFinalizeAsync(exam, report);


        bool finalized = report?.SubmittedAt is not null;

        // Dedicated projection per finalize state — IsCorrect only ever touches the
        // response when finalized=true (do-not-reintroduce #9).
        var dto = new OnlineExamReviewDto
        {
            ExamId = exam.Id,
            ExamName = exam.Title,
            Finalized = finalized,
            ReportStatus = report?.Status.ToString(),
            Score = finalized ? report!.Score : null,
            Percentage = finalized ? report!.Percentage : null,
        };

        var answersByQuestion = report?.Answers.ToDictionary(a => a.QuestionId) ?? new();

        if (finalized)
        {
            var teacherRows = await _unitOfWork.OnlineExamsRepo.GetQuestionsForTeacherAsync(onlineExamId);
            // PERF: batch-resolve every question-image gated URL in ONE query (was an N+1).
            var imageUrls = await _fileAccess.TryBuildGatedUrlsAsync(
                teacherRows.Where(q => q.ImageFileInternalId is not null).Select(q => q.ImageFileInternalId!.Value));
            foreach (var q in teacherRows)
                q.ImageUrl = q.ImageFileInternalId is long fid ? imageUrls.GetValueOrDefault(fid) : null;
            dto.Questions = teacherRows.Select(q =>
            {
                answersByQuestion.TryGetValue(q.Id, out var answer);
                var selectedIds = answer?.SelectedOptions.Select(o => o.QuestionOptionId).ToHashSet() ?? new HashSet<long>();

                return new OnlineExamReviewQuestionDto
                {
                    QuestionId = q.Id,
                    QuestionText = q.QuestionText,
                    QuestionType = q.QuestionType,
                    Degree = q.Degree,
                    AwardedDegree = answer?.AwardedDegree,
                    ImageUrl = q.ImageUrl,
                    Options = q.Options.Select(o => new OnlineExamReviewOptionDto
                    {
                        OptionId = o.Id,
                        OptionText = o.OptionText,
                        IsSelected = selectedIds.Contains(o.Id),
                        IsCorrect = o.IsCorrect,
                    }).ToList(),
                };
            }).ToList();
        }
        else
        {
            var studentRows = await _unitOfWork.OnlineExamsRepo.GetQuestionsForStudentAsync(onlineExamId);
            // PERF: batch-resolve every question-image gated URL in ONE query (was an N+1).
            var imageUrls = await _fileAccess.TryBuildGatedUrlsAsync(
                studentRows.Where(q => q.ImageFileInternalId is not null).Select(q => q.ImageFileInternalId!.Value));
            foreach (var q in studentRows)
                q.ImageUrl = q.ImageFileInternalId is long fid ? imageUrls.GetValueOrDefault(fid) : null;
            dto.Questions = studentRows.Select(q =>
            {
                answersByQuestion.TryGetValue(q.Id, out var answer);
                var selectedIds = answer?.SelectedOptions.Select(o => o.QuestionOptionId).ToHashSet() ?? new HashSet<long>();

                return new OnlineExamReviewQuestionDto
                {
                    QuestionId = q.Id,
                    QuestionText = q.QuestionText,
                    QuestionType = q.QuestionType,
                    Degree = q.Degree,
                    AwardedDegree = null,
                    ImageUrl = q.ImageUrl,
                    Options = q.Options.Select(o => new OnlineExamReviewOptionDto
                    {
                        OptionId = o.Id,
                        OptionText = o.OptionText,
                        IsSelected = selectedIds.Contains(o.Id),
                        IsCorrect = null,
                    }).ToList(),
                };
            }).ToList();
        }

        return Result<OnlineExamReviewDto>.Success(dto, _localizer);
    }
    // ══════════════════════════════════════════════════════════════════════
    // S4 — ANSWER-BY-ANSWER (never finalizes)
    // ══════════════════════════════════════════════════════════════════════
    public async Task<Result<OnlineExamStatsDto>> SubmitAnswerAsync(
        long teacherId, long teacherStudentId, long onlineExamId, SubmitOnlineExamAnswerRequest request)
    {
        var (exam, failure) = await ValidateSubmissionWindowAsync(teacherId, teacherStudentId, onlineExamId);
        if (failure is not null) return failure;

        var questions = await _unitOfWork.OnlineExamsRepo.GetQuestionsForTeacherAsync(onlineExamId);
        var question = questions.FirstOrDefault(q => q.Id == request.QuestionId);
        if (question is null)
            return Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.QuestionNotFound, HttpStatusCode.NotFound);

        var optionValidation = ValidateSelectedOptions(question, request.SelectedOptionIds);
        if (optionValidation is not null)
            return Result<OnlineExamStatsDto>.Failure(_localizer, optionValidation, HttpStatusCode.BadRequest);

        var report = await GetOrCreateReportAsync(teacherId, teacherStudentId, onlineExamId);
        var lockCheck = CheckNotLocked(report);
        if (lockCheck is not null)
            return Result<OnlineExamStatsDto>.Failure(_localizer, lockCheck, HttpStatusCode.Conflict);

        var selectedIds = request.SelectedOptionIds.Distinct().ToHashSet();
        decimal awarded = _grading.GradeQuestion(question, selectedIds);
        var utcNow = DateTime.UtcNow;

        await UpsertAnswerAsync(report.Id, request.QuestionId, awarded, selectedIds, utcNow);
        await _unitOfWork.SaveChangesAsync();

        var allAnswers = await _unitOfWork.StudentOnlineExamReportsRepo.GetAnswersForReportAsync(report.Id);
        decimal totalGrade = questions.Sum(q => q.Degree);
        decimal score = allAnswers.Sum(a => a.AwardedDegree);

        // Running total only — Status/SubmittedAt untouched (never finalizes, §3.5).
        report.Score = score;
        report.Percentage = totalGrade > 0 ? Math.Round(score / totalGrade * 100, 2) : 0;
        report.UpdatedAt = utcNow;
        await _unitOfWork.StudentOnlineExamReportsRepo.UpdateAsync(report);
        await _unitOfWork.SaveChangesAsync();

        var stats = _grading.ComputeStats(questions, allAnswers, score, totalGrade);
        stats.Status = report.Status.ToString();
        return Result<OnlineExamStatsDto>.Success(stats, _localizer);
    }

    // ══════════════════════════════════════════════════════════════════════
    // S3 — BULK SUBMIT (finalize + lock)
    // ══════════════════════════════════════════════════════════════════════
    public async Task<Result<OnlineExamStatsDto>> SubmitExamAsync(
        long teacherId, long teacherStudentId, long onlineExamId, SubmitOnlineExamRequest request)
    {
        var (exam, failure) = await ValidateSubmissionWindowAsync(teacherId, teacherStudentId, onlineExamId);
        if (failure is not null) return failure;

        var questions = await _unitOfWork.OnlineExamsRepo.GetQuestionsForTeacherAsync(onlineExamId);
        var questionsById = questions.ToDictionary(q => q.Id);

        foreach (var a in request.Answers)
        {
            if (!questionsById.TryGetValue(a.QuestionId, out var q))
                return Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.QuestionNotFound, HttpStatusCode.BadRequest);

            var optionValidation = ValidateSelectedOptions(q, a.SelectedOptionIds);
            if (optionValidation is not null)
                return Result<OnlineExamStatsDto>.Failure(_localizer, optionValidation, HttpStatusCode.BadRequest);
        }

        var report = await GetOrCreateReportAsync(teacherId, teacherStudentId, onlineExamId);
        var lockCheck = CheckNotLocked(report);
        if (lockCheck is not null)
            return Result<OnlineExamStatsDto>.Failure(_localizer, lockCheck, HttpStatusCode.Conflict);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction) await _unitOfWork.BeginTransactionAsync();

        try
        {
            var utcNow = DateTime.UtcNow;

            foreach (var a in request.Answers)
            {
                var q = questionsById[a.QuestionId];
                var selected = a.SelectedOptionIds.Distinct().ToHashSet();
                decimal awarded = _grading.GradeQuestion(q, selected);
                await UpsertAnswerAsync(report.Id, a.QuestionId, awarded, selected, utcNow);
            }

            await _unitOfWork.SaveChangesAsync();

            var allAnswers = await _unitOfWork.StudentOnlineExamReportsRepo.GetAnswersForReportAsync(report.Id);
            decimal totalGrade = questions.Sum(q => q.Degree);
            decimal score = allAnswers.Sum(a => a.AwardedDegree);
            decimal percentage = totalGrade > 0 ? Math.Round(score / totalGrade * 100, 2) : 0;

            report.Score = score;
            report.Percentage = percentage;
            report.SubmittedAt = utcNow; // finalize + lock
            report.Status = percentage >= exam!.PassPercentage ? StudentOnlineExamStatus.Passed : StudentOnlineExamStatus.Failed;
            report.UpdatedAt = utcNow;
            await _unitOfWork.StudentOnlineExamReportsRepo.UpdateAsync(report);

            try
            {
                await _unitOfWork.SaveChangesAsync();
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
            {
                // Concurrent submit already finalized this report — RowVersion caught the race.
                if (ownsTransaction) await _unitOfWork.RollbackAsync();
                return Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.AlreadySubmitted, HttpStatusCode.Conflict);
            }

            if (ownsTransaction) await _unitOfWork.CommitAsync();

            var stats = _grading.ComputeStats(questions, allAnswers, score, totalGrade);
            stats.Status = report.Status.ToString();
            return Result<OnlineExamStatsDto>.Success(stats, _localizer);
        }
        catch
        {
            if (ownsTransaction) await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // S5 — RESULT (triggers auto-finalize if window has closed)
    // ══════════════════════════════════════════════════════════════════════
    public async Task<Result<OnlineExamStatsDto>> GetResultAsync(
        long teacherId, long teacherStudentId, long onlineExamId)
    {
        var exam = await _unitOfWork.OnlineExamsRepo.GetByIdAndTeacherAsync(onlineExamId, teacherId);
        if (exam is null)
            return Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.NotFound, HttpStatusCode.NotFound);

        var report = await _unitOfWork.StudentOnlineExamReportsRepo.GetByExamAndStudentAsync(onlineExamId, teacherStudentId);
        if (report is null)
        {
            // Never-attempted + availability window CLOSED + student is in scope ⇒ a virtual "Missed"
            // result (zeroed body) instead of 404, so the app can show a proper "you missed this exam"
            // screen. No report row is created (Missed is derived, like NotAttended). Before the window
            // closes, or for a student not in scope, keep the 404 (nothing to show yet).
            bool windowClosed = exam.Status != OnlineExamStatus.Draft && DateTime.UtcNow > exam.EndDateTime;
            bool isAssigned = windowClosed && await _unitOfWork.OnlineExamsRepo
                .BuildAssignedStudentIdsQuery(onlineExamId, teacherId)
                .AnyAsync(id => id == teacherStudentId);
            if (windowClosed && isAssigned)
            {
                var missedQuestions = await _unitOfWork.OnlineExamsRepo.GetQuestionsForTeacherAsync(onlineExamId);
                var missedStats = _grading.ComputeStats(
                    missedQuestions, Array.Empty<StudentQuestionAnswer>(), 0m, missedQuestions.Sum(q => q.Degree));
                missedStats.Status = StudentOnlineExamStatus.Missed.ToString();
                return Result<OnlineExamStatsDto>.Success(missedStats, _localizer);
            }

            return Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.ReportNotFound, HttpStatusCode.NotFound);
        }

        await _grading.TryAutoFinalizeAsync(exam, report);

        var questions = await _unitOfWork.OnlineExamsRepo.GetQuestionsForTeacherAsync(onlineExamId);
        var answers = await _unitOfWork.StudentOnlineExamReportsRepo.GetAnswersForReportAsync(report.Id);
        decimal totalGrade = questions.Sum(q => q.Degree);

        var stats = _grading.ComputeStats(questions, answers, report.Score, totalGrade);
        stats.Status = report.Status.ToString();
        stats.ViolationCount = report.ViolationCount;
        return Result<OnlineExamStatsDto>.Success(stats, _localizer);
    }

    // ══════════════════════════════════════════════════════════════════════
    // O1 — SELF-SERVICE BLOCK (front-end fires this when the student leaves an
    // in-progress exam; sets the caller's OWN report to Blocked). Distinct from
    // the teacher T5s block (OnlineExamService.UpdateStudentStatusAsync) — the
    // same underlying report status-setting sequence, but self-service (identity
    // is the JWT-resolved caller) with an assignment gate instead of an ownership
    // guard. Online exams have NO retake, so blocking is terminal for the attempt.
    // ══════════════════════════════════════════════════════════════════════
    public async Task<Result<OnlineExamStatsDto>> BlockMyExamAsync(
        long teacherId, long teacherStudentId, long onlineExamId)
    {
        var exam = await _unitOfWork.OnlineExamsRepo.GetByIdAndTeacherAsync(onlineExamId, teacherId);
        if (exam is null || exam.Status == OnlineExamStatus.Draft)
            return Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.NotFound, HttpStatusCode.NotFound);

        var report = await _unitOfWork.StudentOnlineExamReportsRepo.GetByExamAndStudentAsync(onlineExamId, teacherStudentId);

        // Assignment gate — a caller with an existing report was assigned when they started, so
        // allow the block even if later unassigned (case-3); otherwise require live scope.
        if (report is null)
        {
            bool isAssigned = await _unitOfWork.OnlineExamsRepo
                .BuildAssignedStudentIdsQuery(onlineExamId, teacherId)
                .AnyAsync(id => id == teacherStudentId);
            if (!isAssigned)
                return Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.NotInScope, HttpStatusCode.Forbidden);
        }

        var utcNow = DateTime.UtcNow;
        string resultCode;

        if (report is null)
        {
            // Lazy-create as Blocked — same shape as GetOrCreateReportAsync / the T5s create branch.
            report = new StudentOnlineExamReport
            {
                OnlineExamId = onlineExamId,
                TeacherStudentId = teacherStudentId,
                TeacherId = teacherId,
                Status = StudentOnlineExamStatus.Blocked,
                Score = 0,
                Percentage = 0,
                CreateAt = utcNow,
            };
            await _unitOfWork.StudentOnlineExamReportsRepo.AddAsync(report);
            try
            {
                await _unitOfWork.SaveChangesAsync();
                resultCode = OnlineExamConstants.Messages.ExamBlocked;
            }
            catch (DbUpdateException)
            {
                // First-write INSERT race: a concurrent submit/block created the report between our
                // null-read and this insert, colliding on UX_StudentOnlineExamReports_Exam_Student.
                // Re-read and return a terminal outcome instead of a 500. No second write here — the
                // change tracker still holds the failed insert, so we only READ from this point.
                var raced = await _unitOfWork.StudentOnlineExamReportsRepo
                    .GetByExamAndStudentAsync(onlineExamId, teacherStudentId);
                if (raced is null)
                    return Result<OnlineExamStatsDto>.Failure(
                        _localizer, OnlineExamConstants.Messages.NotFound, HttpStatusCode.NotFound);
                if (raced.SubmittedAt is not null)
                    return Result<OnlineExamStatsDto>.Failure(
                        _localizer, OnlineExamConstants.Messages.ExamAlreadyFinalized, HttpStatusCode.Conflict);

                // The report now exists (Blocked — the dominant self-block race — or still InProgress);
                // the caller's "leaving the exam" intent is already satisfied by a live non-finalized
                // report, so report it with the inert AlreadyBlocked code (idempotent, same as below).
                report = raced;
                resultCode = OnlineExamConstants.Messages.AlreadyBlocked;
            }
        }
        else if (report.Status == StudentOnlineExamStatus.Blocked)
        {
            // Idempotent — already blocked (a Blocked report never carries SubmittedAt).
            resultCode = OnlineExamConstants.Messages.AlreadyBlocked;
        }
        else if (report.SubmittedAt is not null)
        {
            // Finalized (Passed/Failed) — blocking is moot, and online exams have no retake.
            return Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.ExamAlreadyFinalized, HttpStatusCode.Conflict);
        }
        else
        {
            // InProgress → Blocked.
            report.Status = StudentOnlineExamStatus.Blocked;
            report.UpdatedAt = utcNow;
            await _unitOfWork.StudentOnlineExamReportsRepo.UpdateAsync(report);
            try
            {
                await _unitOfWork.SaveChangesAsync();
                resultCode = OnlineExamConstants.Messages.ExamBlocked;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent SubmitExamAsync finalized this report between our read and save — the
                // block's UPDATE matched 0 rows (RowVersion moved). Blocking a finalized report is
                // moot; return the same 409 the already-finalized branch does (never a 500), matching
                // the sibling submit/auto-finalize paths.
                return Result<OnlineExamStatsDto>.Failure(
                    _localizer, OnlineExamConstants.Messages.ExamAlreadyFinalized, HttpStatusCode.Conflict);
            }
        }

        var questions = await _unitOfWork.OnlineExamsRepo.GetQuestionsForTeacherAsync(onlineExamId);
        var answers = await _unitOfWork.StudentOnlineExamReportsRepo.GetAnswersForReportAsync(report.Id);
        decimal totalGrade = questions.Sum(q => q.Degree);

        var stats = _grading.ComputeStats(questions, answers, report.Score, totalGrade);
        stats.Status = report.Status.ToString();
        return Result<OnlineExamStatsDto>.Success(stats, _localizer, resultCode);
    }

    // ══════════════════════════════════════════════════════════════════════
    // O2 — RECORD ANTI-CHEAT VIOLATION (tolerant path; the app calls this each
    // time the student leaves/backgrounds the exam when BlockOnViolation is on).
    // Server-authoritative so the tally survives an app kill; blocks at the
    // exam's MaxViolations tolerance. Distinct from O1 /block (the terminal action).
    // ══════════════════════════════════════════════════════════════════════
    public async Task<Result<ViolationRecordedDto>> RecordViolationAsync(
        long teacherId, long teacherStudentId, long onlineExamId)
    {
        var exam = await _unitOfWork.OnlineExamsRepo.GetByIdAndTeacherAsync(onlineExamId, teacherId);
        if (exam is null || exam.Status == OnlineExamStatus.Draft)
            return Result<ViolationRecordedDto>.Failure(_localizer, OnlineExamConstants.Messages.NotFound, HttpStatusCode.NotFound);

        // A violation only makes sense while the exam is open. (Same window shape as
        // ValidateSubmissionWindowAsync, inlined — that helper is hard-typed to OnlineExamStatsDto.)
        var now = DateTime.UtcNow;
        if (now < exam.StartDateTime || now > exam.EndDateTime)
            return Result<ViolationRecordedDto>.Failure(_localizer, OnlineExamConstants.Messages.WindowClosed, HttpStatusCode.Conflict);

        var report = await _unitOfWork.StudentOnlineExamReportsRepo.GetByExamAndStudentAsync(onlineExamId, teacherStudentId);
        if (report is null)
        {
            bool isAssigned = await _unitOfWork.OnlineExamsRepo
                .BuildAssignedStudentIdsQuery(onlineExamId, teacherId)
                .AnyAsync(id => id == teacherStudentId);
            if (!isAssigned)
                return Result<ViolationRecordedDto>.Failure(_localizer, OnlineExamConstants.Messages.NotInScope, HttpStatusCode.Forbidden);

            // Lazy-create InProgress (a student can violate before answering anything) — same first-write
            // race handling as BlockMyExamAsync (re-read on the UX collision instead of a 500).
            report = new StudentOnlineExamReport
            {
                OnlineExamId = onlineExamId,
                TeacherStudentId = teacherStudentId,
                TeacherId = teacherId,
                Status = StudentOnlineExamStatus.InProgress,
                Score = 0,
                Percentage = 0,
                ViolationCount = 0,
                CreateAt = now,
            };
            await _unitOfWork.StudentOnlineExamReportsRepo.AddAsync(report);
            try
            {
                await _unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                var raced = await _unitOfWork.StudentOnlineExamReportsRepo
                    .GetByExamAndStudentAsync(onlineExamId, teacherStudentId);
                if (raced is null)
                    return Result<ViolationRecordedDto>.Failure(_localizer, OnlineExamConstants.Messages.NotFound, HttpStatusCode.NotFound);
                report = raced;
            }
        }

        // Terminal states — nothing to increment; report the current tally idempotently.
        if (report.SubmittedAt is not null)
            return Result<ViolationRecordedDto>.Success(new ViolationRecordedDto
            {
                ViolationCount = report.ViolationCount,
                MaxViolations = exam.MaxViolations,
                IsBlocked = false,
            }, _localizer, OnlineExamConstants.Messages.ViolationRecorded);

        if (report.Status == StudentOnlineExamStatus.Blocked)
            return Result<ViolationRecordedDto>.Success(new ViolationRecordedDto
            {
                ViolationCount = report.ViolationCount,
                MaxViolations = exam.MaxViolations,
                IsBlocked = true,
            }, _localizer, OnlineExamConstants.Messages.AlreadyBlocked);

        // Atomic increment (set-based — no lost updates, and it leaves the tracked `report` copy stale on
        // purpose; we never SaveChanges it after this).
        int newCount = await _unitOfWork.StudentOnlineExamReportsRepo.IncrementViolationCountAsync(report.Id);

        bool isBlocked = false;
        if (exam.BlockOnViolation && newCount >= exam.MaxViolations)
            isBlocked = await _unitOfWork.StudentOnlineExamReportsRepo.TryBlockForViolationAsync(report.Id, now);

        return Result<ViolationRecordedDto>.Success(new ViolationRecordedDto
        {
            ViolationCount = newCount,
            MaxViolations = exam.MaxViolations,
            IsBlocked = isBlocked,
        }, _localizer, isBlocked ? OnlineExamConstants.Messages.ExamBlocked : OnlineExamConstants.Messages.ViolationRecorded);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════════════════════════════

    private async Task<(OnlineExam? Exam, Result<OnlineExamStatsDto>? Failure)> ValidateSubmissionWindowAsync(
        long teacherId, long teacherStudentId, long onlineExamId)
    {
        var exam = await _unitOfWork.OnlineExamsRepo.GetByIdAndTeacherAsync(onlineExamId, teacherId);
        if (exam is null || exam.Status != OnlineExamStatus.Published)
            return (null, Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.NotFound, HttpStatusCode.NotFound));

        var now = DateTime.UtcNow;
        if (now < exam.StartDateTime || now > exam.EndDateTime)
            return (null, Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.WindowClosed, HttpStatusCode.Conflict));

        // Assignment gate — a caller with an existing report was assigned when they
        // started, so let them finish even if later unassigned (case-3), exactly as the
        // result, review, block and violation paths already do. Without this, a teacher
        // editing an exam's recipients mid-sitting would 403 that student's next answer
        // and their final submit, stranding an attempt they were entitled to make; the
        // window and status checks above still apply to everyone.
        //
        // The report is read only AFTER a failed scope check, unlike the sibling paths
        // that need it regardless: this runs on every answer save, and the caller loads
        // the report itself straight afterwards.
        bool isAssigned = await _unitOfWork.OnlineExamsRepo
            .BuildAssignedStudentIdsQuery(onlineExamId, teacherId)
            .AnyAsync(id => id == teacherStudentId);
        if (!isAssigned)
        {
            var startedReport = await _unitOfWork.StudentOnlineExamReportsRepo
                .GetByExamAndStudentAsync(onlineExamId, teacherStudentId);
            if (startedReport is null)
                return (null, Result<OnlineExamStatsDto>.Failure(_localizer, OnlineExamConstants.Messages.NotInScope, HttpStatusCode.Forbidden));
        }

        return (exam, null);
    }

    private static string? ValidateSelectedOptions(Domain.Interfaces.OnlineExamQuestionRow question, List<long> selectedOptionIds)
    {
        var validOptionIds = question.Options.Select(o => o.Id).ToHashSet();
        var selected = selectedOptionIds.Distinct().ToHashSet();

        if (selected.Any(id => !validOptionIds.Contains(id)))
            return OnlineExamConstants.Messages.InvalidOptionSelection;

        if (question.QuestionType == OnlineExamQuestionType.SingleChoice && selected.Count != 1)
            return OnlineExamConstants.Messages.SingleChoiceNeedsExactlyOneCorrect;

        if (question.QuestionType == OnlineExamQuestionType.MultipleChoice && selected.Count == 0)
            return OnlineExamConstants.Messages.MultipleChoiceNeedsAtLeastOneCorrect;

        return null;
    }

    private static string? CheckNotLocked(StudentOnlineExamReport report)
    {
        if (report.Status == StudentOnlineExamStatus.Blocked)
            return OnlineExamConstants.Messages.StudentBlocked;

        if (report.SubmittedAt is not null) // locked — Passed or Failed already
            return OnlineExamConstants.Messages.AlreadySubmitted;

        return null;
    }

    private async Task<StudentOnlineExamReport> GetOrCreateReportAsync(long teacherId, long teacherStudentId, long onlineExamId)
    {
        var report = await _unitOfWork.StudentOnlineExamReportsRepo.GetByExamAndStudentAsync(onlineExamId, teacherStudentId);
        if (report is not null) return report;

        report = new StudentOnlineExamReport
        {
            OnlineExamId = onlineExamId,
            TeacherStudentId = teacherStudentId,
            TeacherId = teacherId,
            Status = StudentOnlineExamStatus.InProgress,
            Score = 0,
            Percentage = 0,
            CreateAt = DateTime.UtcNow,
        };
        await _unitOfWork.StudentOnlineExamReportsRepo.AddAsync(report);
        await _unitOfWork.SaveChangesAsync(); // need report.Id for child answers
        return report;
    }

    private async Task UpsertAnswerAsync(
        long reportId, long questionId, decimal awardedDegree, HashSet<long> selectedIds, DateTime utcNow)
    {
        var existing = await _unitOfWork.StudentOnlineExamReportsRepo.GetAnswerByReportAndQuestionAsync(reportId, questionId);

        if (existing is null)
        {
            await _unitOfWork.StudentOnlineExamReportsRepo.AddAnswerAsync(new StudentQuestionAnswer
            {
                StudentReportId = reportId,
                QuestionId = questionId,
                AwardedDegree = awardedDegree,
                CreateAt = utcNow,
                SelectedOptions = selectedIds.Select(id => new StudentQuestionAnswerOption
                {
                    QuestionOptionId = id,
                    CreateAt = utcNow,
                }).ToList(),
            });
        }
        else
        {
            existing.AwardedDegree = awardedDegree; // re-answer overwrites
            await _unitOfWork.StudentOnlineExamReportsRepo.ReplaceAnswerOptionsAsync(
                existing.Id,
                selectedIds.Select(id => new StudentQuestionAnswerOption
                {
                    StudentQuestionAnswerId = existing.Id,
                    QuestionOptionId = id,
                    CreateAt = utcNow,
                }).ToList());
        }
    }
}