using Edvanz.Domain.Entities.ShareProp;
using Edvanz.Domain.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace Edvanz.Domain.Entities;

/// <summary>
/// Junction table linking a Student User account to a Teacher.
///
/// REQUEST/APPROVAL FLOW (supersedes the original AAM-FR-05.5 credential flow):
/// the student sends a request carrying the teacher's 8-digit TeacherCode plus a
/// self-typed name (and optionally the student code the teacher assigned them).
/// The row is created with LinkStatus.Pending and TeacherStudentId = null.
/// The teacher then accepts it — binding it to one of their TeacherStudent roster
/// records and setting LinkStatus.Active — or rejects it (LinkStatus.Rejected).
///
/// AAM-FR-05.6: Upon acceptance, the teacher appears on the student's dashboard.
/// AAM-FR-05.7: A student can link to multiple teachers; each is displayed distinctly.
/// AAM-BR-06: Each teacher's visibility settings apply independently.
/// AAM-FR-05.8: Content visibility is governed by TeacherConfiguration (AAM-FR-04.8).
///
/// This is the primary data-access join path for the student dashboard:
///   StudentUser → StudentTeacherLink → Teacher → TeacherConfiguration (visibility)
///   StudentUser → StudentTeacherLink → TeacherStudent (attendance, payments, grades)
///
/// Terminal rows (Rejected/Unlinked/RemovedByTeacher/CancelledByStudent) are kept
/// for audit; a filtered unique index allows at most one live (Pending/Active) row
/// per (StudentUserId, TeacherId).
/// </summary>
public class StudentTeacherLink : BaseEntity
{
    /// <summary>
    /// Foreign key to the Student User account performing the link.
    /// </summary>
    [ForeignKey(nameof(StudentUser))]
    public long StudentUserId { get; set; }
    public StudentUser StudentUser { get; set; } = null!;

    /// <summary>
    /// Foreign key to the Teacher being linked.
    /// Resolved from the TeacherCode provided during linking (AAM-FR-05.5 credential #1).
    /// </summary>
    [ForeignKey(nameof(Teacher))]
    public long TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;

    /// <summary>
    /// Foreign key to the specific TeacherStudent record under that teacher.
    /// Bound by the TEACHER at accept time (explicit selection, or auto-matched
    /// from the student-supplied RequestedStudentCode).
    ///
    /// Nullable: null while the request is Pending/Rejected, and set to null if
    /// the teacher later deletes the student record (the link survives but the
    /// UI should show a "removed enrollment" state).
    ///
    /// This FK is the performance-critical join path for all student data access:
    /// attendance, payments, homework, exams all route through this reference.
    /// </summary>
    [ForeignKey(nameof(TeacherStudent))]
    public long? TeacherStudentId { get; set; }
    public TeacherStudent? TeacherStudent { get; set; }

    // ─── Request snapshot (filled by the student when the request is created) ───

    /// <summary>
    /// The name the student typed when sending the request, shown to the teacher
    /// so they can identify who is asking. Null on legacy (pre-redesign) rows.
    /// </summary>
    public string? RequestedStudentName { get; set; }

    /// <summary>
    /// Optional: the student code the teacher's account assigned to this student,
    /// as typed by the student. Used to auto-suggest the matching TeacherStudent
    /// roster record at accept time. Never trusted blindly — the teacher confirms.
    /// </summary>
    public string? RequestedStudentCode { get; set; }

    /// <summary>UTC timestamp the student submitted the link request.</summary>
    public DateTime? RequestedAt { get; set; }

    // ─── Teacher decision audit ───

    /// <summary>UTC timestamp the teacher accepted or rejected the request.</summary>
    public DateTime? RespondedAt { get; set; }

    /// <summary>
    /// User.Id of the account that accepted or rejected the request on the
    /// teacher side (the teacher, or an assistant acting for them).
    /// Plain audit column — no FK.
    /// </summary>
    public long? RespondedByUserId { get; set; }

