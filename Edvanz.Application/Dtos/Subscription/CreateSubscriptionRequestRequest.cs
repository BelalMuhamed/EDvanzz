using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Edvanz.Domain.Enums;

namespace Edvanz.Application.Dtos.Subscription;

/// <summary>
/// Input DTO for POST /api/subscription/requests — a teacher (typically a new tutor with no
/// subscription, or one renewing) asking the super admin to activate a subscription. The fee is
/// computed server-side from the current pricing; any client amount is ignored.
/// Unknown JSON fields are rejected (400).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class CreateSubscriptionRequestRequest
{
    /// <summary>The requested plan: Full, Managerial, or ManagerialPlus (Managerial + Parents).</summary>
    [Required]
    public SubscriptionPlanType PlanType { get; set; }

    /// <summary>
    /// Number of students to cover. Required and must be &gt; 0 for <see cref="SubscriptionPlanType.Full"/>
    /// (drives the fee and granted capacity); ignored for the flat-priced managerial plans.
    /// </summary>
    public int RequestedStudents { get; set; }

    /// <summary>
    /// Number of student APP ACCOUNTS to cover — the PRICED number (it becomes
    /// <c>Teacher.LinkedStudentCapacity</c> on approve). Must be &gt; 0 and never greater than
    /// <see cref="RequestedStudents"/> for <see cref="SubscriptionPlanType.Full"/>; ignored for
    /// the flat-priced managerial plans.
    ///
    /// NULLABLE ON PURPOSE: an app build that predates this field sends only
    /// <see cref="RequestedStudents"/>, and null falls back to it — such a request is priced
    /// and granted exactly as it was before this field existed.
    /// </summary>
    public int? RequestedLinkedStudents { get; set; }

    /// <summary>Optional free-text note shown to the reviewing admin (max 500 chars).</summary>
    public string? Note { get; set; }
}
