using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories;

/// <summary>
/// Read queries behind the admin insights screens. See <see cref="IAdminInsightsRepo"/> for the
/// performance contract — nothing here may materialise the teacher table.
///
/// SUBSCRIPTION STATUS: derived inline as a SQL CASE rather than through
/// <see cref="SubscriptionStatusCalculator"/>. That helper's own doc comment points at a
/// <c>BuildStatusExpression()</c> for exactly this purpose, but the method was never written — so
/// the rules (documented on the helper, authoritative) are spelled out in the projection below. If
/// the helper ever grows that expression, collapse this onto it.
/// </summary>
public class AdminInsightsRepo : IAdminInsightsRepo
{
    private readonly EdvanzDbContext _context;

    public AdminInsightsRepo(EdvanzDbContext context) => _context = context;

    /// <summary>
    /// Teachers LEFT JOINed to their usage snapshot, sales rep and current subscription.
    ///
    /// The join to the snapshot is a LEFT join on purpose: a teacher registered after last night's
    /// rollup has no row yet and must still appear on the grid — as "Never", which is the truth —
    /// rather than vanishing from the admin's view entirely.
    /// </summary>
    private IQueryable<TeacherUsageRow> BuildRowQuery(DateTime nowUtc)
    {
        DateTime soonUtc = nowUtc.AddDays(SubscriptionStatusCalculator.ExpiringSoonThresholdDays);

        return from t in _context.Teachers.AsNoTracking()
               join sn in _context.TeacherUsageSnapshots.AsNoTracking()
                   on t.Id equals sn.TeacherId into snj
               from sn in snj.DefaultIfEmpty()
               join rep in _context.SalesReps.AsNoTracking()
                   on t.SalesRepId equals rep.Id into repj
               from rep in repj.DefaultIfEmpty()
               join sub in _context.TeacherSubscriptions.AsNoTracking().Where(x => x.IsCurrent)
                   on t.Id equals sub.TeacherId into subj
               from sub in subj.DefaultIfEmpty()
               select new TeacherUsageRow
               {
                   TeacherId = t.Id,
                   UserId = t.UserId,
                   FullName = t.User.FullName,
                   Username = t.User.Username,
                   TeacherCode = t.TeacherCode,
                   PhoneNumber = t.User.PhoneNumber,
                   Email = t.User.Email,
                   RegisteredAt = t.CreateAt,
                   AccountStatus = t.AccountStatus,

                   // The helper's documented rules, as a translatable CASE.
                   SubscriptionStatus =
                       sub == null ? Domain.Enums.SubscriptionStatus.Expired
                       : nowUtc < sub.StartDate ? Domain.Enums.SubscriptionStatus.Expired
                       : nowUtc >= sub.EndDate ? Domain.Enums.SubscriptionStatus.Expired
                       : sub.EndDate <= soonUtc ? Domain.Enums.SubscriptionStatus.ExpiringSoon
                       : Domain.Enums.SubscriptionStatus.Active,
                   SubscriptionStartDate = sub == null ? null : sub.StartDate,
                   SubscriptionEndDate = sub == null ? null : sub.EndDate,
                   PlanType = sub == null ? null : sub.PlanType,

                   SalesRepId = t.SalesRepId,
                   SalesRepName = rep == null ? null : rep.Name,
                   AcquisitionSource = t.AcquisitionSource,

                   // Snapshot columns default to "never started" when the rollup has not run.
                   Cadence = sn == null ? UsageCadence.Never : sn.Cadence,
                   Depth = sn == null ? UsageDepth.None : sn.Depth,
                   Operators = sn == null ? OperatorMix.Nobody : sn.Operators,
                   ActiveDays7 = sn == null ? 0 : sn.ActiveDays7,
                   ActiveDays30 = sn == null ? 0 : sn.ActiveDays30,
                   ActiveDays90 = sn == null ? 0 : sn.ActiveDays90,
                   TotalWrites30 = sn == null ? 0 : sn.TotalWrites30,
                   ModulesUsedMask = sn == null ? 0 : sn.ModulesUsedMask,
                   ModulesUsedAllTimeMask = sn == null ? 0 : sn.ModulesUsedAllTimeMask,
                   EntitledModulesMask = sn == null ? 0 : sn.EntitledModulesMask,
                   FirstActivityAt = sn == null ? null : sn.FirstActivityAt,
                   LastActivityAt = sn == null ? null : sn.LastActivityAt,
                   LastTeacherActivityAt = sn == null ? null : sn.LastTeacherActivityAt,
                   LastAssistantActivityAt = sn == null ? null : sn.LastAssistantActivityAt,
                   ActiveAssistantCount = sn == null ? 0 : sn.ActiveAssistantCount,
                   StudentCount = sn == null ? 0 : sn.StudentCount,
                   StudentsAssignedToSession = sn == null ? 0 : sn.StudentsAssignedToSession,
                   SessionCount = sn == null ? 0 : sn.SessionCount,
                   SessionsWithOccurrences = sn == null ? 0 : sn.SessionsWithOccurrences,
                   LinkedAccountCount = sn == null ? 0 : sn.LinkedAccountCount,
                   BoundAccountCount = sn == null ? 0 : sn.BoundAccountCount,
                   HasEverMarkedAttendance = sn != null && sn.HasEverMarkedAttendance,
                   HasEverCollectedPayment = sn != null && sn.HasEverCollectedPayment,
                   HasRealData = sn != null && sn.HasRealData,
                   ComputedAt = sn == null ? null : sn.ComputedAt
               };
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<TeacherUsageRow> Rows, int TotalCount)> GetUsageGridAsync(
        AdminUsageFilter filter, CancellationToken ct = default)
    {
        var query = BuildRowQuery(DateTime.UtcNow);

        if (!string.IsNullOrWhiteSpace(filter.NormalizedSearch))
        {
            // Both sides folded through the dbo.ArabicNormalize UDF, so مصطفي ≡ مصطفى.
            string term = filter.NormalizedSearch;
            query = query.Where(r =>
                DbSearch.ArabicNormalize(r.FullName).Contains(term) ||
                DbSearch.ArabicNormalize(r.TeacherCode).Contains(term) ||
                (r.Username != null && DbSearch.ArabicNormalize(r.Username).Contains(term)) ||
                (r.PhoneNumber != null && r.PhoneNumber.Contains(term)));
        }

        if (filter.Cadence is not null) query = query.Where(r => r.Cadence == filter.Cadence);
        if (filter.Depth is not null) query = query.Where(r => r.Depth == filter.Depth);
        if (filter.Operators is not null) query = query.Where(r => r.Operators == filter.Operators);
        if (filter.HasRealData is not null) query = query.Where(r => r.HasRealData == filter.HasRealData);
        if (filter.SalesRepId is not null) query = query.Where(r => r.SalesRepId == filter.SalesRepId);
        if (filter.UnassignedSalesRep == true) query = query.Where(r => r.SalesRepId == null);
        if (filter.SubscriptionStatus is not null)
            query = query.Where(r => r.SubscriptionStatus == filter.SubscriptionStatus);

        if (filter.IsActive is not null)
            query = filter.IsActive.Value
                ? query.Where(r => r.ActiveDays30 > 0)
                : query.Where(r => r.ActiveDays30 == 0);

        if (filter.SubscribedOnly == true)
            query = query.Where(r =>
                r.SubscriptionStatus == Domain.Enums.SubscriptionStatus.Active ||
                r.SubscriptionStatus == Domain.Enums.SubscriptionStatus.ExpiringSoon);

        if (filter.NeverUsedFeatureMask is not null)
        {
            int bit = filter.NeverUsedFeatureMask.Value;
            // Entitled to it AND never opened it. Both halves matter: without the entitlement
            // check this would list everyone who lacks the feature as ignoring it.
            query = query.Where(r => (r.EntitledModulesMask & bit) != 0
                                  && (r.ModulesUsedAllTimeMask & bit) == 0);
        }

        if (filter.UsingModuleMask is not null)
        {
            int bit = filter.UsingModuleMask.Value;
            query = query.Where(r => (r.ModulesUsedMask & bit) != 0);
        }

        // Registration bounds: `from` inclusive from midnight, `to` exclusive of the NEXT day, so a
        // "20 Aug → 25 Aug" filter catches everything on the 25th regardless of time of day. Same
        // convention as the existing teacher-list registeredFrom/registeredTo filter.
        // Newly subscribed: the CURRENT subscription started inside the window. Same meaning as
        // `subscribedWithinDays` on the legacy teacher list, so the two screens never disagree.
        if (filter.SubscribedWithinDays is > 0)
        {
            DateTime subCutoff = DateTime.UtcNow.AddDays(-filter.SubscribedWithinDays.Value);
            query = query.Where(r => r.SubscriptionStartDate != null && r.SubscriptionStartDate >= subCutoff);
        }

        if (filter.RegisteredFrom is not null)
            query = query.Where(r => r.RegisteredAt >= filter.RegisteredFrom.Value.Date);
        if (filter.RegisteredToExclusive is not null)
            query = query.Where(r => r.RegisteredAt < filter.RegisteredToExclusive.Value);

        int total = await query.CountAsync(ct);

        // The newly-subscribed filter carries its own ordering, exactly as the legacy teacher list
        // does — asking for "who just subscribed" and getting them in last-activity order would
        // bury the newest signups, which are the whole point of the question.
        query = filter.SubscribedWithinDays is > 0
            ? query.OrderByDescending(r => r.SubscriptionStartDate).ThenByDescending(r => r.TeacherId)
            : ApplySort(query, filter.SortBy, filter.Descending);

        var rows = await query
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct);

