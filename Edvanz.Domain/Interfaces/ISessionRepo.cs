using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Extended repository interface for the Session Module (Module 2: Sessions).
/// Centralizes all domain-specific query methods for Session, SessionGroup, and SessionLink records.
/// 
/// WHY THIS EXISTS (same rationale as ITeacherStudentRepo and IUserRepo):
/// All query logic lives here in named methods. The Application layer never
/// builds raw expression predicates. If a query changes, you edit ONE method
/// in the repo — not every service that uses it.
/// 
/// Inherits from IGenericRepo&lt;Session, long&gt; so basic CRUD is still available.
/// </summary>
public interface ISessionRepo : IGenericRepo<Session, long>
{
    // ══════════════════════════════════════════════
    // SESSION QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Finds a session by Id, scoped to the teacher.
    /// Returns null if the session doesn't exist or belongs to another teacher.
    /// </summary>
    Task<Session?> GetByIdAndTeacherAsync(long sessionId, long teacherId);

    /// <summary>
    /// Checks if a session name already exists under this teacher's account.
    /// BR-SES-001: Session name must be unique per tutor account.
    /// </summary>
    Task<bool> SessionNameExistsAsync(long teacherId, string sessionName);

    /// <summary>
    /// Checks if a session name exists, excluding a specific session Id.
    /// Used during update to allow the session to keep its own name.
    /// </summary>
    Task<bool> SessionNameExistsExcludingAsync(long teacherId, string sessionName, long excludeSessionId);

    /// <summary>
    /// Checks whether a session has any student assignments or membership links.
    /// REQ-SES-009: OccurrenceType is not editable if session has assignments or links.
    /// </summary>
    Task<bool> HasStudentsOrLinksAsync(long sessionId);

    /// <summary>
    /// Gets the count of students assigned to a specific session.
    /// REQ-SES-021: Real-time student count displayed on session detail view.
    /// REQ-SES-NFR-009: Live student count badge on session cards.
    /// </summary>
    Task<int> CountStudentsBySessionAsync(long sessionId);

    /// <summary>
    /// Counts the sessions owned by a teacher. Used to enforce the free-tier session quota
    /// for unsubscribed teachers (Sessions are hard-deleted, so live rows == the real count).
    /// </summary>
    Task<int> CountSessionsByTeacherAsync(long teacherId);

    /// <summary>
    /// Per-teacher session counts for one PAGE of the SuperAdmin teacher list (one GROUP BY,
    /// mirrors ITeacherStudentRepo.GetActiveStudentCountsAsync). Sessions are hard-deleted,
    /// so live rows == the real count. Teachers with no sessions are absent from the
    /// dictionary — callers default to 0. Activity Monitor "Sessions" column.
    /// </summary>
    Task<Dictionary<long, int>> GetSessionCountsAsync(IReadOnlyCollection<long> teacherIds);

    /// <summary>
    /// ACTIVE (not-yet-expired, EndDate &gt;= today) sessions across MANY teachers in ONE query — the
    /// center front-desk attendance picker fans a whole center's active teachers into a single
    /// schedule feed with no per-teacher round-trip. Mirrors the teacher session-list "activeOnly"
    /// filter (BuildSessionListQuery) so the center picker sees exactly the sessions the teacher home
    /// would. Empty input → empty result.
    /// </summary>
    Task<IReadOnlyList<Session>> GetActiveSessionsByTeacherIdsAsync(IReadOnlyCollection<long> teacherIds, DateTime today);

    /// <summary>
    /// Live roster student counts for MANY sessions in ONE GROUP BY (sessionId → count). Same source
    /// as <see cref="CountStudentsBySessionAsync"/> (active TeacherStudents rows) so the center
    /// picker's per-session count matches the teacher-home card exactly. Sessions with no students are
    /// absent from the dictionary — callers default to 0. Empty input → empty result.
    /// </summary>
    Task<Dictionary<long, int>> GetStudentCountsBySessionIdsAsync(IReadOnlyCollection<long> sessionIds);

    /// <summary>
    /// Counts the session groups owned by a teacher. Used to enforce the free-tier group quota
    /// for unsubscribed teachers.
    /// </summary>
    Task<int> CountGroupsByTeacherAsync(long teacherId);

