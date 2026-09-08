namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Best-effort notifications for the student-teacher link request/approval flow.
///
/// Every method persists a UserNotification inbox row and fans out an FCM push
/// to the recipient's active device tokens, localized to the RECIPIENT's
/// language preference (not the caller's request culture).
///
/// CONTRACT: call these AFTER the owning transaction has committed, wrapped in
/// try/catch by the caller — a notification failure must never roll back or fail
/// the link operation itself. Methods commit their own inbox row (post-commit
/// side-effect unit, mirroring the Hangfire notification jobs).
/// </summary>
public interface IStudentLinkNotifier
{
    /// <summary>
    /// Tells the teacher a new link request arrived.
    /// </summary>
    /// <param name="teacherId">Teacher (recipient) id.</param>
    /// <param name="requestedStudentName">The name the student typed on the request.</param>
    Task NotifyRequestReceivedAsync(long teacherId, string requestedStudentName);

    /// <summary>
    /// Tells the student their request was accepted or rejected —
    /// the "student should be aware of the decision" requirement.
    /// </summary>
    /// <param name="studentUserId">StudentUser (recipient) id.</param>
    /// <param name="teacherId">Teacher whose decision this is (for the name in the text).</param>
    /// <param name="accepted">True = accepted, false = rejected.</param>
    Task NotifyRequestResolvedAsync(long studentUserId, long teacherId, bool accepted);

    /// <summary>
    /// Tells the student the teacher removed them from the linked students list.
    /// </summary>
    Task NotifyRemovedByTeacherAsync(long studentUserId, long teacherId);

    /// <summary>
    /// Tells the TEACHER that a linked student deleted the link from their side
    /// (the student-initiated "Delete" action). Recipient is the teacher; the text
    /// carries the student's name so the teacher knows who left. Nothing on the
    /// teacher's roster/attendance/payment data changes — this is awareness only.
    /// </summary>
    /// <param name="teacherId">Teacher (recipient) id.</param>
    /// <param name="studentName">The student's display name for the notification text.</param>
    Task NotifyLinkDeletedByStudentAsync(long teacherId, string studentName);

    /// <summary>
    /// Tells the student their profile was linked to (or unlinked from) a student
    /// record — i.e. they gained or lost access to this teacher's content, with no
    /// change to the accepted connection itself.
    /// </summary>
    /// <param name="linked">True = linked (access granted), false = unlinked (access paused).</param>
    Task NotifyLinkBindingChangedAsync(long studentUserId, long teacherId, bool linked);

    /// <summary>
    /// Tells the TEACHER that a linked student was BLOCKED trying to open their content from a
    /// device other than the one bound to them, so the teacher can reset the binding if it is
    /// legitimate. Recipient is the teacher; the text carries the student's name.
    ///
    /// Before this existed a locked-out student had to physically find their teacher — the teacher
    /// was never told anything had happened. The caller MUST have already won the
    /// <c>TryStampDeviceBlockNotifiedAsync</c> cooldown claim: a blocked app re-hits the gate on
    /// every screen, so this is only safe to call once per link per cooldown window.
    /// </summary>
    /// <param name="teacherId">Teacher (recipient) id.</param>
    /// <param name="studentName">The student's display name for the notification text.</param>
    Task NotifyDeviceBlockedAsync(long teacherId, string studentName);
}