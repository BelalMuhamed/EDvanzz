using System.ComponentModel.DataAnnotations;
using Edvanz.Application.Dtos.Attendance;
using Edvanz.Application.Dtos.ParentUser;
using Edvanz.Application.Dtos.Payment;

namespace Edvanz.Application.Dtos.ParentPortal;

// ══════════════════════════════════════════════════════════════════════════
// PUBLIC PARENT PORTAL (parent.edvanz.io) — wire contract
//
// Every shape here is consumed by a PHP page and a Flutter client, so field names and the
// lowercase state literals are a FIXED contract. Add fields, never rename or reorder.
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Body of <c>POST api/parent-portal/access-requests</c> — the parent typing their way in.
/// </summary>
public class ParentPortalAccessRequestDto
{
    /// <summary>The teacher's public 8-digit code, as printed on their share card.</summary>
    [Required]
    public string TeacherCode { get; set; } = string.Empty;

    /// <summary>
    /// The TEACHER'S roster code for the student (e.g. "A12") — <c>TeacherStudent.StudentCode</c>,
    /// unique per teacher. NOT the student account code (<c>StudentUser.StudentAccountCode</c>).
    /// </summary>
    [Required]
    public string StudentCode { get; set; } = string.Empty;

    /// <summary>
    /// The parent's Egyptian mobile number. When it matches the student record's parent phone the
    /// grant is auto-approved; otherwise the request waits for the teacher. Any common spelling is
    /// accepted (Arabic-Indic digits, spaces, +20 …) — it is normalized server-side.
    ///
    /// REQUIRED once <c>ParentPortal__RequirePhone</c> is on (400 <c>ParentPortalPhoneRequired</c>
    /// when blank); the flag exists only so the API can ship before the portal build that marks the
    /// field required. It is not a formality: the phone is what admits a parent with no teacher
    /// involvement at all, and the ONLY thing that lets an approved parent back in from a new
    /// handset or a browser that lost its cookie. A phone-less grant is pinned to one browser
    /// forever.
    ///
    /// Same reason as <see cref="ParentName"/> for having no <c>[Required]</c> attribute: the check
    /// lives in the service so the failure is localized.
    /// </summary>
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// The parent's own name, so the teacher can tell who is asking instead of judging a bare phone
    /// number. Self-declared and unverified.
    ///
    /// REQUIRED — a blank value fails with <c>ParentPortalNameRequired</c>. The portal enforced this
    /// first and shipped ahead of the API precisely so that tightening it here could not break the
    /// live sign-in form; do not relax it back without checking what the deployed portal sends.
    ///
    /// Deliberately left <c>string?</c> with no <c>[Required]</c> attribute: model-binding validation
    /// would return an unlocalized 400 that the portal cannot render, instead of the localized
    /// <c>Result</c> failure the rest of this endpoint produces. The check lives in the service.
    ///
    /// The stored COLUMN stays nullable — grants created before this field existed have no name and
    /// remain valid; they are healed by the fill-only backfill in ParentPortalService.
    /// </summary>
    public string? ParentName { get; set; }

    /// <summary>
    /// Opaque per-browser id minted by the portal. Only its SHA-256 is stored; the raw value never
    /// touches the database and is required on every subsequent read.
    /// </summary>
    [Required]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Preferred language ("en" / "ar"). Reserved for the portal's own rendering; the API localizes from Accept-Language.</summary>
    public string? Language { get; set; }
}

/// <summary>
/// Result of an access request.
///
/// SECURITY — read this before "fixing" a null: on a <c>pending</c> result the student fields are
/// ALWAYS null, even when the student is real and the row was written. A pending response for a
/// real student and one for a student code that does not exist must be byte-identical, otherwise
/// the endpoint becomes a roster-enumeration oracle (student codes are a sequential counter).
/// Student details appear only on an <c>active</c> result, which requires a phone match.
///
/// A teacher who has the portal switched off is NOT folded into this shape — that call fails with
/// 403 <c>ParentPortalDisabled</c>, because the flag is already public through the preview
/// endpoint and a fake "pending" would strand a real parent forever.
/// </summary>
public class ParentPortalAccessRequestResultDto
{
    /// <summary>"active" (auto-approved) or "pending". Never anything else on a success.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The teacher's display name — safe to echo: the teacher code is public.</summary>
    public string TeacherName { get; set; } = string.Empty;

    /// <summary>Student's name. Populated on "active" only (see the class remarks).</summary>
    public string? StudentName { get; set; }

    /// <summary>Student's roster code. Populated on "active" only.</summary>
    public string? StudentCode { get; set; }

    /// <summary>The roster record id used on every subsequent read route. Populated on "active" only.</summary>
    public long? RosterId { get; set; }
}

/// <summary><c>GET api/parent-portal/teachers/{teacherCode}/preview</c> — what the portal shows before the parent commits.</summary>
public class ParentPortalTeacherPreviewDto
{
    public string TeacherName { get; set; } = string.Empty;

    /// <summary>Subject label in the caller's language; empty when the teacher has none on file.</summary>
    public string SubjectName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this teacher currently accepts portal followers. False → the portal shows
    /// "ask your teacher to enable it" instead of the code form.
    /// </summary>
    public bool PortalEnabled { get; set; }
}

/// <summary>Which sections the teacher shares with parents. Re-read LIVE on every state call — a teacher can revoke a section at any moment.</summary>
public class ParentPortalVisibilityDto
{
    public bool Attendance { get; set; }
    public bool Payments { get; set; }

    /// <summary>True when EITHER exam channel (offline or online) is shared; the grades list then carries only the shared channel(s).</summary>
    public bool Grades { get; set; }
}

