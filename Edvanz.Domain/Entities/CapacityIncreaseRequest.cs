using Edvanz.Domain.Entities.ShareProp;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Entities;

/// <summary>
/// A teacher-initiated request to raise <see cref="Teacher.StudentCapacity"/> — the
/// configuration limit that both bounds roster size AND drives the per-student
/// subscription price (capacity × PricePerStudentEGP). Requests are increase-only and
/// require super-admin approval: on approve the capacity applies immediately while the
/// new price naturally applies from the NEXT renewal (renewal reads capacity at
/// initiation time, BR-SUB-009).
///
/// Lifecycle (CapacityRequestStatus): Pending → Approved | Rejected | Cancelled.
/// Terminal rows are kept for audit; a filtered unique index allows one live Pending
/// row per teacher (mirrors the PendingSubscriptionPayment queue pattern).
///
/// FK behavior is configured ENTIRELY in Fluent API (EdvanzDbContext) — no [ForeignKey]
/// annotations here, per the BUG-4 rule (annotation + Fluent OnDelete silently drops
/// the OnDelete).
/// </summary>
public class CapacityIncreaseRequest : BaseEntity
{
    /// <summary>The teacher whose capacity should be raised (tenant owner).</summary>
    public long TeacherId { get; set; }

    public Teacher Teacher { get; set; } = null!;

    /// <summary>
    /// WHICH limit this request targets: <see cref="Enums.CapacityKind.AccountStudents"/>
    /// (Teacher.StudentCapacity — student records in the account) or
    /// <see cref="Enums.CapacityKind.LinkedStudents"/> (Teacher.LinkedStudentCapacity — the
    /// priced student-app-account limit). NOT NULL, defaults to AccountStudents so every row
    /// written before this column existed keeps its original meaning.
    /// One live Pending request is allowed PER KIND per teacher (filtered unique index).
    /// </summary>
    public CapacityKind CapacityKind { get; set; } = CapacityKind.AccountStudents;

    /// <summary>
    /// Snapshot of the targeted limit at submission time (audit context for the reviewer) —
    /// Teacher.StudentCapacity for AccountStudents, Teacher.LinkedStudentCapacity for LinkedStudents.
    /// </summary>
    public int CapacityAtRequest { get; set; }

    /// <summary>The capacity the teacher is asking for on the targeted limit. Must exceed CapacityAtRequest and stay within SubscriptionConstants.MaxStudentCapacity.</summary>
    public int RequestedCapacity { get; set; }

    /// <summary>Optional free-text justification from the teacher (max 500, Fluent).</summary>
    public string? Note { get; set; }

    /// <summary>Current lifecycle state.</summary>
    public CapacityRequestStatus Status { get; set; } = CapacityRequestStatus.Pending;

    /// <summary>When the teacher submitted the request (UTC). Distinct from CreateAt for semantic clarity.</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>
    /// User id of whoever submitted the request (the teacher, or an assistant acting for
    /// the tutor). Plain audit column — no FK (RemovedByUserId precedent).
    /// </summary>
    public long RequestedByUserId { get; set; }

    /// <summary>When Status reached a terminal state (Approved/Rejected/Cancelled). Null while Pending.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>The super admin who approved/rejected, or the teacher-side user who cancelled.</summary>
    public long? ResolvedByUserId { get; set; }

    public User? ResolvedByUser { get; set; }

    /// <summary>Reason given when Status = Rejected (max 500, Fluent). Shown to the teacher in the rejection notification.</summary>
    public string? RejectionReason { get; set; }
}
