using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Attendance;
using Edvanz.Application.Dtos.ParentPortal;
using Edvanz.Application.Dtos.Payment;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// PUBLIC parent-portal surface (parent.edvanz.io → this API, server-to-server). Read-only for the
/// parent: one device follows ONE roster student under ONE teacher.
///
/// THE CALLER IS THE DEVICE, BUT THE TRUST IS THE PHONE. Every read takes a <c>deviceHash</c>
/// (SHA-256 of the portal's raw device id) and resolves the grant from it, so the device says
/// WHICH grant is calling. What EARNS a grant, though, is the phone number: a request is admitted
/// immediately when that number matches the student's roster parent phone or already holds an
/// approved grant on that student. A parent therefore keeps access across browsers and handsets —
/// and revocation has to be phone-wide to mean anything (see <c>ITeacherParentPortalService</c>).
///
/// A roster id supplied on the route is NEVER trusted — it is only compared against the grant,
/// exactly the rule CLAUDE.md §3.3 states for teacherId and BUG-12 generalized to every identity id.
///
/// EVERY read re-validates the full chain LIVE: teacher active → portal enabled → subscription
/// eligible → roster row still exists → grant still Active → the module's own parent-visibility
/// flag. Nothing is cached in the grant row, so a teacher revoking a section or switching the
/// portal off takes effect on the very next request.
/// </summary>
public interface IParentPortalService
{
    /// <summary>
    /// Public teacher card for the "is this the right teacher?" step: name, subject, and whether
    /// they accept portal followers. The teacher code is public by design, so this reveals nothing
    /// a share card does not already.
    /// </summary>
    Task<Result<ParentPortalTeacherPreviewDto>> GetTeacherPreviewAsync(string teacherCode, string? language);

    /// <summary>
    /// Creates (or resurfaces) a grant: resolves the teacher by code, checks eligibility, resolves
    /// the roster student by code, then decides whether the typed phone is already trusted for
    /// that student — it matches the roster's parent phone (<c>Origin = RosterPhone</c>,
    /// <c>AutoApproved = true</c>) or it already holds an Active grant a teacher vetted
    /// (<c>Origin = TrustedPhone</c>). Either way the row is written Active; otherwise Pending.
    ///
    /// A re-request within 24h of a REJECTION on the same (student, device) or (student, phone) is
    /// discarded through the uniform pending payload, writing nothing.
    ///
    /// <b>UNIFORM RESPONSE ON THE STUDENT AXIS — the load-bearing anti-enumeration rule.</b> A
    /// request for a student code that does NOT exist returns the EXACT SAME success payload as a
    /// genuine pending request (<c>state: "pending"</c>, no student fields) and writes nothing.
    /// Roster codes are a sequential counter (A1, A2 … Z999) and teacher codes are public, so any
    /// divergence there — a 404, a different code, an extra field — turns this endpoint into a
    /// roster oracle.
    ///
    /// The TEACHER axis is deliberately NOT uniform: a teacher who has the portal switched off
    /// gets an honest 403 <c>ParentPortalDisabled</c>. That fact is already public through
    /// <see cref="GetTeacherPreviewAsync"/>, so hiding it protects nothing, while a fake "pending"
    /// would strand a real parent on a screen that can never resolve (nothing is written, so no
    /// teacher can ever approve it).
    /// </summary>
    /// <param name="dto">The parent's typed codes, optional phone, and the portal's device id.</param>
    /// <param name="clientIp">Caller IP forwarded by the portal (<c>X-Portal-Client-IP</c>). Hashed for audit; never compared. Optional.</param>
    /// <param name="userAgent">Requesting browser's User-Agent. Audit only. Optional.</param>
    Task<Result<ParentPortalAccessRequestResultDto>> RequestAccessAsync(
        ParentPortalAccessRequestDto dto, string? clientIp, string? userAgent);

    /// <summary>
    /// Where this device stands right now, with the LIVE visibility flags. Returns a
    /// <c>state</c> the portal renders directly (active / pending / rejected / revoked /
    /// disabled / studentRemoved / none) — never an error for a merely-unapproved device.
    /// </summary>
    /// <param name="deviceHash">Hashed device id from the X-Portal-Device header.</param>
    /// <param name="rosterId">
    /// Which followed child to describe. Omit for the newest active grant — the behaviour before
    /// multi-child support, and what a one-child parent always gets. An id this device holds no
    /// active grant for falls back to the newest rather than erroring: the selection is a UI
    /// preference, and a child whose access was revoked must degrade to the other child.
    /// </param>
    Task<Result<ParentPortalAccessStateDto>> GetAccessStateAsync(string deviceHash, long? rosterId = null);

    /// <summary>The whole portal home in one call: header + attendance + payments + grades, each behind its own visibility flag.</summary>
    Task<Result<ParentPortalDashboardDto>> GetDashboardAsync(string deviceHash, long rosterId);

    /// <summary>One month of attendance. Null year/month → the teacher-local (Africa/Cairo) current month.</summary>
    Task<Result<ParentPortalAttendanceSectionDto>> GetAttendanceAsync(
        string deviceHash, long rosterId, int? year, int? month);

    /// <summary>The student's payment tracking screen (paid / overdue / upcoming), parent-gated.</summary>
    Task<Result<ParentPortalPaymentsSectionDto>> GetPaymentsAsync(string deviceHash, long rosterId);

    /// <summary>Merged offline + online exam results, newest first, with whole-history aggregates.</summary>
    Task<Result<ParentPortalGradesSectionDto>> GetGradesAsync(
        string deviceHash, long rosterId, int page, int pageSize);

    /// <summary>
    /// The parent removing their own access from this device ("sign out of following"). Ends the
    /// live grant as <c>Revoked</c> with no responder recorded, which is how the state endpoint
    /// tells a self-removal apart from a teacher revocation.
    /// </summary>
    Task<Result<bool>> RevokeOwnAccessAsync(string deviceHash);
}
