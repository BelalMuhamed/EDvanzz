using Edvanz.Domain.Constants;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Helpers;

/// <summary>
/// The single definition of "how far has this student got through this video".
///
/// One rule, three outcomes — <see cref="VideoWatchStatus.NotStarted"/> (never opened),
/// <see cref="VideoWatchStatus.InProgress"/> (started but not finished),
/// <see cref="VideoWatchStatus.Completed"/> (watched through). Every surface that talks
/// about watch state derives it from here:
///
/// <list type="bullet">
///   <item>the student video-list row badge (<c>WatchStatus</c>) — in memory, via
///         <see cref="Compute"/>;</item>
///   <item>the "Not watched / In progress / Watched" list filter — in SQL, via the
///         mirror described below;</item>
///   <item>the per-unit watched count behind the unit progress rail — in memory, via
///         <see cref="IsWatched"/>;</item>
///   <item>the student home Videos tile counts — in SQL, via the same mirror.</item>
/// </list>
///
/// Before 2026-09-09 the filter and the home tile each had their own private notion of
/// "not watched": the filter meant "no watch seconds", the tile meant "no analytics row"
/// (and a row is written with 0 seconds on the first play report). A student who pressed
/// play for one second therefore dropped out of the tile's count while the list still
/// badged the video NotStarted. Both now go through this rule.
///
/// The completion threshold is <see cref="VideoConstants.CompletionThresholdPercent"/> —
/// the same one the teacher-side analytics <c>completedCount</c> uses, so the teacher's
/// "completed" figure and the student's own badge agree too.
///
/// <para>
/// SQL MIRROR — repositories cannot call these methods inside an EF query, so they
/// reproduce the rule as set membership over the student's <c>VideoAnalytics</c> rows:
/// <code>
///   started   := TotalWatchSeconds &gt; 0
///   completed := started AND DurationSeconds &gt; 0
///                        AND TotalWatchSeconds * 100 &gt;= DurationSeconds * threshold
///   NotStarted := NOT started
///   InProgress := started AND NOT completed
///   Completed  := completed
/// </code>
/// Any change to <see cref="Compute"/> must be made in both places on the same commit —
/// there is no other copy.
/// </para>
/// </summary>
public static class VideoWatchRules
{
    /// <summary>
    /// The completion threshold, as a percentage of the video's duration. Exposed so the
    /// SQL mirror above and this class read the same constant.
    /// </summary>
    public static int CompletionThresholdPercent => VideoConstants.CompletionThresholdPercent;

    /// <summary>
    /// 3-state watch indicator from the student's accumulated watch seconds
    /// against the video's duration.
    ///
    /// A zero/unknown duration deliberately reads as <see cref="VideoWatchStatus.InProgress"/>
    /// rather than Completed: the backend learns the duration from the first
    /// play report, and until it does, no amount of watch time can prove the
    /// video was finished.
    /// </summary>
    public static VideoWatchStatus Compute(long totalWatchSeconds, int durationSeconds)
    {
        if (totalWatchSeconds <= 0) return VideoWatchStatus.NotStarted;
        if (durationSeconds <= 0) return VideoWatchStatus.InProgress;
        double pct = (double)totalWatchSeconds / durationSeconds * 100.0;
        return pct >= VideoConstants.CompletionThresholdPercent
            ? VideoWatchStatus.Completed
            : VideoWatchStatus.InProgress;
    }

    /// <summary>Watched to the completion threshold — what the unit progress bar counts.</summary>
    public static bool IsWatched(long totalWatchSeconds, int durationSeconds)
        => Compute(totalWatchSeconds, durationSeconds) == VideoWatchStatus.Completed;

    /// <summary>Never opened — what the student home's "new" badge counts.</summary>
    public static bool IsNotStarted(long totalWatchSeconds, int durationSeconds)
        => Compute(totalWatchSeconds, durationSeconds) == VideoWatchStatus.NotStarted;

    /// <summary>
    /// In-memory equivalent of the list filter: does this video's watch state match the
    /// state the caller asked for? A null <paramref name="wanted"/> matches everything
    /// (no filter).
    /// </summary>
    public static bool Matches(VideoWatchStatus? wanted, long totalWatchSeconds, int durationSeconds)
        => wanted is null || Compute(totalWatchSeconds, durationSeconds) == wanted.Value;
}
