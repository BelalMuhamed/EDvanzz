using Edvanz.Domain.Entities;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Data access for <see cref="ParentPortalAccess"/> — the public parent-portal grant registry.
///
/// NAMED METHODS ONLY (CLAUDE.md §3.1): every query the portal and the teacher inbox need is a
/// method here. No <c>GetQueryable</c>, no expression predicates crossing into the services.
/// Commits are owned by the calling service (§5.2) — nothing here calls SaveChanges.
/// </summary>
public interface IParentPortalAccessRepo : IGenericRepo<ParentPortalAccess, long>
{
    /// <summary>
    /// The LIVE grant (Active or Pending) for one (student, device) pair, or null. Mirrors the
    /// filtered unique index <c>UX_PPA_Student_Device_Live</c>, so at most one row can match.
    /// Used by the access-request path to return the existing state instead of creating a
    /// duplicate row.
    /// </summary>
    Task<ParentPortalAccess?> GetLiveByStudentAndDeviceAsync(long teacherStudentId, string deviceHash);

    /// <summary>
    /// Resolves the CALLER of a portal read: the ACTIVE grant bound to this device, with its
    /// <see cref="ParentPortalAccess.TeacherStudent"/> loaded (the read endpoints need the
    /// student's name/code/session and must confirm the roster row still exists).
    /// Returns null when the device has no active grant — the read must then 401/404, never
    /// fall back to a route-supplied id.
    /// </summary>
    Task<ParentPortalAccess?> GetActiveByDeviceAsync(string deviceHash);

    /// <summary>
    /// The ACTIVE grant this device holds FOR ONE NAMED STUDENT, with its
    /// <see cref="ParentPortalAccess.TeacherStudent"/> loaded.
    ///
    /// This is what makes a parent of siblings work. Reads used to resolve the device to its
    /// NEWEST active grant and then demand the requested roster id match it, so a parent who
    /// signed in for a second child could never reach the first again — every screen followed the
    /// newest grant, and re-entering the older child's code still landed on the newer one. The
    /// tenant guard is unchanged and just as strict: a roster id this device holds no ACTIVE grant
    /// for returns null, exactly as a foreign id always did.
    /// </summary>
    Task<ParentPortalAccess?> GetActiveByDeviceAndStudentAsync(string deviceHash, long teacherStudentId);

    /// <summary>
    /// EVERY active grant on this device, newest first, each with its
    /// <see cref="ParentPortalAccess.TeacherStudent"/> loaded — the children this browser follows.
    /// Drives the portal's child switcher; a single-child parent simply gets a one-item list.
    /// </summary>
    Task<IReadOnlyList<ParentPortalAccess>> GetActiveGrantsByDeviceAsync(string deviceHash);

    /// <summary>
    /// The teacher's display name, subject labels and configuration row for the portal header, in
    /// ONE query.
    ///
    /// Exists because the portal used to build this from <c>GetTeacherDashboardDataAsync</c>, a
    /// BULK dashboard loader that issues five separate round-trips (teachers, users,
    /// teacher-subjects, subjects, configurations) to produce one name and one label — and it runs
    /// on every access request AND every poll of the waiting screen. Deliberately NOT cached: the
    /// configuration it returns gates portal eligibility and per-section visibility, and a teacher
    /// who switches sharing off must have it take effect on the next read, not minutes later.
    /// </summary>
    Task<ParentPortalTeacherHeader?> GetPortalTeacherHeaderAsync(long teacherId);

    /// <summary>
    /// The NEWEST grant row for this device regardless of status (including terminal
    /// Rejected/Revoked rows), with its <see cref="ParentPortalAccess.TeacherStudent"/> loaded.
    /// Drives the portal's "where do I stand?" screen so a rejected or revoked parent gets a
    /// real explanation instead of a blank "not found".
    /// </summary>
    Task<ParentPortalAccess?> GetLatestByDeviceAsync(string deviceHash);

    /// <summary>
    /// TRUSTED-PHONE RULE: does this student already have an ACTIVE grant whose stored
    /// <c>ClaimedPhone</c> equals <paramref name="normalizedPhone"/>?
    ///
    /// True means a teacher has already vetted that number for that student, so a request arriving
    /// from a NEW device with the same number is created Active instead of queued — which is what
    /// makes access follow the phone rather than the browser. Compares the raw column, which is
    /// safe because <c>ClaimedPhone</c> is only ever written normalized.
    /// </summary>
    Task<bool> HasActiveGrantWithPhoneAsync(long teacherStudentId, string normalizedPhone);

