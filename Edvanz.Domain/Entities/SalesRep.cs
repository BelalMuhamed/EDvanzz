using Edvanz.Domain.Entities.ShareProp;
using System.ComponentModel.DataAnnotations;

namespace Edvanz.Domain.Entities;

/// <summary>
/// A member of the sales team, so every teacher account can be attributed to the person who sold it.
///
/// Until now this lived ONLY in a disconnected Google Sheet (<c>sales-crm/EdvanzCRM.gs</c>) with no
/// shared identifier back to the platform, so no admin screen could answer "which of my reps' accounts
/// actually went live?". This table is the missing join: <see cref="Teacher.SalesRepId"/> points here.
///
/// Deliberately NOT a <c>User</c>: a rep does not need a login to be attributed, and most never will.
/// If reps are ever given accounts, add a nullable <c>UserId</c> — do not repurpose <see cref="Id"/>.
///
/// Soft-deleted via <see cref="IsActive"/> rather than a row delete: attribution on historical teachers
/// must survive a rep leaving the company.
/// </summary>
public class SalesRep : BaseEntity
{
    /// <summary>Display name, shown on every teacher row and insight card.</summary>
    [MaxLength(128)]
    public string Name { get; set; } = null!;

    /// <summary>Optional contact number, so an admin can reach the rep straight from a teacher's page.</summary>
    [MaxLength(32)]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// False = left the team. Hidden from the "assign a rep" picker, but still rendered on the teachers
    /// they brought in, so historical attribution never silently disappears.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Teachers attributed to this rep.</summary>
    public ICollection<Teacher> Teachers { get; set; } = new List<Teacher>();
}
