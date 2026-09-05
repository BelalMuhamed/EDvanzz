using System.ComponentModel.DataAnnotations;

namespace Edvanz.Application.Dtos.TeacherLinks;

// ════════════════════════════════════════════════════════════════════════════
// TEACHER-SIDE DTOs for the student-teacher link request/approval flow.
// Consumed by TeacherStudentLinksController (route api/teacher/student-links).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The teacher's shareable linking code (GET my-code).
/// Students type this code when sending a link request.
/// </summary>
public class TeacherCodeDto
{
    /// <summary>The teacher's unique, immutable 8-digit code (AAM-FR-03.3).</summary>
    public string TeacherCode { get; set; } = null!;
}

/// <summary>
/// One pending link request on the teacher's inbox.
/// Combines what the student typed with their account identity, plus a
/// suggested roster match when the typed student code resolves to one.
/// </summary>
public class TeacherLinkRequestListItemDto
{
    /// <summary>Link row id — pass to the accept/reject endpoints.</summary>
    public long LinkId { get; set; }

    /// <summary>Name the student typed when sending the request.</summary>
    public string? RequestedStudentName { get; set; }

    /// <summary>Teacher-assigned student code the student claims to have (optional).</summary>
    public string? RequestedStudentCode { get; set; }

    /// <summary>UTC timestamp the request was submitted.</summary>
    public DateTime? RequestedAt { get; set; }

    /// <summary>The requesting account's platform-wide student account code.</summary>
    public string StudentAccountCode { get; set; } = null!;

    /// <summary>The requesting account's registered full name.</summary>
    public string StudentFullName { get; set; } = null!;

    /// <summary>The requesting account's registered phone number, if any.</summary>
    public string? StudentPhoneNumber { get; set; }

    /// <summary>
    /// Roster record matched from <see cref="RequestedStudentCode"/>, or null when
    /// no code was supplied / no match exists. The teacher can accept with this
    /// suggestion or select a different roster record explicitly.
    /// </summary>
    public RosterStudentSuggestionDto? SuggestedMatch { get; set; }
}

/// <summary>
/// A TeacherStudent roster record suggested as the accept-time binding target.
/// </summary>
public class RosterStudentSuggestionDto
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;

    /// <summary>
    /// True when another student account already holds an Active link to this
    /// roster record — accepting with it will be rejected (one account per record).
    /// </summary>
    public bool IsAlreadyLinked { get; set; }
}

/// <summary>
/// Accept-request body. Accepting only CONNECTS the account (LinkStatus = Active);
/// binding it to a student record is a SEPARATE step (see BindStudentLinkDto and
/// the /bind endpoint). <see cref="TeacherStudentId"/> or <see cref="StudentCode"/>
/// is an optional convenience to link in the same call ("Accept &amp; link"): when
/// supplied it is validated and bound atomically — an id/code that does not resolve
/// FAILS the accept (it never silently accepts unbound when the caller tried to
/// link). When BOTH are omitted the link is accepted UNBOUND — connected but Not
/// linked, no data access — and can be linked later.
/// Unknown JSON fields are rejected (400) so a mistyped field cannot silently
/// downgrade an "Accept &amp; link" into a plain accept.
/// </summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public class AcceptLinkRequestDto
{
    /// <summary>
    /// Optional student record to bind at accept time (takes precedence over the
    /// code). Omit both to accept without linking (the account is connected but
    /// sees nothing until it is linked).
    /// </summary>
    public long? TeacherStudentId { get; set; }

    /// <summary>
    /// Optional TEACHER-assigned roster student code (TeacherStudent.StudentCode,
    /// e.g. "A12") to resolve the record from when no id is given. This is NOT the
    /// student's account code (the 10-char studentAccountCode shown on link rows).
    /// </summary>
    [MaxLength(20)]
    public string? StudentCode { get; set; }
}

/// <summary>
/// One Active link on the teacher's linked-students screen.
/// </summary>
public class LinkedStudentListItemDto
{
    /// <summary>Link row id — pass to the bulk remove endpoint.</summary>
    public long LinkId { get; set; }

    /// <summary>UTC timestamp the link became Active.</summary>
    public DateTime LinkedAt { get; set; }

    public string StudentAccountCode { get; set; } = null!;
    public string StudentFullName { get; set; } = null!;
    public string? StudentPhoneNumber { get; set; }