    /// <summary>
    /// User.Id of the account that ENDED the link: the student themself
    /// (cancel/unlink) or the teacher/assistant who removed the student.
    /// Interpret together with LinkStatus (Unlinked/CancelledByStudent = the
    /// student, RemovedByTeacher = teacher side). Plain audit column — no FK.
    /// </summary>
    public long? RemovedByUserId { get; set; }

    /// <summary>
    /// Current status of the link. See <see cref="Enums.LinkStatus"/> for the
    /// full request/approval lifecycle. Defaults to Pending — a row only becomes
    /// Active through an explicit teacher accept.
    /// </summary>
    public LinkStatus LinkStatus { get; set; } = LinkStatus.Pending;

    /// <summary>
    /// Timestamp when the link became Active (teacher accepted the request).
    /// AAM-FR-05.6: The moment the teacher entry appears on the student's dashboard.
    /// Equals RequestedAt on legacy rows created by the old credential flow.
    /// </summary>
    public DateTime LinkedAt { get; set; }

    /// <summary>
    /// Timestamp when the student removed the teacher from their dashboard.
    /// Null if the link is still active.
    /// </summary>
    public DateTime? UnlinkedAt { get; set; }

    // ─── Student-initiated dismissal (the "Delete" action, distinct from "Unlink") ───

    /// <summary>
    /// True when the STUDENT chose "Delete" (not merely "Unlink") for this teacher:
    /// the link is ended AND the card is permanently removed from the student's home
    /// list. "Unlink" leaves the row visible (status Unlinked, re-requestable) with
    /// this flag false; "Delete" sets it true so <c>GetMyTeachersAsync</c> hides the
    /// teacher entirely. A later re-request creates a NEW (non-dismissed) row, so the
    /// teacher reappears naturally. Never touches the teacher's roster/attendance/
    /// payment data — it only affects what the STUDENT sees on their own home.
    /// </summary>
    public bool StudentDismissed { get; set; }

    /// <summary>UTC timestamp the student dismissed (deleted) the card. Null otherwise.</summary>
    public DateTime? StudentDismissedAt { get; set; }

    // ─── Device lock (per teacher; governed by TeacherConfiguration.IsDeviceLockEnabled) ───

    /// <summary>
    /// The device this student is bound to for THIS teacher. Set (with consent) the
    /// first time the student opens the teacher after the teacher enabled the device
    /// lock. Null = no device registered yet. Carries a client-generated device id
    /// (same shape as <see cref="VideoWatchEvent.DeviceId"/>); never used as an
    /// authentication credential — only to gate teacher access to one phone.
    /// </summary>
    public string? LockedDeviceId { get; set; }

    /// <summary>UTC timestamp the current <see cref="LockedDeviceId"/> was registered.</summary>
    public DateTime? DeviceBoundAt { get; set; }

    /// <summary>
    /// UTC timestamp a teacher/assistant last reset (cleared) the bound device,
    /// allowing the student to register a new phone. Null if never reset.
    /// </summary>
    public DateTime? DeviceResetAt { get; set; }

    /// <summary>
    /// User.Id of the teacher/assistant who performed the last device reset.
    /// Plain audit column — no FK.
    /// </summary>
    public long? DeviceResetByUserId { get; set; }

    /// <summary>
    /// UTC timestamp the teacher was last told this student was BLOCKED trying to open from a
    /// device other than the bound one. Purely a throttle stamp: a blocked app retries the gate on
    /// every screen, and without this one lockout would fan out into dozens of pushes. Only the
    /// request that wins the conditional stamp (see
    /// <c>IUserRepo.TryStampDeviceBlockNotifiedAsync</c>) sends the notification; the rest are
    /// denied silently. Cleared on device reset so the NEXT genuine block notifies again.
    /// Null = never notified.
    /// </summary>
    public DateTime? DeviceBlockNotifiedAt { get; set; }
}