    /// <summary>
    /// Builds a filtered, searchable, sortable IQueryable for the teacher's sessions.
    /// The caller (service layer) uses this with GetPagedAsync for pagination.
    /// 
    /// REQ-SES-044: Search by session name.
    /// REQ-SES-045: Filter by group, occurrence type, active/expired status.
    /// REQ-SES-NFR-001: Must load and render in under 2 seconds.
    /// 
    /// Returns IQueryable so pagination is applied at the database level.
    /// </summary>
    /// <param name="teacherId">The owning teacher's Id (multi-tenant scope).</param>
    /// <param name="asOfLocalDate">
    /// The TEACHER's current local date, from <c>ITimeZoneService.GetTeacherLocalDate</c>.
    /// Required rather than derived here: <c>EndDate</c> is a calendar day, and
    /// <c>DateTime.UtcNow.Date</c> is still yesterday between midnight and 2-3 AM Cairo, so a
    /// session was filtered as expired for the first hours of the day it actually ended.
    /// </param>
    /// <param name="search">Optional search term (partial match on session name).</param>
    /// <param name="groupId">Optional filter: only sessions in this group. Use -1 for ungrouped only.</param>
    /// <param name="occurrenceType">Optional filter: only sessions with this occurrence type.</param>
    /// <param name="activeOnly">If true, only sessions where EndDate >= today.</param>
    /// <param name="expiredOnly">If true, only sessions where EndDate &lt; today.</param>
    /// <param name="sortBy">Column to sort by. Defaults to DateAdded.</param>
    /// <param name="sortDirection">Sort direction. Defaults to Desc.</param>
    IQueryable<Session> BuildSessionListQuery(
        long teacherId,
        DateTime asOfLocalDate,
        string? search = null,
        long? groupId = null,
        OccurrenceType? occurrenceType = null,
        bool activeOnly = false,
        bool expiredOnly = false,
        SessionSortBy sortBy = SessionSortBy.DateAdded,
        SortDirection sortDirection = SortDirection.Desc);

    // ══════════════════════════════════════════════
    // SESSION NAME GENERATION SUPPORT
    // ══════════════════════════════════════════════

    /// <summary>
    /// Retrieves the highest (latest) auto-generated session name for a teacher.
    /// Used by the session name generator to determine the next sequential name.
    /// REQ-SES-002: Sequential names Session A1 → Session Z999 (English) or حصة أ1 → حصة ي999 (Arabic).
    /// Considers ALL existing session names to avoid collisions.
    /// </summary>
    Task<string?> GetHighestSessionNameAsync(long teacherId);

    // ══════════════════════════════════════════════
    // SESSION GROUP QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Finds a session group by Id, scoped to the teacher.
    /// </summary>
    Task<SessionGroup?> GetGroupByIdAndTeacherAsync(long groupId, long teacherId);

    /// <summary>
    /// Checks if a group name already exists under this teacher's account.
    /// </summary>
    Task<bool> GroupNameExistsAsync(long teacherId, string groupName);

    /// <summary>
    /// Checks if a group name exists, excluding a specific group Id.
    /// Used during rename to allow the group to keep its own name.
    /// </summary>
    Task<bool> GroupNameExistsExcludingAsync(long teacherId, string groupName, long excludeGroupId);

    /// <summary>
    /// Retrieves all session groups for a teacher, ordered by creation date.
    /// REQ-SES-027: Rendered as box/card layout in the session list.
    /// </summary>
    Task<IReadOnlyList<SessionGroup>> GetGroupsByTeacherAsync(long teacherId);

    /// <summary>
    /// Returns the sessions (id + name + owning group id) belonging to any of the given groups,
    /// scoped to the teacher. Expands group selections into their member sessions (exam creation,
    /// exam home). Empty groups contribute no rows.
    /// </summary>
    Task<IReadOnlyList<GroupSessionRow>> GetSessionsByGroupIdsAsync(long teacherId, IEnumerable<long> groupIds);

    /// <summary>
    /// Adds a new session group.
    /// </summary>
    Task AddGroupAsync(SessionGroup group);

    /// <summary>
    /// Updates an existing session group.
    /// </summary>
    Task UpdateGroupAsync(SessionGroup group);

    /// <summary>
    /// Deletes a session group (hard delete).
    /// REQ-SES-031: Sessions within become ungrouped (SessionGroupId set to null
    /// via database SetNull NoAction).
    /// </summary>
    Task DeleteGroupAsync(SessionGroup group);

