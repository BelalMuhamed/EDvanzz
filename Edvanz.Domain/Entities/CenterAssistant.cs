using Edvanz.Domain.Entities.ShareProp;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Entities;

/// <summary>
/// An assistant operating under a <see cref="Center"/>. Unlike <see cref="Assistant"/> (which is
/// scoped to ONE owning teacher via TeacherAccountId), a center assistant spans ALL of the
/// center's teachers — it "acts as" any of them via the <c>X-Acting-Teacher-Id</c> header
/// (validated against the center's membership). Kept as a SEPARATE entity from
/// <see cref="Assistant"/> so the load-bearing teacher-owned assistant path is never destabilized.
///
/// Permissions reuse the same <see cref="TemplatePermissionsUsers"/> profile machinery as the
/// teacher assistant. Role on the login <see cref="User"/> is <c>CenterAssistant</c>.
///
/// FK behavior is configured ENTIRELY in Fluent API (EdvanzDbContext) — no [ForeignKey]
/// annotations here, per the BUG-4 rule.
/// </summary>
public class CenterAssistant : BaseEntity
{
    /// <summary>Foreign key to the login <see cref="User"/> (UserType = CenterAssistant). 1:1.</summary>
    public long UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>The center this assistant operates under.</summary>
    public long CenterId { get; set; }
    public Center Center { get; set; } = null!;

    public string? LanguagePreference { get; set; }
    public DateTime? DeactivatedAt { get; set; }
    public AccountStatus AccountStatus { get; set; } = AccountStatus.Active;

    /// <summary>Soft-delete timestamp. Null if not deleted.</summary>
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

    // NOTE (P5): granular permission profiles for a center assistant are stored via a dedicated
    // junction (CenterAssistantPermissionProfile) added in the center-assistant phase — NOT the
    // teacher-owned TemplatePermissionsUsers table (which FKs Assistant only). Until then, the v1
    // authorization design treats CenterAssistant as a role-sufficient operator over the center's
    // teachers (see plan §C2), so no per-assistant permission rows are required yet.
}
