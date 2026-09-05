using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of ICapacityIncreaseRequestRepo.
/// Inherits GenericRepo&lt;CapacityIncreaseRequest, long&gt; for basic CRUD;
/// adds named methods for every domain-specific query (SubscriptionPaymentRepo pattern).
/// </summary>
public class CapacityIncreaseRequestRepo
    : GenericRepo<CapacityIncreaseRequest, long>, ICapacityIncreaseRequestRepo
{
    public CapacityIncreaseRequestRepo(EdvanzDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<CapacityIncreaseRequest?> GetByIdAndTeacherAsync(long requestId, long teacherId)
    {
        // Tracked: the teacher-side cancel flow transitions Status.
        return await _context.Set<CapacityIncreaseRequest>()
            .FirstOrDefaultAsync(r => r.Id == requestId && r.TeacherId == teacherId);
    }

    /// <inheritdoc />
    public async Task<CapacityIncreaseRequest?> GetByIdForAdminAsync(long requestId)
    {
        // Tracked: admin approve / reject flips Status and writes ResolvedByUserId.
        return await _context.Set<CapacityIncreaseRequest>()
            .FirstOrDefaultAsync(r => r.Id == requestId);
    }

    /// <inheritdoc />
    public async Task<bool> HasPendingRequestAsync(long teacherId, CapacityKind capacityKind)
    {
        return await _context.Set<CapacityIncreaseRequest>()
            .AnyAsync(r => r.TeacherId == teacherId
                        && r.CapacityKind == capacityKind
                        && r.Status == CapacityRequestStatus.Pending);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<CapacityIncreaseRequest> Items, int TotalCount)>
        GetAdminQueuePagedAsync(int page, int pageSize)
    {
        // FIFO (oldest first) — mirrors the pending-payment admin queue so teachers
        // are served in submission order. Served by IX_CapacityIncreaseRequests_Status_RequestedAt.
        IQueryable<CapacityIncreaseRequest> query = _context.Set<CapacityIncreaseRequest>()
            .AsNoTracking()
            .Where(r => r.Status == CapacityRequestStatus.Pending)
            .OrderBy(r => r.RequestedAt);

        int totalCount = await query.CountAsync();

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<CapacityIncreaseRequest> Items, int TotalCount)>
        GetByTeacherPagedAsync(long teacherId, int page, int pageSize)
    {
        IQueryable<CapacityIncreaseRequest> query = _context.Set<CapacityIncreaseRequest>()
            .AsNoTracking()
            .Where(r => r.TeacherId == teacherId)
            .OrderByDescending(r => r.RequestedAt);

        int totalCount = await query.CountAsync();

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public void UpdateRequest(CapacityIncreaseRequest request)
    {
        _context.Entry(request).State = EntityState.Modified;
    }
}
