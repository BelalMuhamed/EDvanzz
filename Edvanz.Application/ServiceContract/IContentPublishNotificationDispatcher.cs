namespace Edvanz.Application.IservicesContract;

/// <summary>
/// Enqueues <see cref="IContentPublishNotificationJob"/> after a publish commits.
/// Decouples the Application layer from Hangfire's <c>IBackgroundJobClient</c>
/// (CLAUDE.md §6.2) exactly as <c>IChatPushDispatcher</c> does.
///
/// Fan-out to a whole session does not belong on the request thread: the teacher
/// pressing Publish should not wait on a push round trip per student, and a push
/// failure must never fail their save.
/// </summary>
public interface IContentPublishNotificationDispatcher
{
    /// <summary>
    /// Announces a video. <paramref name="visibleAtUtc"/> is the moment students can
    /// actually open it (its scheduled PublishDate, or now): a video published with a
    /// future date is NOT visible yet, and announcing it early would send students to
    /// an empty screen. A future time schedules the job for then; the job re-checks
    /// visibility when it runs, so a video unpublished in the meantime stays silent.
    /// </summary>
    void DispatchVideoPublished(long teacherId, long videoAssetId, DateTime? visibleAtUtc);

    /// <summary>Announces a published online exam. Sent immediately — an exam has no scheduled-publish concept.</summary>
    void DispatchOnlineExamPublished(long teacherId, long onlineExamId);
}
