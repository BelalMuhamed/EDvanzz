using System.ComponentModel.DataAnnotations;
using Edvanz.Domain.Enums;

namespace Edvanz.Application.Dtos.ParentPortal;

// ══════════════════════════════════════════════════════════════════════════
// TEACHER SIDE of the parent portal (api/teacher/parent-portal)
//
// PHONE NUMBERS ARE RETURNED IN FULL (changed 2026-09-02, was masked). The teacher has to
// recognize who is asking — "that's Ahmed's mum" — and be able to ring them back; a masked number
// makes an approve/reject decision guesswork. These endpoints are [Authorize] + Student/Edit +
// tenant-scoped, and the teacher normally has the same number on the roster already.
// ══════════════════════════════════════════════════════════════════════════

/// <summary>One row in the teacher's pending parent-request inbox.</summary>
public class ParentPortalRequestListItemDto
{
    /// <summary>Grant id — the value passed to approve / reject / bulk.</summary>
    public long Id { get; set; }

    /// <summary>The roster record the parent asked to follow.</summary>
    public long TeacherStudentId { get; set; }

    /// <summary>Student's name, or null when the roster record has since been deleted.</summary>
    public string? StudentName { get; set; }

    /// <summary>Student's roster code, or null when the roster record has since been deleted.</summary>
    public string? StudentCode { get; set; }

    /// <summary>The full normalized number the parent typed (e.g. "01012345678"). Null when they skipped it.</summary>
    public string? ClaimedPhone { get; set; }

    /// <summary>
    /// The name the parent gave for themselves. Self-declared and UNVERIFIED — a recognition aid
    /// for the teacher, never proof of identity. Null on grants created before the portal asked
    /// for it, so the client must still render a row that has only a phone number.
    /// </summary>
    public string? ParentName { get; set; }

    /// <summary>
    /// Does the number this parent TYPED equal the one saved on the student? On a PENDING row it
    /// is normally false (a match would have auto-approved) — it is the teacher's cue that this
    /// parent could not be verified automatically. It can turn true after the fact if the teacher
    /// added the number to the student while the request was already waiting.
    ///
    /// NOT the same question as <see cref="StudentHasParentPhone"/> — read both together.
    /// </summary>
    public bool PhoneMatchesRoster { get; set; }

    /// <summary>
    /// Does the student have ANY parent number saved on their record right now?
    ///
    /// <para>Drives the default of the inline "save this number to the student" checkbox on the
    /// approve card: tick it ON only when this is FALSE. Approval never overwrites an existing
    /// number, so offering the option when one is already saved shows the teacher a choice that
    /// was never real — the save would come back <c>AlreadySaved</c> or
    /// <c>StudentHasDifferentPhone</c>.</para>
    ///
    /// <para>DISTINCT FROM <see cref="PhoneMatchesRoster"/>, and the two are easily confused:
    /// that one asks "is the typed number the saved one?", this asks "is there a saved one at
    /// all?". The combination <c>studentHasParentPhone: true</c> + <c>phoneMatchesRoster:
    /// false</c> is the CONFLICT case — a different number is on file — where the checkbox must be
    /// off AND the teacher warned before they approve a stranger.</para>
    /// </summary>
    public bool StudentHasParentPhone { get; set; }

    public DateTime RequestedAt { get; set; }

    /// <summary>Serialized as a string ("Pending"). Always Pending in the inbox; present for symmetry with the followers list.</summary>
    public ParentPortalAccessStatus Status { get; set; }
}

/// <summary>One row in the "who follows this student?" panel.</summary>
public class ParentPortalFollowerListItemDto
{
    public long Id { get; set; }
    public long TeacherStudentId { get; set; }
    public string? StudentName { get; set; }
    public string? StudentCode { get; set; }

    /// <summary>The full normalized number the parent typed (e.g. "01012345678"), or null.</summary>
    public string? ClaimedPhone { get; set; }

    /// <summary>The parent's self-declared name; null on grants that predate the portal asking for it.</summary>
    public string? ParentName { get; set; }

    /// <summary>Serialized as a string: "Active" or "Pending".</summary>
    public ParentPortalAccessStatus Status { get; set; }

    /// <summary>True ONLY when the grant skipped the inbox because the phone matched the roster (i.e. <see cref="Origin"/> = RosterPhone).</summary>
    public bool AutoApproved { get; set; }

    /// <summary>
    /// WHY this follower has access, serialized as a string: "RosterPhone" | "TeacherApproved" |
    /// "TrustedPhone". Null while Pending, and null on legacy rows created before this field
    /// shipped — fall back to <see cref="AutoApproved"/> when it is null on an Active row.
    /// "TrustedPhone" means the number already held an approved grant on this student, so a new
    /// device of theirs got in without troubling the teacher.
    /// </summary>
    public ParentPortalAccessOrigin? Origin { get; set; }

    public DateTime RequestedAt { get; set; }
    public DateTime? RespondedAt { get; set; }

    /// <summary>Last time this follower opened the portal. Null if never since being approved.</summary>
    public DateTime? LastSeenAt { get; set; }
}

/// <summary>
/// OPTIONAL body of <c>POST api/teacher/parent-portal/requests/{id}/approve</c>. Send no body at
/// all for a plain approval.
/// </summary>
public class ParentPortalApproveRequestDto
{
    /// <summary>
    /// When true, also write the approved parent's number onto the student's roster record —
    /// turning each approval into roster data quality: next time that parent is auto-approved with
    /// no teacher involvement, and the "students missing a parent number" count drops.
    ///
    /// NEVER overwrites an existing number. If the student already has one (same or different) the
    /// flag is ignored and the response says why via <c>phoneSaveSkippedReason</c>.
    /// </summary>
    public bool SavePhoneToStudent { get; set; }
}

