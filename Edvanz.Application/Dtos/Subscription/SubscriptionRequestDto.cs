using Edvanz.Domain.Enums;

namespace Edvanz.Application.Dtos.Subscription;

/// <summary>
/// Output DTO for the teacher-facing subscription-request endpoints
/// (POST/GET/DELETE api/subscription/requests). Enums serialize as strings.
/// </summary>
public class SubscriptionRequestDto
{
    /// <summary>The SubscriptionRequest row id.</summary>
    public long Id { get; set; }

    /// <summary>The requested plan: Full or Managerial.</summary>
    public SubscriptionPlanType PlanType { get; set; }

    /// <summary>Requested student count (0 for Managerial).</summary>
    public int RequestedStudents { get; set; }

    /// <summary>
    /// Requested student APP ACCOUNT count — the number the fee was computed from
    /// (0 for the managerial plans). Rows submitted by app builds that predate this field
    /// carry 0 and were priced off <see cref="RequestedStudents"/>.
    /// </summary>
    public int RequestedLinkedStudents { get; set; }

    /// <summary>Server-computed fee at submission time, in EGP.</summary>
    public decimal ComputedAmountEGP { get; set; }

    /// <summary>Pending, Approved, Rejected, or Cancelled.</summary>
    public SubscriptionRequestStatus Status { get; set; }

    /// <summary>The teacher's optional note.</summary>
    public string? Note { get; set; }

    /// <summary>Reason given when the request was rejected; null otherwise.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>When the request was submitted (UTC).</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>When the request reached a terminal state; null while Pending.</summary>
    public DateTime? ResolvedAt { get; set; }
}