        return (rows, total);
    }

    /// <summary>
    /// Orders in SQL. Every branch adds <c>TeacherId</c> as the final tiebreak — without a unique
    /// last key, SQL Server may order ties differently between pages and a row can appear twice or
    /// not at all while paging.
    /// </summary>
    private static IQueryable<TeacherUsageRow> ApplySort(
        IQueryable<TeacherUsageRow> query, string sortBy, bool descending) => (sortBy, descending) switch
        {
            ("ActiveDays30", true) => query.OrderByDescending(r => r.ActiveDays30).ThenByDescending(r => r.TeacherId),
            ("ActiveDays30", false) => query.OrderBy(r => r.ActiveDays30).ThenBy(r => r.TeacherId),
            ("TotalWrites30", true) => query.OrderByDescending(r => r.TotalWrites30).ThenByDescending(r => r.TeacherId),
            ("TotalWrites30", false) => query.OrderBy(r => r.TotalWrites30).ThenBy(r => r.TeacherId),
            ("StudentCount", true) => query.OrderByDescending(r => r.StudentCount).ThenByDescending(r => r.TeacherId),
            ("StudentCount", false) => query.OrderBy(r => r.StudentCount).ThenBy(r => r.TeacherId),
            ("RegisteredAt", true) => query.OrderByDescending(r => r.RegisteredAt).ThenByDescending(r => r.TeacherId),
            ("RegisteredAt", false) => query.OrderBy(r => r.RegisteredAt).ThenBy(r => r.TeacherId),
            ("Name", true) => query.OrderByDescending(r => r.FullName).ThenByDescending(r => r.TeacherId),
            ("Name", false) => query.OrderBy(r => r.FullName).ThenBy(r => r.TeacherId),
            (_, false) => query.OrderBy(r => r.LastActivityAt).ThenBy(r => r.TeacherId),
            _ => query.OrderByDescending(r => r.LastActivityAt).ThenByDescending(r => r.TeacherId)
        };

    /// <inheritdoc />
    public async Task<TeacherUsageRow?> GetUsageRowAsync(long teacherId, CancellationToken ct = default)
        => await BuildRowQuery(DateTime.UtcNow).FirstOrDefaultAsync(r => r.TeacherId == teacherId, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<UsageDayTotals>>> GetDayTotalsForTeachersAsync(
        IReadOnlyCollection<long> teacherIds, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default)
    {
        if (teacherIds.Count == 0)
            return new Dictionary<long, IReadOnlyList<UsageDayTotals>>();

        // ONE query for the whole page. Fetching per row would be an N+1 on every scroll.
        var rows = await _context.TeacherUsageDays
            .AsNoTracking()
            .Where(d => teacherIds.Contains(d.TeacherId)
                     && d.ActivityDate >= fromDate
                     && d.ActivityDate <= toDate)
            .Select(d => new { d.TeacherId, d.ActivityDate, d.TotalWrites, d.ModulesMask, d.TeacherWrites, d.AssistantWrites })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.TeacherId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<UsageDayTotals>)g
                    .OrderBy(r => r.ActivityDate)
                    .Select(r => new UsageDayTotals(r.ActivityDate, r.TotalWrites, r.ModulesMask, r.TeacherWrites, r.AssistantWrites))
                    .ToList());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeacherUsageDay>> GetDaysAsync(
        long teacherId, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default)
        => await _context.TeacherUsageDays
            .AsNoTracking()
            .Where(d => d.TeacherId == teacherId && d.ActivityDate >= fromDate && d.ActivityDate <= toDate)
            .OrderBy(d => d.ActivityDate)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<AdminOverviewAggregates> GetOverviewAggregatesAsync(
        DateOnly today, CancellationToken ct = default)
    {
        var snapshots = _context.TeacherUsageSnapshots.AsNoTracking();

        int totalTeachers = await _context.Teachers.AsNoTracking().CountAsync(ct);

        // "Live" is real activity in the last 30 days — never a login. That distinction is the whole
        // reason this model exists.
        int live = await snapshots.CountAsync(s => s.ActiveDays30 > 0, ct);
        int dormant = await snapshots.CountAsync(s => s.Cadence == UsageCadence.Dormant, ct);
        int withRealData = await snapshots.CountAsync(s => s.HasRealData, ct);
        int assistantOnly = await snapshots.CountAsync(s => s.Operators == OperatorMix.AssistantsOnly, ct);

        // Never-started counts teachers with NO snapshot too — they have never been rolled up, which
        // for a brand-new account is the same thing as never having done anything.
        int neverStarted = totalTeachers
            - await snapshots.CountAsync(s => s.Cadence != UsageCadence.Never, ct);

        // The previous 30-day window, straight from the day facts, so the UI shows a real delta
        // rather than a number floating on its own.
        DateOnly prevFrom = today.AddDays(-59);
        DateOnly prevTo = today.AddDays(-30);
        int livePrevious = await _context.TeacherUsageDays
            .AsNoTracking()
            .Where(d => d.ActivityDate >= prevFrom && d.ActivityDate <= prevTo)
            .Select(d => d.TeacherId)
            .Distinct()
            .CountAsync(ct);

        DateTime? oldestSnapshot = await snapshots
            .OrderBy(s => s.ComputedAt)
            .Select(s => (DateTime?)s.ComputedAt)
            .FirstOrDefaultAsync(ct);

        var byCadence = await snapshots
            .GroupBy(s => s.Cadence)
            .Select(g => new { Band = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Band, x => x.Count, ct);

        var byDepth = await snapshots
            .GroupBy(s => s.Depth)
            .Select(g => new { Band = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Band, x => x.Count, ct);

        var byOperator = await snapshots
            .GroupBy(s => s.Operators)
            .Select(g => new { Band = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Band, x => x.Count, ct);

        // Module adoption: group by the DISTINCT mask values in SQL, then expand the bits in memory.
        // There are at most 512 possible masks and realistically a couple of dozen, so this is a tiny
        // result set — far cheaper and less fragile than nine conditional COUNTs in one projection.
        var maskCounts = await snapshots
            .GroupBy(s => s.ModulesUsedMask)
            .Select(g => new { Mask = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var adoption = new Dictionary<UsageModules, int>();
        foreach (var module in Enum.GetValues<UsageModules>())
        {
            if (module == UsageModules.None) continue;
            adoption[module] = maskCounts
                .Where(m => (m.Mask & (int)module) != 0)
                .Sum(m => m.Count);
        }

        return new AdminOverviewAggregates(
            totalTeachers, live, livePrevious, dormant, neverStarted, withRealData, assistantOnly,
            oldestSnapshot, byCadence, byDepth, byOperator, adoption);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<TeacherUsageRow> Rows, int TotalCount)> GetInsightTeachersAsync(
        AdminInsightKind kind, DateOnly today, int take, CancellationToken ct = default)
    {
        DateTime nowUtc = DateTime.UtcNow;
        var query = BuildRowQuery(nowUtc);

        DateTime quietBefore = nowUtc.AddDays(-AdminInsightsConstants.WentQuietAfterDays);
        DateTime startedBefore = nowUtc.AddDays(-AdminInsightsConstants.NeverStartedGraceDays);
        DateTime newlyLiveAfter = nowUtc.AddDays(-AdminInsightsConstants.NewlyLiveWithinDays);
        DateTime newlySubscribedAfter = nowUtc.AddDays(-AdminInsightsConstants.NewlySubscribedWithinDays);

        query = kind switch
        {
            // Was working, went silent. Ordered most-recently-lost first — the freshest ones are the
            // most recoverable.
            AdminInsightKind.WentQuiet => query
                .Where(r => r.LastActivityAt != null && r.LastActivityAt < quietBefore && r.ActiveDays90 > 0)
                .OrderByDescending(r => r.LastActivityAt),

            // The owner has stopped while staff carry on. Biggest accounts first — most to lose.
            AdminInsightKind.AssistantOnly => query
                .Where(r => r.Operators == OperatorMix.AssistantsOnly)
                .OrderByDescending(r => r.StudentCount),

            // Past the grace period with nothing that works. Oldest registrations first — they have
            // been stuck the longest.
            AdminInsightKind.NeverStarted => query
                .Where(r => !r.HasRealData && r.ActiveDays90 == 0 && r.RegisteredAt < startedBefore)
                .OrderBy(r => r.RegisteredAt),

            // Properly configured and abandoned. The most winnable list on the page.
            AdminInsightKind.SetUpNotRunning => query
                .Where(r => r.HasRealData && r.ActiveDays30 == 0)
                .OrderByDescending(r => r.StudentCount),

            AdminInsightKind.SingleModule => query
                .Where(r => r.Depth == UsageDepth.Single)
                .OrderByDescending(r => r.ActiveDays30),

            // Students exist but none is assigned to a session, so every one of them opens an empty
            // app. A silent misconfiguration nobody reports because it looks like "no content yet".
            AdminInsightKind.SessionLessRoster => query
                .Where(r => r.StudentCount > 0 && r.StudentsAssignedToSession == 0)
                .OrderByDescending(r => r.StudentCount),

            AdminInsightKind.NewlyLive => query
                .Where(r => r.FirstActivityAt != null && r.FirstActivityAt >= newlyLiveAfter)
                .OrderByDescending(r => r.FirstActivityAt),

            // Actively working and about to lose access — the most urgent money on the platform.
            AdminInsightKind.ExpiringWhileActive => query
                .Where(r => r.ActiveDays30 > 0
                         && r.SubscriptionStatus != Domain.Enums.SubscriptionStatus.Active)
                .OrderByDescending(r => r.ActiveDays30),

            // Paid inside the window. Ordered so the ones with NOTHING working come first: a
            // teacher who has just paid and cannot get started is the call that prevents a refund.
            AdminInsightKind.NewlySubscribed => query
                .Where(r => r.SubscriptionStartDate != null && r.SubscriptionStartDate >= newlySubscribedAfter)
                .OrderBy(r => r.HasRealData)
                .ThenByDescending(r => r.SubscriptionStartDate),

            // Entitled to something they have NEVER opened. Restricted to teachers who are actually
            // running (HasRealData) — telling someone who never set up that they are ignoring videos
            // is noise; they have a bigger problem, and it is already its own reason above.
            AdminInsightKind.UnusedEntitlements => query
                .Where(r => r.HasRealData
                         && (r.EntitledModulesMask & ~r.ModulesUsedAllTimeMask) != 0)
                .OrderByDescending(r => r.StudentCount),

            _ => query.OrderByDescending(r => r.LastActivityAt)
        };

        int total = await query.CountAsync(ct);
        var rows = await query.Take(take).ToListAsync(ct);
        return (rows, total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, (int Count, DateTime LastAt)>> GetNoteStatsAsync(
        IReadOnlyCollection<long> teacherIds, CancellationToken ct = default)
    {
        if (teacherIds.Count == 0)
            return new Dictionary<long, (int, DateTime)>();

        var rows = await _context.AdminNotes
            .AsNoTracking()
            .Where(n => teacherIds.Contains(n.TeacherId))
            .GroupBy(n => n.TeacherId)
            .Select(g => new { TeacherId = g.Key, Count = g.Count(), LastAt = g.Max(n => n.CreateAt) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.TeacherId, r => (r.Count, r.LastAt));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminNote>> GetNotesForTeacherAsync(
        long teacherId, CancellationToken ct = default)
        // Pinned first, then newest — the pin exists for the one thing that must not be scrolled
        // past. Ordering lives here rather than in the service so every caller gets it identically.
        => await _context.AdminNotes
            .AsNoTracking()
            .Where(n => n.TeacherId == teacherId)
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.CreateAt)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<AdminNote>>> GetNotesForTeachersAsync(
        IReadOnlyCollection<long> teacherIds, CancellationToken ct = default)
    {
        if (teacherIds.Count == 0)
            return new Dictionary<long, IReadOnlyList<AdminNote>>();

        // ONE query for every exported teacher. Same pinned-first ordering as the single-teacher
        // read, so the export reads in the order the Notes tab shows.
        var rows = await _context.AdminNotes
            .AsNoTracking()
            .Where(n => teacherIds.Contains(n.TeacherId))
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.CreateAt)
            .ToListAsync(ct);

        return rows
            .GroupBy(n => n.TeacherId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AdminNote>)g.ToList());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeacherOperatorRow>> GetOperatorsAsync(
        long teacherId, CancellationToken ct = default)
    {
        var teacher = await _context.Teachers
            .AsNoTracking()
            .Where(t => t.Id == teacherId)
            .Select(t => new TeacherOperatorRow(
                t.UserId, t.User.FullName, t.User.Username, "Teacher",
                t.AccountStatus == AccountStatus.Active,
                t.User.LastLoginAt, t.User.LastActivityAt))
            .FirstOrDefaultAsync(ct);

        // Removed assistants are listed too — their past work is part of the account's history, and
        // an admin diagnosing "who did this?" needs the name even after the person has gone.
        var assistants = await _context.Assistants
            .AsNoTracking()
            .Where(a => a.TeacherAccountId == teacherId)
            .Select(a => new TeacherOperatorRow(
                a.UserId, a.User.FullName, a.User.Username, "Assistant",
                a.RemovedAt == null && a.DeletedAt == null,
                a.User.LastLoginAt, a.User.LastActivityAt))
            .ToListAsync(ct);

        var result = new List<TeacherOperatorRow>();
        if (teacher is not null) result.Add(teacher);
        result.AddRange(assistants.OrderByDescending(a => a.IsActive).ThenBy(a => a.FullName));
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeacherAdoptionRow>> GetAdoptionRowsAsync(
        bool subscribedOnly, CancellationToken ct = default)
    {
        var query = BuildRowQuery(DateTime.UtcNow);

        // "Subscribed" means a live subscription — Active or ExpiringSoon. An expired teacher is
        // not a paying customer and counting them drags every platform figure down.
        if (subscribedOnly)
            query = query.Where(r =>
                r.SubscriptionStatus == Domain.Enums.SubscriptionStatus.Active ||
                r.SubscriptionStatus == Domain.Enums.SubscriptionStatus.ExpiringSoon);

        return await query
            .Select(r => new TeacherAdoptionRow(
                r.TeacherId,
                r.PlanType,
                r.SubscriptionStatus == Domain.Enums.SubscriptionStatus.Active ||
                r.SubscriptionStatus == Domain.Enums.SubscriptionStatus.ExpiringSoon,
                r.ActiveDays30,
                r.HasRealData,
                r.EntitledModulesMask,
                r.ModulesUsedMask,
                r.ModulesUsedAllTimeMask))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SalesRepPerformanceRow>> GetSalesRepPerformanceAsync(
        bool includeInactive, CancellationToken ct = default)
    {
        var reps = _context.SalesReps.AsNoTracking();
        if (!includeInactive) reps = reps.Where(r => r.IsActive);

        // The counts are correlated subqueries over the snapshot, so the whole rollup is one SELECT.
        // "Assigned" is the vanity number; the three outcome columns are what the conversation with
        // a rep is actually about.
        return await reps
            .OrderBy(r => r.Name)
            .Select(r => new SalesRepPerformanceRow(
                r.Id,
                r.Name,
                r.PhoneNumber,
                r.IsActive,
                r.CreateAt,
                _context.Teachers.Count(t => t.SalesRepId == r.Id),
                _context.Teachers.Count(t => t.SalesRepId == r.Id
                    && _context.TeacherUsageSnapshots.Any(s => s.TeacherId == t.Id && s.ActiveDays30 > 0)),
                _context.Teachers.Count(t => t.SalesRepId == r.Id
                    && _context.TeacherUsageSnapshots.Any(s => s.TeacherId == t.Id && s.Cadence == UsageCadence.Dormant)),
                _context.Teachers.Count(t => t.SalesRepId == r.Id
                    && !_context.TeacherUsageSnapshots.Any(s => s.TeacherId == t.Id && s.Cadence != UsageCadence.Never)),
                _context.Teachers.Count(t => t.SalesRepId == r.Id
                    && _context.TeacherUsageSnapshots.Any(s => s.TeacherId == t.Id && s.HasRealData))))
            .ToListAsync(ct);
    }
}
