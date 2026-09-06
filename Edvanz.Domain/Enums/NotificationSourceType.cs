namespace Edvanz.Domain.Enums;

/// <summary>
/// Discriminates which background job produced a UserNotification row, paired with
/// UserNotification.SourceEntityId to form a per-job idempotency key — the same role
/// SubscriptionAlerts(TeacherId, SubscriptionEndDate, AlertDay) plays for the
/// subscription-reminder job. Internal bookkeeping only; not exposed on NotificationDto.
/// </summary>
public enum NotificationSourceType : byte
{
    Renewal = 1,
    PaymentRejected = 2,
    CapacityResolved = 3,

    /// <summary>
    /// A video became visible to a student. Unlike the three above, this one
    /// fans OUT — one row per recipient for the same video — which is why the
    /// idempotency index carries UserId as well.
    /// </summary>
    VideoPublished = 4,

    /// <summary>An online exam was published to a student. Fans out like <see cref="VideoPublished"/>.</summary>
    OnlineExamPublished = 5
}