    // ══════════════════════════════════════════════
    // SESSION LINK (MEMBERSHIP) QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Retrieves all sessions linked to the specified session (symmetric lookup).
    /// REQ-SES-034: Visual indicator showing linked session names.
    /// REQ-SES-035: A session may be linked to more than one session.
    /// </summary>
    Task<IReadOnlyList<Session>> GetLinkedSessionsAsync(long sessionId);

    /// <summary>
    /// Checks if a link already exists between two sessions (order-independent).
    /// Used to prevent duplicate links.
    /// </summary>
    Task<bool> LinkExistsAsync(long sessionIdA, long sessionIdB);

    /// <summary>
    /// Finds the SessionLink record between two sessions (order-independent).
    /// Used for removing a link. Returns null if no link exists.
    /// </summary>
    Task<SessionLink?> GetLinkAsync(long sessionIdA, long sessionIdB);

    /// <summary>
    /// Retrieves all session link records involving the specified session.
    /// REQ-SES-047: Used in deletion confirmation to list affected linked sessions.
    /// </summary>
    Task<IReadOnlyList<SessionLink>> GetLinksBySessionAsync(long sessionId);

    /// <summary>
    /// Adds a new session link.
    /// </summary>
    Task AddLinkAsync(SessionLink link);

    /// <summary>
    /// Deletes a session link (hard delete).
    /// REQ-SES-037: Removing a link does not affect sessions or student assignments.
    /// </summary>
    Task DeleteLinkAsync(SessionLink link);
    /// <summary>
    /// Returns every session (Id + name) for the teacher, ordered by name.
    /// Used as the stable chip catalog for the "Assign Students" screen.
    /// </summary>
    Task<IReadOnlyList<SessionNameRow>> GetTeacherSessionNamesAsync(long teacherId);

    /// <summary>
    /// Returns an Id→SessionName map for the given session Ids, scoped to the teacher.
    /// Used to populate the assigned-session badge on the student list without an N+1.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> GetSessionNamesByIdsAsync(long teacherId, IEnumerable<long> sessionIds);

    /// <summary>
    /// Returns an Id→GroupName map for the given session-group Ids, scoped to the teacher.
    /// Mirrors <see cref="GetSessionNamesByIdsAsync"/>; used by the video allowed-scope-targets
    /// picker to label the groups a video's units cover.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> GetGroupNamesByIdsAsync(long teacherId, IEnumerable<long> groupIds);
    /// <summary>
    /// Returns a compact session summary (name, occurrence type, payment type, amount)
    /// for a single session scoped to the teacher, or null if not found / not owned.
    /// Populates the assigned-session card on the student profile screen without
    /// materializing the full Session entity.
    /// </summary>
    Task<SessionSummaryRow?> GetSessionSummaryByIdAsync(long teacherId, long sessionId);

    // ══════════════════════════════════════════════
    // SCOPE TARGET SUMMARIES (name + student count, batched)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Batch-resolves sessions to <see cref="ScopeTargetSummaryRow"/> (name + active
    /// student count), scoped to the teacher. Used to enrich unit/video scope reads.
    /// </summary>
    Task<IReadOnlyList<ScopeTargetSummaryRow>> GetSessionTargetSummariesAsync(
        long teacherId, IEnumerable<long> sessionIds);

    /// <summary>
    /// Batch-resolves session-groups to <see cref="ScopeTargetSummaryRow"/> (name +
    /// active student count across all sessions in the group), scoped to the teacher.
    /// </summary>
    Task<IReadOnlyList<ScopeTargetSummaryRow>> GetGroupTargetSummariesAsync(
        long teacherId, IEnumerable<long> groupIds);

    /// <summary>
    /// Deduplicated count of active students reachable across a mixed set of session
    /// and session-group targets — a student assigned to a session that is ALSO
    /// covered by a scoped group is counted once, not twice (a student has exactly
    /// one <c>SessionId</c>, so no DISTINCT is needed).
    /// </summary>
    Task<int> CountStudentsInTargetsAsync(
        long teacherId, IEnumerable<long> sessionIds, IEnumerable<long> groupIds);

    // Interface
    Task<IReadOnlyDictionary<long, string>> GetSessionNamesByIdsAsync(long? teacherId, IEnumerable<long> sessionIds);
}