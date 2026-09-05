using Edvanz.Domain.Entities.ShareProp;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Entities;

/// <summary>
/// A teacher-initiated request to ACTIVATE a subscription — used mainly by new tutors who
/// download the app and have no subscription yet, and for renewals. The teacher chooses a
/// plan (<see cref="SubscriptionPlanType.Full"/> or <see cref="SubscriptionPlanType.Managerial"/>)
/// and, for Full, the number of students they want covered; the super admin reviews the request
/// and on approve activates the matching subscription (Full → capacity = RequestedStudents;
/// Managerial → managerial activation). Payment is arranged OUTSIDE the app — this request is
/// only the "please set me up" signal to the team.
///
/// Distinct from <see cref="CapacityIncreaseRequest"/> (raises the roster limit on an already
/// active subscription). Lifecycle (<see cref="SubscriptionRequestStatus"/>):
/// Pending → Approved | Rejected | Cancelled. Terminal rows are kept for audit; a filtered
/// unique index allows one live Pending row per teacher (mirrors the CapacityIncreaseRequest
/// / PendingSubscriptionPayment queue pattern).
///
/// FK behavior is configured ENTIRELY in Fluent API (EdvanzDbContext) — no [ForeignKey]
/// annotations here, per the BUG-4 rule.
/// </summary>
public class SubscriptionRequest : BaseEntity
{
    /// <summary>The teacher the subscription is requested for (tenant owner).</summary>
    public long TeacherId { get; set; }

    public Teacher Teacher { get; set; } = null!;

    /// <summary>The requested plan: Full (students/parents allowed) or Managerial (blocked).</summary>
    public SubscriptionPlanType PlanType { get; set; }

    /// <summary>
    /// Number of students to cover. Only meaningful for <see cref="SubscriptionPlanType.Full"/>
    /// (drives the fee and the granted capacity); stored as 0 for Managerial.
    /// </summary>
    public int RequestedStudents { get; set; }

    /// <summary>
    /// Number of student APP ACCOUNTS to cover — the PRICED number
    /// (<c>Teacher.LinkedStudentCapacity</c>). Only meaningful for
    /// <see cref="SubscriptionPlanType.Full"/>; stored as 0 for the managerial plans.
    ///
    /// 0 also means "an older client submitted this row before the field existed" — those rows
    /// fall back to <see cref="RequestedStudents"/> everywhere (pricing at submit time and the
    /// capacity granted on approve), so old app builds keep their exact previous behaviour.
    /// Never exceeds <see cref="RequestedStudents"/> (app accounts need student records).
    /// </summary>
    public int RequestedLinkedStudents { get; set; }

    /// <summary>
    /// Fee computed server-side at submission time (Full = RequestedLinkedStudents × per-student rate;
    /// Managerial = flat managerial monthly price). A snapshot for the admin's context and the
    /// teacher's record; the client-supplied amount (if any) is never trusted.
    /// </summary>
    public decimal ComputedAmountEGP { get; set; }

    /// <summary>Optional free-text note from the teacher (max 500, Fluent).</summary>
    public string? Note { get; set; }

    /// <summary>Current lifecycle state.</summary>
    public SubscriptionRequestStatus Status { get; set; } = SubscriptionRequestStatus.Pending;

    /// <summary>When the teacher submitted the request (UTC).</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>User id of whoever submitted (the teacher, or an assistant acting for the tutor). Plain audit column — no FK.</summary>
    public long RequestedByUserId { get; set; }

    /// <summary>When Status reached a terminal state (Approved/Rejected/Cancelled). Null while Pending.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>The super admin who approved/rejected, or the teacher-side user who cancelled.</summary>
    public long? ResolvedByUserId { get; set; }

    public User? ResolvedByUser { get; set; }

    /// <summary>Reason given when Status = Rejected (max 500, Fluent). Shown to the teacher.</summary>
    public string? RejectionReason { get; set; }
}
