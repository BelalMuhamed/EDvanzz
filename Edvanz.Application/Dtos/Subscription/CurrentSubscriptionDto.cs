using Edvanz.Domain.Enums;

namespace Edvanz.Application.Dtos.Subscription;

/// <summary>
/// Output DTO for GET /api/subscription/current (REQ-SUB-003 / FR-SUB-003).
/// Status is DERIVED at read time via SubscriptionStatusCalculator (D-08 / C-6).
/// Returned when the teacher has a subscription row; an absent subscription is
/// represented by a Result.Failure with the NoActiveSubscription message key.
/// </summary>
public class CurrentSubscriptionDto
{
    /// <summary>
    /// The TeacherSubscription row id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Subscription period start (UTC).
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// Subscription period end (UTC). Always StartDate + 30 days unless overridden by super admin.
    /// </summary>
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Days remaining until EndDate. Zero on day-of-expiry; negative values clamped to zero.
    /// </summary>
    public int DaysRemaining { get; set; }

    /// <summary>
    /// Derived status: Active, ExpiringSoon, or Expired (FR-SUB-004).
    /// </summary>
    public SubscriptionStatus Status { get; set; }

    /// <summary>
    /// Plan type: Full (students/parents allowed) or Managerial (students/parents blocked).
    /// Serialized as a string via the global JsonStringEnumConverter.
    /// </summary>
    public SubscriptionPlanType PlanType { get; set; }

    /// <summary>
    /// Price the next renewal will charge, sourced from the teacher's current
    /// StudentCapacityPackage.MonthlyPriceEGP at read time (BR-SUB-009).
    /// Zero when the teacher has no package assigned yet.
    /// </summary>
    public decimal RenewalAmountEGP { get; set; }

    /// <summary>
    /// The teacher's student-APP-ACCOUNT limit (<c>Teacher.LinkedStudentCapacity</c>) — the
    /// number <see cref="RenewalAmountEGP"/> is computed from on the Full plan. Additive; 0
    /// when the teacher row cannot be read. Not meaningful for the flat managerial plans.
    /// </summary>
    public int LinkedStudentCapacity { get; set; }
}