/// <summary><c>GET api/parent-portal/access</c> — everything the portal needs to decide which screen to render.</summary>
public class ParentPortalAccessStateDto
{
    /// <summary>
    /// "active" | "pending" | "rejected" | "revoked" | "disabled" | "studentRemoved" | "none".
    /// Only "active" grants data access. See <c>ParentPortalConstants.States</c>.
    /// </summary>
    public string State { get; set; } = string.Empty;

    public string TeacherName { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;

    /// <summary>Null unless the grant is (or was) approved — never leaked on a plain "pending".</summary>
    public string? StudentName { get; set; }

    /// <summary>Null unless the grant is (or was) approved.</summary>
    public string? StudentCode { get; set; }

    /// <summary>
    /// The roster id this response DESCRIBES — the selected child. Null unless approved.
    ///
    /// No longer "the only id this device may read": a device may hold an active grant per child
    /// (see <see cref="Students"/>). It is still the only id this PAYLOAD describes, and a read for
    /// any other id is still authorized against the device's own grants, never trusted from the
    /// route.
    /// </summary>
    public long? RosterId { get; set; }

    /// <summary>The student's current session name, when assigned to one.</summary>
    public string? SessionName { get; set; }

    /// <summary>
    /// EVERY child this browser currently follows, newest grant first — the child switcher.
    ///
    /// Added 2026-09-11. Reads used to resolve a device to its newest active grant alone, so a
    /// parent of siblings who signed in for a second child silently lost the first: every screen
    /// followed the newest grant, and re-entering the older child's code still landed on the newer
    /// one. The only escape was to end following altogether. Empty on any non-active state, and a
    /// single-child parent gets a one-item list, so nothing changes for them.
    /// </summary>
    public List<ParentPortalFollowedStudentDto> Students { get; set; } = new();

    public ParentPortalVisibilityDto Visibility { get; set; } = new();
}

/// <summary>
/// One child a browser follows. Carries its own teacher/subject because the two children may sit
/// with DIFFERENT teachers, and the switcher has to label them apart.
/// </summary>
public class ParentPortalFollowedStudentDto
{
    public long RosterId { get; set; }
    public string StudentName { get; set; } = string.Empty;
    public string StudentCode { get; set; } = string.Empty;
    public string TeacherName { get; set; } = string.Empty;

    /// <summary>True for the child this payload describes.</summary>
    public bool IsSelected { get; set; }
}

/// <summary>Header block shared by the portal's dashboard screen.</summary>
public class ParentPortalHeaderDto
{
    public string StudentName { get; set; } = string.Empty;
    public string StudentCode { get; set; } = string.Empty;
    public string TeacherName { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public string? SessionName { get; set; }

    /// <summary>Scoped month as "yyyy-MM" (teacher-local Africa/Cairo current month).</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>Full month name, invariant culture (e.g. "March") — matches the student home aggregate's convention.</summary>
    public string MonthLabel { get; set; } = string.Empty;
}

/// <summary>Attendance section of a portal response. <c>Data</c> is null whenever <c>Visible</c> is false.</summary>
public class ParentPortalAttendanceSectionDto
{
    public bool Visible { get; set; }
    public MonthlyAttendanceSummaryDto? Data { get; set; }
}

/// <summary>Payments section of a portal response. <c>Data</c> is null whenever <c>Visible</c> is false.</summary>
public class ParentPortalPaymentsSectionDto
{
    public bool Visible { get; set; }
    public StudentPaymentTrackingDto? Data { get; set; }
}

/// <summary>Grades section of a portal response. <c>Data</c> is null whenever <c>Visible</c> is false.</summary>
public class ParentPortalGradesSectionDto
{
    public bool Visible { get; set; }
    public ParentPortalGradesDto? Data { get; set; }
}

/// <summary>Aggregates over EVERY visible exam row (not just the current page).</summary>
public class ParentPortalGradesSummaryDto
{
    /// <summary>Rows that produced a valid percentage.</summary>
    public int CompletedCount { get; set; }

    /// <summary>Rows with no usable grade yet (upcoming, pending, missed, no max grade).</summary>
    public int UngradedCount { get; set; }

    public decimal? AveragePercentage { get; set; }
    public decimal? HighestPercentage { get; set; }
    public decimal? LowestPercentage { get; set; }
}

/// <summary>
/// Grades payload: the whole-history summary plus one page of merged offline+online rows,
/// newest first.
/// </summary>
public class ParentPortalGradesDto
{
    public ParentPortalGradesSummaryDto Summary { get; set; } = new();

    /// <summary>The requested page of rows, sorted by date descending.</summary>
    public List<ParentGradeRowDto> Items { get; set; } = new();

    // ── Paging metadata (additive; the summary above is always whole-history) ──

    /// <summary>1-based page number that produced <see cref="Items"/>.</summary>
    public int Page { get; set; }

    /// <summary>Rows per page actually applied (clamped server-side).</summary>
    public int PageSize { get; set; }

    /// <summary>Total merged rows across both channels.</summary>
    public int TotalCount { get; set; }

    /// <summary>Total pages at the applied <see cref="PageSize"/>.</summary>
    public int TotalPages { get; set; }
}

/// <summary><c>GET api/parent-portal/students/{rosterId}/dashboard</c> — the whole portal home in one call.</summary>
public class ParentPortalDashboardDto
{
    public ParentPortalHeaderDto Header { get; set; } = new();
    public ParentPortalAttendanceSectionDto Attendance { get; set; } = new();
    public ParentPortalPaymentsSectionDto Payments { get; set; } = new();
    public ParentPortalGradesSectionDto Grades { get; set; } = new();
}
