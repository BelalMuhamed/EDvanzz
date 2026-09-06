using Edvanz.Domain.Enums;
﻿using Edvanz.Domain.Entities;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Repository for the UserNotification inbox (§5.4 / FR-SUB-050…053).
/// Backs the bell-icon feed: paginated history, unread counter, mark-read,
/// and bulk mark-all-read.
///
/// SCOPE: PUSH notifications only (D-05). WhatsApp messages live in
/// the Messaging module's MessageLog — not in this table.
/// </summary>
public interface IUserNotificationRepo : IGenericRepo<UserNotification, long>
{
    // ══════════════════════════════════════════════
    // READ / LIST
    // ══════════════════════════════════════════════

    /// <summary>
    /// Loads a notification by Id, scoped to the owning user (tenant guard).
    /// Used by mark-read on a single notification.
    /// </summary>
    Task<UserNotification?> GetByIdAndUserAsync(long notificationId, long userId);

    /// <summary>
    /// Paginated bell-icon feed for a user, ordered SentAt DESC.
    /// Backed by IX_UserNotifications_UserId_IsRead_SentAt — the index's column
    /// order matches this query exactly (M-6, no INCLUDE needed).
    /// </summary>
    Task<(IReadOnlyList<UserNotification> Items, int TotalCount)> GetPagedByUserAsync(
        long userId, int page, int pageSize);

    /// <summary>
    /// Returns the unread count for the bell-icon badge.
    /// Backed by the same index — IsRead = false rows are at the leading edge
    /// of the (UserId, IsRead, SentAt) seek.
    /// </summary>
    Task<int> GetUnreadCountByUserAsync(long userId);

    // ══════════════════════════════════════════════
    // INSERT / WRITE
    // ══════════════════════════════════════════════

    /// <summary>
    /// Inserts a new push-notification record. Called by the per-teacher
    /// reminder worker (§7.3) and the renewal-confirmation Hangfire job (§5.9).
    /// SaveChanges is NOT called here — the caller owns the unit of work.
    /// </summary>
    /// <summary>
    /// Which of <paramref name="userIds"/> already have a notification for this exact
    /// (source, entity) pair — the pre-check that keeps a content-publish fan-out from
    /// re-notifying anyone on a Hangfire retry.
    ///
    /// The unique index is still the real guarantee; this only avoids provoking it (and
    /// the wasted push that would precede it) on the common path.
    /// </summary>
    Task<IReadOnlyList<long>> GetUserIdsAlreadyNotifiedAsync(
        NotificationSourceType sourceType, long sourceEntityId, IReadOnlyCollection<long> userIds);

    Task InsertNotificationAsync(UserNotification notification);

    // ══════════════════════════════════════════════
    // STATE TRANSITIONS
    // ══════════════════════════════════════════════

    /// <summary>
    /// Bulk mark-all-read for a user — flips IsRead=true and stamps ReadAt
    /// for every currently-unread row. Idempotent; running it twice is a no-op
    /// because the WHERE clause excludes already-read rows.
    ///
    /// Uses ExecuteUpdateAsync — no in-memory load, runs as a single SQL UPDATE.
    /// Returns the number of rows transitioned.
    /// </summary>
    Task<int> MarkAllReadByUserAsync(long userId, DateTime readAtUtc);
}