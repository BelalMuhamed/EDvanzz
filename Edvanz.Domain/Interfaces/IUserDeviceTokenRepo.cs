using Edvanz.Domain.Entities;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Repository for FCM device tokens (§5.5 / D-06).
/// Decoupled from RefreshTokens so push reaches a logged-out tutor for
/// pre-expiry reminders.
///
/// Lifecycle owned by this repo:
///   - Upsert on (UserId, FcmToken) — register endpoint
///   - Read active tokens for a user — per-teacher reminder worker (§7.3)
///   - Deactivate a single token — Firebase reported it unregistered
/// </summary>
public interface IUserDeviceTokenRepo : IGenericRepo<UserDeviceToken, long>
{
    /// <summary>
    /// Looks up a device-token row by the unique (UserId, FcmToken) key.
    /// Returns null if no token is registered for that user/token combination.
    /// Used by the upsert path on POST /api/notifications/register-fcm-token.
    /// </summary>
    Task<UserDeviceToken?> GetByUserAndTokenAsync(long userId, string fcmToken);

    /// <summary>
    /// Returns the user's currently-active FCM tokens (IsActive = true).
    /// Per-teacher reminder worker (§7.3) sends a push to every returned token.
    /// EC-12: a user may have multiple devices; the worker fans out to all of them
    /// but inserts only one UserNotification row.
    /// </summary>
    Task<IReadOnlyList<UserDeviceToken>> GetActiveTokensForUserAsync(long userId);

    /// <summary>
    /// Active tokens for MANY users in one query, grouped by user id. A user with no active device is
    /// simply absent from the dictionary.
    ///
    /// Exists for content-publish fan-out: announcing a video scoped to a 200-student session used to
    /// issue one SELECT per recipient, on one of only four Hangfire workers, holding a scoped
    /// DbContext and its SQL connection for the whole run - on the same queue that carries
    /// subscription reminders and payment-rejection pushes.
    /// </summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<UserDeviceToken>>> GetActiveTokensForUsersAsync(
        IReadOnlyCollection<long> userIds);

    /// <summary>
    /// Inserts a new device-token row. Called when register-fcm-token sees no
    /// existing match for (UserId, FcmToken).
    /// SaveChanges is NOT called here — the caller owns the unit of work.
    /// </summary>
    Task InsertTokenAsync(UserDeviceToken token);

    /// <summary>
    /// Flips IsActive=false on a single token row by Id. Called when the push
    /// sender receives "registration-token-not-registered" from Firebase (EC-11).
    /// Uses ExecuteUpdateAsync — no entity load.
    /// </summary>
    Task DeactivateTokenAsync(long tokenId);
    /// <summary>
    /// Flips IsActive=false on a single token row identified by (UserId, FcmToken),
    /// scoped to the owning user so a caller can only ever deactivate their own
    /// device token — never another user's row (IDOR guard). Called by
    /// DELETE /api/notifications/fcm-token — the client-initiated unregister, typically
    /// invoked right before/at logout.
    /// Idempotent: a token that doesn't exist, or is already inactive, still
    /// completes with 0 rows affected — never an error.
    /// Uses ExecuteUpdateAsync — no entity load, no SaveChanges required.
    /// </summary>
    Task DeactivateByUserAndTokenAsync(long userId, string fcmToken);

    /// <summary>
    /// Flips IsActive=false on every device-token row belonging to a user. Called by
    /// AuthService.Logout when logoutAllSessions is true, so every device stops
    /// receiving push once every session is revoked.
    /// Idempotent: a user with no tokens still completes with 0 rows affected.
    /// Uses ExecuteUpdateAsync — no entity load, no SaveChanges required.
    /// </summary>
    Task DeactivateAllForUserAsync(long userId);
}