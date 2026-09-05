using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Edvanz.Application.Dtos.Subscription;

/// <summary>
/// Input DTO for PUT /api/admin/subscriptions/teachers/{teacherId}/linked-capacity — a super
/// admin setting a teacher's <c>LinkedStudentCapacity</c> (how many student APP ACCOUNTS may be
/// linked at once). This is the limit the subscription price is computed from.
///
/// Unlike the students-in-the-account endpoint (AdminSetCapacityRequest, increase-only), this
/// one allows BOTH increases and decreases — tuning the number down is the normal way to move a
/// teacher to a smaller package. Lowering it never breaks already-linked students; it only stops
/// new links until usage falls back under the limit.
///
/// Side effects mirror the other capacity endpoint: the value applies immediately, an Approved
/// CapacityIncreaseRequest audit row (CapacityKind = LinkedStudents) is written, and the new
/// price applies from the NEXT renewal (BR-SUB-009). When the new value exceeds the teacher's
/// StudentCapacity, that limit is raised to match (a linked account always needs a student
/// record, and students-in-the-account is a free operational quota). TeacherId comes from the
/// route. Unknown JSON fields are rejected (400).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class AdminSetLinkedCapacityRequest
{
    /// <summary>
    /// The student-app-account limit to set. Must be greater than 0 and at most
    /// SubscriptionConstants.MaxStudentCapacity. May be lower than the current value.
    /// </summary>
    [Required]
    public int NewCapacity { get; set; }

    /// <summary>Optional admin note stored on the audit row (max 500 chars).</summary>
    public string? Note { get; set; }
}
