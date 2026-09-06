using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Edvanz.Application.Common;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Attendance;
using Edvanz.Application.Dtos.Payment;
using Edvanz.Application.Dtos.StudentUser;
using Edvanz.Application.Dtos.VideoContentManagement;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Edvanz.Application.Services;

/// <summary>
/// Aggregates the student "teacher home" screen (Figma 232:7033) into a single
/// call. Resolves the caller once (JWT student → ACTIVE, bound link under the
/// selected teacher), then fills each module section behind its own
/// <see cref="Edvanz.Domain.Entities.TeacherConfiguration"/> visibility flag,
/// reusing the existing per-module student services. Resilient by design: a module
/// that errors degrades to visible+empty for that section, never a whole-page 500.
/// </summary>
public sealed class StudentTeacherHomeService : IStudentTeacherHomeService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAttendanceService _attendanceService;
    private readonly IPaymentService _paymentService;
    private readonly IVideoService _videoService;
    private readonly IStudentOnlineExamService _onlineExamService;
    private readonly IExamHomeworkService _examHomeworkService;
    private readonly ITimeZoneService _timeZoneService;
    private readonly IStringLocalizer<Edvanz.Domain.Resources.Messages> _localizer;
    private readonly ILogger<StudentTeacherHomeService> _logger;

    /// <summary>Max merged upcoming-exam rows returned (the card shows the nearest; the rest feed "view all").</summary>
    private const int UpcomingExamsCap = 10;

    /// <summary>Offline-exam month strip window: this many months back through the current month (plus future months that have exams).</summary>
    private const int OfflineStripMonths = 6;

    /// <summary>Page size used to pull the offline-exam list for the count / strip (bounded per student·teacher).</summary>
    private const int OfflineFetchPageSize = 200;

    public StudentTeacherHomeService(
        IUnitOfWork unitOfWork,
        IAttendanceService attendanceService,
        IPaymentService paymentService,
        IVideoService videoService,
        IStudentOnlineExamService onlineExamService,
        IExamHomeworkService examHomeworkService,
        ITimeZoneService timeZoneService,
        IStringLocalizer<Edvanz.Domain.Resources.Messages> localizer,
        ILogger<StudentTeacherHomeService> logger)
    {
        _unitOfWork = unitOfWork;
        _attendanceService = attendanceService;
        _paymentService = paymentService;
        _videoService = videoService;
        _onlineExamService = onlineExamService;
        _examHomeworkService = examHomeworkService;
        _timeZoneService = timeZoneService;
        _localizer = localizer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<StudentTeacherHomeDto>> GetTeacherHomeAsync(
        long studentUserId, long teacherId, int? year, int? month, string? deviceId)
    {
        // ── Access gate (mirrors every other student read: active + bound link) ──
        var studentUser = await _unitOfWork.Users.GetActiveStudentUserByIdAsync(studentUserId);
        if (studentUser is null)
            return Result<StudentTeacherHomeDto>.Failure(_localizer, "StudentUserNotFound", HttpStatusCode.NotFound);

        var link = await _unitOfWork.Users.GetActiveStudentTeacherLinkAsync(studentUserId, teacherId);
        if (link is null || link.LinkStatus != LinkStatus.Active)
            return Result<StudentTeacherHomeDto>.Failure(_localizer, "TeacherLinkNotFound", HttpStatusCode.Forbidden);

        if (link.TeacherStudentId is null)
            return Result<StudentTeacherHomeDto>.Failure(_localizer, "StudentEnrollmentRemoved", HttpStatusCode.Forbidden);

        long teacherStudentId = link.TeacherStudentId.Value;
        string? language = studentUser.LanguagePreference;

        // ── Teacher header + visibility flags (one batch call, same resolution as the dashboard) ──
        var batch = await _unitOfWork.Users.GetTeacherDashboardDataAsync(new List<long> { teacherId });
        var teacher = batch.Teachers.GetValueOrDefault(teacherId);
        batch.Configurations.TryGetValue(teacherId, out var config);

        // ── Device lock (per teacher): only opens from the student's registered device. ──
        var deviceDecision = StudentDeviceLockPolicy.Evaluate(link, config, deviceId);
        if (deviceDecision == DeviceLockDecision.RegistrationRequired)
            return Result<StudentTeacherHomeDto>.Failure(_localizer, StudentDeviceLockPolicy.RegistrationRequiredCode, HttpStatusCode.Conflict);
        if (deviceDecision == DeviceLockDecision.Mismatch)
            return Result<StudentTeacherHomeDto>.Failure(_localizer, StudentDeviceLockPolicy.MismatchCode, HttpStatusCode.Forbidden);

        string teacherName = string.Empty;
        if (teacher is not null && batch.Users.TryGetValue(teacher.UserId, out var teacherUser))
            teacherName = teacherUser.FullName;

        string subjectName = teacher?.CustomSubject ?? string.Empty;
        if (teacher is not null &&
            batch.TeacherSubjects.TryGetValue(teacherId, out var teacherSubjects) &&
            teacherSubjects.Any() &&
            batch.Subjects.TryGetValue(teacherSubjects.First().SubjectId, out var subject))
        {
            // Respect the student's language preference (AAM-FR-02.2) — same rule as the dashboard.
            subjectName = language == "ar" ? subject.NameAr : subject.NameEn;
        }

        // ── Per-module visibility (fail-open to the entity defaults when the config row is missing) ──
        bool vAttendance = config?.StudentVisibilityAttendance ?? true;
        bool vPayment = config?.StudentVisibilityPayment ?? true;
        bool vVideo = config?.StudentVisibilityVideo ?? true;
        bool vHomework = config?.StudentVisibilityHomework ?? true;
        bool vOnlineExam = config?.StudentVisibilityOnlineExamDefault ?? true;
        bool vOfflineExam = config?.StudentVisibilityExamDefault ?? true;

        // ── Month scoping: teacher-local Africa/Cairo current month, or an explicit year+month override ──
        DateTime localToday = _timeZoneService.GetTeacherLocalDate(teacherId);
        bool explicitMonth = year is >= 1 and <= 9999 && month is >= 1 and <= 12;
        int scopeYear = explicitMonth ? year!.Value : localToday.Year;
        int scopeMonth = explicitMonth ? month!.Value : localToday.Month;

        // ── Assigned session (REQ-STU-004 / BR-SES-002): null when the teacher hasn't put this
        // student in a session yet. Videos AND online exams are session-scoped only, so this is
        // what lets the app explain an empty list instead of showing a bare empty state.
        // One indexed seek, tenant-scoped inside the repo — not fail-soft on purpose: a silent
        // null here would read as "unassigned" and show the student a wrong, alarming note.
        var assignedSession = await _unitOfWork.Students
            .GetAssignedSessionAsync(teacherStudentId, teacherId);

        var home = new StudentTeacherHomeDto
        {
            TeacherId = teacherId,
            TeacherName = teacherName,
            SubjectName = subjectName,
            TeacherCode = teacher?.TeacherCode ?? string.Empty,
            LinkStatus = link.LinkStatus.ToString(),
            IsLinked = true, // guaranteed by the gate above (Active + bound)
            SessionId = assignedSession?.Id,
            SessionName = assignedSession?.SessionName,
            // Fail-open to InApp (soft QR visible) when the config row is missing, same as the
            // visibility toggles above. HardCopyOnly means the teacher hands out printed cards.
            IsBarcodeInAppEnabled =
                (config?.BarcodeDisplayMode ?? BarcodeDisplayMode.InApp) == BarcodeDisplayMode.InApp,
            Month = $"{scopeYear:D4}-{scopeMonth:D2}",
            MonthLabel = MonthName(scopeYear, scopeMonth),
            Attendance = await BuildAttendanceAsync(teacherId, teacherStudentId, year, month, vAttendance),
            Payment = await BuildPaymentAsync(teacherId, teacherStudentId, localToday, vPayment),
            Videos = await BuildVideosAsync(teacherId, teacherStudentId, language, vVideo),
            Homework = new HomeHomeworkDto { Visible = vHomework, Count = 0 }, // read surface not built yet — keys only
            Exams = await BuildExamsAsync(teacherId, teacherStudentId, language, localToday, vOnlineExam, vOfflineExam)
        };

        return Result<StudentTeacherHomeDto>.Success(home, _localizer, "Success", HttpStatusCode.OK);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MODULE BUILDERS (each fail-soft: a module error → visible + empty data)
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<HomeAttendanceDto> BuildAttendanceAsync(
        long teacherId, long teacherStudentId, int? year, int? month, bool visible)
    {
        var section = new HomeAttendanceDto { Visible = visible };
        if (!visible) return section;
        try
        {
            var request = new StudentTimelineMonthRequest { Year = year, Month = month };
            var result = await _attendanceService.GetStudentViewAttendanceAsync(
                teacherId, teacherStudentId, request, AttendanceViewerType.Student);
            if (result.IsSuccess && result.Data is { } data)
            {
                section.MonthLabel = MonthName(data.Year, data.Month);
                section.AttendancePercentage = data.AttendancePercentage;
                section.PresentDays = data.TotalPresent;
                section.AbsentDays = data.TotalAbsences;
                section.TotalOccurrences = data.TotalOccurrences;
                section.MarkedOccurrences = data.MarkedOccurrences;
                section.SessionName = data.SessionName;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Home aggregate: attendance failed (teacher {TeacherId}, roster {RosterId})", teacherId, teacherStudentId);
        }
        return section;
    }

    private async Task<HomePaymentDto> BuildPaymentAsync(
        long teacherId, long teacherStudentId, DateTime localToday, bool visible)
    {
        var section = new HomePaymentDto { Visible = visible };
        if (!visible) return section;
        try
        {
            var result = await _paymentService.GetStudentPaymentTrackingAsync(
                teacherId, teacherStudentId, PaymentViewerType.Student);
            if (result.IsSuccess && result.Data is { } data)
            {
                // The payment tracking screen is always scoped to the teacher-local current month.
                section.MonthLabel = MonthName(localToday.Year, localToday.Month);
                section.PaidProgressRatio = data.PaidProgressRatio;

                // The current month's period lands in Paid (settled) or Overdue (owed through this month).
                var currentMonthPeriod = EnumerateAllPeriods(data).FirstOrDefault(p =>
                    p.PeriodStartDate.Year == localToday.Year && p.PeriodStartDate.Month == localToday.Month);
                if (currentMonthPeriod is not null)
                {
                    section.CurrentMonthStatus = currentMonthPeriod.Status.ToString();
                    section.CurrentMonthAmountDue = currentMonthPeriod.AmountDue;
                    section.CurrentMonthAmountPaid = currentMonthPeriod.AmountPaid;
                }
                else
                {
                    section.CurrentMonthStatus = "NoObligation";
                }

                section.OverdueAmount = data.OverdueSection.TotalAmount;
                var earliestOverdue = data.OverdueSection.Periods
                    .OrderBy(p => p.PeriodStartDate).FirstOrDefault();
                if (earliestOverdue is not null)
                    section.OverdueFromMonthLabel = MonthName(earliestOverdue.PeriodStartDate.Year, earliestOverdue.PeriodStartDate.Month);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Home aggregate: payment failed (teacher {TeacherId}, roster {RosterId})", teacherId, teacherStudentId);
        }
        return section;
    }

    private async Task<HomeVideosDto> BuildVideosAsync(
        long teacherId, long teacherStudentId, string? language, bool visible)
    {
        var section = new HomeVideosDto { Visible = visible };
        if (!visible) return section;
        try
        {
            // Two COUNTs over the same visible-video predicate the student's own
            // list uses. Replaces fetching a page of one row purely to read its
            // total, and carries the unopened count the tile now shows.
            var result = await _videoService.GetStudentVideoProgressAsync(teacherId, teacherStudentId);
            if (result.IsSuccess && result.Data is { } progress)
            {
                section.Count = progress.Total;
                section.NotStartedCount = progress.NotStarted;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Home aggregate: videos failed (teacher {TeacherId}, roster {RosterId})", teacherId, teacherStudentId);
        }
        return section;
    }

    private async Task<HomeExamsDto> BuildExamsAsync(
        long teacherId, long teacherStudentId, string? language, DateTime localToday,
        bool onlineVisible, bool offlineVisible)
    {
        var section = new HomeExamsDto { OnlineVisible = onlineVisible, OfflineVisible = offlineVisible };
        var upcoming = new List<HomeExamItemDto>();
        var today = DateOnly.FromDateTime(localToday);
        int tileCount = 0;

        // ── Online upcoming exams ──
        if (onlineVisible)
        {
            try
            {
                var result = await _onlineExamService.GetMyExamsAsync(teacherId, teacherStudentId, language);
                if (result.IsSuccess && result.Data is { } data)
                {
                    foreach (var e in data.Upcoming)
                    {
                        upcoming.Add(new HomeExamItemDto
                        {
                            ExamType = "Online",
                            ExamId = e.ExamId,
                            ExamName = e.ExamName,
                            Subject = e.Subject,
                            Date = e.ExamDate,
                            Time = e.ExamTime,
                            Status = "Upcoming",
                            QuestionsCount = e.QuestionsCount,
                            DurationMinutes = (int)Math.Round(e.Duration.TotalMinutes),
                            MaxGrade = e.ExamDegree,
                            StudentStatus = e.StudentStatus
                        });
                    }
                    // Tile count = ALL online exams this student has (upcoming + past), so it reflects
                    // the exam page rather than reading 0 when nothing is upcoming. Offline exams are
                    // deliberately NOT counted here. The `upcoming` preview list below stays upcoming-only.
                    tileCount += data.Upcoming.Count + data.Past.Count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Home aggregate: online exams failed (teacher {TeacherId}, roster {RosterId})", teacherId, teacherStudentId);
            }
        }

        // ── Offline upcoming exams + month strip ──
        if (offlineVisible)
        {
            try
            {
                var result = await _examHomeworkService.GetMyOfflineExamsAsync(
                    teacherId, teacherStudentId, language, page: 1, pageSize: OfflineFetchPageSize);
                if (result.IsSuccess && result.Data?.data is { } rows)
                {
                    var offlineUpcoming = rows.Where(r => r.Date >= today).ToList();
                    foreach (var r in offlineUpcoming)
                    {
                        upcoming.Add(new HomeExamItemDto
                        {
                            ExamType = "Offline",
                            ExamId = r.ExamId,
                            ExamName = r.ExamName,
                            Subject = r.Subject,
                            Date = r.Date,
                            Status = r.Status.ToString(),
                            MaxGrade = r.MaxGrade
                        });
                    }
                    // Offline exams contribute to the upcoming preview + month strip but NOT to tileCount
                    // (the tile counts online exams only, per product decision).

                    section.OfflineMonths = BuildOfflineMonths(rows, localToday);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Home aggregate: offline exams failed (teacher {TeacherId}, roster {RosterId})", teacherId, teacherStudentId);
            }
        }

        section.Upcoming = upcoming
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Time ?? TimeOnly.MinValue)
            .Take(UpcomingExamsCap)
            .ToList();
        section.TileCount = tileCount;
        return section;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Full month name in the invariant culture (e.g. "March").</summary>
    private static string MonthName(int year, int month) =>
        new DateTime(year, month, 1).ToString("MMMM", CultureInfo.InvariantCulture);

    /// <summary>Flattens the tracking payload's Paid + Overdue + Upcoming periods into one stream.</summary>
    private static IEnumerable<StudentPaymentPeriodDto> EnumerateAllPeriods(StudentPaymentTrackingDto data)
    {
        foreach (var p in data.PaidSection.Periods) yield return p;
        foreach (var p in data.OverdueSection.Periods) yield return p;
        if (data.UpcomingPayment is not null) yield return data.UpcomingPayment;
    }

    /// <summary>
    /// Builds the offline-exam month strip: one cell per month (within a rolling
    /// <see cref="OfflineStripMonths"/>-month window through the current month, plus any
    /// future month that already has an exam) that has ≥1 offline exam. A month is "Done"
    /// when all its exams are in the past, "Pending" when one is still upcoming.
    /// </summary>
    private static List<HomeOfflineExamMonthDto> BuildOfflineMonths(
        List<StudentOfflineExamListItemDto> rows, DateTime localToday)
    {
        var today = DateOnly.FromDateTime(localToday);
        var windowStart = new DateOnly(localToday.Year, localToday.Month, 1).AddMonths(-(OfflineStripMonths - 1));

        return rows
            .GroupBy(r => new { r.Date.Year, r.Date.Month })
            .Where(g => new DateOnly(g.Key.Year, g.Key.Month, 1) >= windowStart)
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new HomeOfflineExamMonthDto
            {
                Month = $"{g.Key.Year:D4}-{g.Key.Month:D2}",
                Label = new DateOnly(g.Key.Year, g.Key.Month, 1).ToString("MMM", CultureInfo.InvariantCulture),
                Status = g.All(r => r.Date < today) ? "Done" : "Pending",
                ExamCount = g.Count()
            })
            .ToList();
    }
}
