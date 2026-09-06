using Edvanz.Domain.Constants;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Helpers;

/// <summary>
/// The single definition of "has this student watched this video".
///
/// The rule now has three consumers — the student video list (per-row
/// <c>WatchStatus</c>), the per-unit watched count that drives the unit
/// progress bar, and the "not started yet" count on the student's teacher
/// home. It lives here so those three can never disagree: a unit reading
/// "4 of 6 watched" while the list shows five Watched badges is worse than
/// showing nothing at all.
///
/// Mirrors the threshold the teacher-side analytics <c>completedCount</c>
/// uses (<see cref="VideoConstants.CompletionThresholdPercent"/>), so the
/// teacher's "completed" figure and the student's own badge agree too.
/// </summary>
public static class VideoWatchRules
{
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
}