/// <summary>Result of an approval: the follower row plus what happened to the optional phone save.</summary>
public class ParentPortalApproveResultDto
{
    /// <summary>The now-Active follower.</summary>
    public ParentPortalFollowerListItemDto Follower { get; set; } = new();

    /// <summary>True when the approved number was written onto the student's roster record by this call.</summary>
    public bool PhoneSavedToStudent { get; set; }

    /// <summary>
    /// Why the phone was NOT saved, or null when it was saved (or never requested). Stable
    /// literals — see <c>ParentPortalConstants.PhoneSaveSkipReasons</c>:
    /// "NoPhoneOnRequest" (the parent left the number blank),
    /// "AlreadySaved" (the student already has this exact number),
    /// "StudentHasDifferentPhone" (a different number is on file and is never overwritten).
    /// </summary>
    public string? PhoneSaveSkippedReason { get; set; }
}

/// <summary>Body of <c>POST api/teacher/parent-portal/requests/bulk</c>.</summary>
public class ParentPortalBulkActionDto
{
    /// <summary>Grant ids to act on. Ids outside the caller's tenant are silently ignored, never an error.</summary>
    [Required]
    public List<long> Ids { get; set; } = new();

    /// <summary>"approve" or "reject" (case-insensitive). Anything else is a 400.</summary>
    [Required]
    public string Action { get; set; } = string.Empty;
}

/// <summary>
/// Outcome of a bulk approve/reject. <see cref="ProcessedIds"/> and <see cref="SkippedIds"/>
/// together account for every id the client sent that was not a duplicate or non-positive.
/// </summary>
public class ParentPortalBulkResultDto
{
    /// <summary>Rows actually transitioned — always equals <c>ProcessedIds.Count</c>.</summary>
    public int Affected { get; set; }

    /// <summary>
    /// The ids that actually transitioned. The client MUST clear its list from THIS, not from
    /// <see cref="Affected"/>: acting on the count alone assumes everything sent succeeded and
    /// would show the teacher silently-skipped requests as approved.
    /// </summary>
    public List<long> ProcessedIds { get; set; } = new();

    /// <summary>Ids that were skipped because they were not pending, not this teacher's, or their student has been removed.</summary>
    public List<long> SkippedIds { get; set; } = new();
}

/// <summary>
/// Outcome of revoking a follower. Revocation is PHONE-WIDE, so it usually ends more than the one
/// row the teacher tapped — every device that number had approved for this student.
/// </summary>
public class ParentPortalRevokeResultDto
{
    /// <summary>
    /// How many grants were ended — i.e. how many devices lost access. Drives the "removed on 3
    /// devices" confirmation. 1 for a device-only (no phone) grant.
    /// </summary>
    public int RevokedCount { get; set; }

    /// <summary>The full number that was revoked, or null for a device-only grant that carried no phone.</summary>
    public string? RevokedPhone { get; set; }

    /// <summary>The student the access was for.</summary>
    public long TeacherStudentId { get; set; }
}

/// <summary>Counters for the teacher's parent-portal settings screen.</summary>
public class ParentPortalSummaryDto
{
    /// <summary>Requests waiting for a decision.</summary>
    public int PendingCount { get; set; }

    /// <summary>Distinct students with at least one ACTIVE follower.</summary>
    public int FollowedStudentsCount { get; set; }

    /// <summary>
    /// Active roster records with no parent phone on file. Those students can never be
    /// auto-approved, so every request for them lands in the inbox — the number is the teacher's
    /// nudge to complete their roster.
    /// </summary>
    public int StudentsMissingParentPhone { get; set; }

    /// <summary>Whether the teacher currently accepts portal followers (<c>TeacherConfiguration.ParentPortalEnabled</c>).</summary>
    public bool PortalEnabled { get; set; }

    /// <summary>
    /// Whether THIS caller may actually manage parent requests for the resolved teacher — i.e.
    /// whether they hold <c>Student / Edit</c>.
    ///
    /// <para>THIS FIELD EXISTS SO AN UNGATED READ CAN GATE A UI AFFORDANCE WHOSE REAL ENDPOINTS
    /// ARE PERMISSION-GATED. <c>GET /summary</c> is deliberately ungated (a 403 here is read by the
    /// app's <c>ActingTeacherUnavailableInterceptor</c> as "acting teacher gone" and ejects the
    /// operator from the acting-as shell), but every route it links to — requests, approve,
    /// reject, bulk, followers, revoke — still requires <c>Student / Edit</c>. Without this flag a
    /// caller who lacks that permission would be shown a real pending badge, tap it, and hit a 403
    /// on <c>GET /requests</c>. The client MUST hide the menu entry and the badge when this is
    /// false, so nobody is ever offered a door that will slam.</para>
    ///
    /// <para>Computed through the SAME <c>PermissionRequirement(Student, Edit)</c> that the
    /// <c>[ModulePermission]</c> attribute evaluates, so it cannot drift from the real gate.
    /// False whenever no acting teacher resolves (counters are all zero in that case too).</para>
    /// </summary>
    public bool CanManage { get; set; }

    /// <summary>
    /// Whether the teacher's PLAN includes the parent follow-up page (Full / ManagerialPlus, or
    /// no live subscription — free tier restricts nothing). False only under a live plain
    /// Managerial plan. Distinct from <see cref="PortalEnabled"/> (the teacher's own opt-in):
    /// the app renders the settings section as a LOCKED upsell when this is false, regardless of
    /// the stored toggle. Mirrors <c>ParentPortalService.IsPortalEligibleAsync</c>'s plan half —
    /// additive; older clients ignore it. Defaults TRUE so an older server response never locks.
    /// </summary>
    public bool PortalAllowed { get; set; } = true;
}
