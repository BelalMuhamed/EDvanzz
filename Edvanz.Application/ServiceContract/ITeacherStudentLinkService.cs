using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.TeacherLinks;
using Edvanz.Domain.Enums;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Teacher-side operations of the student-teacher link request/approval flow:
/// exposing the shareable teacher code, reviewing incoming link requests
/// (accept / reject), listing linked students, and removing one or many links.
///
/// The student-side half (sending requests, cancelling, listing own teachers)
/// lives on <see cref="IStudentUserService"/>.
///
/// All methods take <c>teacherId</c> resolved from the JWT by the controller
/// (assistants resolve to their owning tutor) — never from the route or body.
/// </summary>
public interface ITeacherStudentLinkService
{
    /// <summary>
    /// Returns the teacher's unique 8-digit TeacherCode — the only credential a
    /// student needs to SEND a link request (the teacher approves afterwards, so
    /// exposing the code is safe by design).
    /// </summary>
    Task<Result<TeacherCodeDto>> GetMyTeacherCodeAsync(long teacherId);

    /// <summary>
    /// Pages the Pending link requests addressed to this teacher, newest first.
    /// Each item carries the requesting account identity plus a suggested roster
    /// match when the student supplied a student code that resolves.
    /// </summary>
    Task<Result<PaginatedResponse<List<TeacherLinkRequestListItemDto>>>> GetPendingLinkRequestsAsync(
        long teacherId, int page, int pageSize);

    /// <summary>
    /// Accepts a Pending request, CONNECTING the account (LinkStatus = Active).
    /// Binding to a student record is separate: pass an explicit TeacherStudentId
    /// to link in the same call ("Accept &amp; link"), or omit it to accept UNBOUND
    /// (connected but Not linked — no data access) and link later via
    /// <see cref="BindStudentLinkAsync"/>. Accepting never fails for a missing
    /// binding; a supplied record already claimed by another account fails 409.
    /// Notifies the student (inbox + push) post-commit, best-effort.
    /// </summary>
    /// <param name="actingUserId">User.Id of the teacher/assistant performing the accept (audit).</param>
    Task<Result<LinkedStudentListItemDto>> AcceptLinkRequestAsync(
        long teacherId, long linkId, long actingUserId, AcceptLinkRequestDto dto);

    /// <summary>
    /// Links (binds) or re-links an accepted connection to one of the teacher's
    /// students — the step that actually unlocks the student's access, kept SEPARATE
    /// from accept. Resolves the target by explicit id or by student code, enforces
    /// one account per record (409), and re-points an already-linked connection when
    /// a different record is given ("Change linked student"). Fails 404 when the link
    /// or record is missing, 409 when the link is not Active, 400 when no target is
    /// supplied. Notifies the student (now has access) post-commit, best-effort.
    /// </summary>
    /// <param name="actingUserId">User.Id of the teacher/assistant performing the link (audit).</param>
    Task<Result<LinkedStudentListItemDto>> BindStudentLinkAsync(
        long teacherId, long linkId, long actingUserId, BindStudentLinkDto dto);

    /// <summary>
    /// SUPER-ADMIN variant of <see cref="BindStudentLinkAsync"/> — no tenant
    /// scope. Resolves the owning TeacherId from the link row itself (the
    /// linkId already fully identifies the tenant, so no TeacherId needs to
    /// be supplied or guessed by the caller), then delegates to the identical
    /// bind logic. Must only be reachable behind a roleOnly SuperAdmin gate.
    /// </summary>
    /// <param name="actingUserId">User.Id of the SuperAdmin performing the bind (audit).</param>
    Task<Result<LinkedStudentListItemDto>> BindStudentLinkForAdminAsync(
        long linkId, long actingUserId, BindStudentLinkDto dto);

    /// <summary>
    /// Removes the student-record binding from an accepted link. The connection
    /// stays Active (the student remains accepted) but loses data access until it is
    /// re-linked. Idempotent when already unbound. Fails 404 when the link is missing
    /// and 409 when it is not Active. Notifies the student post-commit, best-effort.
    /// </summary>
    /// <param name="actingUserId">User.Id of the teacher/assistant performing the unlink (audit).</param>
    Task<Result<LinkedStudentListItemDto>> UnbindStudentLinkAsync(
        long teacherId, long linkId, long actingUserId);

