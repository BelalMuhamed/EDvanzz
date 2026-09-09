using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Edvanz.Infrastructure.Repositories.Queries;
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

        // A unit has no publish state of its own — it is a boundary, not a grant
        // (see VideoUnit's remarks). Its card badge is DERIVED from how many
        // member videos are actually live. NOTE: these counts apply the VIDEO-level half of
        // VideoAssetRepo.GetStudentVisibleUnitsAsync only - that query ALSO requires a matching
        // VideoScopes row, so a unit whose videos are published but scoped to nobody still counts as
        // Published here while no student can open it. Deliberately left as-is for now (reviewed
        // 2026-09-08); the previous claim that the two "can never disagree" was not true.
        var utcNow = DateTime.UtcNow;

        // Rolled-up child aggregates via correlated subqueries — one round
        // trip, no per-unit N+1. Seen/unseen are DELIBERATELY absent from this
        // projection: they need a distinct-students-per-unit rollup that a
        // correlated COUNT cannot express, and are filled in below.
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

                // Driven off VideoAssetUnits.UnitId (the same index VideoCount
                // uses) with a PK seek per member video — never a scan of the
                // teacher's whole video table.
                PublishedVideoCount = _context.VideoAssetUnits.Count(au =>
                    au.UnitId == u.Id
                    && _context.VideoAssets.Any(v => v.Id == au.VideoAssetId
                        && v.Status == VideoStatus.Published
                        && (v.PublishDate == null || v.PublishDate <= utcNow))),
                ScheduledVideoCount = _context.VideoAssetUnits.Count(au =>
                    au.UnitId == u.Id
                    && _context.VideoAssets.Any(v => v.Id == au.VideoAssetId
                        && v.Status == VideoStatus.Published
                        && v.PublishDate != null && v.PublishDate > utcNow)),
                // Live attachments and quiz-bearing videos across the unit's
                // members — same definitions the per-video card uses
                // (Attached VideoAttachment FileObjects; a video "has a quiz"
                // when a VideoExam exists for it), so the unit chip and the
                // videos inside it can never disagree.
                // TENANT-NARROWED FIRST (2026-09-09). FileObjects is the account-wide file registry and
                // grows with every upload of every category; filtering only on Category + Status left
                // this correlated EXISTS scanning the whole table once per unit row, on a screen
                // teachers keep open. FileObject carries a denormalized TeacherId for exactly this.
                AttachmentCount = _context.Set<FileObject>()
                    .Count(f => f.TeacherId == teacherId
                             && f.Category == FileCategory.VideoAttachment
                             && f.Status == FileStatus.Attached
                             && f.VideoAssetId != null
                             && _context.VideoAssetUnits.Any(au =>
                                    au.UnitId == u.Id && au.VideoAssetId == f.VideoAssetId)),
                QuizCount = _context.VideoAssetUnits.Count(au =>
                    au.UnitId == u.Id
                    && _context.VideoExams.Any(e => e.VideoAssetId == au.VideoAssetId)),
                CreatedAt = u.CreateAt,
            })
            .AsNoTracking()
            .ToListAsync();

        // Seen / unseen roll up to DISTINCT STUDENTS across the unit's videos,
        // using the same resolved-audience definition as the top-level video
        // list (VideoAudienceQueries). The old projection counted raw
        // VideoAnalytics ROWS as "seen" — so one student who opened four videos
        // in the unit read as four — and scope ROWS as the audience, then
        // subtracted one wrong number from the other and clamped, which is why
        // unseen read 0 on real data. Batched for the materialised page only.
        var pageUnitIds = rows.Select(r => r.Id).ToList();
        var audience = await VideoAudienceQueries
            .GetAudienceCountsForUnitsAsync(_context, teacherId, pageUnitIds);

        foreach (var row in rows)
        {
            if (audience.TryGetValue(row.Id, out var counts))
            {
                row.SeenStudentCount = counts.Seen;
                row.UnseenStudentCount = Math.Max(0, counts.InScope - counts.Seen);
            }
            else
            {
                // No member videos, or none of them target anyone yet.
                row.SeenStudentCount = 0;
                row.UnseenStudentCount = 0;
            }
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
                TotalOpens = _context.VideoAnalytics
                    .Where(a => a.VideoAssetId == v.Id)
                    .Sum(a => (int?)a.OpenCount) ??0,
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

        // StudentsInScope / SeenStudentCount / UnseenStudentCount use the SAME
        // resolved-audience definition as the top-level Videos tab and the
        // analytics endpoint (VideoAudienceQueries), batched for the whole page.
        // Before this, the videos-in-unit list still ran the pre-0aa4b35 rule —
        // scope ROWS as the audience and ALL analytics rows as "seen", including
        // out-of-scope and soft-deleted students — so the SAME video reported
        // honest numbers on the Videos tab and inflated seen / 0 unseen when
        // opened through its unit.
        var videoIds = rows.Select(r => r.Id).ToList();
        var audience = await VideoAudienceQueries
            .GetAudienceCountsForVideosAsync(_context, teacherId, videoIds);

        foreach (var row in rows)
        {
            if (audience.TryGetValue(row.Id, out var counts))
            {
                row.StudentsInScope = counts.InScope;
                row.SeenStudentCount = counts.Seen;
            }
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
