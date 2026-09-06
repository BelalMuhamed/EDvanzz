using Edvanz.Domain.Entities;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Edvanz.Domain.Enums;

namespace Edvanz.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of IUserNotificationRepo.
/// All read queries leverage IX_UserNotifications_UserId_IsRead_SentAt (M-6).
/// </summary>
public class UserNotificationRepo
    : GenericRepo<UserNotification, long>, IUserNotificationRepo
{
    public UserNotificationRepo(EdvanzDbContext context) : base(context)
    {
    }

    // ══════════════════════════════════════════════
    // READ / LIST
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<UserNotification?> GetByIdAndUserAsync(long notificationId, long userId)
    {
        // Tracked: caller (mark-read) flips IsRead and ReadAt.
        return await _context.Set<UserNotification>()
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<UserNotification> Items, int TotalCount)>
        GetPagedByUserAsync(long userId, int page, int pageSize)
    {
        IQueryable<UserNotification> query = _context.Set<UserNotification>()
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.SentAt);

        int totalCount = await query.CountAsync();

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<int> GetUnreadCountByUserAsync(long userId)
    {
        return await _context.Set<UserNotification>()
            .AsNoTracking()
            .CountAsync(n => n.UserId == userId && !n.IsRead);
    }

    // ══════════════════════════════════════════════
    // INSERT / WRITE
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task InsertNotificationAsync(UserNotification notification)
    {
        await _context.Set<UserNotification>().AddAsync(notification);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetUserIdsAlreadyNotifiedAsync(
        NotificationSourceType sourceType, long sourceEntityId, IReadOnlyCollection<long> userIds)
    {
        if (userIds is null || userIds.Count == 0) return Array.Empty<long>();

        return await _context.Set<UserNotification>()
            .Where(n => n.SourceType == sourceType
                     && n.SourceEntityId == sourceEntityId
                     && userIds.Contains(n.UserId))
            .Select(n => n.UserId)
            .Distinct()
            .ToListAsync();
    }

    // ══════════════════════════════════════════════
    // STATE TRANSITIONS
    // ══════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<int> MarkAllReadByUserAsync(long userId, DateTime readAtUtc)
    {
        // Single SQL UPDATE — no entities materialized.
        return await _context.Set<UserNotification>()
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, readAtUtc));
    }
}