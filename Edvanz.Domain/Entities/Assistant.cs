using Edvanz.Domain.Entities.ShareProp;
using Edvanz.Domain.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace Edvanz.Domain.Entities;

/// <summary>
/// Represents an Assistant account operating under a Teacher's account.
/// AAM-BR-01: Assistants can only be created by a Teacher.
/// BR-USR-005: An assistant can only interact with data belonging to their owning teacher.
/// </summary>
public class Assistant : BaseEntity
{
    public long UserId { get; set; }
    public User User { get; set; } = null!;

    [ForeignKey(nameof(Teacher))]
    public long TeacherAccountId { get; set; }
    public Teacher Teacher { get; set; } = null!;
    public string? LanguagePreference { get; set; }
    public DateTime? DeactivatedAt { get; set; }
    public AccountStatus AccountStatus { get; set; } = AccountStatus.Active;


    /// <summary>
    /// Soft-delete timestamp. Null if account is not deleted.
    /// REQ-ADM-020 through 024: Data preservation period before permanent removal.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// When this assistant was REMOVED from the account (soft-delete). Null while they are on the
    /// account, whatever their <see cref="AccountStatus"/>.
    ///
    /// Distinct from <see cref="DeletedAt"/> ON PURPOSE: DeletedAt is written by BOTH the delete
    /// path AND a temporary Suspend, so it cannot answer "is this person gone?". Reading it as a
    /// removal labelled a merely suspended assistant "Removed" on the payments tracking card and
    /// hid them from the next month entirely. Removal-driven behaviour (payment tracking
    /// visibility, the Removed chip) keys on THIS column; DeletedAt keeps its existing
    /// login/visibility duties untouched.
    /// </summary>
    public DateTime? RemovedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
    public virtual ICollection<TemplatePermissionsUsers> PermissionProfiles { get; set; } = new List<TemplatePermissionsUsers>();


}