    /// <summary>Bound student record id — null when Not linked (accepted only) or the record was deleted.</summary>
    public long? TeacherStudentId { get; set; }
    public string? RosterStudentName { get; set; }
    public string? RosterStudentCode { get; set; }

    /// <summary>
    /// True when this accepted connection is bound to a student record
    /// (<see cref="TeacherStudentId"/> is set) — i.e. the account can see the
    /// teacher's data. False = accepted/connected but Not linked yet (no access).
    /// Drives the Linked / Not linked badge on the My Students screen.
    /// </summary>
    public bool IsLinked { get; set; }

    /// <summary>
    /// Device lock: true when this student has a device registered for this teacher (they can open
    /// the teacher only from it). Only meaningful when the page's <c>deviceLockEnabled</c> is true.
    /// The teacher/assistant clears it via the reset-device endpoint so the student can register a new phone.
    /// </summary>
    public bool IsDeviceRegistered { get; set; }

    /// <summary>UTC timestamp the current device was registered, or null when none is registered.</summary>
    public DateTime? DeviceBoundAt { get; set; }
}

/// <summary>
/// Paginated linked-students envelope. Extends the shared <see cref="PaginatedResponse{T}"/>
/// (keeping <c>totalCount</c>/<c>page</c>/<c>pageSize</c>/<c>totalPages</c>/<c>data</c>) with
/// the two connection-status headcounts the mobile app reads: <c>linkedCount</c> (Active links
/// bound to a roster record) and <c>unlinkedCount</c> (Active but Not linked). Both are counted
/// across the WHOLE Active-link set, not just the current page.
/// </summary>
public class LinkedStudentsPageResponse : PaginatedResponse<List<LinkedStudentListItemDto>>
{
    /// <summary>Active links bound to a roster record (<c>TeacherStudentId != null</c>).</summary>
    public int linkedCount { get; set; }

    /// <summary>Active links not yet bound to a roster record (= totalCount − linkedCount).</summary>
    public int unlinkedCount { get; set; }

    /// <summary>
    /// Whether this teacher has the device lock turned on. When false, the app hides all device
    /// status/reset UI (the <c>isDeviceRegistered</c> flags on rows are irrelevant).
    /// </summary>
    public bool deviceLockEnabled { get; set; }

    /// <summary>
    /// How many student APP ACCOUNTS this teacher may have linked at once
    /// (<c>Teacher.LinkedStudentCapacity</c>) — the limit the subscription price is based on.
    /// </summary>
    public int linkedStudentCapacity { get; set; }

    /// <summary>
    /// Seats still free: capacity minus the live BOUND-link count, clamped at 0 (never negative,
    /// so a limit lowered below current usage reads as "0 left" rather than a negative number).
    /// Computed over ALL of the teacher's links, so it is unaffected by the page's search filter —
    /// unlike <see cref="linkedCount"/>.
    /// </summary>
    public int linkedStudentsRemaining { get; set; }
}

/// <summary>
/// Bulk remove body — one or many link ids (the user story allows removing
/// a single linked student or several at once).
/// </summary>
public class RemoveLinkedStudentsDto
{
    [Required]
    [MinLength(1)]
    public List<long> LinkIds { get; set; } = new();
}

/// <summary>
/// Bulk remove outcome: how many links were removed and which requested ids
/// were skipped (not found, not owned by this teacher, or not Active).
/// </summary>
public class RemoveLinkedStudentsResultDto
{
    public int RemovedCount { get; set; }
    public List<long> SkippedLinkIds { get; set; } = new();
}

/// <summary>
/// Bind (link) or re-bind an accepted connection to a student record — the
/// separate step that unlocks the student's access. Supply either an explicit
/// <see cref="TeacherStudentId"/> or a <see cref="StudentCode"/> (the
/// teacher-assigned code); the server resolves the record, enforces one account
/// per record, and points the link at it. Powers "Link a student" (Not linked →
/// Linked) and "Change linked student" (re-point). Unbinding has no body (/unbind).
/// </summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public class BindStudentLinkDto
{
    /// <summary>Explicit student record to link to (takes precedence over the code).</summary>
    public long? TeacherStudentId { get; set; }

    /// <summary>
    /// TEACHER-assigned roster student code (TeacherStudent.StudentCode, e.g. "A12")
    /// to resolve the record from, when no id is given. This is NOT the student's
    /// account code (the 10-char studentAccountCode shown on link rows).
    /// </summary>
    [MaxLength(20)]
    public string? StudentCode { get; set; }
}