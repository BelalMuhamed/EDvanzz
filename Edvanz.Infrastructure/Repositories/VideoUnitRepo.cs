using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories;

/// <summary>
/// Extended repository for <see cref="VideoUnit"/> (Track C / G-UNIT). Same
/// query-pattern conventions as <see cref="VideoAssetRepo"/> — paged
/// projections, tenant-scoped, aggregates computed in one round trip.
/// </summary>
public class VideoUnitRepo : GenericRepo<VideoUnit, long>, IVideoUnitRepo
{
    public VideoUnitRepo(EdvanzDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task AddUnitAsync(VideoUnit unit)
    {
        await _context.VideoUnits.AddAsync(unit);
    }

    /// <inheritdoc />
    public async Task<VideoUnit?> GetUnitByIdAndTeacherAsync(long unitId, long teacherId)
    {
        // Tracked: caller may mutate for update or soft-delete.
        return await _context.VideoUnits
            .FirstOrDefaultAsync(u => u.Id == unitId && u.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task SoftDeleteUnitAsync(VideoUnit unit, DateTime deletedAtUtc)
    {
        unit.DeletedAt = deletedAtUtc;
        _context.Entry(unit).State = EntityState.Modified;
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<int> DetachVideosFromUnitAsync(long unitId)
    {
        // Single SQL DELETE against the M:N join table — makes the unit's
        // videos loose immediately rather than waiting for a hard DB delete
        // (this is a soft-delete of the unit, so the FK's NoAction never fires).
        return await _context.VideoAssetUnits
            .Where(au => au.UnitId == unitId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<TeacherVideoUnitListRow> Items, int TotalCount)>
        GetTeacherUnitsPagedAsync(long teacherId, string? search, int page, int pageSize)
    {
        var query = _context.VideoUnits
            .Where(u => u.TeacherId == teacherId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{ArabicTextNormalizer.Normalize(search.Trim())}%";
            query = query.Where(u => EF.Functions.Like(DbSearch.ArabicNormalize(u.Title), pattern));
        }

        int totalCount = await query.CountAsync();

        // Rolled-up child aggregates via correlated subqueries — one round
        // trip, no per-unit N+1. SeenStudentCount mirrors the definition used
        // by the top-level teacher video list (distinct students with any
        // VideoAnalytics row across the unit's videos).
        var rows = await query
            .OrderByDescending(u => u.CreateAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new TeacherVideoUnitListRow
            {
                Id = u.Id,
                Title = u.Title,
                Description = u.Description,
                VideoCount = _context.VideoAssetUnits.Count(au => au.UnitId == u.Id),
                SeenStudentCount = _context.VideoAnalytics
                    .Count(a => _context.VideoAssetUnits
                        .Any(au => au.UnitId == u.Id && au.VideoAssetId == a.VideoAssetId)),
                UnseenStudentCount = _context.VideoScopes
                    .Count(s => _context.VideoAssetUnits.Any(au => au.UnitId == u.Id && au.VideoAssetId == s.VideoAssetId))
                    - _context.VideoAnalytics
                        .Count(a => _context.VideoAssetUnits
                            .Any(au => au.UnitId == u.Id && au.VideoAssetId == a.VideoAssetId)),
                CreatedAt = u.CreateAt,
            })
            .AsNoTracking()
            .ToListAsync();

        foreach (var row in rows)
        {
            row.UnseenStudentCount = Math.Max(0, row.UnseenStudentCount);
        }

        return (rows, totalCount);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<(IReadOnlyList<TeacherVideoListRow> Items, int TotalCount)>
        GetVideosInUnitPagedAsync(long unitId, long teacherId, string? search, int page, int pageSize,
            VideoStatus? status = null)
    {
        var query = _context.VideoAssets
            .Where(v => v.TeacherId == teacherId
                && _context.VideoAssetUnits.Any(au => au.UnitId == unitId && au.VideoAssetId == v.Id));

        // Same Title search as GetTeacherVideosPagedAsync — parity with the top-level list.
        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{ArabicTextNormalizer.Normalize(search.Trim())}%";
            query = query.Where(v => EF.Functions.Like(DbSearch.ArabicNormalize(v.Title), pattern));
        }

        // Optional publish-state filter (Draft / Published). Null keeps both,
        // which is the behaviour every existing caller relies on.
        if (status.HasValue)
        {
            query = query.Where(v => v.Status == status.Value);
        }

        int totalCount = await query.CountAsync();

        var rows = await query
            .OrderByDescending(v => v.CreateAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(v => new TeacherVideoListRow
            {
                Id = v.Id,
                Title = v.Title,
                Description = v.Description,
                SourceUrl = v.SourceUrl,
                SourceType = v.SourceType,
                DurationSeconds = v.DurationSeconds,
                StudentsInScope = _context.VideoScopes.Count(s => s.VideoAssetId == v.Id),
                TotalOpens = _context.VideoAnalytics
                    .Where(a => a.VideoAssetId == v.Id)
                    .Sum(a => (int?)a.OpenCount) ??0,
                SeenStudentCount = _context.VideoAnalytics.Count(a => a.VideoAssetId == v.Id),
                Status = v.Status,
                PublishDate = v.PublishDate,
                // Parity with GetTeacherVideosPagedAsync: cover-photo id (resolved to
                // PublicId + gated URL in the service) and the exam/attachment counts.
                VideoPhotoFileId = v.VideoPhotoFileId,
                QuestionsNumber = _context.VideoExamQuestions
                    .Count(q => q.Exam.VideoAssetId == v.Id),
                AttachmentsNumber = _context.Set<FileObject>()
                    .Count(f => f.VideoAssetId == v.Id
                             && f.Category == FileCategory.VideoAttachment
                             && f.Status == FileStatus.Attached),
                CreatedAt = v.CreateAt,
            })
            .AsNoTracking()
            .ToListAsync();

        foreach (var row in rows)
        {
            row.UnseenStudentCount = Math.Max(0, row.StudentsInScope - row.SeenStudentCount);
        }

        return (rows, totalCount);
    }

    // ══════════════════════════════════════════════════════════════════════
    // UNIT SCOPE — collection-level Target Scope (final decision)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<VideoUnit?> GetUnitWithScopesAsync(long unitId, long teacherId)
    {
        return await _context.VideoUnits
            .Include(u => u.Scopes)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == unitId && u.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task AddUnitScopesAsync(IEnumerable<VideoUnitScope> scopes)
    {
        await _context.VideoUnitScopes.AddRangeAsync(scopes);
    }

    /// <inheritdoc />
    public async Task DeleteAllScopesForUnitAsync(long unitId)
    {
        await _context.VideoUnitScopes
            .Where(s => s.VideoUnitId == unitId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task DeleteUnitScopesBySessionAsync(long sessionId)
    {
        await _context.VideoUnitScopes
            .Where(s => s.SessionId == sessionId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task DeleteUnitScopesByStudentAsync(long teacherStudentId)
    {
        await _context.VideoUnitScopes
            .Where(s => s.TeacherStudentId == teacherStudentId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task DeleteUnitScopesByGroupAsync(long sessionGroupId)
    {
        await _context.VideoUnitScopes
            .Where(s => s.SessionGroupId == sessionGroupId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteUnitScopeByIdAndTeacherAsync(long scopeId, long teacherId)
    {
        var rowsAffected = await _context.VideoUnitScopes
            .Where(s => s.Id == scopeId && s.TeacherId == teacherId)
            .ExecuteDeleteAsync();

        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task<int> CountScopesForUnitAsync(long unitId)
    {
        return await _context.VideoUnitScopes
            .CountAsync(s => s.VideoUnitId == unitId);
    }

    /// <inheritdoc />
    public async Task<List<VideoUnitScope>> GetScopeRowsForUnitsAsync(IEnumerable<long> unitIds)
    {
        var ids = unitIds.Distinct().ToList();
        if (ids.Count == 0)
            return new List<VideoUnitScope>();

        return await _context.VideoUnitScopes
            .AsNoTracking()
            .Where(s => ids.Contains(s.VideoUnitId))
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<long>> GetVideoIdsInUnitAsync(long unitId)
    {
        return await _context.VideoAssetUnits
            .Where(au => au.UnitId == unitId)
            .Select(au => au.VideoAssetId)
            .ToListAsync();
    }
}
