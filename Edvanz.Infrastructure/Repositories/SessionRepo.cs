using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories;

/// <summary>
/// Extended repository for the Session Module (Module 2: Sessions).
/// Centralizes all domain-specific query logic for Session, SessionGroup, and SessionLink records.
/// 
/// ARCHITECTURAL NOTE (same rationale as UserRepo and TeacherStudentRepo):
/// All expression-based queries are encapsulated here so the Application layer
/// never builds raw predicates. If a query changes, you edit it HERE —
/// not in every service that uses it.
/// 
/// Inherits from GenericRepo&lt;Session, long&gt; for basic CRUD.
/// </summary>
public class SessionRepo : GenericRepo<Session, long>, ISessionRepo
{
    public SessionRepo(EdvanzDbContext context) : base(context)
    {
    }

    // ══════════════════════════════════════════════
    // SESSION QUERIES
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<Session?> GetByIdAndTeacherAsync(long sessionId, long teacherId)
    {
        return await _context.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<bool> SessionNameExistsAsync(long teacherId, string sessionName)
    {
        return await _context.Sessions
            .AnyAsync(s => s.TeacherId == teacherId && s.SessionName == sessionName);
    }

    /// <inheritdoc />
    public async Task<bool> SessionNameExistsExcludingAsync(long teacherId, string sessionName, long excludeSessionId)
    {
        return await _context.Sessions
            .AnyAsync(s => s.TeacherId == teacherId
                        && s.SessionName == sessionName
                        && s.Id != excludeSessionId);
    }

    /// <inheritdoc />
    public async Task<bool> HasStudentsOrLinksAsync(long sessionId)
    {
        // Check students assigned to this session
        bool hasStudents = await _context.TeacherStudents
            .AnyAsync(ts => ts.SessionId == sessionId);

        if (hasStudents)
            return true;

        // Check membership links involving this session (either side of canonical pair)
        return await _context.SessionLinks
            .AnyAsync(sl => sl.SessionId == sessionId || sl.LinkedSessionId == sessionId);
    }

    /// <inheritdoc />
    public async Task<int> CountStudentsBySessionAsync(long sessionId)
    {
        return await _context.TeacherStudents
            .CountAsync(ts => ts.SessionId == sessionId);
    }

    /// <inheritdoc />
    public async Task<int> CountSessionsByTeacherAsync(long teacherId)
    {
        return await _context.Sessions
            .CountAsync(s => s.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<Dictionary<long, int>> GetSessionCountsAsync(
        IReadOnlyCollection<long> teacherIds)
    {
        if (teacherIds.Count == 0) return new Dictionary<long, int>();

        return await _context.Sessions
            .Where(s => teacherIds.Contains(s.TeacherId))
            .GroupBy(s => s.TeacherId)
            .Select(g => new { TeacherId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeacherId, x => x.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Session>> GetActiveSessionsByTeacherIdsAsync(
        IReadOnlyCollection<long> teacherIds, DateTime today)
    {
        if (teacherIds.Count == 0) return new List<Session>();

        // Same "activeOnly" predicate as BuildSessionListQuery (EndDate >= today), fanned across a
        // whole center's teachers in a single query — no ordering (the client groups per teacher).
        return await _context.Sessions
            .Where(s => teacherIds.Contains(s.TeacherId) && s.EndDate >= today)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<Dictionary<long, int>> GetStudentCountsBySessionIdsAsync(
        IReadOnlyCollection<long> sessionIds)
    {
        if (sessionIds.Count == 0) return new Dictionary<long, int>();

        // Mirrors CountStudentsBySessionAsync (active roster rows via the global filter), grouped so
        // the center picker gets every session's live count in one round-trip.
        return await _context.TeacherStudents
            .Where(ts => ts.SessionId != null && sessionIds.Contains(ts.SessionId.Value))
            .GroupBy(ts => ts.SessionId!.Value)
            .Select(g => new { SessionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SessionId, x => x.Count);
    }

    /// <inheritdoc />
    public async Task<int> CountGroupsByTeacherAsync(long teacherId)
    {
        return await _context.SessionGroups
            .CountAsync(g => g.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public IQueryable<Session> BuildSessionListQuery(
        long teacherId,
        DateTime asOfLocalDate,
        string? search = null,
        long? groupId = null,
        OccurrenceType? occurrenceType = null,
        bool activeOnly = false,
        bool expiredOnly = false,
        SessionSortBy sortBy = SessionSortBy.DateAdded,
        SortDirection sortDirection = SortDirection.Desc)
    {
        // Start with all sessions for this teacher
        var query = _context.Sessions
            .Where(s => s.TeacherId == teacherId);

        // ── FILTERS ──

        // Filter by group: positive Id = specific group, -1 = ungrouped only
        if (groupId.HasValue)
        {
            if (groupId.Value == -1)
            {
                query = query.Where(s => s.SessionGroupId == null);
            }
            else
            {
                query = query.Where(s => s.SessionGroupId == groupId.Value);
            }
        }

        // Filter by occurrence type
        if (occurrenceType.HasValue)
        {
            query = query.Where(s => s.OccurrenceType == occurrenceType.Value);
        }

        // Filter by active/expired status (REQ-SES-015/045). The day comes from the caller so
        // it is the TEACHER's — see ISessionRepo for why UtcNow.Date was wrong here.
        var today = asOfLocalDate.Date;
        if (activeOnly)
        {
            query = query.Where(s => s.EndDate >= today);
        }
        else if (expiredOnly)
        {
            query = query.Where(s => s.EndDate < today);
        }

        // ── SEARCH ──
        // REQ-SES-044: Search by session name (partial match)
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = ArabicTextNormalizer.Normalize(search.Trim());
            query = query.Where(s => DbSearch.ArabicNormalize(s.SessionName).Contains(term));
        }

        // ── SORT ──
        query = sortBy switch
        {
            SessionSortBy.SessionName => sortDirection == SortDirection.Asc
                ? query.OrderBy(s => s.SessionName)
                : query.OrderByDescending(s => s.SessionName),

            SessionSortBy.StartDate => sortDirection == SortDirection.Asc
                ? query.OrderBy(s => s.StartDate)
                : query.OrderByDescending(s => s.StartDate),

            SessionSortBy.StudentCount => sortDirection == SortDirection.Asc
                ? query.OrderBy(s => s.TeacherStudents.Count)
                : query.OrderByDescending(s => s.TeacherStudents.Count),

            // Default: DateAdded (CreateAt) descending
            _ => sortDirection == SortDirection.Asc
                ? query.OrderBy(s => s.CreateAt)
                : query.OrderByDescending(s => s.CreateAt)
        };

        return query;
    }

    // ══════════════════════════════════════════════
    // SESSION NAME GENERATION SUPPORT
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<string?> GetHighestSessionNameAsync(long teacherId)
    {
        // Get all session names for this teacher so the name generator can determine
        // the highest auto-generated name. The generator parses the prefix ("Session "/"حصة ")
        // and the code suffix (e.g., "A1", "ب15") to compute the next name.
        // We order descending by name length then name to find the highest sequential name.
        return await _context.Sessions
            .Where(s => s.TeacherId == teacherId)
            .OrderByDescending(s => s.SessionName.Length)
            .ThenByDescending(s => s.SessionName)
            .Select(s => s.SessionName)
            .FirstOrDefaultAsync();
    }

    // ══════════════════════════════════════════════
    // SESSION GROUP QUERIES
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<SessionGroup?> GetGroupByIdAndTeacherAsync(long groupId, long teacherId)
    {
        return await _context.SessionGroups
            .FirstOrDefaultAsync(g => g.Id == groupId && g.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<bool> GroupNameExistsAsync(long teacherId, string groupName)
    {
        return await _context.SessionGroups
            .AnyAsync(g => g.TeacherId == teacherId && g.GroupName == groupName);
    }

    /// <inheritdoc />
    public async Task<bool> GroupNameExistsExcludingAsync(long teacherId, string groupName, long excludeGroupId)
    {
        return await _context.SessionGroups
            .AnyAsync(g => g.TeacherId == teacherId
                        && g.GroupName == groupName
                        && g.Id != excludeGroupId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionGroup>> GetGroupsByTeacherAsync(long teacherId)
    {
        return await _context.SessionGroups
            .Where(g => g.TeacherId == teacherId)
            .OrderBy(g => g.CreateAt)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GroupSessionRow>> GetSessionsByGroupIdsAsync(
        long teacherId, IEnumerable<long> groupIds)
    {
        var ids = groupIds.Distinct().ToList();
        if (ids.Count == 0) return new List<GroupSessionRow>();

        return await _context.Sessions
            .Where(s => s.TeacherId == teacherId
                && s.SessionGroupId.HasValue && ids.Contains(s.SessionGroupId.Value))
            .OrderBy(s => s.SessionName)
            .Select(s => new GroupSessionRow
            {
                GroupId = s.SessionGroupId!.Value,
                Id = s.Id,
                SessionName = s.SessionName,
            })
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task AddGroupAsync(SessionGroup group)
    {
        await _context.SessionGroups.AddAsync(group);
    }

    /// <inheritdoc />
    public async Task UpdateGroupAsync(SessionGroup group)
    {
        _context.Entry(group).State = EntityState.Modified;
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteGroupAsync(SessionGroup group)
    {
        _context.SessionGroups.Remove(group);
        await Task.CompletedTask;
    }

    // ══════════════════════════════════════════════
    // SESSION LINK (MEMBERSHIP) QUERIES
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<IReadOnlyList<Session>> GetLinkedSessionsAsync(long sessionId)
    {
        // Symmetric lookup: find all sessions linked to this one from either side
        var linkedIds = await _context.SessionLinks
            .Where(sl => sl.SessionId == sessionId || sl.LinkedSessionId == sessionId)
            .Select(sl => sl.SessionId == sessionId ? sl.LinkedSessionId : sl.SessionId)
            .ToListAsync();

        if (linkedIds.Count == 0)
            return Array.Empty<Session>();

        return await _context.Sessions
            .Where(s => linkedIds.Contains(s.Id))
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<bool> LinkExistsAsync(long sessionIdA, long sessionIdB)
    {
        // Canonical ordering: always check with lower Id first
        long lower = Math.Min(sessionIdA, sessionIdB);
        long upper = Math.Max(sessionIdA, sessionIdB);

        return await _context.SessionLinks
            .AnyAsync(sl => sl.SessionId == lower && sl.LinkedSessionId == upper);
    }

    /// <inheritdoc />
    public async Task<SessionLink?> GetLinkAsync(long sessionIdA, long sessionIdB)
    {
        long lower = Math.Min(sessionIdA, sessionIdB);
        long upper = Math.Max(sessionIdA, sessionIdB);

        return await _context.SessionLinks
            .FirstOrDefaultAsync(sl => sl.SessionId == lower && sl.LinkedSessionId == upper);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionLink>> GetLinksBySessionAsync(long sessionId)
    {
        return await _context.SessionLinks
            .Where(sl => sl.SessionId == sessionId || sl.LinkedSessionId == sessionId)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task AddLinkAsync(SessionLink link)
    {
        await _context.SessionLinks.AddAsync(link);
    }

    /// <inheritdoc />
    public async Task DeleteLinkAsync(SessionLink link)
    {
        _context.SessionLinks.Remove(link);
        await Task.CompletedTask;
    }
    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionNameRow>> GetTeacherSessionNamesAsync(long teacherId)
    {
        return await _context.Sessions
            .Where(s => s.TeacherId == teacherId)
            .OrderBy(s => s.SessionName)
            .Select(s => new SessionNameRow { Id = s.Id, SessionName = s.SessionName })
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, string>> GetSessionNamesByIdsAsync(
        long teacherId, IEnumerable<long> sessionIds)
    {
        var ids = sessionIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, string>();

        return await _context.Sessions
            .Where(s => s.TeacherId == teacherId && ids.Contains(s.Id))
            .Select(s => new { s.Id, s.SessionName })
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Id, s => s.SessionName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, string>> GetGroupNamesByIdsAsync(
        long teacherId, IEnumerable<long> groupIds)
    {
        var ids = groupIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, string>();

        return await _context.SessionGroups
            .Where(g => g.TeacherId == teacherId && ids.Contains(g.Id))
            .Select(g => new { g.Id, g.GroupName })
            .AsNoTracking()
            .ToDictionaryAsync(g => g.Id, g => g.GroupName);
    }
    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<SessionSummaryRow?> GetSessionSummaryByIdAsync(long teacherId, long sessionId)
    {
        return await _context.Sessions
            .Where(s => s.TeacherId == teacherId && s.Id == sessionId)
            .Select(s => new SessionSummaryRow
            {
                Id = s.Id,
                SessionName = s.SessionName,
                OccurrenceType = s.OccurrenceType,
                PaymentType = s.PaymentType,
                SessionAmount = s.SessionAmount
            })
            .AsNoTracking()
            .FirstOrDefaultAsync();
    }

    // ══════════════════════════════════════════════
    // SCOPE TARGET SUMMARIES (name + student count, batched)
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScopeTargetSummaryRow>> GetSessionTargetSummariesAsync(
        long teacherId, IEnumerable<long> sessionIds)
    {
        var ids = sessionIds.Distinct().ToList();
        if (ids.Count == 0) return new List<ScopeTargetSummaryRow>();

        return await _context.Sessions
            .Where(s => s.TeacherId == teacherId && ids.Contains(s.Id))
            .Select(s => new ScopeTargetSummaryRow
            {
                Id = s.Id,
                Name = s.SessionName,
                StudentCount = _context.TeacherStudents.Count(ts => ts.SessionId == s.Id),
            })
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScopeTargetSummaryRow>> GetGroupTargetSummariesAsync(
        long teacherId, IEnumerable<long> groupIds)
    {
        var ids = groupIds.Distinct().ToList();
        if (ids.Count == 0) return new List<ScopeTargetSummaryRow>();

        return await _context.SessionGroups
            .Where(g => g.TeacherId == teacherId && ids.Contains(g.Id))
            .Select(g => new ScopeTargetSummaryRow
            {
                Id = g.Id,
                Name = g.GroupName,
                StudentCount = _context.TeacherStudents.Count(ts =>
                    ts.Session != null && ts.Session.SessionGroupId == g.Id),
            })
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> CountStudentsInTargetsAsync(
        long teacherId, IEnumerable<long> sessionIds, IEnumerable<long> groupIds)
    {
        var sIds = sessionIds.Distinct().ToList();
        var gIds = groupIds.Distinct().ToList();
        if (sIds.Count == 0 && gIds.Count == 0) return 0;

        return await _context.TeacherStudents
            .Where(ts => ts.TeacherId == teacherId
                      && ts.SessionId.HasValue
                      && (sIds.Contains(ts.SessionId!.Value)
                          || (ts.Session != null && ts.Session.SessionGroupId.HasValue
                              && gIds.Contains(ts.Session.SessionGroupId.Value))))
            .CountAsync();
    }

    // Repo
    public async Task<IReadOnlyDictionary<long, string>> GetSessionNamesByIdsAsync(
        long? teacherId, IEnumerable<long> sessionIds)
    {
        var ids = sessionIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, string>();

        var query = _context.Sessions.Where(s => ids.Contains(s.Id));
        if (teacherId.HasValue)
            query = query.Where(s => s.TeacherId == teacherId.Value);

        return await query
            .Select(s => new { s.Id, s.SessionName })
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Id, s => s.SessionName);
    }
}