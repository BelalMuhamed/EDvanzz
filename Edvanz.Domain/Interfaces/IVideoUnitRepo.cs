using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Extended repository for <see cref="VideoUnit"/> (Track C / G-UNIT). Kept
/// as its own repo rather than folded into <see cref="IVideoAssetRepo"/> —
/// units are a distinct sub-aggregate with their own CRUD and paged-list
/// shape, same rationale as splitting <c>IVideoQuizRepo</c> out later.
/// </summary>
public interface IVideoUnitRepo : IGenericRepo<VideoUnit, long>
{
    /// <summary>Adds a new unit to the change tracker.</summary>
    Task AddUnitAsync(VideoUnit unit);

    /// <summary>
    /// Fetches a unit by id, scoped to the teacher who owns it (and not
    /// soft-deleted — covered by the default query filter). Tracked: caller
    /// may mutate for update, or soft-delete.
    /// </summary>
    Task<VideoUnit?> GetUnitByIdAndTeacherAsync(long unitId, long teacherId);

    /// <summary>
    /// Soft-deletes a unit (sets <c>DeletedAt</c>). Does NOT touch its
    /// videos — the FK's <c>NoAction</c> `OnDelete` behavior only fires on a
    /// hard DB delete, so the service layer must explicitly remove this
    /// unit's <c>VideoAssetUnit</c> join rows in the same transaction
    /// (soft-delete leaves the row in place at the DB level).
    /// </summary>
    Task SoftDeleteUnitAsync(VideoUnit unit, DateTime deletedAtUtc);

    /// <summary>
    /// Bulk-deletes every <c>VideoAssetUnit</c> join row for the given unit.
    /// Called by the service layer alongside <see cref="SoftDeleteUnitAsync"/>
    /// so a soft-deleted unit's videos become loose immediately, not just
    /// whenever each video is next touched.
    /// </summary>
    Task<int> DetachVideosFromUnitAsync(long unitId);

    /// <summary>
    /// Paged list of a teacher's units with rolled-up child aggregates
    /// (video count, distinct seen/unseen students across the unit's
    /// videos) — backs S2. Computed as one grouped query, not per-unit
    /// round trips.
    /// </summary>
    Task<(IReadOnlyList<TeacherVideoUnitListRow> Items, int TotalCount)>
        GetTeacherUnitsPagedAsync(long teacherId, string? search, int page, int pageSize);

    /// <summary>
    /// Paged list of videos inside a specific unit — backs the S6 drill-down.
    /// Reuses <see cref="TeacherVideoListRow"/>, the same shape (and same
    /// optional Title <paramref name="search"/> filter) as the top-level
    /// teacher video list — the only difference is the unit filter.
    /// </summary>
    Task<(IReadOnlyList<TeacherVideoListRow> Items, int TotalCount)>
        GetVideosInUnitPagedAsync(long unitId, long teacherId, string? search, int page, int pageSize,
            VideoStatus? status = null);

    // ══════════════════════════════════════════════════════════════════════
    // UNIT SCOPE — collection-level Target Scope (final decision)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fetches a unit together with its scope rows eagerly loaded. Used by
    /// the append-scopes flow to dedupe client-side before insert — same
    /// rationale as <c>IVideoAssetRepo.GetVideoWithScopesAsync</c>.
    /// AsNoTracking — caller does not mutate.
    /// </summary>
    Task<VideoUnit?> GetUnitWithScopesAsync(long unitId, long teacherId);

    /// <summary>
    /// Adds a batch of scope rows for an existing unit. Service layer is
    /// responsible for validating each scope's target belongs to the same
    /// teacher (via <c>IVideoAssetRepo.IsScopeTargetOwnedByTeacherAsync</c> —
    /// reused as-is, since target ownership is not video/unit-specific) and
    /// for enforcing the single-recipient-type-per-unit rule before calling.
    /// </summary>
    Task AddUnitScopesAsync(IEnumerable<VideoUnitScope> scopes);

    /// <summary>
    /// Hard-deletes every scope row for a given unit. Used by the
    /// PUT-replace-all flow, wrapped in the service layer's transaction.
    /// Implemented as <c>ExecuteDeleteAsync</c> for a single round trip.
    /// </summary>
    Task DeleteAllScopesForUnitAsync(long unitId);

    /// <summary>
    /// Hard-deletes every <c>VideoUnitScope</c> row targeting a given session
    /// (<c>ScopeType = Session</c>). Called by the session-delete cleanup in
    /// <c>SessionService.DeleteSessionAsync</c> BEFORE the session is hard-
    /// deleted: the <c>VideoUnitScopes.SessionId</c> FK is <c>NoAction</c> and
    /// would otherwise block the delete with a 409. Mirrors
    /// <c>IVideoAssetRepo.DeleteScopesBySessionAsync</c>.
    /// </summary>
    Task DeleteUnitScopesBySessionAsync(long sessionId);

    /// <summary>
    /// Hard-deletes every <c>VideoUnitScope</c> row targeting a given session
    /// group (<c>ScopeType = SessionGroup</c>). Called by
    /// <c>SessionService.DeleteGroupAsync</c> so the <c>NoAction</c>
    /// <c>SessionGroupId</c> FK cannot block the group delete.
    /// </summary>
    Task DeleteUnitScopesByGroupAsync(long sessionGroupId);

    /// <summary>
    /// Hard-deletes every <c>VideoUnitScope</c> row targeting a given roster record
    /// (<c>ScopeType = Student</c>). Called by the student permanent-purge flow: the
    /// <c>VideoUnitScopes.TeacherStudentId</c> FK is <c>NoAction</c> and would otherwise
    /// block the hard delete. The row is DELETED rather than nulled because
    /// <c>CK_VideoUnitScopes_ExactlyOneTarget</c> forbids a scope with no target.
    /// Idempotent — a second call deletes zero rows.
    /// </summary>
    Task DeleteUnitScopesByStudentAsync(long teacherStudentId);

    /// <summary>
    /// Hard-deletes a single unit-scope row, scoped to the teacher. Returns
    /// <c>false</c> if the row does not exist or belongs to a different
    /// teacher.
    /// </summary>
    Task<bool> DeleteUnitScopeByIdAndTeacherAsync(long scopeId, long teacherId);

    /// <summary>
    /// Returns the count of scope rows for a unit. Unlike a video, a unit is
    /// allowed to reach zero scope rows — removing the last one is a valid
    /// state (the unit simply stops granting supplemental access; its videos
    /// still work through their own <c>VideoScope</c> rows), so there is no
    /// "last scope cannot be removed" guard at the unit level.
    /// </summary>
    Task<int> CountScopesForUnitAsync(long unitId);

    /// <summary>
    /// Returns the scope rows for a set of units (AsNoTracking). Used to compute
    /// the union of allowed sessions/groups a video's units cover — the boundary
    /// for the video-scope containment rule and the allowed-scope-targets picker.
    /// </summary>
    Task<List<VideoUnitScope>> GetScopeRowsForUnitsAsync(IEnumerable<long> unitIds);

    /// <summary>
    /// Returns the ids of the videos currently linked to a unit (via
    /// <c>VideoAssetUnit</c>). Used by the unit-scope block guard to check that a
    /// scope shrink / unit delete would not leave a member video uncovered.
    /// </summary>
    Task<List<long>> GetVideoIdsInUnitAsync(long unitId);
}
