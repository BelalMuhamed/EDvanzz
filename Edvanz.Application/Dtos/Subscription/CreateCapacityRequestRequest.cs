using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Edvanz.Domain.Enums;

namespace Edvanz.Application.Dtos.Subscription;

/// <summary>
/// Input DTO for POST /api/subscription/capacity-requests — a teacher asking the super
/// admin to raise ONE of their two limits, selected by <see cref="CapacityKind"/>:
/// the number of students in their account (<c>Teacher.StudentCapacity</c>) or the number of
/// student app accounts they may link (<c>Teacher.LinkedStudentCapacity</c> — the limit the
/// subscription price is based on). Increase-only: the value must exceed the teacher's
/// current value OF THAT limit.
/// Unknown JSON fields are rejected (400) so a typo'd field never silently succeeds.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class CreateCapacityRequestRequest
{
    /// <summary>
    /// The capacity the teacher is asking for on the limit named by <see cref="CapacityKind"/>.
    /// Must be greater than the teacher's current value of that limit and at most
    /// SubscriptionConstants.MaxStudentCapacity (100,000).
    /// </summary>
    [Required]
    public int RequestedCapacity { get; set; }

    /// <summary>
    /// Which limit to raise. NULLABLE ON PURPOSE: omitted / null means
    /// <see cref="Edvanz.Domain.Enums.CapacityKind.AccountStudents"/>, so an app build that
    /// predates the second limit behaves exactly as before. Serialized as a string.
    /// </summary>
    public CapacityKind? CapacityKind { get; set; }

    /// <summary>Optional free-text justification shown to the reviewing admin (max 500 chars).</summary>
    public string? Note { get; set; }
}
