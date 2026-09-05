using Edvanz.Domain.Enums;

namespace Edvanz.Application.Dtos.Subscription;

/// <summary>
/// One row of GET /api/admin/subscriptions/capacity-requests — a Pending
/// capacity-increase request enriched with live teacher context so the admin can decide
/// with the usage picture in front of them.
/// </summary>
public class AdminCapacityRequestQueueItemDto
{
    /// <summary>The CapacityIncreaseRequest row id (used by approve/reject).</summary>
    public long Id { get; set; }

    public long TeacherId { get; set; }

    public string TeacherName { get; set; } = string.Empty;

    public string TeacherCode { get; set; } = string.Empty;

    /// <summary>
    /// Which limit this request targets: AccountStudents (students in the account, a free
    /// operational quota) or LinkedStudents (student app accounts — the PRICED limit).
    /// Every field below is expressed in terms of THIS limit. Serialized as a string.
    /// </summary>
    public CapacityKind CapacityKind { get; set; } = CapacityKind.AccountStudents;

    /// <summary>The teacher's LIVE value of the targeted limit (may differ from CapacityAtRequest if an admin changed it since).</summary>
    public int CurrentCapacity { get; set; }

    /// <summary>The teacher's value of the targeted limit, snapshotted at submission time.</summary>
    public int CapacityAtRequest { get; set; }

    /// <summary>The capacity the teacher is asking for on the targeted limit.</summary>
    public int RequestedCapacity { get; set; }

    /// <summary>Live count of the teacher's active roster students (usage vs. limit context).</summary>
    public int ActiveStudentCount { get; set; }

    /// <summary>
    /// What the teacher would pay per renewal if approved. Only the student-app-account limit is
    /// priced, so this is RequestedCapacity × per-student rate for a LinkedStudents request and
    /// the teacher's LIVE LinkedStudentCapacity × rate for an AccountStudents one (raising the
    /// students-in-the-account quota is free). 0 when the rate is unconfigured.
    /// </summary>
    public decimal ProjectedMonthlyPriceEGP { get; set; }

    /// <summary>The teacher's optional justification.</summary>
    public string? Note { get; set; }

    /// <summary>When the request was submitted (UTC). Queue is FIFO on this value.</summary>
    public DateTime RequestedAt { get; set; }
}
