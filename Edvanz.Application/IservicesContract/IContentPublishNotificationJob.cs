using Hangfire;

namespace Edvanz.Application.IservicesContract;

/// <summary>
/// Tells students that new content is available to them.
///
/// Before this, publishing a video or an online exam notified nobody: a student
/// learned an exam existed only by opening the app, picking the teacher and
/// tapping into Exams. For a time-boxed online exam that is the difference
/// between sitting it and being marked absent.
///
/// <para>
/// Intent-based per CLAUDE.md §6.3, with <c>[Queue("notifications")]</c> declared on
/// the INTERFACE method (§6.1) so Hangfire routes it correctly regardless of the
/// implementation. Enqueued through <see cref="IContentPublishNotificationDispatcher"/>
/// so the Application layer never touches <c>IBackgroundJobClient</c> (§6.2).
/// </para>
///
/// <para>
/// Idempotent (§6.4) in two independent ways: each recipient row carries
/// (UserId, SourceType, SourceEntityId) against the unique index, and the job
/// re-checks that the content is genuinely visible before sending — so a Hangfire
/// retry, or a scheduled run whose content was unpublished meanwhile, is a no-op
/// rather than a duplicate or a lie.
/// </para>
/// </summary>
public interface IContentPublishNotificationJob
{
    /// <summary>Notifies every student a newly-visible video is scoped to.</summary>
    [Queue("notifications")]
    Task NotifyVideoPublishedAsync(long teacherId, long videoAssetId);

    /// <summary>Notifies every student a newly-published online exam is assigned to.</summary>
    [Queue("notifications")]
    Task NotifyOnlineExamPublishedAsync(long teacherId, long onlineExamId);
}
