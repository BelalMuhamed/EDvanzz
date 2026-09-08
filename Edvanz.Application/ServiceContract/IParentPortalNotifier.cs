namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Best-effort teacher notification for the public parent portal: "a parent wants to follow X".
/// Persists a <c>UserNotification</c> inbox row and fans an FCM push out to the teacher's active
/// device tokens, rendered in the RECIPIENT's language (not the portal request's culture) —
/// modelled directly on <see cref="IStudentLinkNotifier"/>.
///
/// CONTRACT: call this AFTER the owning transaction commits, wrapped in try/catch by the caller —
/// a notification failure must never roll back or fail the parent's request. The implementation
/// owns its own SaveChanges for the inbox row (post-commit side-effect unit).
///
/// The implementation lives in the INFRASTRUCTURE layer (CLAUDE.md §6.2 — notification fan-out is
/// an infrastructure concern and must never drag Hangfire or transport types into Application).
///
/// BATCHING is the caller's decision, not this interface's: the service reads the newest pending
/// request's timestamp BEFORE inserting and only calls this when the teacher has not already been
/// notified within the last hour, so a burst of requests produces one notification.
/// </summary>
public interface IParentPortalNotifier
{
    /// <summary>
    /// Tells the teacher that parent requests are waiting.
    /// </summary>
    /// <param name="teacherId">Teacher (recipient) id.</param>
    /// <param name="studentName">
    /// Name of the student the newest request targets — used for the singular message. Ignored
    /// when <paramref name="pendingCount"/> is greater than 1.
    /// </param>
    /// <param name="pendingCount">
    /// Total pending requests in the teacher's inbox INCLUDING the one that triggered this call.
    /// 1 → the singular "A parent wants to follow {name}" text; more → the batched
    /// "{n} parents are waiting for your approval".
    /// </param>
    /// <param name="parentName">
    /// The requesting parent's self-declared name, so the singular message can say WHO is asking
    /// rather than only who they want to follow. Optional: grants written before the portal
    /// collected a name have none, and those fall back to the anonymous wording.
    /// </param>
    Task NotifyPendingRequestsAsync(
        long teacherId, string studentName, int pendingCount, string? parentName = null);
}