    /// <summary>
    /// SUPER-ADMIN variant of <see cref="UnbindStudentLinkAsync"/> — no tenant
    /// scope. Resolves the owning TeacherId from the link row itself, then
    /// delegates to the identical unbind logic. Must only be reachable behind
    /// a roleOnly SuperAdmin gate.
    /// </summary>
    /// <param name="actingUserId">User.Id of the SuperAdmin performing the unbind (audit).</param>
    Task<Result<LinkedStudentListItemDto>> UnbindStudentLinkForAdminAsync(
        long linkId, long actingUserId);

    /// <summary>
    /// Device lock: clears the student's registered device for this teacher so they can register a
    /// new phone the next time they open the teacher (with consent). Idempotent — clearing an
    /// already-empty binding succeeds. Leaves the connection/binding state untouched. Fails 404 when
    /// the link is missing for this teacher. Stamps <c>DeviceResetAt</c>/<c>DeviceResetByUserId</c> (audit).
    /// </summary>
    /// <param name="actingUserId">User.Id of the teacher/assistant performing the reset (audit).</param>
    Task<Result<LinkedStudentListItemDto>> ResetStudentDeviceAsync(
        long teacherId, long linkId, long actingUserId);

    /// <summary>
    /// SUPER-ADMIN device reset: same effect as <see cref="ResetStudentDeviceAsync"/> but resolves
    /// the link by id ALONE, with no teacher predicate — support must be able to unblock a student
    /// without going through their teacher. Every non-admin caller MUST use the teacher-scoped
    /// overload; this one performs no tenant check by design and is reachable only from the
    /// SuperAdmin-gated admin controller.
    /// </summary>
    /// <param name="linkId">The student-teacher link whose device binding to clear.</param>
    /// <param name="actingUserId">User.Id of the SuperAdmin performing the reset (audit).</param>
    Task<Result<LinkedStudentListItemDto>> ResetStudentDeviceForAdminAsync(
        long linkId, long actingUserId);

    /// <summary>
    /// Rejects a Pending request (terminal, kept for audit — the student may send
    /// a new request later). Notifies the student post-commit, best-effort.
    /// </summary>
    /// <param name="actingUserId">User.Id of the teacher/assistant performing the reject (audit).</param>
    Task<Result<bool>> RejectLinkRequestAsync(long teacherId, long linkId, long actingUserId);

    /// <summary>
    /// Pages the teacher's linked students (Active links), newest first, with both
    /// the account identity and the bound roster record. Optional <paramref name="search"/>
    /// filters by the account name/code and the bound roster name/code (case-insensitive).
    ///
    /// <paramref name="filter"/> narrows the returned page to bound / not-yet-bound accounts;
    /// the response's chip headcounts stay counted over the whole searched set regardless.
    /// </summary>
    Task<Result<LinkedStudentsPageResponse>> GetLinkedStudentsAsync(
        long teacherId, int page, int pageSize, string? search = null,
        LinkedStudentFilter filter = LinkedStudentFilter.All);

    /// <summary>
    /// SUPER-ADMIN ONLY: lists a teacher's Active links that are NOT yet bound to
    /// any roster record — the pool of connected student accounts a SuperAdmin can
    /// attach to a Student Management roster row via <see cref="BindStudentLinkForAdminAsync"/>.
    /// Powers the "Link" picker on the Admin Portal's student list; teacherId is an
    /// explicit route parameter (not JWT-resolved) since the caller is choosing which
    /// tenant's pool to browse, not acting as that tenant. Must only be reachable
    /// behind a roleOnly SuperAdmin gate.
    /// </summary>
    Task<Result<List<LinkedStudentListItemDto>>> GetUnboundActiveLinksForAdminAsync(long teacherId);

    /// <summary>
    /// Removes one or many linked students (LinkStatus = RemovedByTeacher).
    /// Ids that are not owned by this teacher or not Active are skipped and
    /// reported back. Notifies each affected student post-commit, best-effort.
    /// </summary>
    /// <param name="actingUserId">User.Id of the teacher/assistant performing the removal — persisted to RemovedByUserId for audit.</param>
    Task<Result<RemoveLinkedStudentsResultDto>> RemoveLinkedStudentsAsync(
        long teacherId, long actingUserId, RemoveLinkedStudentsDto dto);
}