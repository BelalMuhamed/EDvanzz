using Edvanz.Domain.Enums;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories.Queries;

/// <summary>
/// EF-translatable projection shape for
/// <see cref="VideoAudienceQueries.ResolvedStudentPairsForVideos"/> (UNION
/// requires a named type with an identical member shape on every branch).
/// </summary>
internal sealed class VideoAudiencePair
{
    public long VideoAssetId { get; set; }
    public long TeacherStudentId { get; set; }
}

/// <summary>
/// THE single definition of a video's "resolved audience", shared by
/// <see cref="VideoAssetRepo"/> (top-level teacher list + analytics) and
/// <see cref="VideoUnitRepo"/> (unit rollup card + the videos-in-unit list).
/// <para>
/// It exists as one helper on purpose: the counts on the Videos tab and the
/// counts on the same video opened through its unit used to be computed by two
/// different rules, so the same video reported different seen/unseen numbers
/// depending on which screen you reached it from. Extend this file — never
/// re-implement the audience rule in a repo.
/// </para>
/// </summary>
internal static class VideoAudienceQueries
{
    /// <summary>
    /// Distinct (VideoAssetId, TeacherStudentId) audience pairs for a SET of
    /// videos: the students each video's OWN <c>VideoScope</c> rows target
    /// (session / group; the TeacherStudentId branch is dead — VideoScopeType
    /// has no IndividualStudent value — kept harmless for legacy data).
    /// <para>
    /// Unit scope is deliberately NOT unioned in: under the boundary design a
    /// video's audience is defined by its own scope (guaranteed to sit within
    /// its units' scope), so counting the whole unit boundary would over-count
    /// students who cannot actually open the video.
    /// </para>
    /// <para>
    /// UNION dedupes pairs, so a student reachable through BOTH a session scope
    /// and a group scope counts once. TeacherStudents' global soft-delete filter
    /// applies, so deleted students never inflate the audience — which is also
    /// why "seen" must be an INTERSECTION with this set rather than a raw
    /// <c>VideoAnalytics</c> count (analytics rows deliberately outlive both
    /// scope removal and student deletion).
    /// </para>
    /// </summary>
    public static IQueryable<VideoAudiencePair> ResolvedStudentPairsForVideos(
        EdvanzDbContext context, long teacherId, IReadOnlyCollection<long> videoAssetIds)
    {
        var individualScope = context.VideoScopes
            .Where(s => videoAssetIds.Contains(s.VideoAssetId) && s.TeacherStudentId.HasValue)
            .Select(s => new VideoAudiencePair
            {
                VideoAssetId = s.VideoAssetId,
                TeacherStudentId = s.TeacherStudentId!.Value,
            });

        var sessionScope = context.VideoScopes
            .Where(s => videoAssetIds.Contains(s.VideoAssetId)
                     && s.ScopeType == VideoScopeType.Session
                     && s.SessionId.HasValue)
            .Join(context.TeacherStudents,
                  s => s.SessionId,
                  ts => ts.SessionId,
                  (s, ts) => new { s.VideoAssetId, ts.Id, ts.TeacherId })
            .Where(x => x.TeacherId == teacherId)
            .Select(x => new VideoAudiencePair
            {
                VideoAssetId = x.VideoAssetId,
                TeacherStudentId = x.Id,
            });

        var groupScope = context.VideoScopes
            .Where(s => videoAssetIds.Contains(s.VideoAssetId)
                     && s.ScopeType == VideoScopeType.SessionGroup
                     && s.SessionGroupId.HasValue)
            .Join(context.Sessions,
                  s => s.SessionGroupId,
                  se => se.SessionGroupId,
                  (s, se) => new { s.VideoAssetId, SessionId = se.Id })
            .Join(context.TeacherStudents,
                  x => x.SessionId,
                  ts => ts.SessionId,
                  (x, ts) => new { x.VideoAssetId, ts.Id, ts.TeacherId })
            .Where(x => x.TeacherId == teacherId)
            .Select(x => new VideoAudiencePair
            {
                VideoAssetId = x.VideoAssetId,
                TeacherStudentId = x.Id,
            });

        return individualScope
            .Union(sessionScope)
            .Union(groupScope);
    }

