using Edvanz.Domain.Entities;
using Edvanz.Domain.Interfaces;
using Edvanz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edvanz.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of IUserDeviceTokenRepo.
/// Reads leverage IX_UserDeviceTokens_UserId_IsActive; the upsert lookup
/// hits the unique IX_UserDeviceTokens_UserId_FcmToken.
/// </summary>
public class UserDeviceTokenRepo
    : GenericRepo<UserDeviceToken, long>, IUserDeviceTokenRepo
{
    public UserDeviceTokenRepo(EdvanzDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<UserDeviceToken?> GetByUserAndTokenAsync(long userId, string fcmToken)
    {
        // Tracked: upsert path may modify LastSeenAt / IsActive on an existing row.
        return await _context.Set<UserDeviceToken>()
            .FirstOrDefaultAsync(t => t.UserId == userId && t.FcmToken == fcmToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserDeviceToken>> GetActiveTokensForUserAsync(long userId)
    {
        return await _context.Set<UserDeviceToken>()
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.IsActive)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<UserDeviceToken>>>
        GetActiveTokensForUsersAsync(IReadOnlyCollection<long> userIds)
    {
        if (userIds is null || userIds.Count == 0)
            return new Dictionary<long, IReadOnlyList<UserDeviceToken>>();

        // Same index as the single-user read (IX_UserDeviceTokens_UserId_IsActive); one round trip.
        var rows = await _context.Set<UserDeviceToken>()
            .AsNoTracking()
            .Where(t => userIds.Contains(t.UserId) && t.IsActive)
            .ToListAsync();

        return rows
            .GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UserDeviceToken>)g.ToList());
    }

    /// <inheritdoc />
    public async Task InsertTokenAsync(UserDeviceToken token)
    {
        await _context.Set<UserDeviceToken>().AddAsync(token);
    }

    /// <inheritdoc />
    public async Task DeactivateTokenAsync(long tokenId)
    {
        // Single SQL UPDATE — no entity materialized.
        await _context.Set<UserDeviceToken>()
            .Where(t => t.Id == tokenId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.IsActive, false));
    }
    /// <inheritdoc />
    public async Task DeactivateByUserAndTokenAsync(long userId, string fcmToken)
    {
        // Single SQL UPDATE, scoped to (UserId, FcmToken) — matches the unique index
        // IX_UserDeviceTokens_UserId_FcmToken, so this can never touch another user's row.
        await _context.Set<UserDeviceToken>()
            .Where(t => t.UserId == userId && t.FcmToken == fcmToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.IsActive, false));
    }

    /// <inheritdoc />
    public async Task DeactivateAllForUserAsync(long userId)
    {
        // Single SQL UPDATE — no entity materialized. Used by AuthService.Logout's
        // all-devices path so every device stops receiving push at once.
        await _context.Set<UserDeviceToken>()
            .Where(t => t.UserId == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.IsActive, false));
    }
}