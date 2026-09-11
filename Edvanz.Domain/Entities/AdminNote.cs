using Edvanz.Domain.Entities.ShareProp;
using System.ComponentModel.DataAnnotations;

namespace Edvanz.Domain.Entities;

/// <summary>
/// A free-text note an admin writes about a teacher — the call that was made, what they asked for,
/// why their account looks the way it does. There was nowhere to record any of this before; the
/// context lived in people's heads and in the sales sheet.
///
/// INTERNAL ONLY. Never exposed on any teacher-, assistant-, student- or parent-facing endpoint —
/// these notes are written about the teacher, not for them.
///
/// Soft-deleted (CLAUDE.md §4.3) so a deleted note stays auditable; the list query filters on
/// <see cref="IsDeleted"/> via a global query filter.
/// </summary>
public class AdminNote : BaseEntity
{
    /// <summary>The teacher this note is about. FK configured in Fluent API (CLAUDE.md §4.1).</summary>
    public long TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;

    /// <summary>
    /// The admin who wrote it. Plain audit column with NO FK — mirrors the convention used by
    /// <c>ModuleQuota.UpdatedByUserId</c> and <c>AttendanceRecord.RecordedByUserId</c>, so purging a
    /// user can never cascade into or block the deletion of the notes they wrote.
    /// </summary>
    public long AuthorUserId { get; set; }

    /// <summary>Denormalised author name, captured at write time. The note must stay readable even if
    /// the authoring account is later renamed or removed — the same reason payment rows keep a
    /// denormalised collector name.</summary>
    [MaxLength(128)]
    public string AuthorName { get; set; } = null!;

    /// <summary>The note itself.</summary>
    [MaxLength(4000)]
    public string Body { get; set; } = null!;

    /// <summary>Pinned notes sort to the top of the teacher's Notes tab. For the one thing that must
    /// not be scrolled past ("do not call before 6pm", "disputes every invoice").</summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// Optional date to come back to this teacher. Drives the admin's "needs follow-up" list without
    /// needing a separate task feature. Teacher-local calendar day, so it is a <see cref="DateOnly"/>
    /// and not a <c>DateTime</c> (CLAUDE.md §11b).
    /// </summary>
    public DateOnly? FollowUpDate { get; set; }

    /// <summary>Soft-delete marker.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>When it was soft-deleted (UTC). Null while live.</summary>
    public DateTime? DeletedAt { get; set; }
}