    /// <summary>
    /// Batched PER-VIDEO audience counts for a set of the teacher's videos:
    /// <c>InScope</c> = distinct students the video's scopes resolve to;
    /// <c>Seen</c> = the subset of those students holding a
    /// <c>VideoAnalytics</c> row (opened at least once). Two grouped queries for
    /// the whole page — never a per-row subquery.
    /// </summary>
    public static async Task<Dictionary<long, (int InScope, int Seen)>> GetAudienceCountsForVideosAsync(
        EdvanzDbContext context, long teacherId, IReadOnlyCollection<long> videoIds)
    {
        var result = new Dictionary<long, (int InScope, int Seen)>();
        if (videoIds.Count == 0) return result;

        var pairs = ResolvedStudentPairsForVideos(context, teacherId, videoIds);

        var inScope = await pairs
            .GroupBy(p => p.VideoAssetId)
            .Select(g => new { VideoAssetId = g.Key, Count = g.Count() })
            .ToListAsync();

        var seen = await pairs
            .Join(context.VideoAnalytics,
                  p => new { p.VideoAssetId, p.TeacherStudentId },
                  a => new { a.VideoAssetId, a.TeacherStudentId },
                  (p, a) => p)
            .GroupBy(p => p.VideoAssetId)
            .Select(g => new { VideoAssetId = g.Key, Count = g.Count() })
            .ToListAsync();

        var seenById = seen.ToDictionary(x => x.VideoAssetId, x => x.Count);
        foreach (var entry in inScope)
        {
            result[entry.VideoAssetId] =
                (entry.Count, seenById.TryGetValue(entry.VideoAssetId, out var s) ? s : 0);
        }

        return result;
    }

    /// <summary>
    /// Batched PER-UNIT audience counts, rolled up across the units' member
    /// videos: <c>InScope</c> = DISTINCT students reachable by any member video;
    /// <c>Seen</c> = distinct students who have opened AT LEAST ONE member
    /// video.
    /// <para>
    /// The distinct-on-student step is the point: a student who opened four
    /// videos in the unit must count ONCE. The old rollup counted raw analytics
    /// rows, so that student read as four.
    /// </para>
    /// <para>
    /// Three round trips for the whole page (member ids + two grouped
    /// aggregates) — never per-unit, and only ever for the units on the
    /// materialised page.
    /// </para>
    /// </summary>
    public static async Task<Dictionary<long, (int InScope, int Seen)>> GetAudienceCountsForUnitsAsync(
        EdvanzDbContext context, long teacherId, IReadOnlyCollection<long> unitIds)
    {
        var result = new Dictionary<long, (int InScope, int Seen)>();
        if (unitIds.Count == 0) return result;

        // Member videos of the page's units. Cheap seek on VideoAssetUnits.UnitId;
        // a unit with no videos simply contributes nothing and stays absent from
        // the dictionary (the caller reads that as 0/0).
        var memberVideoIds = await context.VideoAssetUnits
            .Where(au => unitIds.Contains(au.UnitId))
            .Select(au => au.VideoAssetId)
            .Distinct()
            .ToListAsync();

        if (memberVideoIds.Count == 0) return result;

        var pairs = ResolvedStudentPairsForVideos(context, teacherId, memberVideoIds);

        // (UnitId, VideoAssetId, TeacherStudentId) triples. The VideoAssetId is
        // carried because "seen" joins analytics per VIDEO before collapsing to
        // distinct students per UNIT.
        var unitPairs = context.VideoAssetUnits
            .Where(au => unitIds.Contains(au.UnitId))
            .Join(pairs,
                  au => au.VideoAssetId,
                  p => p.VideoAssetId,
                  (au, p) => new { au.UnitId, p.VideoAssetId, p.TeacherStudentId });

        var inScope = await unitPairs
            .Select(x => new { x.UnitId, x.TeacherStudentId })
            .Distinct()
            .GroupBy(x => x.UnitId)
            .Select(g => new { UnitId = g.Key, Count = g.Count() })
            .ToListAsync();

        var seen = await unitPairs
            .Join(context.VideoAnalytics,
                  x => new { x.VideoAssetId, x.TeacherStudentId },
                  a => new { a.VideoAssetId, a.TeacherStudentId },
                  (x, a) => new { x.UnitId, x.TeacherStudentId })
            .Distinct()
            .GroupBy(x => x.UnitId)
            .Select(g => new { UnitId = g.Key, Count = g.Count() })
            .ToListAsync();

        var seenById = seen.ToDictionary(x => x.UnitId, x => x.Count);
        foreach (var entry in inScope)
        {
            result[entry.UnitId] =
                (entry.Count, seenById.TryGetValue(entry.UnitId, out var s) ? s : 0);
        }

        return result;
    }
}
