using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.AdminInsights;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Domain.Resources;
using Microsoft.Extensions.Localization;
using System.Net;

namespace Edvanz.Application.Services;

/// <summary>
/// The SuperAdmin insights surface. See <see cref="IAdminInsightsService"/> for the contract.
///
/// This service MAPS and COMPOSES; it does not aggregate. Every number it returns was computed by the
/// nightly rollup and filtered in SQL by <see cref="IAdminInsightsRepo"/>.
/// </summary>
public class AdminInsightsService : IAdminInsightsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITeacherUsageRollupService _rollup;
    private readonly ITimeZoneService _timeZone;
    private readonly IStringLocalizer<Messages> _localizer;

    public AdminInsightsService(
        IUnitOfWork unitOfWork,
        ITeacherUsageRollupService rollup,
        ITimeZoneService timeZone,
        IStringLocalizer<Messages> localizer)
    {
        _unitOfWork = unitOfWork;
        _rollup = rollup;
        _timeZone = timeZone;
        _localizer = localizer;
    }

    // ════════════════════════════════════════════════════════════════════════
    // OVERVIEW
    // ════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<AdminOverviewDto>> GetOverviewAsync()
    {
        var repo = _unitOfWork.AdminInsightsRepo;

        // "Today" for the platform is the tenant timezone's today (Africa/Cairo), not UTC's — at
        // 01:00 Cairo, UtcNow.Date is still yesterday and every window would be off by a day.
        DateOnly today = DateOnly.FromDateTime(_timeZone.ConvertUtcToLocal(DateTime.UtcNow));

        var agg = await repo.GetOverviewAggregatesAsync(today);

        var cards = new List<AdminInsightCardDto>();
        foreach (var (kind, severity) in InsightOrder)
        {
            var (rows, total) = await repo.GetInsightTeachersAsync(
                kind, today, AdminInsightsConstants.InsightCardPreviewSize);

            // A card with nobody on it is noise — an admin should see the problems they HAVE.
            if (total == 0) continue;

            cards.Add(new AdminInsightCardDto
            {
                Key = kind.ToString(),
                Description = _localizer[$"Insight{kind}"],
                Severity = severity,
                TotalCount = total,
                Teachers = rows.Select(r => ToInsightTeacher(r, kind, today)).ToList()
            });
        }

        var dto = new AdminOverviewDto
        {
            GeneratedAt = DateTime.UtcNow,
            OldestSnapshotAt = agg.OldestSnapshotAt,
            Totals = new AdminOverviewTotalsDto
            {
                Teachers = agg.TotalTeachers,
                Live = agg.Live,
                LivePrevious = agg.LivePrevious,
                Dormant = agg.Dormant,
                NeverStarted = agg.NeverStarted,
                WithRealData = agg.WithRealData,
                AssistantOnly = agg.AssistantOnly
            },
            // Every band is emitted, including zero-count ones, so the charts keep a stable shape
            // instead of re-ordering themselves as the platform changes.
            CadenceBreakdown = Enum.GetValues<UsageCadence>()
                .Select(b => new BandCountDto { Key = b.ToString(), Count = agg.ByCadence.GetValueOrDefault(b) })
                .ToList(),
            DepthBreakdown = Enum.GetValues<UsageDepth>()
                .Select(b => new BandCountDto { Key = b.ToString(), Count = agg.ByDepth.GetValueOrDefault(b) })
                .ToList(),
            OperatorBreakdown = Enum.GetValues<OperatorMix>()
                .Select(b => new BandCountDto { Key = b.ToString(), Count = agg.ByOperator.GetValueOrDefault(b) })
                .ToList(),
            ModuleAdoption = agg.ModuleAdoption
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new BandCountDto { Key = kv.Key.ToString(), Count = kv.Value })
                .ToList(),
            Insights = cards
        };

        return Result<AdminOverviewDto>.Success(dto, _localizer);
    }

    /// <summary>
    /// The cards, in the order an admin should read them: churn risk first, then the accounts that
    /// never got going, then growth. Severity drives colour only.
    /// </summary>
    private static readonly (AdminInsightKind Kind, string Severity)[] InsightOrder =
    {
        (AdminInsightKind.WentQuiet, "attention"),
        (AdminInsightKind.AssistantOnly, "attention"),
        (AdminInsightKind.ExpiringWhileActive, "attention"),
        (AdminInsightKind.SetUpNotRunning, "warning"),
        (AdminInsightKind.SessionLessRoster, "warning"),
        (AdminInsightKind.NeverStarted, "warning"),
        (AdminInsightKind.NewlySubscribed, "info"),
        (AdminInsightKind.SingleModule, "info"),
        (AdminInsightKind.NewlyLive, "info")
    };

    /// <summary>
    /// Maps a row onto an insight card, with a <c>Detail</c> line that answers "why is this teacher
    /// here?" in the card's own terms — days silent, students stranded, and so on. A generic row
    /// would make every card look the same and none of them actionable.
    /// </summary>
    private static InsightTeacherDto ToInsightTeacher(TeacherUsageRow r, AdminInsightKind kind, DateOnly today)
    {
        string? detail = kind switch
        {
            AdminInsightKind.WentQuiet or AdminInsightKind.SetUpNotRunning =>
                r.LastActivityAt is null
                    ? null
                    : $"{(int)(DateTime.UtcNow - r.LastActivityAt.Value).TotalDays} days silent",
            AdminInsightKind.AssistantOnly =>
                $"{r.ActiveAssistantCount} assistant(s) working · teacher last seen "
                + (r.LastTeacherActivityAt?.ToString("yyyy-MM-dd") ?? "never"),
            AdminInsightKind.NeverStarted =>
                $"registered {(int)(DateTime.UtcNow - r.RegisteredAt).TotalDays} days ago",
            AdminInsightKind.SessionLessRoster =>
                $"{r.StudentCount} student(s), none in a session",
            AdminInsightKind.SingleModule =>
                ModuleNames(r.ModulesUsedMask).FirstOrDefault(),
            AdminInsightKind.NewlyLive =>
                r.FirstActivityAt is null ? null : $"first activity {r.FirstActivityAt:yyyy-MM-dd}",
            AdminInsightKind.ExpiringWhileActive =>
                $"{r.ActiveDays30} active days · subscription {r.SubscriptionStatus}",
            // Whether they have got going yet is the whole point of this card, so it leads.
            AdminInsightKind.NewlySubscribed =>
                (r.HasRealData ? "set up" : "NOT set up yet")
                + (r.SubscriptionStartDate is null
                    ? string.Empty
                    : $" · subscribed {(int)(DateTime.UtcNow - r.SubscriptionStartDate.Value).TotalDays} days ago"),
            _ => null
        };

        return new InsightTeacherDto
        {
            TeacherId = r.TeacherId,
            FullName = r.FullName,
            TeacherCode = r.TeacherCode,
            PhoneNumber = r.PhoneNumber,
            SalesRepName = r.SalesRepName,
            LastActivityAt = r.LastActivityAt,
            RegisteredAt = r.RegisteredAt,
            StudentCount = r.StudentCount,
            Detail = detail
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // USAGE GRID
    // ════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<TeacherUsageListItemDto>>>> GetUsageGridAsync(
        TeacherUsageQueryRequest request)
    {
        var filter = BuildFilter(request, Math.Max(1, request.Page), Math.Clamp(request.PageSize, 1, 100));

        var (rows, total) = await _unitOfWork.AdminInsightsRepo.GetUsageGridAsync(filter);
        var items = await EnrichRowsAsync(rows);

        var response = new PaginatedResponse<List<TeacherUsageListItemDto>>
        {
            totalCount = total,
            page = filter.Page,
            pageSize = filter.PageSize,
            totalPages = (int)Math.Ceiling((double)total / filter.PageSize),
            data = items
        };

        return Result<PaginatedResponse<List<TeacherUsageListItemDto>>>.Success(response, _localizer);
    }

    /// <summary>
    /// Translates the wire request into the repo filter. Shared by the grid and the CSV export so
    /// the export can never disagree with the list the admin is looking at — an export that quietly
    /// applies different filters is worse than no export.
    /// </summary>
    private static AdminUsageFilter BuildFilter(TeacherUsageQueryRequest request, int page, int pageSize)
        => new()
        {
            Page = page,
            PageSize = pageSize,
            // Folded through the same normalizer the SQL side uses, so مصطفي ≡ مصطفى.
            NormalizedSearch = string.IsNullOrWhiteSpace(request.Search)
                ? null
                : ArabicTextNormalizer.Normalize(request.Search.Trim()),
            Cadence = request.Cadence,
            Depth = request.Depth,
            Operators = request.Operators,
            UsingModuleMask = request.UsingModule is null or UsageModules.None
                ? null
                : (int)request.UsingModule.Value,
            HasRealData = request.HasRealData,
            SalesRepId = request.SalesRepId,
            UnassignedSalesRep = request.UnassignedSalesRep,
            SubscriptionStatus = Enum.TryParse<SubscriptionStatus>(request.SubscriptionStatus, true, out var st)
                ? st
                : null,
            RegisteredFrom = request.RegisteredFrom,
            // Inclusive of the whole `to` day, via an exclusive next-day bound — same convention as
            // the existing teacher-list filter, so "20 Aug → 25 Aug" catches all of the 25th.
            RegisteredToExclusive = request.RegisteredTo?.Date.AddDays(1),
            SubscribedWithinDays = request.SubscribedWithinDays,
            SortBy = request.SortBy.ToString(),
            Descending = request.SortDirection == SortDirection.Desc
        };

    /// <inheritdoc />
    public async Task<Result<PaginatedResponse<List<TeacherUsageListItemDto>>>> GetInsightTeachersAsync(
        string insightKey, int page, int pageSize)
    {
        if (!Enum.TryParse<AdminInsightKind>(insightKey, true, out var kind))
            return Result<PaginatedResponse<List<TeacherUsageListItemDto>>>.Failure(
                _localizer, "InvalidRequest", HttpStatusCode.BadRequest);

        DateOnly today = DateOnly.FromDateTime(_timeZone.ConvertUtcToLocal(DateTime.UtcNow));
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // The card query is ordered by its own relevance rule; paging it means taking the first
        // (page × size) and skipping — the lists are small by construction and this keeps the single
        // ordering definition in the repo rather than duplicating it here.
        var (rows, total) = await _unitOfWork.AdminInsightsRepo
            .GetInsightTeachersAsync(kind, today, page * pageSize);

        var pageRows = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var items = await EnrichRowsAsync(pageRows);

        var response = new PaginatedResponse<List<TeacherUsageListItemDto>>
        {
            totalCount = total,
            page = page,
            pageSize = pageSize,
            totalPages = (int)Math.Ceiling((double)total / pageSize),
            data = items
        };

        return Result<PaginatedResponse<List<TeacherUsageListItemDto>>>.Success(response, _localizer);
    }

    /// <summary>
    /// Adds the two things the grid needs that the snapshot does not carry: the 30-day sparkline and
    /// the note counts. BOTH are fetched in ONE query each for the whole page — per-row fetches here
    /// would be an N+1 on every scroll.
    /// </summary>
    private async Task<List<TeacherUsageListItemDto>> EnrichRowsAsync(IReadOnlyList<TeacherUsageRow> rows)
    {
        if (rows.Count == 0) return new List<TeacherUsageListItemDto>();

        var ids = rows.Select(r => r.TeacherId).ToList();
        DateOnly today = DateOnly.FromDateTime(_timeZone.ConvertUtcToLocal(DateTime.UtcNow));
        DateOnly from = today.AddDays(-29);

        var series = await _unitOfWork.AdminInsightsRepo.GetDayTotalsForTeachersAsync(ids, from, today);
        var notes = await _unitOfWork.AdminInsightsRepo.GetNoteStatsAsync(ids);

        return rows.Select(r =>
        {
            var dto = ToListItem(r);
            dto.Sparkline30 = BuildSparkline(series.GetValueOrDefault(r.TeacherId), from, today);
            if (notes.TryGetValue(r.TeacherId, out var n))
            {
                dto.NoteCount = n.Count;
                dto.LastNoteAt = n.LastAt;
            }
            return dto;
        }).ToList();
    }

    /// <summary>
    /// Zero-fills a day series across the window. Days with no activity have no stored row (that is
    /// the storage design), but a chart needs a value for every day or it draws a misleading shape
    /// where quiet stretches simply vanish.
    /// </summary>
    private static IReadOnlyList<int> BuildSparkline(
        IReadOnlyList<UsageDayTotals>? days, DateOnly from, DateOnly to)
    {
        int span = to.DayNumber - from.DayNumber + 1;
        var result = new int[span];
        if (days is null) return result;

        foreach (var d in days)
        {
            int idx = d.ActivityDate.DayNumber - from.DayNumber;
            if (idx >= 0 && idx < span) result[idx] = d.TotalWrites;
        }
        return result;
    }

    /// <summary>Maps the SQL row onto the wire contract.</summary>
    private static TeacherUsageListItemDto ToListItem(TeacherUsageRow r) => new()
    {
        TeacherId = r.TeacherId,
        UserId = r.UserId,
        FullName = r.FullName,
        Username = r.Username,
        TeacherCode = r.TeacherCode,
        PhoneNumber = r.PhoneNumber,
        RegisteredAt = r.RegisteredAt,
        AccountStatus = r.AccountStatus,
        Email = r.Email,
        SubscriptionStatus = r.SubscriptionStatus?.ToString(),
        SubscriptionStartDate = r.SubscriptionStartDate,
        SubscriptionEndDate = r.SubscriptionEndDate,
        PlanType = r.PlanType?.ToString(),
        SalesRepId = r.SalesRepId,
        SalesRepName = r.SalesRepName,
        AcquisitionSource = r.AcquisitionSource,
        Cadence = r.Cadence,
        ActiveDays7 = r.ActiveDays7,
        ActiveDays30 = r.ActiveDays30,
        ActiveDays90 = r.ActiveDays90,
        TotalWrites30 = r.TotalWrites30,
        Depth = r.Depth,
        Modules = ModuleNames(r.ModulesUsedMask),
        ModulesAllTime = ModuleNames(r.ModulesUsedAllTimeMask),
        Operators = r.Operators,
        LastTeacherActivityAt = r.LastTeacherActivityAt,
        LastAssistantActivityAt = r.LastAssistantActivityAt,
        ActiveAssistantCount = r.ActiveAssistantCount,
        FirstActivityAt = r.FirstActivityAt,
        LastActivityAt = r.LastActivityAt,
        StudentCount = r.StudentCount,
        StudentsAssignedToSession = r.StudentsAssignedToSession,
        SessionCount = r.SessionCount,
        SessionsWithOccurrences = r.SessionsWithOccurrences,
        LinkedAccountCount = r.LinkedAccountCount,
        BoundAccountCount = r.BoundAccountCount,
        HasEverMarkedAttendance = r.HasEverMarkedAttendance,
        HasEverCollectedPayment = r.HasEverCollectedPayment,
        HasRealData = r.HasRealData,
        ComputedAt = r.ComputedAt
    };

    /// <summary>Expands a <see cref="UsageModules"/> mask into module names for the wire.</summary>
    private static IReadOnlyList<string> ModuleNames(int mask) =>
        Enum.GetValues<UsageModules>()
            .Where(m => m != UsageModules.None && (mask & (int)m) != 0)
            .Select(m => m.ToString())
            .ToList();

    // ════════════════════════════════════════════════════════════════════════
    // TEACHER 360
    // ════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<TeacherUsageDetailDto>> GetTeacherUsageAsync(long teacherId)
    {
        var repo = _unitOfWork.AdminInsightsRepo;

        var row = await repo.GetUsageRowAsync(teacherId);
        if (row is null)
            return Result<TeacherUsageDetailDto>.Failure(
                _localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        DateOnly today = DateOnly.FromDateTime(_timeZone.ConvertUtcToLocal(DateTime.UtcNow));
        DateOnly from90 = today.AddDays(-89);
        DateOnly from30 = today.AddDays(-29);

        var days = await repo.GetDaysAsync(teacherId, from90, today);
        var operators = await repo.GetOperatorsAsync(teacherId);

        var summary = (await EnrichRowsAsync(new[] { row })).Single();

        // Zero-filled so the chart shows quiet stretches as flat, not as absent.
        var byDate = days.ToDictionary(d => d.ActivityDate);
        var seriesPoints = new List<UsageDayPointDto>(90);
        for (var d = from90; d <= today; d = d.AddDays(1))
        {
            byDate.TryGetValue(d, out var row90);
            seriesPoints.Add(new UsageDayPointDto
            {
                Date = d,
                TotalWrites = row90?.TotalWrites ?? 0,
                TeacherWrites = row90?.TeacherWrites ?? 0,
                AssistantWrites = row90?.AssistantWrites ?? 0,
                Modules = row90 is null ? Array.Empty<string>() : ModuleNames(row90.ModulesMask)
            });
        }

        var recent = days.Where(d => d.ActivityDate >= from30).ToList();
        var breakdown = new List<BandCountDto>
        {
            new() { Key = nameof(UsageModules.Attendance), Count = recent.Sum(d => d.AttendanceWrites) },
            new() { Key = nameof(UsageModules.Payments), Count = recent.Sum(d => d.PaymentWrites) },
            new() { Key = nameof(UsageModules.Students), Count = recent.Sum(d => d.StudentWrites) },
            new() { Key = nameof(UsageModules.Sessions), Count = recent.Sum(d => d.SessionWrites) },
            new() { Key = nameof(UsageModules.Videos), Count = recent.Sum(d => d.VideoWrites) },
            new() { Key = nameof(UsageModules.OnlineExams), Count = recent.Sum(d => d.OnlineExamWrites) },
            new() { Key = nameof(UsageModules.ExamsHomework), Count = recent.Sum(d => d.ExamHomeworkWrites) },
            new() { Key = nameof(UsageModules.Messaging), Count = recent.Sum(d => d.MessagingWrites) },
            new() { Key = nameof(UsageModules.ParentPortal), Count = recent.Sum(d => d.ParentPortalWrites) }
        }
        .Where(b => b.Count > 0)
        .OrderByDescending(b => b.Count)
        .ToList();

        var dto = new TeacherUsageDetailDto
        {
            Summary = summary,
            DailySeries = seriesPoints,
            ModuleBreakdown30 = breakdown,
            Operators = operators.Select(o => new TeacherOperatorDto
            {
                UserId = o.UserId,
                FullName = o.FullName,
                Username = o.Username,
                Role = o.Role,
                IsActive = o.IsActive,
                LastLoginAt = o.LastLoginAt,
                LastActivityAt = o.LastActivityAt
            }).ToList()
        };

        return Result<TeacherUsageDetailDto>.Success(dto, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<TeacherUsageDetailDto>> RecomputeTeacherUsageAsync(long teacherId, int days)
    {
        int window = Math.Clamp(days <= 0 ? AdminInsightsConstants.BackfillDays : days, 1, 730);
        var outcome = await _rollup.RollupTeacherAsync(teacherId, window);

        if (outcome.Skipped)
            return Result<TeacherUsageDetailDto>.Failure(
                _localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        var detail = await GetTeacherUsageAsync(teacherId);
        if (!detail.IsSuccess) return detail;

        return Result<TeacherUsageDetailDto>.Success(
            detail.Data!, _localizer, "TeacherUsageRecomputed");
    }

    // ════════════════════════════════════════════════════════════════════════
    // CSV EXPORT
    // ════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<byte[]>> ExportTeachersCsvAsync(TeacherUsageQueryRequest request)
    {
        // The SAME filter the grid is showing, but the whole set rather than one page — handing a
        // rep a call list truncated at 25 rows would be worse than handing them nothing.
        var filter = BuildFilter(request, page: 1, pageSize: AdminInsightsConstants.CsvExportMaxRows);
        var (rows, _) = await _unitOfWork.AdminInsightsRepo.GetUsageGridAsync(filter);

        var ids = rows.Select(r => r.TeacherId).ToList();
        var notes = await _unitOfWork.AdminInsightsRepo.GetNotesForTeachersAsync(ids);

        var headers = new[]
        {
            // Identify
            "Teacher ID", "Name", "Username", "Teacher code", "Account status", "Registered on",
            // Contact
            "Phone", "Email",
            // Commercial
            "Subscription", "Plan", "Subscribed on", "Expires on", "Sales rep", "How acquired",
            // Axis 1 — how often
            "How often", "Active days (7)", "Active days (30)", "Active days (90)", "Actions (30d)",
            // Axis 2 — what they use
            "Depth", "Modules used (30d)", "Modules ever used",
            // Axis 3 — who works it
            "Who works it", "Teacher last active", "Assistant last active", "Assistants",
            // Lifespan
            "First activity", "Last activity",
            // Setup health — each pair is (total, the part that actually works)
            "Students", "Students in a session", "Sessions", "Sessions with class days",
            "Linked accounts", "Linked accounts bound", "Ever marked attendance",
            "Ever collected money", "Properly set up",
            // Admin context
            "Notes", "Note count", "Last note", "Follow up on", "Figures computed"
        };

        var csvRows = rows.Select(r =>
        {
            var teacherNotes = notes.GetValueOrDefault(r.TeacherId) ?? (IReadOnlyList<AdminNote>)Array.Empty<AdminNote>();

            // Notes are flattened into ONE cell, newline-separated and stamped with author and date.
            // A spreadsheet cannot hold a nested list, and splitting them across columns would make
            // the row width depend on whoever wrote the most notes.
            string flattenedNotes = string.Join(
                "\n",
                teacherNotes.Select(n =>
                    $"[{AdminInsightsCsv.Date(n.CreateAt)}] {n.AuthorName}"
                    + (n.IsPinned ? " (pinned)" : string.Empty)
                    + $": {n.Body}"));

            // The soonest outstanding follow-up, so a rep can sort the sheet by it.
            string nextFollowUp = AdminInsightsCsv.Day(
                teacherNotes.Where(n => n.FollowUpDate is not null)
                            .Select(n => n.FollowUpDate)
                            .OrderBy(d => d)
                            .FirstOrDefault());

            return (IReadOnlyList<string?>)new List<string?>
            {
                r.TeacherId.ToString(),
                r.FullName,
                r.Username,
                r.TeacherCode,
                r.AccountStatus.ToString(),
                AdminInsightsCsv.Date(r.RegisteredAt),

                r.PhoneNumber,
                r.Email,

                r.SubscriptionStatus?.ToString(),
                r.PlanType?.ToString(),
                AdminInsightsCsv.Date(r.SubscriptionStartDate),
                AdminInsightsCsv.Date(r.SubscriptionEndDate),
                r.SalesRepName,
                r.AcquisitionSource,

                r.Cadence.ToString(),
                r.ActiveDays7.ToString(),
                r.ActiveDays30.ToString(),
                r.ActiveDays90.ToString(),
                r.TotalWrites30.ToString(),

                r.Depth.ToString(),
                string.Join(" | ", ModuleNames(r.ModulesUsedMask)),
                string.Join(" | ", ModuleNames(r.ModulesUsedAllTimeMask)),

                r.Operators.ToString(),
                AdminInsightsCsv.Date(r.LastTeacherActivityAt),
                AdminInsightsCsv.Date(r.LastAssistantActivityAt),
                r.ActiveAssistantCount.ToString(),

                AdminInsightsCsv.Date(r.FirstActivityAt),
                AdminInsightsCsv.Date(r.LastActivityAt),

                r.StudentCount.ToString(),
                r.StudentsAssignedToSession.ToString(),
                r.SessionCount.ToString(),
                r.SessionsWithOccurrences.ToString(),
                r.LinkedAccountCount.ToString(),
                r.BoundAccountCount.ToString(),
                AdminInsightsCsv.YesNo(r.HasEverMarkedAttendance),
                AdminInsightsCsv.YesNo(r.HasEverCollectedPayment),
                AdminInsightsCsv.YesNo(r.HasRealData),

                flattenedNotes,
                teacherNotes.Count.ToString(),
                AdminInsightsCsv.Date(teacherNotes.FirstOrDefault()?.CreateAt),
                nextFollowUp,
                AdminInsightsCsv.Date(r.ComputedAt)
            };
        });

        return Result<byte[]>.Success(AdminInsightsCsv.Build(headers, csvRows), _localizer);
    }

    // ════════════════════════════════════════════════════════════════════════
    // SALES ATTRIBUTION
    // ════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<List<SalesRepDto>>> GetSalesRepsAsync(bool includeInactive)
    {
        var rows = await _unitOfWork.AdminInsightsRepo.GetSalesRepPerformanceAsync(includeInactive);

        var dtos = rows.Select(r => new SalesRepDto
        {
            Id = r.Id,
            Name = r.Name,
            PhoneNumber = r.PhoneNumber,
            IsActive = r.IsActive,
            CreatedAt = r.CreatedAt,
            TeachersAssigned = r.TeachersAssigned,
            TeachersLive = r.TeachersLive,
            TeachersDormant = r.TeachersDormant,
            TeachersNeverStarted = r.TeachersNeverStarted,
            TeachersWithRealData = r.TeachersWithRealData
        }).ToList();

        return Result<List<SalesRepDto>>.Success(dtos, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<SalesRepDto>> CreateSalesRepAsync(SaveSalesRepRequest request)
    {
        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return Result<SalesRepDto>.Failure(_localizer, "SalesRepNameRequired", HttpStatusCode.BadRequest);

        var repo = _unitOfWork.GetRepository<SalesRep, long>();

        // Names are how reps are recognised on every screen; two "Ahmed" rows would make the
        // attribution unreadable and the per-rep rollup meaningless.
        var existing = await _unitOfWork.AdminInsightsRepo.GetSalesRepPerformanceAsync(includeInactive: true);
        if (existing.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Result<SalesRepDto>.Failure(_localizer, "SalesRepNameDuplicate", HttpStatusCode.Conflict);

        var rep = new SalesRep
        {
            Name = name,
            PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim(),
            IsActive = request.IsActive,
            CreateAt = DateTime.UtcNow
        };

        await repo.AddAsync(rep);
        await _unitOfWork.SaveChangesAsync();

        return Result<SalesRepDto>.Success(
            ToSalesRepDto(rep), _localizer, "SalesRepCreated", HttpStatusCode.Created);
    }

    /// <inheritdoc />
    public async Task<Result<SalesRepDto>> UpdateSalesRepAsync(long salesRepId, SaveSalesRepRequest request)
    {
        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return Result<SalesRepDto>.Failure(_localizer, "SalesRepNameRequired", HttpStatusCode.BadRequest);

        var repo = _unitOfWork.GetRepository<SalesRep, long>();
        var rep = await repo.GetByIdAsync(salesRepId);
        if (rep is null)
            return Result<SalesRepDto>.Failure(_localizer, "SalesRepNotFound", HttpStatusCode.NotFound);

        var existing = await _unitOfWork.AdminInsightsRepo.GetSalesRepPerformanceAsync(includeInactive: true);
        if (existing.Any(r => r.Id != salesRepId
                           && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Result<SalesRepDto>.Failure(_localizer, "SalesRepNameDuplicate", HttpStatusCode.Conflict);

        rep.Name = name;
        rep.PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
        // Deactivating only hides the rep from the picker. Their name stays on the teachers they
        // brought in, so historical attribution never silently disappears.
        rep.IsActive = request.IsActive;

        await repo.UpdateAsync(rep);
        await _unitOfWork.SaveChangesAsync();

        return Result<SalesRepDto>.Success(ToSalesRepDto(rep), _localizer, "SalesRepUpdated");
    }

    /// <inheritdoc />
    public async Task<Result<TeacherUsageListItemDto>> AssignSalesRepAsync(
        long teacherId, AssignSalesRepRequest request)
    {
        var teacherRepo = _unitOfWork.GetRepository<Teacher, long>();
        var teacher = await teacherRepo.GetByIdAsync(teacherId);
        if (teacher is null)
            return Result<TeacherUsageListItemDto>.Failure(
                _localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        if (request.SalesRepId is not null)
        {
            var rep = await _unitOfWork.GetRepository<SalesRep, long>().GetByIdAsync(request.SalesRepId.Value);
            if (rep is null)
                return Result<TeacherUsageListItemDto>.Failure(
                    _localizer, "SalesRepNotFound", HttpStatusCode.NotFound);
        }

        // Null clears the attribution — an explicit, supported action, not an accident.
        teacher.SalesRepId = request.SalesRepId;
        teacher.AcquisitionSource = string.IsNullOrWhiteSpace(request.AcquisitionSource)
            ? null
            : request.AcquisitionSource.Trim();

        await teacherRepo.UpdateAsync(teacher);
        await _unitOfWork.SaveChangesAsync();

        var row = await _unitOfWork.AdminInsightsRepo.GetUsageRowAsync(teacherId);
        var dto = row is null ? new TeacherUsageListItemDto() : (await EnrichRowsAsync(new[] { row })).Single();

        return Result<TeacherUsageListItemDto>.Success(dto, _localizer, "SalesRepAssigned");
    }

    private static SalesRepDto ToSalesRepDto(SalesRep rep) => new()
    {
        Id = rep.Id,
        Name = rep.Name,
        PhoneNumber = rep.PhoneNumber,
        IsActive = rep.IsActive,
        CreatedAt = rep.CreateAt
    };

    // ════════════════════════════════════════════════════════════════════════
    // INTERNAL NOTES
    // ════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Result<List<AdminNoteDto>>> GetNotesAsync(long teacherId)
    {
        // Named repo method (CLAUDE.md §3.1) — the pinned-first ordering lives with the query.
        var notes = await _unitOfWork.AdminInsightsRepo.GetNotesForTeacherAsync(teacherId);
        var dtos = notes.Select(ToNoteDto).ToList();

        return Result<List<AdminNoteDto>>.Success(dtos, _localizer);
    }

    /// <inheritdoc />
    public async Task<Result<AdminNoteDto>> CreateNoteAsync(
        long teacherId, CreateAdminNoteRequest request, long authorUserId)
    {
        string body = request.Body?.Trim() ?? string.Empty;
        if (body.Length == 0)
            return Result<AdminNoteDto>.Failure(_localizer, "AdminNoteBodyRequired", HttpStatusCode.BadRequest);

        var teacher = await _unitOfWork.GetRepository<Teacher, long>().GetByIdAsync(teacherId);
        if (teacher is null)
            return Result<AdminNoteDto>.Failure(_localizer, "TeacherNotFound", HttpStatusCode.NotFound);

        // The author's name is DENORMALISED at write time so the note stays readable even if that
        // admin account is later renamed or removed — the same reasoning as payment rows keeping a
        // collector name.
        var author = await _unitOfWork.Users.GetUserByIdAsync(authorUserId);

        var note = new AdminNote
        {
            TeacherId = teacherId,
            AuthorUserId = authorUserId,
            AuthorName = author?.FullName ?? author?.Username ?? $"User #{authorUserId}",
            Body = body,
            IsPinned = request.IsPinned,
            FollowUpDate = request.FollowUpDate,
            CreateAt = DateTime.UtcNow
        };

        await _unitOfWork.GetRepository<AdminNote, long>().AddAsync(note);
        await _unitOfWork.SaveChangesAsync();

        return Result<AdminNoteDto>.Success(
            ToNoteDto(note), _localizer, "AdminNoteCreated", HttpStatusCode.Created);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> DeleteNoteAsync(long teacherId, long noteId)
    {
        var repo = _unitOfWork.GetRepository<AdminNote, long>();
        var note = await repo.GetByIdAsync(noteId);

        // The teacher id is checked as well as the note id: a note from another teacher must 404
        // rather than be deleted through the wrong teacher's page.
        if (note is null || note.TeacherId != teacherId)
            return Result<bool>.Failure(_localizer, "AdminNoteNotFound", HttpStatusCode.NotFound);

        // Soft delete — the row survives for audit (CLAUDE.md §4.3).
        note.IsDeleted = true;
        note.DeletedAt = DateTime.UtcNow;

        await repo.UpdateAsync(note);
        await _unitOfWork.SaveChangesAsync();

        return Result<bool>.Success(true, _localizer, "AdminNoteDeleted");
    }

    private static AdminNoteDto ToNoteDto(AdminNote n) => new()
    {
        Id = n.Id,
        TeacherId = n.TeacherId,
        AuthorUserId = n.AuthorUserId,
        AuthorName = n.AuthorName,
        Body = n.Body,
        IsPinned = n.IsPinned,
        FollowUpDate = n.FollowUpDate,
        CreatedAt = n.CreateAt
    };
}
