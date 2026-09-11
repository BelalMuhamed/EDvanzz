using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories;

/// <summary>
/// Every query behind the admin usage rollup. See <see cref="ITeacherUsageRepo"/> for the contract
/// and the reasoning behind the hour-bucket shape.
///
/// SHARED SHAPE: each activity query groups to the UTC hour in SQL and returns counts, so the
/// database moves a few dozen rows instead of every write a teacher ever made. The teacher-local day
/// conversion is deliberately NOT done here — it belongs to the service, which owns
/// <c>ITimeZoneService</c> and therefore handles DST and any future per-teacher timezone in one place.
/// Doing it here would mean a hardcoded offset, which is exactly the class of bug CLAUDE.md §11b exists
/// to prevent.
/// </summary>
public class TeacherUsageRepo : ITeacherUsageRepo
{
    private readonly EdvanzDbContext _context;

    public TeacherUsageRepo(EdvanzDbContext context) => _context = context;

    // NOTE ON THE HOUR GROUPING: each query groups by `{ timestamp.Date, timestamp.Hour }` rather than
    // by a constructed DateTime. Both members have guaranteed SQL Server translations
    // (CONVERT(date, x) and DATEPART(hour, x)), so the grouping is provably server-side — a
    // constructed DateTime is far more fragile and risks silently dragging every row into memory.
    // The two parts are recombined in C#, where the Kind is also stamped back to Utc: SQL Server has
    // no notion of one, so EF hands these back as Unspecified.

    // ════════════════════════════════════════════════════════════════════════
    // ACTIVITY SOURCES — attributed (the module records who acted)
    // ════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageActorHourBucket>> GetAttendanceBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // IsAutoAbsent is excluded here, at the source, so no caller can forget it. The 02:30 sweep
        // writes those rows on the teacher's behalf; counting them would report every dormant
        // teacher on the platform as a daily user.
        var rows = await _context.AttendanceRecords
            .AsNoTracking()
            .Where(a => a.TeacherId == teacherId
                     && !a.IsAutoAbsent
                     && a.RecordedAt >= fromUtc
                     && a.RecordedAt < toUtc)
            .GroupBy(a => new { Day = a.RecordedAt.Date, a.RecordedAt.Hour, a.RecordedByUserId })
            .Select(g => new { g.Key.Day, g.Key.Hour, g.Key.RecordedByUserId, Count = g.Count() })
            .ToListAsync(ct);