    /// <summary>
    /// The NEWEST grant, in ANY status, matching this student on EITHER the device OR the phone.
    /// Drives the post-rejection cooldown, which keys on "what happened last", not "was there ever
    /// a rejection" — a parent rejected yesterday but approved today must not be held back.
    /// <paramref name="normalizedPhone"/> null → device only.
    /// </summary>
    Task<ParentPortalAccess?> GetNewestForStudentByDeviceOrPhoneAsync(
        long teacherStudentId, string deviceHash, string? normalizedPhone);

    /// <summary>
    /// Revokes EVERY live (Active or Pending) grant for one (student, phone) pair in a single
    /// atomic UPDATE, returning how many rows were cut off.
    ///
    /// THIS MUST STAY PHONE-WIDE. Because the trusted-phone rule grants Active status to any
    /// device presenting a number that already holds an Active grant on the student, revoking only
    /// the one row the teacher tapped would leave a sibling row alive — and the parent would be
    /// let straight back in on their next submit. A per-row revoke is not a weaker revoke, it is
    /// a revoke that silently does nothing.
    ///
    /// One statement, so no service-side loop and no partial state.
    /// </summary>
    Task<int> RevokeByStudentAndPhoneAsync(
        long teacherId, long teacherStudentId, string normalizedPhone, long actingUserId, DateTime nowUtc);

    /// <summary>Tenant-scoped page of PENDING requests for a teacher's inbox, newest first.</summary>
    Task<IReadOnlyList<ParentPortalAccess>> GetPendingForTeacherPagedAsync(long teacherId, int page, int pageSize);

    /// <summary>Total PENDING requests for the teacher — the paging total and the inbox badge.</summary>
    Task<int> CountPendingForTeacherAsync(long teacherId);

    /// <summary>
    /// Tenant-scoped fetch by id, with the roster record loaded. The ONLY way approve / reject /
    /// revoke resolve their target: passing a foreign id simply returns null (never another
    /// tenant's row).
    /// </summary>
    Task<ParentPortalAccess?> GetByIdForTeacherAsync(long id, long teacherId);

    /// <summary>Bulk tenant-scoped fetch for the approve/reject-many action. Ids outside the tenant are silently dropped.</summary>
    Task<IReadOnlyList<ParentPortalAccess>> GetByIdsForTeacherAsync(IEnumerable<long> ids, long teacherId);

    /// <summary>
    /// Every non-terminal follower (Active or Pending) of one roster student, newest first —
    /// the teacher's "who is following this student?" panel.
    /// </summary>
    Task<IReadOnlyList<ParentPortalAccess>> GetFollowersForStudentAsync(long teacherId, long teacherStudentId);

    /// <summary>Distinct roster students with at least one ACTIVE follower — the teacher summary tile.</summary>
    Task<int> CountFollowedStudentsForTeacherAsync(long teacherId);

    /// <summary>
    /// Abuse cap: access requests this DEVICE created since <paramref name="sinceUtc"/>, in ANY
    /// status — a request that was rejected or later revoked still counts, otherwise the cap could
    /// be reset simply by having attempts refused.
    /// </summary>
    Task<int> CountPendingByDeviceSinceAsync(string deviceHash, DateTime sinceUtc);

    /// <summary>Abuse cap: access requests aimed at this TEACHER since <paramref name="sinceUtc"/>, in any status (same rationale as the per-device cap).</summary>
    Task<int> CountPendingForTeacherSinceAsync(long teacherId, DateTime sinceUtc);

    /// <summary>
    /// The most recent PENDING request's <see cref="ParentPortalAccess.RequestedAt"/> for a
    /// teacher, or null when the inbox is empty. Read BEFORE inserting a new request so the
    /// notifier can batch (at most one teacher notification per hour).
    /// </summary>
    Task<DateTime?> GetNewestPendingRequestedAtAsync(long teacherId);

    /// <summary>
    /// Stamps <see cref="ParentPortalAccess.LastSeenAt"/> without loading or tracking the row
    /// (ExecuteUpdate). Called on every portal read, so it must stay off the change tracker.
    /// </summary>
    Task TouchLastSeenAsync(long id, DateTime utcNow);

    /// <summary>
    /// Deletes every grant of a roster student. Called from the student PURGE path, inside the
    /// purge transaction: the composite FK is NoAction, so SQL Server cleans nothing and a
    /// surviving row would both block the hard delete and leave a follower pointed at a ghost.
    /// </summary>
    Task DeleteForStudentAsync(long teacherStudentId);
}

/// <summary>
/// Exactly what the parent portal needs to render a header and decide eligibility, fetched in one
/// query. <see cref="Configuration"/> is the live row — visibility flags and the portal opt-in are
/// read straight off it, so it must never be served from a cache.
/// </summary>
public sealed record ParentPortalTeacherHeader(
    string TeacherName,
    string? SubjectNameAr,
    string? SubjectNameEn,
    string? CustomSubject,
    TeacherConfiguration? Configuration);
