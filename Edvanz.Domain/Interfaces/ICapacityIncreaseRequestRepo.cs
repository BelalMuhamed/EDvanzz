using Edvanz.Domain.Entities;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Repository for CapacityIncreaseRequest — the teacher-initiated queue for raising
/// Teacher.StudentCapacity (the configuration limit that bounds the roster AND drives
/// per-student subscription pricing). Mirrors the PendingSubscriptionPayment queue
/// pattern: tenant-scoped lookups for the teacher side, unscoped lookups for the
/// super-admin review side, and a paginated FIFO admin queue.
///
/// All query logic is encapsulated here; the Application layer never builds raw
/// expression predicates.
/// </summary>
public interface ICapacityIncreaseRequestRepo : IGenericRepo<CapacityIncreaseRequest, long>
{
    /// <summary>
    /// Loads a request by Id, scoped to the owning teacher (tenant guard).
    /// Used by the teacher-facing cancel/status endpoints.
    /// </summary>
    Task<CapacityIncreaseRequest?> GetByIdAndTeacherAsync(long requestId, long teacherId);

    /// <summary>
    /// Super-admin lookup that bypasses the teacher tenant guard (admin sees all).
    /// Used by the admin approve / reject endpoints and the notification job.
    /// </summary>
    Task<CapacityIncreaseRequest?> GetByIdForAdminAsync(long requestId);

    /// <summary>
    /// Returns true if the teacher has a live Pending request FOR THIS KIND of limit
    /// (account students vs. linked student app accounts). Used to short-circuit a duplicate
    /// submit — the filtered unique index UX_CapacityIncreaseRequests_Teacher_Kind_Pending
    /// is the DB-level backstop for the same rule. The two kinds queue independently, so a
    /// pending account-students request never blocks a linked-accounts request.
    /// </summary>
    Task<bool> HasPendingRequestAsync(long teacherId, Enums.CapacityKind capacityKind);

    /// <summary>
    /// Paginated admin queue of Pending requests, ordered RequestedAt ASC (FIFO — oldest
    /// first, mirrors the pending-payment queue). Returns raw entities; the
    /// AdminSubscriptionService enriches with teacher context and live student counts.
    /// </summary>
    Task<(IReadOnlyList<CapacityIncreaseRequest> Items, int TotalCount)> GetAdminQueuePagedAsync(
        int page, int pageSize);

    /// <summary>
    /// Paginated history of the teacher's own requests, all statuses, newest first.
    /// </summary>
    Task<(IReadOnlyList<CapacityIncreaseRequest> Items, int TotalCount)> GetByTeacherPagedAsync(
        long teacherId, int page, int pageSize);

    /// <summary>
    /// Marks a tracked request as Modified so EF writes its changes on the next
    /// SaveChanges. SaveChanges is NOT called here (caller owns the commit boundary).
    /// </summary>
    void UpdateRequest(CapacityIncreaseRequest request);
}
