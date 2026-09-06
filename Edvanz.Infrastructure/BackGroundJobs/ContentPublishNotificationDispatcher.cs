using Edvanz.Application.IservicesContract;
using Hangfire;

namespace Edvanz.Infrastructure.BackGroundJobs;

/// <summary>
/// Infrastructure half of <see cref="IContentPublishNotificationDispatcher"/> — the
/// only place that knows Hangfire exists for this flow (CLAUDE.md §6.2). Mirrors
/// <see cref="ChatPushDispatcher"/>.
/// </summary>
public class ContentPublishNotificationDispatcher : IContentPublishNotificationDispatcher
{
    private readonly IBackgroundJobClient _backgroundJobs;

    public ContentPublishNotificationDispatcher(IBackgroundJobClient backgroundJobs)
    {
        _backgroundJobs = backgroundJobs;
    }

    /// <inheritdoc />
    public void DispatchVideoPublished(long teacherId, long videoAssetId, DateTime? visibleAtUtc)
    {
        // A video published with a future PublishDate is not visible yet — announcing
        // it now would send students to a screen that does not show it. Schedule for
        // the moment it opens instead; the job re-checks visibility when it runs, so a
        // video unpublished or re-dated in the meantime simply goes quiet.
        if (visibleAtUtc is DateTime when && when > DateTime.UtcNow)
        {
            _backgroundJobs.Schedule<IContentPublishNotificationJob>(
                job => job.NotifyVideoPublishedAsync(teacherId, videoAssetId),
                when - DateTime.UtcNow);
            return;
        }

        _backgroundJobs.Enqueue<IContentPublishNotificationJob>(
            job => job.NotifyVideoPublishedAsync(teacherId, videoAssetId));
    }

    /// <inheritdoc />
    public void DispatchOnlineExamPublished(long teacherId, long onlineExamId)
    {
        _backgroundJobs.Enqueue<IContentPublishNotificationJob>(
            job => job.NotifyOnlineExamPublishedAsync(teacherId, onlineExamId));
    }
}
