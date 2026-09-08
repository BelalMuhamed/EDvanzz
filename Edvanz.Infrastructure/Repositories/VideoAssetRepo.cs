using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Edvanz.Infrastructure.Repositories.Queries;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories;

/// <summary>
/// Extended repository for the Video Content Management Module (Module 14).
/// Centralizes all domain-specific query logic for the five VCM entities.
///
/// Inherits <see cref="GenericRepo{T, Tkey}"/> over <see cref="VideoAsset"/>
/// for basic CRUD on the primary entity. Other entities are accessed via
/// <c>_context</c> directly through named methods — same pattern as
/// <see cref="ExamHomeworkRepo"/> and <see cref="PaymentRepo"/>.
///
/// QUERY PATTERNS (matching the spec's §4 query plans Q1–Q5):
/// <list type="bullet">
///   <item>Paged queries use <c>CountAsync</c> + <c>Skip</c>/<c>Take</c>.</item>
///   <item>Search uses <c>EF.Functions.Like</c> for SQL Server index-friendly
///         case-insensitive matching.</item>
///   <item>Hot reader paths project directly to projection types via
///         <c>Select</c> — avoids materializing full entity graphs.</item>
///   <item>Atomic UPSERT path uses <c>ExecuteUpdateAsync</c> with SQL-side
///         increment for multi-device safety (Story C).</item>
///   <item>All queries enforce tenant scope via composite-FK joins — the
///         schema makes cross-tenant rows impossible (Phase 2 design), but
///         the queries still pass <c>teacherId</c> explicitly so the EF Core
///         plan stays index-aligned.</item>
/// </list>
///
/// CONCURRENCY:
/// <see cref="IncrementWatchAtomicAsync"/> issues a single SQL UPDATE that
/// increments <c>TotalWatchSeconds</c> on the SQL side. Two concurrent Stop
/// calls from different devices serialize on SQL Server's row-level X lock
/// (~1ms wait). No application-level locking needed.
///
/// TRUST BOUNDARIES:
/// <see cref="TryUpdateDurationWithinToleranceAsync"/> implements the ±5%
/// tolerance check inside the SQL <c>WHERE</c> clause so the update is one
/// round trip and the tolerance check is atomic with the write.
/// </summary>
public class VideoAssetRepo : GenericRepo<VideoAsset, long>, IVideoAssetRepo
{
    public VideoAssetRepo(EdvanzDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<int> CountByTeacherAsync(long teacherId)
    {
        // VideoAsset is hard-delete-only (no IsDeleted), so live rows == the real count.
        return await _context.VideoAssets.CountAsync(v => v.TeacherId == teacherId);
    }

    // ══════════════════════════════════════════════════════════════════════
    // VIDEO ↔ UNIT LINKS (M:N join VideoAssetUnits)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<List<long>> GetLinkedUnitIdsAsync(long videoAssetId)
    {
        return await _context.VideoAssetUnits
            .Where(x => x.VideoAssetId == videoAssetId)
            .Select(x => x.UnitId)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task ReplaceUnitLinksAsync(long videoAssetId, IEnumerable<long> unitIds)
    {
        var desired = unitIds.Distinct().ToList();
        var existing = await _context.VideoAssetUnits
            .Where(x => x.VideoAssetId == videoAssetId)
            .ToListAsync();
        var existingIds = existing.Select(x => x.UnitId).ToHashSet();

        var toRemove = existing.Where(x => !desired.Contains(x.UnitId)).ToList();
        if (toRemove.Count > 0)
            _context.VideoAssetUnits.RemoveRange(toRemove);

        var toAdd = desired
            .Where(id => !existingIds.Contains(id))
            .Select(id => new VideoAssetUnit { VideoAssetId = videoAssetId, UnitId = id });
        await _context.VideoAssetUnits.AddRangeAsync(toAdd);
        // Caller owns the commit boundary (SaveChanges / UnitOfWork), per the module's write-path convention.
    }

    // ══════════════════════════════════════════════════════════════════════
    // VIDEO ASSET — WRITE PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddVideoAsync(VideoAsset video)
    {
        await _context.VideoAssets.AddAsync(video);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task DeleteVideoAsync(VideoAsset video)
    {
        // Hard delete per REQ-VCM-BR-03. FIX: NoAction on SQL Server does NOT
        // cascade — it BLOCKS the delete while child rows exist (the previous
        // version of this method relied on a doc comment claiming the
        // opposite, which was simply wrong, and threw
        // FK_VideoScopes_VideoAssets_VideoAssetId_TeacherId on any scoped
        // video). Every NoAction-FK child table is cleared explicitly here,
        // one ExecuteDeleteAsync round trip each, before the VideoAsset
        // removal is staged. VideoExam is the one exception — its FK is
        // CASCADE, so SQL Server removes it (and its cascaded Questions/
        // Options) automatically; no explicit delete needed for it.
        //
        // Audit row is INSERTed by the service layer in the same transaction
        // BEFORE this call — VideoAssetAudits has no FK back to VideoAssets
        // (by design), so it's untouched by any of this.
        long videoAssetId = video.Id;

        await _context.VideoScopes
            .Where(s => s.VideoAssetId == videoAssetId)
            .ExecuteDeleteAsync();

        // Registry files (attachments et al.) back-reference this video via a NoAction FK
        // (FileObjects.VideoAssetId). The service layer detaches them first
        // (VideoService.DetachVideoFilesAsync), but this defensive detach guarantees the FK can
        // never block the delete regardless of caller. DETACH — never hard-delete — so the GC
        // reaps the blob + row (a hard delete here would orphan the blob permanently).
        await _context.FileObjects
            .Where(f => f.VideoAssetId == videoAssetId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.VideoAssetId, (long?)null)
                .SetProperty(f => f.Status, FileStatus.Detached));

        await _context.VideoAnalytics
            .Where(a => a.VideoAssetId == videoAssetId)
            .ExecuteDeleteAsync();

        await _context.VideoWatchEvents
            .Where(e => e.VideoAssetId == videoAssetId)
            .ExecuteDeleteAsync();

        await _context.VideoAssetUnits
            .Where(u => u.VideoAssetId == videoAssetId)
            .ExecuteDeleteAsync();

        _context.VideoAssets.Remove(video);
        await Task.CompletedTask;
    }


    /// <inheritdoc />
    public async Task<bool> TryUpdateDurationWithinToleranceAsync(
        long videoAssetId,
        int reportedDurationSeconds,
        double toleranceFraction)
    {
        // Trust-boundary rule (Story B step 5):
        //   - Current = 0      → accept the report unconditionally.
        //   - Current > 0      → accept only if within ±tolerance.
        //   - Out of band      → silently ignore.
        //
        // Implemented as a single ExecuteUpdateAsync with the predicate IN
        // the WHERE clause. SQL Server evaluates the tolerance check; if it
        // fails, zero rows update and we return false. No SELECT-then-UPDATE,
        // no read-modify-write. Atomic with respect to other duration
        // updates that may be racing.
        //
        // The arithmetic uses casting to double inside SQL via the
        // EF.Functions equivalents — but EF Core 10 doesn't expose a clean
        // way to do that in ExecuteUpdateAsync. So we compute the bounds
        // as parameters and build the predicate against integer columns.

        var rowsAffected = await _context.VideoAssets
             .Where(v => v.Id == videoAssetId)
             .Where(v =>
                 // First-watch claim: current value is 0 AND the teacher has
                 // NOT manually set it (Phase 5) — accept anything. Without
                 // the IsDurationManuallySet guard, a teacher explicitly
                 // setting 0 (plausible when they genuinely don't know the
                 // duration) would silently re-open first-report-wins.
                 (v.DurationSeconds == 0 && !v.IsDurationManuallySet)
                 // Subsequent (or manually-set): |reported - stored| / stored <= tolerance.
                 // Rearranged to integer-only math:
                 //   stored * (1 - tolerance) <= reported <= stored * (1 + tolerance)
                 // NOTE: if a teacher manually sets 0 (IsDurationManuallySet=true,
                 // DurationSeconds=0), this branch's bounds collapse to exactly
                 // 0, so every nonzero student report is silently ignored —
                 // that is the correct, intended behavior here, not a bug: a
                 // manually-set 0 means "stay 0," not "still unset."
                 || (reportedDurationSeconds >= v.DurationSeconds * (1 - toleranceFraction)
                  && reportedDurationSeconds <= v.DurationSeconds * (1 + toleranceFraction)))
             .ExecuteUpdateAsync(s => s
                 .SetProperty(v => v.DurationSeconds, reportedDurationSeconds));

        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> SetVideoStatusAsync(
        long videoAssetId, long teacherId, VideoStatus status, DateTime? publishDate)
    {
        int rowsAffected = await _context.VideoAssets
            .Where(v => v.Id == videoAssetId && v.TeacherId == teacherId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, status)
                .SetProperty(v => v.PublishDate, publishDate));

        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task ResetAnalyticsForVideoAsync(long videoAssetId)
    {
        // G-EDIT: sourceUrl changed — this is now a different video.
        // ExecuteDeleteAsync: single SQL DELETE per table, no in-memory
        // loading, same pattern as DeleteAllScopesForVideoAsync.
        await _context.VideoWatchEvents
            .Where(e => e.VideoAssetId == videoAssetId)
            .ExecuteDeleteAsync();

        await _context.VideoAnalytics
            .Where(a => a.VideoAssetId == videoAssetId)
            .ExecuteDeleteAsync();

        await _context.VideoAssets
            .Where(v => v.Id == videoAssetId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.DurationSeconds, 0));
    }

    // ══════════════════════════════════════════════════════════════════════
    // VIDEO ASSET — READ PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<VideoAsset?> GetVideoByIdAndTeacherAsync(long videoAssetId, long teacherId)
    {
        // Tracked: caller may pass to DeleteVideoAsync.
        return await _context.VideoAssets
            .FirstOrDefaultAsync(v => v.Id == videoAssetId && v.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<VideoAsset?> GetVideoWithScopesAsync(long videoAssetId, long teacherId)
    {
        // AsNoTracking — caller is read-only (snapshot building, manage-access view).
        return await _context.VideoAssets
            .Include(v => v.Scopes)
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == videoAssetId && v.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<TeacherVideoListRow> Items, int TotalCount)>
        GetTeacherVideosPagedAsync(long teacherId, string? search, int page, int pageSize,
            VideoStatus? status = null)
    {
        // Backed by IX_VideoAssets_TeacherId_CreatedAt (newest first).
        var query = _context.VideoAssets
            .Where(v => v.TeacherId == teacherId);

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

        // TotalOpens sums OpenCount across analytics rows (a "views" metric —
        // deliberately unscoped so historical rewatches keep counting).
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
                    .Sum(a => (int?)a.OpenCount) ?? 0,
               Status = v.Status,
               PublishDate = v.PublishDate,
               // Cover-photo registry id (resolved to PublicId + gated URL in the service).
               VideoPhotoFileId = v.VideoPhotoFileId,
               // Exam question count (0 when the video has no exam) and live attachment count —
               // both as SQL subquery aggregates so the whole list is one round trip.
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
        // resolved-audience definition as the analytics endpoint (distinct
        // students the video's scopes target; seen = those with an analytics
        // row), batched for the whole page in two grouped queries. The old
        // per-row shortcut counted scope ROWS as "students" and ALL analytics
        // rows (including out-of-scope/removed students) as "seen", so the
        // list card could show seen=311 / total=4 / unseen=0.
        var videoIds = rows.Select(r => r.Id).ToList();
        var audience = await GetAudienceCountsForVideosAsync(teacherId, videoIds);

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

    /// <summary>
    /// Batched per-video audience counts for a set of the teacher's videos.
    /// Thin delegate to <see cref="VideoAudienceQueries"/> — the audience rule
    /// is defined once there and shared with <see cref="VideoUnitRepo"/>.
    /// </summary>
    private Task<Dictionary<long, (int InScope, int Seen)>> GetAudienceCountsForVideosAsync(
        long teacherId, IReadOnlyCollection<long> videoIds)
        => VideoAudienceQueries.GetAudienceCountsForVideosAsync(_context, teacherId, videoIds);

    /// <inheritdoc />
    public Task<(IReadOnlyList<StudentVideoListRow> Items, int TotalCount)>
        GetVisibleVideosForStudentAsync(
            long teacherId, long teacherStudentId, int page, int pageSize)
        => GetStudentVisibleVideosPagedAsync(teacherId, teacherStudentId, unitId: null, page, pageSize);

    /// <inheritdoc />
    public Task<(IReadOnlyList<StudentVideoListRow> Items, int TotalCount)>
        GetVisibleVideosForStudentInUnitAsync(
            long teacherId, long teacherStudentId, long unitId, int page, int pageSize,
            string? search = null, bool unwatchedOnly = false)
        => GetStudentVisibleVideosPagedAsync(
            teacherId, teacherStudentId, unitId, page, pageSize, search, unwatchedOnly);

    /// <summary>
    /// Shared visible-videos query for the student list (V2/V4 enriched) and the V3 unit
    /// drill-down. One code path — <paramref name="unitId"/> null lists all visible videos;
    /// non-null restricts to that unit's videos. Batched; no N+1.
    /// </summary>
    private async Task<(IReadOnlyList<StudentVideoListRow> Items, int TotalCount)>
        GetStudentVisibleVideosPagedAsync(
            long teacherId, long teacherStudentId, long? unitId, int page, int pageSize,
            string? search = null, bool unwatchedOnly = false)
    {
        // Story B Q1 — implements the spec's scope union with analytics LEFT JOIN. Hot path;
        // covered by the filtered scope-target indexes on VideoScopes plus
        // IX_VideoAnalytics_TeacherStudentId_VideoAssetId on the LEFT JOIN.
        //
        // The student's session is needed to resolve Session and SessionGroup
        // scopes. We read it once and use it for the IN clauses.
        var student = await _context.TeacherStudents
            .Where(ts => ts.Id == teacherStudentId && ts.TeacherId == teacherId)
            .Select(ts => new
            {
                ts.SessionId,
                SessionGroupId = ts.Session != null ? ts.Session.SessionGroupId : null
            })
            .AsNoTracking()
            .FirstOrDefaultAsync();

        long? studentSessionId = student?.SessionId;
        long? studentSessionGroupId = student?.SessionGroupId;

        var utcNow = DateTime.UtcNow;

        // Track D1 visibility gate: Draft videos, or Published videos whose
        // PublishDate is still in the future, are excluded from the
        // student-visible set entirely (not just hidden client-side).
        var publishedVideoIds = _context.VideoAssets
            .Where(v => v.TeacherId == teacherId
                     && v.Status == VideoStatus.Published
                     && (v.PublishDate == null || v.PublishDate <= utcNow))
            .Select(v => v.Id);

        var scopeQuery = _context.VideoScopes
            .Where(s => s.TeacherId == teacherId)
            .Where(s => publishedVideoIds.Contains(s.VideoAssetId))
            .Where(s =>
                (s.ScopeType == VideoScopeType.Session
                 && studentSessionId.HasValue
                 && s.SessionId == studentSessionId)
             || (s.ScopeType == VideoScopeType.SessionGroup
                 && studentSessionGroupId.HasValue
                 && s.SessionGroupId == studentSessionGroupId));

        // V3 drill-down: restrict to videos linked to the requested unit.
        if (unitId.HasValue)
        {
            var unitVideoIds = _context.VideoAssetUnits
                .Where(vau => vau.UnitId == unitId.Value)
                .Select(vau => vau.VideoAssetId);
            scopeQuery = scopeQuery.Where(s => unitVideoIds.Contains(s.VideoAssetId));
        }

        // Title search, same Arabic-normalized LIKE the teacher's list uses so
        // both sides match the same way on the same text.
        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{ArabicTextNormalizer.Normalize(search.Trim())}%";
            var matchingIds = _context.VideoAssets
                .Where(v => v.TeacherId == teacherId
                         && EF.Functions.Like(DbSearch.ArabicNormalize(v.Title), pattern))
                .Select(v => v.Id);
            scopeQuery = scopeQuery.Where(s => matchingIds.Contains(s.VideoAssetId));
        }

        // "Not watched yet" — the same rule the row badge uses (zero watch
        // seconds, whether or not an analytics row exists), so filtering by it
        // returns exactly the rows the student sees marked that way.
        if (unwatchedOnly)
        {
            scopeQuery = scopeQuery.Where(s =>
                !_context.VideoAnalytics.Any(an => an.VideoAssetId == s.VideoAssetId
                                                && an.TeacherStudentId == teacherStudentId
                                                && an.TotalWatchSeconds > 0));
        }

        var visibleQuery = scopeQuery
            .GroupBy(s => s.VideoAssetId)
            .Select(g => new
            {
                VideoAssetId = g.Key,
                AssignedAt = g.Min(s => s.AssignedAt)
            });

        // Total before pagination — needed for PaginatedResponse. Filters above
        // are already applied, so the total always describes the rows returned.
        int totalCount = await visibleQuery.CountAsync();

        // Final projection: join the visible-set onto VideoAssets and LEFT JOIN VideoAnalytics
        // for the student's HasOpened / LastOpenedAt / TotalWatchSeconds (V4), plus per-video
        // quiz aggregates (V2) as SQL subqueries so the whole page is one round trip.
        var rows = await visibleQuery
            .OrderByDescending(v => v.AssignedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Join(_context.VideoAssets,
                  v => v.VideoAssetId,
                  a => a.Id,
                  (v, a) => new { v.AssignedAt, Asset = a })
            .GroupJoin(
                _context.VideoAnalytics.Where(an => an.TeacherStudentId == teacherStudentId),
                pair => pair.Asset.Id,
                an => an.VideoAssetId,
                (pair, ans) => new { pair.AssignedAt, pair.Asset, Analytics = ans })
            .SelectMany(
                x => x.Analytics.DefaultIfEmpty(),
                (x, an) => new StudentVideoListRow
                {
                    Id = x.Asset.Id,
                    Title = x.Asset.Title,
                    Description = x.Asset.Description,
                    SourceType = x.Asset.SourceType,
                    SourceUrl = x.Asset.SourceUrl,
                    DurationSeconds = x.Asset.DurationSeconds,
                    AssignedAt = x.AssignedAt,
                    HasOpened = an != null,
                    LastOpenedAt = an != null ? (DateTime?)an.LastUpdated : null,
                    TotalWatchSeconds = an != null ? an.TotalWatchSeconds : 0,
                    HasQuiz = _context.VideoExams.Any(e => e.VideoAssetId == x.Asset.Id),
                    QuestionsCount = _context.VideoExamQuestions
                        .Count(q => q.Exam.VideoAssetId == x.Asset.Id),
                    VideoPhotoFileId = x.Asset.VideoPhotoFileId,
                })
            .AsNoTracking()
            .ToListAsync();

        return (rows, totalCount);
    }

    /// <inheritdoc />
    public async Task<(int Total, int Seen)> GetStudentVideoSeenCountsAsync(
        long teacherId, long teacherStudentId)
    {
        // EXACT same scope predicate as GetStudentVisibleVideosPagedAsync (unitId always null
        // here — this is a whole-teacher rollup) so the totals can never disagree with what the
        // student's own video list shows. Deliberately duplicated rather than calling the paged
        // method with a huge page size: this only needs two COUNTs, not a materialized row set.
        var student = await _context.TeacherStudents
            .Where(ts => ts.Id == teacherStudentId && ts.TeacherId == teacherId)
            .Select(ts => new
            {
                ts.SessionId,
                SessionGroupId = ts.Session != null ? ts.Session.SessionGroupId : null
            })
            .AsNoTracking()
            .FirstOrDefaultAsync();

        long? studentSessionId = student?.SessionId;
        long? studentSessionGroupId = student?.SessionGroupId;

        var utcNow = DateTime.UtcNow;

        var publishedVideoIds = _context.VideoAssets
            .Where(v => v.TeacherId == teacherId
                     && v.Status == VideoStatus.Published
                     && (v.PublishDate == null || v.PublishDate <= utcNow))
            .Select(v => v.Id);

        var visibleVideoIds = _context.VideoScopes
            .Where(s => s.TeacherId == teacherId)
            .Where(s => publishedVideoIds.Contains(s.VideoAssetId))
            .Where(s =>
                (s.ScopeType == VideoScopeType.Session
                 && studentSessionId.HasValue
                 && s.SessionId == studentSessionId)
             || (s.ScopeType == VideoScopeType.SessionGroup
                 && studentSessionGroupId.HasValue
                 && s.SessionGroupId == studentSessionGroupId))
            .Select(s => s.VideoAssetId)
            .Distinct();

        int total = await visibleVideoIds.CountAsync();
        int seen = await _context.VideoAnalytics
            .Where(an => an.TeacherStudentId == teacherStudentId
                      && visibleVideoIds.Contains(an.VideoAssetId))
            .Select(an => an.VideoAssetId)
            .Distinct()
            .CountAsync();

        return (total, seen);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentVideoUnitRow>> GetStudentVisibleUnitsAsync(
        long teacherId, long teacherStudentId)
    {
        // Resolve the student's session/group once (same as the visible-videos query).
        var student = await _context.TeacherStudents
            .Where(ts => ts.Id == teacherStudentId && ts.TeacherId == teacherId)
            .Select(ts => new
            {
                ts.SessionId,
                SessionGroupId = ts.Session != null ? ts.Session.SessionGroupId : null
            })
            .AsNoTracking()
            .FirstOrDefaultAsync();

        long? studentSessionId = student?.SessionId;
        long? studentSessionGroupId = student?.SessionGroupId;

        var utcNow = DateTime.UtcNow;

        var publishedVideoIds = _context.VideoAssets
            .Where(v => v.TeacherId == teacherId
                     && v.Status == VideoStatus.Published
                     && (v.PublishDate == null || v.PublishDate <= utcNow))
            .Select(v => v.Id);

        // The SAME visible-video predicate the list uses — units and the list never disagree.
        var visibleVideoIds = _context.VideoScopes
            .Where(s => s.TeacherId == teacherId)
            .Where(s => publishedVideoIds.Contains(s.VideoAssetId))
            .Where(s =>
                (s.ScopeType == VideoScopeType.Session
                 && studentSessionId.HasValue
                 && s.SessionId == studentSessionId)
             || (s.ScopeType == VideoScopeType.SessionGroup
                 && studentSessionGroupId.HasValue
                 && s.SessionGroupId == studentSessionGroupId))
            .Select(s => s.VideoAssetId)
            .Distinct();

        // One flat query (unit × visible video + hasQuiz), grouped in memory — no N+1.
        // VideoUnit's global soft-delete query filter applies automatically.
        var flat = await (
            from vau in _context.VideoAssetUnits
            where visibleVideoIds.Contains(vau.VideoAssetId)
            join u in _context.VideoUnits on vau.UnitId equals u.Id
            join asset in _context.VideoAssets on vau.VideoAssetId equals asset.Id
            where u.TeacherId == teacherId
            select new
            {
                u.Id,
                u.Title,
                u.Description,
                vau.VideoAssetId,
                HasQuiz = _context.VideoExams.Any(e => e.VideoAssetId == vau.VideoAssetId),
                // Duration comes off the joined asset — a correlated subquery here
                // would be one extra APPLY per (unit, video) row on a hot student
                // path. Watch seconds stay a typed nullable subquery (there may be
                // no analytics row); kept nullable and coalesced in memory, because
                // a bare null branch against a subquery has no type mapping and
                // breaks query compilation (BUG-7).
                DurationSeconds = asset.DurationSeconds,
                WatchSeconds = _context.VideoAnalytics
                    .Where(an => an.VideoAssetId == vau.VideoAssetId
                              && an.TeacherStudentId == teacherStudentId)
                    .Select(an => (long?)an.TotalWatchSeconds)
                    .FirstOrDefault(),
            })
            .AsNoTracking()
            .ToListAsync();

        return flat
            .GroupBy(r => new { r.Id, r.Title, r.Description })
            .Select(g => new StudentVideoUnitRow
            {
                Id = g.Key.Id,
                Title = g.Key.Title,
                Description = g.Key.Description,
                VideoCount = g.Select(x => x.VideoAssetId).Distinct().Count(),
                QuizVideoCount = g.Where(x => x.HasQuiz).Select(x => x.VideoAssetId).Distinct().Count(),
                // Distinct FIRST: a video linked to the unit twice would
                // otherwise count twice here while VideoCount counted it once,
                // and the unit could read "3 of 2 watched".
                WatchedVideoCount = g
                    .GroupBy(x => x.VideoAssetId)
                    .Count(vg => VideoWatchRules.IsWatched(
                        vg.First().WatchSeconds ?? 0,
                        vg.First().DurationSeconds)),
            })
            .OrderBy(u => u.Title)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetAudienceTeacherStudentIdsAsync(
        long teacherId, long videoAssetId)
        => await GetResolvedStudentIdsForVideoQuery(teacherId, videoAssetId)
            .Distinct()
            .ToListAsync();

    /// <inheritdoc />
    public async Task<TeacherSubjectInfo?> GetTeacherSubjectAsync(long teacherId)
    {
        return await _context.Teachers
            .Where(t => t.Id == teacherId)
            .Select(t => new TeacherSubjectInfo
            {
                CustomSubject = t.CustomSubject,
                SubjectNameEn = t.TeacherSubjects
                    .Select(ts => ts.Subject.NameEn).FirstOrDefault(),
                SubjectNameAr = t.TeacherSubjects
                    .Select(ts => ts.Subject.NameAr).FirstOrDefault(),
            })
            .AsNoTracking()
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudentVideoExamQuestionRow>> GetStudentVideoExamQuestionsAsync(long videoAssetId)
    {
        // Dedicated no-IsCorrect projection (security by shape). Degree is a constant 1 —
        // video questions are equally weighted (VideoExamQuestion has no Degree column).
        return await _context.VideoExamQuestions
            .Where(q => q.Exam.VideoAssetId == videoAssetId)
            .OrderBy(q => q.SortOrder)
            .Select(q => new StudentVideoExamQuestionRow
            {
                Id = q.Id,
                QuestionText = q.Text,
                QuestionType = q.QuestionType,
                Degree = 1m,
                SortOrder = q.SortOrder,
                ImageFileInternalId = q.ImageFileId,
                Options = q.Options
                    .OrderBy(o => o.SortOrder)
                    .Select(o => new StudentVideoExamQuestionOptionRow
                    {
                        Id = o.Id,
                        OptionText = o.Text,
                        SortOrder = o.SortOrder,
                    })
                    .ToList(),
            })
            .AsNoTracking()
            .ToListAsync();
    }

    // ══════════════════════════════════════════════════════════════════════
    // SCOPE — WRITE PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddScopesAsync(IEnumerable<VideoScope> scopes)
    {
        await _context.VideoScopes.AddRangeAsync(scopes);
    }

    /// <inheritdoc />
    public async Task DeleteAllScopesForVideoAsync(long videoAssetId)
    {
        // PUT-replace flow: drop all old scopes inside the service's
        // transaction before inserting the new set. ExecuteDeleteAsync —
        // single SQL DELETE, no in-memory loading.
        await _context.VideoScopes
            .Where(s => s.VideoAssetId == videoAssetId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task DeleteScopesBySessionAsync(long sessionId)
    {
        await _context.VideoScopes
            .Where(s => s.SessionId == sessionId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task DeleteScopesByGroupAsync(long sessionGroupId)
    {
        await _context.VideoScopes
            .Where(s => s.SessionGroupId == sessionGroupId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteScopeByIdAndTeacherAsync(long scopeId, long teacherId)
    {
        // Single-scope DELETE with tenant guard inline. Returns false when
        // the row doesn't exist OR belongs to a different teacher — caller
        // distinguishes via the surrounding service logic.
        var rowsAffected = await _context.VideoScopes
            .Where(s => s.Id == scopeId && s.TeacherId == teacherId)
            .ExecuteDeleteAsync();

        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task<int> CountScopesForVideoAsync(long videoAssetId)
    {
        return await _context.VideoScopes
            .CountAsync(s => s.VideoAssetId == videoAssetId);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SCOPE — VALIDATION
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<bool> IsScopeTargetOwnedByTeacherAsync(
        long teacherId, VideoScopeType scopeType, long targetId)
    {
        // Three branches, one per scope type. Each branch hits a single
        // tenant-scoped index lookup. No fall-through default — callers
        // pass valid enum values; an unrecognized value returns false.
        return scopeType switch
        {
          

            VideoScopeType.Session => await _context.Sessions
                .AnyAsync(se => se.Id == targetId && se.TeacherId == teacherId),

            VideoScopeType.SessionGroup => await _context.SessionGroups
                .AnyAsync(sg => sg.Id == targetId && sg.TeacherId == teacherId),

            _ => false,
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // ACCESS CHECK — student-facing endpoints
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<bool> IsStudentInVideoScopeAsync(
        long teacherStudentId, long videoAssetId, long teacherId)
    {
        // BR-VCM-01 enforcement gate. Resolves the union of three scope
        // shapes; returns true if ANY scope row matches.
        //
        // Need the student's session and group to evaluate Session/Group
        // scopes. One round trip to fetch both, then one round trip for the
        // EXISTS check. Total: 2 queries, sub-millisecond each.
        var student = await _context.TeacherStudents
            .Where(ts => ts.Id == teacherStudentId && ts.TeacherId == teacherId)
            .Select(ts => new
            {
                ts.SessionId,
                SessionGroupId = ts.Session != null ? ts.Session.SessionGroupId : null
            })
            .AsNoTracking()
            .FirstOrDefaultAsync();

        if (student is null)
            return false;

        long? sessionId = student.SessionId;
        long? sessionGroupId = student.SessionGroupId;

        // Track D1 visibility gate: Draft videos, or Published videos whose
        // PublishDate is still in the future, are never "in scope" for a
        // student — same posture as the ModuleDeactivated gate.
        var utcNow = DateTime.UtcNow;
        bool isPublished = await _context.VideoAssets
            .AnyAsync(v => v.Id == videoAssetId
                        && v.TeacherId == teacherId
                        && v.Status == VideoStatus.Published
                        && (v.PublishDate == null || v.PublishDate <= utcNow));
        if (!isPublished)
            return false;

        return await _context.VideoScopes
            .AnyAsync(s =>
                s.VideoAssetId == videoAssetId
             && s.TeacherId == teacherId
             && (
                   
                 (s.ScopeType == VideoScopeType.Session
                     && sessionId.HasValue && s.SessionId == sessionId)
                 || (s.ScopeType == VideoScopeType.SessionGroup
                     && sessionGroupId.HasValue && s.SessionGroupId == sessionGroupId)
                ));
    }

    // ══════════════════════════════════════════════════════════════════════
    // ANALYTICS — WRITE PATH
    // ══════════════════════════════════════════════════════════════════════
    // <inheritdoc />
    public async Task<VideoAnalyticsSnapshot?> IncrementOpenCountIfExistsAsync(
        long videoAssetId, long teacherStudentId, DateTime utcNow)
    {
        // ExecuteUpdateAsync writes immediately — no SaveChanges needed for this
        // single-statement update. SQL Server takes a row-level X lock; concurrent
        // increments serialize with sub-millisecond contention.
        int rowsUpdated = await _context.VideoAnalytics
            .Where(a => a.VideoAssetId == videoAssetId
                     && a.TeacherStudentId == teacherStudentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.OpenCount, a => a.OpenCount + 1)
                .SetProperty(a => a.LastUpdated, utcNow));

        if (rowsUpdated == 0)
        {
            // No row exists yet — caller should INSERT a fresh row via
            // AddAnalyticsRowForFirstOpenAsync.
            return null;
        }

        // Read the resulting state for the response. Single seek on the unique
        // index UX_VideoAnalytics_Video_Student.
        return await _context.VideoAnalytics
            .Where(a => a.VideoAssetId == videoAssetId
                     && a.TeacherStudentId == teacherStudentId)
            .Select(a => new VideoAnalyticsSnapshot
            {
                OpenCount = a.OpenCount,
                TotalWatchSeconds = a.TotalWatchSeconds,
                LastResumePositionSeconds = a.LastResumePositionSeconds,
            })
            .AsNoTracking()
            .FirstAsync();
    }

    /// <inheritdoc />
    public async Task AddAnalyticsRowForFirstOpenAsync(VideoAnalytics row)
    {
        // Queue the INSERT into the change tracker. SaveChanges is the service's
        // responsibility — see VideoService.UpsertAnalyticsOnOpenWithRetryAsync
        // for the bounded retry loop that handles unique-violation races.
        await _context.VideoAnalytics.AddAsync(row);
    }

    /// <inheritdoc />
    public async Task<VideoAnalyticsSnapshot?> GetAnalyticsSnapshotAsync(
        long videoAssetId, long teacherStudentId)
    {
        // Pure read — used by idempotency-replay paths. Single seek on the
        // unique index UX_VideoAnalytics_Video_Student.
        return await _context.VideoAnalytics
            .Where(a => a.VideoAssetId == videoAssetId
                     && a.TeacherStudentId == teacherStudentId)
            .Select(a => new VideoAnalyticsSnapshot
            {
                OpenCount = a.OpenCount,
                TotalWatchSeconds = a.TotalWatchSeconds,
                LastResumePositionSeconds = a.LastResumePositionSeconds,
            })
            .AsNoTracking()
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<VideoAnalyticsIncrementResult?> IncrementWatchAtomicAsync(
        long videoAssetId, long teacherStudentId,
        long acceptedDeltaSeconds, int positionSeconds, DateTime utcNow)
    {
        // Story C atomic UPSERT — single SQL UPDATE with SQL-side increment.
        // SQL Server takes a row-level X lock; concurrent stops from
        // multiple devices serialize and both deltas land correctly.
        int rowsUpdated = await _context.VideoAnalytics
            .Where(a => a.VideoAssetId == videoAssetId
                     && a.TeacherStudentId == teacherStudentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.TotalWatchSeconds,
                             a => a.TotalWatchSeconds + acceptedDeltaSeconds)
                .SetProperty(a => a.LastResumePositionSeconds, positionSeconds)
                .SetProperty(a => a.LastUpdated, utcNow));

        if (rowsUpdated == 0)
        {
            // No matching row — caller treats as NoActiveSession.
            return null;
        }

        // Read the resulting state for the response. Single seek on the
        // unique index UX_VideoAnalytics_Video_Student.
        return await _context.VideoAnalytics
            .Where(a => a.VideoAssetId == videoAssetId
                     && a.TeacherStudentId == teacherStudentId)
            .Select(a => new VideoAnalyticsIncrementResult
            {
                TotalWatchSeconds = a.TotalWatchSeconds,
                LastResumePositionSeconds = a.LastResumePositionSeconds,
            })
            .AsNoTracking()
            .FirstAsync();
    }

    // ══════════════════════════════════════════════════════════════════════
    // ANALYTICS — READ PATH
    // ══════════════════════════════════════════════════════════════════════
    /// <summary>
    /// The resolved-students set for a video = the students the video's OWN
    /// VideoScope rows target (session / group; the TeacherStudentId branch is
    /// dead — VideoScopeType has no IndividualStudent value — kept harmless for
    /// legacy data). Unit scope is deliberately NOT unioned in: under the boundary
    /// design a video's audience is defined by its own scope (which is guaranteed
    /// to sit within its units' scope), so counting the whole unit boundary would
    /// over-count students who can't actually see the video. Shared by
    /// GetAnalyticsRowsForTeacherAsync and GetAnalyticsAggregatesAsync so the two
    /// never disagree on who's "in scope" for the same video.
    /// </summary>
    private IQueryable<long> GetResolvedStudentIdsForVideoQuery(long teacherId, long videoAssetId)
    {
        // Single-video convenience over the batched pairs query in
        // VideoAudienceQueries — ONE source of truth for the "resolved
        // audience" definition, shared with VideoUnitRepo.
        return VideoAudienceQueries
            .ResolvedStudentPairsForVideos(_context, teacherId, new[] { videoAssetId })
            .Select(p => p.TeacherStudentId);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<VideoAnalyticsReportRow> Items, int TotalCount)>
        GetAnalyticsRowsForTeacherAsync(
            long teacherId, long videoAssetId, string? search,
            VideoAnalyticsSortBy sortBy, SortDirection sortDirection,
            VideoAnalyticsStatusFilter statusFilter,
            int page, int pageSize)
    {
        // Story F report. Build the resolved-students set in SQL via the
        // three-way scope UNION, then LEFT JOIN VideoAnalytics so students
        // who have never opened the video still appear with HasOpened=false.
        //
        // We need the video's duration for the completion-percentage math,
        // and also to bound the report ("you can't have analytics for a
        // video the teacher doesn't own"). One small lookup.
        var video = await _context.VideoAssets
            .Where(v => v.Id == videoAssetId && v.TeacherId == teacherId)
            .Select(v => new { v.DurationSeconds })
            .AsNoTracking()
            .FirstOrDefaultAsync();

        if (video is null)
        {
            return (Array.Empty<VideoAnalyticsReportRow>(), 0);
        }

        int durationSeconds = video.DurationSeconds;

        // Resolved-students set: video's own scope UNION its unit's scope
        // (if any) — see GetResolvedStudentIdsForVideoQuery.
        var resolvedStudentIds = GetResolvedStudentIdsForVideoQuery(teacherId, videoAssetId);

        // Project each resolved student joined with their analytics row

        // Project each resolved student joined with their analytics row
        // (LEFT JOIN). Compute estimatedCompletionPct and rawWatchPct in
        // SQL so server-side sorting on those columns works without
        // re-projection.
        var rowsQuery =
            from sid in resolvedStudentIds
            join ts in _context.TeacherStudents on sid equals ts.Id
            join an in _context.VideoAnalytics
                    .Where(x => x.VideoAssetId == videoAssetId)
                on ts.Id equals an.TeacherStudentId into anGroup
            from an in anGroup.DefaultIfEmpty()
            // sessionName (Track D1 §5): the student's active session
            // assignment. A student has at most one active assignment at a
            // time (BR-ATT precedent), so DefaultIfEmpty + FirstOrDefault-style
            // left join is safe without a dedup step.
            join ssa in _context.StudentSessionAssignments
                    .Where(x => x.IsActive)
                on ts.Id equals ssa.TeacherStudentId into ssaGroup
            from ssa in ssaGroup.DefaultIfEmpty()
            select new VideoAnalyticsReportRow
            {
                TeacherStudentId = ts.Id,
                StudentName = ts.StudentName,
                StudentCode = ts.StudentCode,
                SessionName = ssa != null ? ssa.SessionName : null,
                HasOpened = an != null,
                OpenCount = an != null ? an.OpenCount : 0,
                TotalWatchSeconds = an != null ? an.TotalWatchSeconds : 0L,
                VideoDurationSeconds = durationSeconds,
                FirstOpenedAt = an != null ? (DateTime?)an.FirstOpenedAt : null,
                LastOpenedAt = an != null ? (DateTime?)an.LastUpdated : null,
                EstimatedCompletionPct = durationSeconds == 0
                    ? (int?)null
                    : (int?)(an != null
                        ? (an.TotalWatchSeconds * 100 / durationSeconds > 100
                            ? 100
                            : (int)(an.TotalWatchSeconds * 100 / durationSeconds))
                        : 0),
                RawWatchPct = durationSeconds == 0
                    ? (int?)null
                    : (int?)(an != null
                        ? (int)(an.TotalWatchSeconds * 100 / durationSeconds)
                        : 0),
            };

        // Optional search on student name or code.
        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{ArabicTextNormalizer.Normalize(search.Trim())}%";
            rowsQuery = rowsQuery.Where(r =>
                EF.Functions.Like(DbSearch.ArabicNormalize(r.StudentName), pattern)
             || EF.Functions.Like(DbSearch.ArabicNormalize(r.StudentCode), pattern));
        }

        // G-ANL-4: server-side status filter. Same 90%-threshold definition
        // of "completed" as GetAnalyticsAggregatesAsync's CompletedCount.
        rowsQuery = statusFilter switch
        {
            VideoAnalyticsStatusFilter.Seen => rowsQuery.Where(r => r.HasOpened),
            VideoAnalyticsStatusFilter.Unseen => rowsQuery.Where(r => !r.HasOpened),
            VideoAnalyticsStatusFilter.Completed => rowsQuery.Where(r =>
                r.EstimatedCompletionPct != null
             && r.EstimatedCompletionPct >= VideoConstants.CompletionThresholdPercent),
            _ => rowsQuery,
        };

        int totalCount = await rowsQuery.CountAsync();

        // Apply sort. EstimatedCompletionPct may be null when duration is
        // unknown — those rows sort last in either direction (we sort by
        // raw int treating null as 0).
        rowsQuery = ApplySort(rowsQuery, sortBy, sortDirection);

        var items = await rowsQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<VideoAnalyticsAggregates> GetAnalyticsAggregatesAsync(
        long teacherId, long videoAssetId)
    {
        // TotalStudentsInScope: video's own scope UNION its unit's scope —
        // same shared helper GetAnalyticsRowsForTeacherAsync uses, so the
        // two can never disagree.
        // TotalStudentsWatched: resolved students holding an analytics row.
        // Intersected with the CURRENT audience (not a bare per-video row
        // count) so Watched + Unseen == InScope and the header always
        // reconciles with the report rows — students who watched but were
        // later removed from the video's sessions keep their row history but
        // no longer count here.
        var resolvedStudentIds = GetResolvedStudentIdsForVideoQuery(teacherId, videoAssetId);

        int totalInScope = await resolvedStudentIds.CountAsync();

        int totalWatched = await resolvedStudentIds
            .Join(_context.VideoAnalytics.Where(a => a.VideoAssetId == videoAssetId),
                  sid => sid,
                  a => a.TeacherStudentId,
                  (sid, a) => sid)
            .CountAsync();

        // G-ANL-1: completedCount — resolved students whose completion meets
        // the threshold. Needs the video's duration; 0 means "unknown", in
        // which case no student can be Completed yet.
        int durationSeconds = await _context.VideoAssets
            .Where(v => v.Id == videoAssetId && v.TeacherId == teacherId)
            .Select(v => v.DurationSeconds)
            .FirstOrDefaultAsync();

        int completedCount = 0;
        if (durationSeconds > 0)
        {
            completedCount = await resolvedStudentIds
                .Join(_context.VideoAnalytics.Where(a => a.VideoAssetId == videoAssetId),
                      sid => sid,
                      a => a.TeacherStudentId,
                      (sid, a) => a.TotalWatchSeconds)
                .CountAsync(watchSeconds =>
                    (watchSeconds * 100 / durationSeconds) >= VideoConstants.CompletionThresholdPercent);
        }

        return new VideoAnalyticsAggregates
        {
            TotalStudentsInScope = totalInScope,
            TotalStudentsWatched = totalWatched,
            UnseenCount = Math.Max(0, totalInScope - totalWatched),
            CompletedCount = completedCount,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VideoAnalyticsAuditRow>> GetAnalyticsForAuditSnapshotAsync(
        long videoAssetId)
    {
        // Snapshot rows: every VideoAnalytics row for the video, joined to
        // TeacherStudents to denormalize StudentName/StudentCode at snapshot
        // time. The student row may be permanently purged later — the
        // snapshot keeps display data for forensic review.
        return await _context.VideoAnalytics
            .Where(a => a.VideoAssetId == videoAssetId)
            .Join(_context.TeacherStudents.IgnoreQueryFilters(),
                  a => a.TeacherStudentId,
                  ts => ts.Id,
                  (a, ts) => new VideoAnalyticsAuditRow
                  {
                      TeacherStudentId = a.TeacherStudentId,
                      StudentName = ts.StudentName,
                      StudentCode = ts.StudentCode,
                      OpenCount = a.OpenCount,
                      TotalWatchSeconds = a.TotalWatchSeconds,
                      FirstOpenedAt = a.FirstOpenedAt,
                      LastUpdated = a.LastUpdated,
                      LastResumePositionSeconds = a.LastResumePositionSeconds,
                  })
            .AsNoTracking()
            .ToListAsync();
    }

    // ══════════════════════════════════════════════════════════════════════
    // WATCH EVENT — WRITE PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddWatchEventAsync(VideoWatchEvent watchEvent)
    {
        await _context.VideoWatchEvents.AddAsync(watchEvent);
    }

    // ══════════════════════════════════════════════════════════════════════
    // WATCH EVENT — READ PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<DateTime?> GetLastEventUtcForDeviceAsync(
        long teacherStudentId, long videoAssetId, string deviceId)
    {
        // Story C anchor lookup. Backed by IX_VWE_Student_Video_Device_TimeDesc:
        // single seek + TOP 1.
        return await _context.VideoWatchEvents
            .Where(e => e.TeacherStudentId == teacherStudentId
                     && e.VideoAssetId == videoAssetId
                     && e.DeviceId == deviceId)
            .OrderByDescending(e => e.EventUtc)
            .Select(e => (DateTime?)e.EventUtc)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<bool> HasEventWithClientIdAsync(Guid clientEventId)
    {
        // Backed by UX_VWE_ClientEventId (filtered unique). Sub-millisecond.
        return await _context.VideoWatchEvents
            .AnyAsync(e => e.ClientEventId == clientEventId);
    }

    // ══════════════════════════════════════════════════════════════════════
    // AUDIT — WRITE PATH
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddAuditAsync(VideoAssetAudit audit)
    {
        await _context.VideoAssetAudits.AddAsync(audit);
    }

    // ══════════════════════════════════════════════════════════════════════
    // INTEGRATION HOOKS
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task DeleteAllVideosForTeacherAsync(long teacherId)
    {
        // Phase 2.2 NoAction-graph decision: Teachers → VideoAssets is
        // NO_ACTION. Admin teacher-purge flow calls this to clear VCM.
        // ExecuteDeleteAsync — single SQL DELETE; NoAction FKs cover scopes,
        // analytics, watch events. Audits are kept (no FK back).
        await _context.VideoAssets
            .Where(v => v.TeacherId == teacherId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task PurgeStudentVideoDataAsync(long teacherStudentId)
    {
        // Order is leaf-first. Every FK below is NoAction, so SQL Server cleans NONE of
        // them on the student's hard delete — without this the purge 500s for any student
        // who ever opened a video (see IVideoAssetRepo remarks).

        // Watch events: aggregate-detail rows, non-nullable student FK.
        await _context.VideoWatchEvents
            .Where(e => e.TeacherStudentId == teacherStudentId)
            .ExecuteDeleteAsync();

        // Per-student analytics rollup, non-nullable student FK.
        await _context.VideoAnalytics
            .Where(a => a.TeacherStudentId == teacherStudentId)
            .ExecuteDeleteAsync();

        // Per-student video scope rows: nullable FK, but nulling would leave a Student-typed
        // scope with no target — delete the row (the video keeps its session/group scopes).
        await _context.VideoScopes
            .Where(s => s.TeacherStudentId == teacherStudentId)
            .ExecuteDeleteAsync();

        // Video-quiz attempts. TeacherStudentId here is a plain scalar (no FK), so it does not
        // block the delete — it is purged for the same "no orphan attempt" reason as the
        // online-exam reports. Answers/options cascade at the DB level.
        await _context.StudentVideoExamReports
            .Where(r => r.TeacherStudentId == teacherStudentId)
            .ExecuteDeleteAsync();
    }

    // ══════════════════════════════════════════════════════════════════════
    // ATTACHMENTS (Track F / §5)
    // ══════════════════════════════════════════════════════════════════════

    // Attachments are now FileObjects (central registry) filtered by VideoAssetId + category —
    // see IFileObjectRepo.GetVideoAttachmentsAsync. The old VideoAttachment table is gone.

    /// <inheritdoc />
    public async Task<long?> GetOwningVideoAssetIdForFileAsync(
        long fileObjectId, FileCategory category, long teacherId)
    {
        switch (category)
        {
            case FileCategory.VideoPhoto:
                return await _context.VideoAssets
                    .Where(v => v.VideoPhotoFileId == fileObjectId && v.TeacherId == teacherId)
                    .Select(v => (long?)v.Id)
                    .FirstOrDefaultAsync();

            case FileCategory.VideoExamQuestionImage:
                return await _context.VideoExamQuestions
                    .Where(q => q.ImageFileId == fileObjectId && q.Exam.TeacherId == teacherId)
                    .Select(q => (long?)q.Exam.VideoAssetId)
                    .FirstOrDefaultAsync();

            default:
                // Attachments carry FileObject.VideoAssetId directly (no lookup needed);
                // any other category has no owning video.
                return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // UNIT LINKS — WRITE/READ PATH (M:N, Phase 1 VideoAssetUnit join table)
    // ══════════════════════════════════════════════════════════════════════

  

    // ══════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies the requested sort to the analytics row query. Same pattern
    /// used by <see cref="TeacherStudentRepo"/> for student sorts.
    /// </summary>
    private static IQueryable<VideoAnalyticsReportRow> ApplySort(
        IQueryable<VideoAnalyticsReportRow> query,
        VideoAnalyticsSortBy sortBy,
        SortDirection sortDirection)
    {
        bool ascending = sortDirection == SortDirection.Asc;

        return sortBy switch
        {
            VideoAnalyticsSortBy.OpenCount => ascending
                ? query.OrderBy(r => r.OpenCount)
                : query.OrderByDescending(r => r.OpenCount),

            VideoAnalyticsSortBy.TotalWatchSeconds => ascending
                ? query.OrderBy(r => r.TotalWatchSeconds)
                : query.OrderByDescending(r => r.TotalWatchSeconds),

            VideoAnalyticsSortBy.EstimatedCompletionPct => ascending
                ? query.OrderBy(r => r.EstimatedCompletionPct ?? 0)
                : query.OrderByDescending(r => r.EstimatedCompletionPct ?? 0),

            VideoAnalyticsSortBy.LastOpenedAt => ascending
                ? query.OrderBy(r => r.LastOpenedAt)
                : query.OrderByDescending(r => r.LastOpenedAt),

            // Default: StudentName.
            _ => ascending
                ? query.OrderBy(r => r.StudentName)
                : query.OrderByDescending(r => r.StudentName),
        };
    }
    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetExamQuestionImageFileIdsAsync(long videoAssetId)
    {
        return await _context.VideoExamQuestions
            .Where(q => q.Exam.VideoAssetId == videoAssetId && q.ImageFileId != null)
            .Select(q => q.ImageFileId!.Value)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task AddExamAsync(VideoExam exam)
    {
        await _context.VideoExams.AddAsync(exam);
    }
    /// <inheritdoc />
    public async Task DeleteExamForVideoAsync(long videoAssetId)
    {
        await _context.VideoExams
            .Where(e => e.VideoAssetId == videoAssetId)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task<VideoExam?> GetExamWithQuestionsAsync(long videoAssetId, long teacherId)
    {
        return await _context.VideoExams
            .Where(e => e.VideoAssetId == videoAssetId && e.TeacherId == teacherId)
            .Include(e => e.Questions.OrderBy(q => q.SortOrder))
                .ThenInclude(q => q.Options.OrderBy(o => o.SortOrder))
            .AsNoTracking()
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<int> GetExamQuestionCountAsync(long videoAssetId)
    {
        return await _context.VideoExamQuestions
            .CountAsync(q => q.Exam.VideoAssetId == videoAssetId);
    }

    /// <inheritdoc />
    public async Task<VideoExamHeaderRow?> GetVideoExamHeaderAsync(long videoAssetId, long teacherId)
    {
        return await _context.VideoExams
            .Where(e => e.VideoAssetId == videoAssetId && e.TeacherId == teacherId)
            .Select(e => new VideoExamHeaderRow
            {
                ExamId = e.Id,
                Title = e.Title,
                Description = e.Description,
            })
            .AsNoTracking()
            .FirstOrDefaultAsync();
    }
}
