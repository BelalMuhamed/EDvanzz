using Edvanz.Domain.Entities.ShareProp;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Entities;

/// <summary>
/// One parent-portal GRANT: permission for a single browser/device to follow ONE
/// <see cref="TeacherStudent"/> roster record under ONE teacher, read-only, from the public
/// portal at parent.edvanz.io (a PHP page that calls this API server-to-server).
///
/// The parent identifies the student by typing the teacher's public 8-digit code plus the
/// teacher's per-roster student code (and, optionally, their phone). Because
/// <see cref="TeacherStudent.StudentCode"/> is a SEQUENTIAL counter (A1, A2 … Z999) and
/// <see cref="Teacher.TeacherCode"/> is public, the codes alone are NOT a credential — access
/// is always a grant the teacher approves.
///
/// THE UNIT OF TRUST IS THE PHONE, NOT THE ROW. A row is per (student, device), but a grant is
/// created Active whenever the typed phone is already trusted for that student — either it
/// matches the roster's parent phone (<see cref="AutoApproved"/>) or it already holds an Active
/// grant a teacher vetted (<see cref="ParentPortalAccessOrigin.TrustedPhone"/>). So clearing
/// cookies, switching browser or buying a new phone does NOT push an approved parent back into
/// the inbox. The corollary is load-bearing: REVOKING must clear every live row sharing that
/// (student, phone), or the parent walks straight back in through a surviving sibling row.
///
/// TENANT INTEGRITY: <see cref="TeacherId"/> is denormalized from the roster record and
/// participates in the composite FK <c>(TeacherStudentId, TeacherId)</c> →
/// <c>TeacherStudents(Id, TeacherId)</c> (CLAUDE.md §4.4, VideoScope is the reference
/// implementation), so a row whose teacher does not match its student's teacher cannot exist.
///
/// PRIVACY: the portal's raw device id is NEVER stored — only <see cref="DeviceHash"/>, its
/// SHA-256 hex. Same for the caller IP (<see cref="RequestIpHash"/>).
/// </summary>
public class ParentPortalAccess : BaseEntity
{
    /// <summary>
    /// Owning teacher — denormalized from <see cref="TeacherStudent.TeacherId"/> so every
    /// teacher-scoped query (inbox, summary) is served without joining back to TeacherStudents.
    /// Kept in sync at the DB level by the composite FK described in the class remarks.
    /// </summary>
    public long TeacherId { get; set; }

    /// <summary>The roster record this grant follows. One grant = one student, never a set.</summary>
    public long TeacherStudentId { get; set; }

    /// <summary>The followed roster record.</summary>
    public TeacherStudent TeacherStudent { get; set; } = null!;

    /// <summary>
    /// SHA-256 hex (64 chars, lowercase) of the portal-supplied device id. The RAW id is never
    /// stored: a leaked database row must not let anyone impersonate a parent's browser, and the
    /// portal proves possession by sending the raw id on every call (hashed server-side and
    /// compared against this column).
    /// </summary>
    public string DeviceHash { get; set; } = null!;

    /// <summary>Grant lifecycle — see <see cref="ParentPortalAccessStatus"/>.</summary>
    public ParentPortalAccessStatus Status { get; set; } = ParentPortalAccessStatus.Pending;

    /// <summary>
    /// The NORMALIZED Egyptian mobile number the parent typed (11 digits, leading 0), or null
    /// when they skipped it.
    ///
    /// ALWAYS STORED NORMALIZED — <c>EgyptianPhoneNumber.Normalize</c> is the only writer. The
    /// trusted-phone grant rule and phone-wide revocation both compare this column with a plain
    /// SQL equality, so a raw or differently-formatted value here would silently break BOTH: a
    /// returning parent would be pushed back into the inbox, and a revocation would miss rows.
    ///
    /// It is never an authentication credential on its own — it only decides whether a request
    /// skips the queue. Shown IN FULL to the teacher (the endpoints are Student/Edit + tenant
    /// scoped, and the teacher usually has the number on the roster anyway).
    /// </summary>
    public string? ClaimedPhone { get; set; }

    /// <summary>
    /// The name the parent typed for themselves when requesting access ("Ahmed's mum", "Mona Samir").
    ///
    /// Self-declared and UNVERIFIED — this is a recognition aid for the teacher deciding whether to
    /// approve, never an identity or an authentication factor. The portal form requires it; the API
    /// keeps it optional so the already-deployed portal cannot start 400-ing the moment this ships,
    /// and so historical rows stay valid. Trimmed and length-checked, never normalized further:
    /// unlike ClaimedPhone nothing compares this column, so there is no canonical form to preserve.
    ///
    /// Deliberately NOT echoed on the "pending" response — that payload is byte-identical across
    /// three different outcomes on purpose, and any branch-dependent field re-opens the
    /// student-code enumeration oracle.
    /// </summary>
    public string? ParentName { get; set; }

    /// <summary>
    /// True ONLY when the grant skipped the approval queue because <see cref="ClaimedPhone"/>
    /// matched the roster record's <see cref="TeacherStudent.ParentPhoneNumber"/>. A grant let in
    /// by the trusted-phone rule (<see cref="ParentPortalAccessOrigin.TrustedPhone"/>) is NOT
    /// "auto-approved" — a teacher did approve that number once — so this stays false there. Read
    /// <see cref="Origin"/> for the full story.
    /// </summary>
    public bool AutoApproved { get; set; }

    /// <summary>
    /// WHY this grant became Active — roster-phone match, an explicit teacher approval, or the
    /// trusted-phone rule. Null while Pending, and null on LEGACY rows written before this column
    /// shipped. See <see cref="ParentPortalAccessOrigin"/>.
    /// </summary>
    public ParentPortalAccessOrigin? Origin { get; set; }

    /// <summary>UTC timestamp the parent submitted the request.</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>UTC timestamp the teacher approved or rejected the request. Null while Pending.</summary>
    public DateTime? RespondedAt { get; set; }

    /// <summary>
    /// <c>User.Id</c> of the teacher/assistant who approved, rejected or revoked this grant.
    /// PLAIN audit column — deliberately NO foreign key, mirroring
    /// <see cref="StudentTeacherLink.RespondedByUserId"/> (§7.2b "End-of-link audit").
    /// Null on an auto-approved grant and on a parent-initiated self-revoke.
    /// </summary>
    public long? RespondedByUserId { get; set; }

    /// <summary>
    /// UTC timestamp of the last authenticated read this device made. Refreshed out-of-band by
    /// <c>TouchLastSeenAsync</c> (ExecuteUpdate, no tracking) so it never joins a read's change
    /// tracker. Drives the teacher's "last opened" column.
    /// </summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// SHA-256 hex of the caller IP the portal forwarded (<c>X-Portal-Client-IP</c>). Audit only —
    /// never compared, never exposed. Null when the portal did not forward one.
    /// </summary>
    public string? RequestIpHash { get; set; }

    /// <summary>Raw User-Agent of the requesting browser, truncated to 256 chars. Audit only.</summary>
    public string? UserAgent { get; set; }
}
