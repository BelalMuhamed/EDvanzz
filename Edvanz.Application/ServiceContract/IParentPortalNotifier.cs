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
/// BATCHING applies to the PUSH ONLY, and is the caller's decision, not this interface's: the
/// service reads the newest pending request's timestamp BEFORE inserting and passes
/// <c>suppressPush</c> when the teacher was already pushed inside the last hour, so a burst
/// produces one buzz. The INBOX ROW is written every time regardless — changed 2026-09-11, because
/// suppressing the row too meant the 2nd..Nth parent in an hour left no trace the teacher could
/// ever find, and with FCM unconfigured in production the inbox row is the only signal that exists.
/// </summary>
public interface IParentPortalNotifier
{
    /// <summary>
    /// Tells the teacher that a parent request is waiting.
    /// </summary>
    /// <param name="teacherId">Teacher (recipient) id.</param>
    /// <param name="studentName">Name of the student this request targets.</param>
    /// <param name="pendingCount">
    /// Total pending requests in the teacher's inbox INCLUDING the one that triggered this call.
    /// Used only for the ANONYMOUS batched wording ("{n} parents are waiting for your approval"),
    /// which now applies solely to legacy requests carrying no parent name — a named request is
    /// always announced by name, because one inbox row describes one parent.
    /// </param>
    /// <param name="parentName">
    /// The requesting parent's self-declared name, so the message can say WHO is asking rather
    /// than only who they want to follow. Optional: grants written before the portal collected a
    /// name have none, and those fall back to the anonymous wording.
    /// </param>
    /// <param name="suppressPush">
    /// True when the teacher was already pushed for a pending request inside the batching window:
    /// persist the inbox row, skip the buzz.
    /// </param>
    Task NotifyPendingRequestsAsync(
        long teacherId, string studentName, int pendingCount, string? parentName = null,
        bool suppressPush = false);
}
