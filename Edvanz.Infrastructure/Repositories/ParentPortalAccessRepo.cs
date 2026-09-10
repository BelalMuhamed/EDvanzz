using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories;

/// <inheritdoc cref="IParentPortalAccessRepo"/>
/// <remarks>
/// NOTE on <c>Include(a =&gt; a.TeacherStudent)</c>: <see cref="TeacherStudent"/> carries the global
/// soft-delete query filter, this entity does not. EF Core keeps the grant row and leaves the
/// navigation NULL when the roster record was soft-deleted, which is exactly what the portal
/// needs (it reports "this student is no longer on the teacher's list" instead of vanishing).
/// Every consumer therefore treats a null <c>TeacherStudent</c> as "student removed".
/// </remarks>
public class ParentPortalAccessRepo : GenericRepo<ParentPortalAccess, long>, IParentPortalAccessRepo
{
    public ParentPortalAccessRepo(EdvanzDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<ParentPortalAccess?> GetLiveByStudentAndDeviceAsync(long teacherStudentId, string deviceHash)
    {
        // Tracked (no AsNoTracking): the request path may promote this row to Active.
        // Served by UX_PPA_Student_Device_Live, which guarantees at most one match.
        return await _context.Set<ParentPortalAccess>()
            .FirstOrDefaultAsync(a =>
                a.TeacherStudentId == teacherStudentId &&
                a.DeviceHash == deviceHash &&
                (a.Status == ParentPortalAccessStatus.Active ||
                 a.Status == ParentPortalAccessStatus.Pending));
    }

    /// <inheritdoc />
    public async Task<ParentPortalAccess?> GetActiveByDeviceAsync(string deviceHash)
    {
        // Served by IX_PPA_DeviceHash. Newest first so an old grant can never shadow a newer one
        // if history ever leaves two Active rows for the same device across different students.
        return await _context.Set<ParentPortalAccess>()
            .AsNoTracking()
            .Include(a => a.TeacherStudent)
            .Where(a => a.DeviceHash == deviceHash && a.Status == ParentPortalAccessStatus.Active)
            .OrderByDescending(a => a.RequestedAt)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<ParentPortalAccess?> GetActiveByDeviceAndStudentAsync(
        string deviceHash, long teacherStudentId)
    {
        // Served by UX_PPA_Student_Device_Live, which guarantees at most one live row per
        // (student, device) — so no ordering is needed to make this deterministic.
        return await _context.Set<ParentPortalAccess>()
            .AsNoTracking()
            .Include(a => a.TeacherStudent)
            .FirstOrDefaultAsync(a =>
                a.DeviceHash == deviceHash &&
                a.TeacherStudentId == teacherStudentId &&
                a.Status == ParentPortalAccessStatus.Active);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParentPortalAccess>> GetActiveGrantsByDeviceAsync(string deviceHash)
    {
        // Served by IX_PPA_DeviceHash. Newest first so the default selection matches what the
        // single-grant lookup would have returned, keeping one-child behaviour byte-identical.
        return await _context.Set<ParentPortalAccess>()
            .AsNoTracking()
            .Include(a => a.TeacherStudent)
            .Where(a => a.DeviceHash == deviceHash && a.Status == ParentPortalAccessStatus.Active)
            .OrderByDescending(a => a.RequestedAt)
            .ThenByDescending(a => a.Id)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<ParentPortalTeacherHeader?> GetPortalTeacherHeaderAsync(long teacherId)
    {
        // ONE round-trip: the subject and configuration ride along as correlated subqueries rather
        // than as four more calls. The subject is projected to its two labels instead of the entity
        // so the reader's language is chosen in the service, where the culture lives.
        return await _context.Set<Teacher>()
            .AsNoTracking()
            .Where(t => t.Id == teacherId && t.DeletedAt == null)
            .Select(t => new ParentPortalTeacherHeader(
                _context.Users
                    .Where(u => u.Id == t.UserId)
                    .Select(u => u.FullName)
                    .FirstOrDefault() ?? string.Empty,
                _context.Set<TeacherSubject>()
                    .Where(ts => ts.TeacherId == t.Id)
                    .Join(_context.Set<Subject>(), ts => ts.SubjectId, s => s.Id, (ts, s) => s.NameAr)
                    .FirstOrDefault(),
                _context.Set<TeacherSubject>()
                    .Where(ts => ts.TeacherId == t.Id)
                    .Join(_context.Set<Subject>(), ts => ts.SubjectId, s => s.Id, (ts, s) => s.NameEn)
                    .FirstOrDefault(),
                t.CustomSubject,
                _context.Set<TeacherConfiguration>()
                    .FirstOrDefault(c => c.TeacherId == t.Id)))
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<ParentPortalAccess?> GetLatestByDeviceAsync(string deviceHash)
    {
        return await _context.Set<ParentPortalAccess>()
            .AsNoTracking()
            .Include(a => a.TeacherStudent)
            .Where(a => a.DeviceHash == deviceHash)
            .OrderByDescending(a => a.RequestedAt)
            .ThenByDescending(a => a.Id)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<bool> HasActiveGrantWithPhoneAsync(long teacherStudentId, string normalizedPhone)
    {
        if (string.IsNullOrWhiteSpace(normalizedPhone))
            return false;

        return await _context.Set<ParentPortalAccess>()
            .AnyAsync(a =>
                a.TeacherStudentId == teacherStudentId &&
                a.Status == ParentPortalAccessStatus.Active &&
                a.ClaimedPhone != null &&
                a.ClaimedPhone == normalizedPhone);
    }

    /// <inheritdoc />
    public async Task<ParentPortalAccess?> GetNewestForStudentByDeviceOrPhoneAsync(
        long teacherStudentId, string deviceHash, string? normalizedPhone)
    {
        // A single OR'd predicate rather than two round-trips: "newest across BOTH axes" is the
        // rule, so picking the newest of each separately and comparing in memory would be a
        // different (and wrong) query.
        bool hasPhone = !string.IsNullOrWhiteSpace(normalizedPhone);

        return await _context.Set<ParentPortalAccess>()
            .AsNoTracking()
            .Where(a => a.TeacherStudentId == teacherStudentId &&
                        (a.DeviceHash == deviceHash ||
                         (hasPhone && a.ClaimedPhone != null && a.ClaimedPhone == normalizedPhone)))
            .OrderByDescending(a => a.RequestedAt)
            .ThenByDescending(a => a.Id)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<int> RevokeByStudentAndPhoneAsync(
        long teacherId, long teacherStudentId, string normalizedPhone, long actingUserId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(normalizedPhone))
            return 0;

        // ONE atomic UPDATE across every live sibling row — see the interface remarks for why a
        // per-row revoke would silently do nothing. ExecuteUpdate bypasses the change tracker, so
        // the caller must not hold and re-save a stale copy of any affected row.
        return await _context.Set<ParentPortalAccess>()
            .Where(a => a.TeacherId == teacherId &&
                        a.TeacherStudentId == teacherStudentId &&
                        a.ClaimedPhone != null &&
                        a.ClaimedPhone == normalizedPhone &&
                        (a.Status == ParentPortalAccessStatus.Active ||
                         a.Status == ParentPortalAccessStatus.Pending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ParentPortalAccessStatus.Revoked)
                .SetProperty(a => a.RespondedAt, nowUtc)
                .SetProperty(a => a.RespondedByUserId, actingUserId));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParentPortalAccess>> GetPendingForTeacherPagedAsync(
        long teacherId, int page, int pageSize)
    {
        int skip = (page < 1 ? 0 : page - 1) * (pageSize < 1 ? 1 : pageSize);

        return await _context.Set<ParentPortalAccess>()
            .AsNoTracking()
            .Include(a => a.TeacherStudent)
            .Where(a => a.TeacherId == teacherId && a.Status == ParentPortalAccessStatus.Pending)
            .OrderByDescending(a => a.RequestedAt)
            .ThenByDescending(a => a.Id)
            .Skip(skip)
            .Take(pageSize < 1 ? 1 : pageSize)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> CountPendingForTeacherAsync(long teacherId)
    {
        return await _context.Set<ParentPortalAccess>()
            .CountAsync(a => a.TeacherId == teacherId && a.Status == ParentPortalAccessStatus.Pending);
    }

    /// <inheritdoc />
    public async Task<ParentPortalAccess?> GetByIdForTeacherAsync(long id, long teacherId)
    {
        // Tracked — approve/reject/revoke mutate the returned row.
        // The teacherId predicate is the tenant guard: a foreign id returns null, never a row.
        return await _context.Set<ParentPortalAccess>()
            .Include(a => a.TeacherStudent)
            .FirstOrDefaultAsync(a => a.Id == id && a.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParentPortalAccess>> GetByIdsForTeacherAsync(
        IEnumerable<long> ids, long teacherId)
    {
        var idList = ids?.Distinct().ToList() ?? new List<long>();
        if (idList.Count == 0)
            return Array.Empty<ParentPortalAccess>();

        return await _context.Set<ParentPortalAccess>()
            .Include(a => a.TeacherStudent)
            .Where(a => a.TeacherId == teacherId && idList.Contains(a.Id))
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParentPortalAccess>> GetFollowersForStudentAsync(
        long teacherId, long teacherStudentId)
    {
        return await _context.Set<ParentPortalAccess>()
            .AsNoTracking()
            .Where(a => a.TeacherId == teacherId &&
                        a.TeacherStudentId == teacherStudentId &&
                        (a.Status == ParentPortalAccessStatus.Active ||
                         a.Status == ParentPortalAccessStatus.Pending))
            .OrderByDescending(a => a.RequestedAt)
            .ThenByDescending(a => a.Id)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> CountFollowedStudentsForTeacherAsync(long teacherId)
    {
        return await _context.Set<ParentPortalAccess>()
            .Where(a => a.TeacherId == teacherId && a.Status == ParentPortalAccessStatus.Active)
            .Select(a => a.TeacherStudentId)
            .Distinct()
            .CountAsync();
    }

    /// <inheritdoc />
    public async Task<int> CountPendingByDeviceSinceAsync(string deviceHash, DateTime sinceUtc)
    {
        return await _context.Set<ParentPortalAccess>()
            .CountAsync(a => a.DeviceHash == deviceHash && a.RequestedAt >= sinceUtc);
    }

    /// <inheritdoc />
    public async Task<int> CountPendingForTeacherSinceAsync(long teacherId, DateTime sinceUtc)
    {
        return await _context.Set<ParentPortalAccess>()
            .CountAsync(a => a.TeacherId == teacherId && a.RequestedAt >= sinceUtc);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetNewestPendingRequestedAtAsync(long teacherId)
    {
        // Nullable projection so an empty inbox returns null rather than default(DateTime).
        return await _context.Set<ParentPortalAccess>()
            .Where(a => a.TeacherId == teacherId && a.Status == ParentPortalAccessStatus.Pending)
            .OrderByDescending(a => a.RequestedAt)
            .Select(a => (DateTime?)a.RequestedAt)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task TouchLastSeenAsync(long id, DateTime utcNow)
    {
        await _context.Set<ParentPortalAccess>()
            .Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, utcNow));
    }

    /// <inheritdoc />
    public async Task DeleteForStudentAsync(long teacherStudentId)
    {
        await _context.Set<ParentPortalAccess>()
            .Where(a => a.TeacherStudentId == teacherStudentId)
            .ExecuteDeleteAsync();
    }
}
