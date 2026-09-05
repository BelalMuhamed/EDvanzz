using Edvanz.Domain.Enums;

namespace Edvanz.Application.Dtos.Subscription;

/// <summary>
/// Output DTO for the teacher-facing capacity-request endpoints
/// (POST/GET/DELETE api/subscription/capacity-requests). Status serializes as a string
/// (global JsonStringEnumConverter).
/// </summary>
public class CapacityRequestDto
{
    /// <summary>The CapacityIncreaseRequest row id.</summary>
    public long Id { get; set; }

    /// <summary>
    /// Which limit the request targets: AccountStudents (students in the account) or
    /// LinkedStudents (student app accounts — the priced limit). Serialized as a string.
    /// Additive: absent on older servers, where every request meant AccountStudents.
    /// </summary>
    public CapacityKind CapacityKind { get; set; } = CapacityKind.AccountStudents;

    /// <summary>The capacity the teacher asked for on the targeted limit.</summary>
    public int RequestedCapacity { get; set; }

    /// <summary>The teacher's value of the targeted limit at submission time.</summary>
    public int CapacityAtRequest { get; set; }

    /// <summary>Pending, Approved, Rejected, or Cancelled.</summary>
    public CapacityRequestStatus Status { get; set; }

    /// <summary>The teacher's optional justification.</summary>
    public string? Note { get; set; }

    /// <summary>Reason given when the request was rejected; null otherwise.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>When the request was submitted (UTC).</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>When the request reached a terminal state; null while Pending.</summary>
    public DateTime? ResolvedAt { get; set; }
}