        return rows
            .Select(r => new UsageActorHourBucket(
                DateTime.SpecifyKind(r.Day.AddHours(r.Hour), DateTimeKind.Utc), r.RecordedByUserId, r.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageActorHourBucket>> GetPaymentBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // CollectedAt (the cash event), never CreateAt. IsDeleted excluded: a reversed collection is
        // a correction, not usage.
        var rows = await _context.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.TeacherId == teacherId
                     && !p.IsDeleted
                     && p.CollectedAt >= fromUtc
                     && p.CollectedAt < toUtc)
            .GroupBy(p => new { Day = p.CollectedAt.Date, p.CollectedAt.Hour, p.CollectedByUserId })
            .Select(g => new { g.Key.Day, g.Key.Hour, g.Key.CollectedByUserId, Count = g.Count() })
            .ToListAsync(ct);

        return rows
            .Select(r => new UsageActorHourBucket(
                DateTime.SpecifyKind(r.Day.AddHours(r.Hour), DateTimeKind.Utc), r.CollectedByUserId, r.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageActorHourBucket>> GetVideoBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var rows = await _context.VideoAssets
            .AsNoTracking()
            .Where(v => v.TeacherId == teacherId && v.CreateAt >= fromUtc && v.CreateAt < toUtc)
            .GroupBy(v => new { Day = v.CreateAt.Date, v.CreateAt.Hour, v.CreatedByUserId })
            .Select(g => new { g.Key.Day, g.Key.Hour, g.Key.CreatedByUserId, Count = g.Count() })
            .ToListAsync(ct);

        return rows
            .Select(r => new UsageActorHourBucket(
                DateTime.SpecifyKind(r.Day.AddHours(r.Hour), DateTimeKind.Utc), r.CreatedByUserId, r.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageActorHourBucket>> GetOnlineExamBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var rows = await _context.OnlineExams
            .AsNoTracking()
            .Where(e => e.TeacherId == teacherId && e.CreateAt >= fromUtc && e.CreateAt < toUtc)
            .GroupBy(e => new { Day = e.CreateAt.Date, e.CreateAt.Hour, e.CreatedByUserId })
            .Select(g => new { g.Key.Day, g.Key.Hour, g.Key.CreatedByUserId, Count = g.Count() })
            .ToListAsync(ct);

        return rows
            .Select(r => new UsageActorHourBucket(
                DateTime.SpecifyKind(r.Day.AddHours(r.Hour), DateTimeKind.Utc), r.CreatedByUserId, r.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageActorHourBucket>> GetExamHomeworkBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var rows = await _context.AssignmentTemplates
            .AsNoTracking()
            .Where(t => t.TeacherId == teacherId && t.CreateAt >= fromUtc && t.CreateAt < toUtc)
            .GroupBy(t => new { Day = t.CreateAt.Date, t.CreateAt.Hour, t.CreatedByUserId })
            .Select(g => new { g.Key.Day, g.Key.Hour, g.Key.CreatedByUserId, Count = g.Count() })
            .ToListAsync(ct);

        return rows
            .Select(r => new UsageActorHourBucket(
                DateTime.SpecifyKind(r.Day.AddHours(r.Hour), DateTimeKind.Utc), r.CreatedByUserId, r.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageActorHourBucket>> GetParentPortalBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // RespondedByUserId is null for auto-approved grants — reported as unattributed by the
        // service, never folded into the teacher's own count.
        var rows = await _context.ParentPortalAccesses
            .AsNoTracking()
            .Where(a => a.TeacherId == teacherId && a.CreateAt >= fromUtc && a.CreateAt < toUtc)
            .GroupBy(a => new { Day = a.CreateAt.Date, a.CreateAt.Hour, a.RespondedByUserId })
            .Select(g => new { g.Key.Day, g.Key.Hour, g.Key.RespondedByUserId, Count = g.Count() })
            .ToListAsync(ct);

        return rows
            .Select(r => new UsageActorHourBucket(
                DateTime.SpecifyKind(r.Day.AddHours(r.Hour), DateTimeKind.Utc), r.RespondedByUserId, r.Count))
            .ToList();
    }

    // ════════════════════════════════════════════════════════════════════════
    // ACTIVITY SOURCES — unattributed (the module records no actor)
    // ════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageHourBucket>> GetStudentBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // IgnoreQueryFilters: a student soft-deleted later was still real work on the day it was
        // added. Filtering them out would quietly erase history every time a roster was cleaned up.
        var rows = await _context.TeacherStudents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.TeacherId == teacherId && s.CreateAt >= fromUtc && s.CreateAt < toUtc)
            .GroupBy(s => new { Day = s.CreateAt.Date, s.CreateAt.Hour })
            .Select(g => new { g.Key.Day, g.Key.Hour, Count = g.Count() })
            .ToListAsync(ct);

        return rows
            .Select(r => new UsageHourBucket(DateTime.SpecifyKind(r.Day.AddHours(r.Hour), DateTimeKind.Utc), r.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageHourBucket>> GetSessionBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // Sessions are HARD-deleted, so a deleted session's creation day is simply gone from history.
        // Nothing can be done about that and it is not worth a shadow table.
        var rows = await _context.Sessions
            .AsNoTracking()
            .Where(s => s.TeacherId == teacherId && s.CreateAt >= fromUtc && s.CreateAt < toUtc)
            .GroupBy(s => new { Day = s.CreateAt.Date, s.CreateAt.Hour })
            .Select(g => new { g.Key.Day, g.Key.Hour, Count = g.Count() })
            .ToListAsync(ct);

        return rows
            .Select(r => new UsageHourBucket(DateTime.SpecifyKind(r.Day.AddHours(r.Hour), DateTimeKind.Utc), r.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageHourBucket>> GetMessagingBucketsAsync(
        long teacherId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var rows = await _context.MessageLogs
            .AsNoTracking()
            .Where(m => m.TeacherId == teacherId && m.SentAt >= fromUtc && m.SentAt < toUtc)
            .GroupBy(m => new { Day = m.SentAt.Date, m.SentAt.Hour })
            .Select(g => new { g.Key.Day, g.Key.Hour, Count = g.Count() })
            .ToListAsync(ct);

        return rows
            .Select(r => new UsageHourBucket(DateTime.SpecifyKind(r.Day.AddHours(r.Hour), DateTimeKind.Utc), r.Count))
            .ToList();
    }

    // ════════════════════════════════════════════════════════════════════════
    // POINT-IN-TIME FACTS
    // ════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<UsageSetupHealth> GetSetupHealthAsync(long teacherId, CancellationToken ct = default)
    {
        // Each pair is (total, the subset that actually works). The second number is the one that
        // matters — see UsageSetupHealth for why a bare count is misleading.
        int studentCount = await _context.TeacherStudents
            .AsNoTracking().CountAsync(s => s.TeacherId == teacherId, ct);

        int studentsAssigned = await _context.TeacherStudents
            .AsNoTracking().CountAsync(s => s.TeacherId == teacherId && s.SessionId != null, ct);

        int sessionCount = await _context.Sessions
            .AsNoTracking().CountAsync(s => s.TeacherId == teacherId, ct);

        // A session with no generated occurrence never produces a class day, so nothing downstream
        // of it — attendance, during-session exams — can work.
        int sessionsWithOccurrences = await _context.Sessions
            .AsNoTracking()
            .CountAsync(s => s.TeacherId == teacherId
                          && _context.SessionOccurrences.Any(o => o.SessionId == s.Id), ct);

        int linkedAccounts = await _context.StudentTeacherLinks
            .AsNoTracking()
            .CountAsync(l => l.TeacherId == teacherId && l.LinkStatus == LinkStatus.Active, ct);

        // Active but UNBOUND means connected and seeing nothing: every module joins through
        // TeacherStudentId.
        int boundAccounts = await _context.StudentTeacherLinks
            .AsNoTracking()
            .CountAsync(l => l.TeacherId == teacherId
                          && l.LinkStatus == LinkStatus.Active
                          && l.TeacherStudentId != null, ct);

        int activeAssistants = await _context.Assistants
            .AsNoTracking()
            .CountAsync(a => a.TeacherAccountId == teacherId && a.RemovedAt == null && a.DeletedAt == null, ct);

        return new UsageSetupHealth(
            studentCount, studentsAssigned, sessionCount, sessionsWithOccurrences,
            linkedAccounts, boundAccounts, activeAssistants);
    }

    /// <inheritdoc />
    public async Task<UsageAllTimeFacts> GetAllTimeFactsAsync(long teacherId, CancellationToken ct = default)
    {
        bool everMarked = await _context.AttendanceRecords
            .AsNoTracking()
            .AnyAsync(a => a.TeacherId == teacherId && !a.IsAutoAbsent, ct);

        bool everCollected = await _context.PaymentTransactions
            .AsNoTracking()
            .AnyAsync(p => p.TeacherId == teacherId && !p.IsDeleted, ct);

        // These span the teacher's ENTIRE stored history, not the snapshot's 90-day read-back —
        // see UsageAllTimeFacts for why that distinction is load-bearing. One aggregate over the
        // teacher's day rows, which are already indexed by (TeacherId, ActivityDate).
        //
        // The bounds come from the stored days rather than a fresh scan of every source table: the
        // backfill window is the horizon of what we honestly claim to know, and a first-activity
        // date from outside it would be a number the rest of the model could not corroborate.
        // Two separate queries rather than one grouped projection: mixing MIN/MAX aggregates with a
        // collection selector in a single GroupBy is not reliably translatable, and the failure mode
        // is either a runtime exception or a silent client evaluation over every row.
        var span = await _context.TeacherUsageDays
            .AsNoTracking()
            .Where(d => d.TeacherId == teacherId)
            .GroupBy(d => d.TeacherId)
            .Select(g => new { First = g.Min(d => d.ActivityDate), Last = g.Max(d => d.ActivityDate) })
            .FirstOrDefaultAsync(ct);

        // SQL Server has no bitwise OR aggregate, so the distinct masks are folded in memory. The
        // set is tiny by construction — at most a few hundred possible values, realistically under
        // twenty — and DISTINCT keeps it that size regardless of how many days the teacher has.
        var masks = await _context.TeacherUsageDays
            .AsNoTracking()
            .Where(d => d.TeacherId == teacherId)
            .Select(d => d.ModulesMask)
            .Distinct()
            .ToListAsync(ct);

        DateTime? firstActivity = span is null
            ? null
            : span.First.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        DateTime? lastActivity = span is null
            ? null
            : span.Last.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        int allTimeMask = masks.Aggregate(0, (acc, m) => acc | m);

        return new UsageAllTimeFacts(
            everMarked, everCollected, firstActivity, lastActivity, allTimeMask);
    }

    /// <inheritdoc />
    public async Task<TeacherActorIdentities?> GetActorIdentitiesAsync(
        long teacherId, CancellationToken ct = default)
    {
        var teacher = await _context.Teachers
            .AsNoTracking()
            .Where(t => t.Id == teacherId)
            .Select(t => new { t.UserId, LastSeen = t.User.LastActivityAt, LastLogin = t.User.LastLoginAt })
            .FirstOrDefaultAsync(ct);

        if (teacher is null) return null;

        // REMOVED assistants are included on purpose: work they did while employed is still assistant
        // work. Excluding them would rewrite the operator mix of every past month each time someone
        // left the team.
        var assistants = await _context.Assistants
            .AsNoTracking()
            .Where(a => a.TeacherAccountId == teacherId)
            .Select(a => new
            {
                a.UserId,
                IsActive = a.RemovedAt == null && a.DeletedAt == null,
                LastSeen = a.User.LastActivityAt,
                LastLogin = a.User.LastLoginAt
            })
            .ToListAsync(ct);

        static DateTime? Later(DateTime? a, DateTime? b) =>
            a is null ? b : b is null ? a : (a > b ? a : b);

        DateTime? lastAssistantSeen = null;
        foreach (var a in assistants)
            lastAssistantSeen = Later(lastAssistantSeen, Later(a.LastSeen, a.LastLogin));

        return new TeacherActorIdentities(
            TeacherUserId: teacher.UserId,
            AssistantUserIds: assistants.Select(a => a.UserId).ToHashSet(),
            ActiveAssistantCount: assistants.Count(a => a.IsActive),
            LastTeacherSeenAt: Later(teacher.LastSeen, teacher.LastLogin),
            LastAssistantSeenAt: lastAssistantSeen);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetTeacherIdsForRollupAsync(CancellationToken ct = default)
        // The global query filter already excludes soft-deleted teachers.
        => await _context.Teachers
            .AsNoTracking()
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlySet<long>> GetTeacherIdsWithoutSnapshotAsync(CancellationToken ct = default)
    {
        var ids = await _context.Teachers
            .AsNoTracking()
            .Where(t => !_context.TeacherUsageSnapshots.Any(s => s.TeacherId == t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    // ════════════════════════════════════════════════════════════════════════
    // PERSISTENCE
    // ════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task ReplaceDaysAsync(
        long teacherId, DateOnly fromDate, DateOnly toDate,
        IReadOnlyCollection<TeacherUsageDay> rows, CancellationToken ct = default)
    {
        // Delete-then-insert over the exact window. This is what makes a re-run idempotent
        // (CLAUDE.md §6.4) and what lets a day self-heal when late offline syncs change its counts:
        // the old row is removed even when the new computation produces no row for that day at all.
        await _context.TeacherUsageDays
            .Where(d => d.TeacherId == teacherId
                     && d.ActivityDate >= fromDate
                     && d.ActivityDate <= toDate)
            .ExecuteDeleteAsync(ct);

        if (rows.Count == 0) return;

        await _context.TeacherUsageDays.AddRangeAsync(rows, ct);
        await _context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageDayTotals>> GetDayTotalsAsync(
        long teacherId, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default)
        => await _context.TeacherUsageDays
            .AsNoTracking()
            .Where(d => d.TeacherId == teacherId
                     && d.ActivityDate >= fromDate
                     && d.ActivityDate <= toDate)
            .OrderBy(d => d.ActivityDate)
            .Select(d => new UsageDayTotals(
                d.ActivityDate, d.TotalWrites, d.ModulesMask, d.TeacherWrites, d.AssistantWrites))
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<TeacherUsageSnapshot?> GetSnapshotAsync(long teacherId, CancellationToken ct = default)
        => await _context.TeacherUsageSnapshots
            .FirstOrDefaultAsync(s => s.TeacherId == teacherId, ct);

    /// <inheritdoc />
    public async Task UpsertSnapshotAsync(TeacherUsageSnapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot.Id == 0)
            await _context.TeacherUsageSnapshots.AddAsync(snapshot, ct);
        else
            _context.TeacherUsageSnapshots.Update(snapshot);

        await _context.SaveChangesAsync(ct);
    }
}
