using Edvanz.Domain.Enums;

namespace Edvanz.Application.Dtos.Teacher;

/// <summary>
/// Lightweight DTO for teacher list views (Super Admin dashboard).
/// REQ-ADM-026: Shows name, username, student count, subscription status,
/// subscription end date, account status, and last login.
/// Does not include full configuration or subject details.
/// </summary>
public class TeacherListItemDto
{
    public long Id { get; set; }
    public long UserId { get; set; }

    public string FullName { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string TeacherCode { get; set; } = null!;
    public string? PhoneNumber { get; set; }
    public int StudentCapacity { get; set; }

    /// <summary>
    /// How many student APP ACCOUNTS this teacher may have linked at once
    /// (<c>Teacher.LinkedStudentCapacity</c>) — the PRICED limit. Compare with
    /// <see cref="LinkedStudentCount"/> for the usage picture. Always &lt;= <see cref="StudentCapacity"/>.
    /// </summary>
    public int LinkedStudentCapacity { get; set; }

    /// <summary>Number of students CREATED (active roster) under this teacher.</summary>
    public int StudentCount { get; set; }

    /// <summary>Number of students who have an ACTIVE account link (connected) to this teacher.</summary>
    public int LinkedStudentCount { get; set; }

    /// <summary>
    /// Consumed student-app-account SEATS: links that are Active AND bound to a student record.
    /// This — not <see cref="LinkedStudentCount"/> — is the number measured against
    /// <see cref="LinkedStudentCapacity"/>, because an accepted-but-unbound connection sees no
    /// data and costs nothing. Identical meaning to <c>features.linkedStudentsUsed</c> on
    /// GET /api/subscription/status. Always &lt;= <see cref="LinkedStudentCount"/>.
    /// </summary>
    public int LinkedStudentsUsed { get; set; }

    /// <summary>
    /// Number of sessions (classes) owned by this teacher. Sessions are hard-deleted, so
    /// live rows == the real count. Activity Monitor "Sessions" column.
    /// </summary>
    public int SessionCount { get; set; }

    public string AccountStatus { get; set; } = null!;
    public bool IsConfigurationCompleted { get; set; }
    public string? SubscriptionStatus { get; set; }

    /// <summary>
    /// Plan type of the teacher's latest subscription: Full or Managerial.
    /// Null when the teacher has never had a subscription. Serialized as a string.
    /// </summary>
    public SubscriptionPlanType? PlanType { get; set; }

    public DateTime? SubscriptionEndDate { get; set; }

    /// <summary>
    /// Start date of the teacher's CURRENT subscription (IsCurrent row), or null when the
    /// teacher has never subscribed / has no current row. "Subscribed on" column and the
    /// Activity Monitor's newly-subscribed tab both read this.
    /// </summary>
    public DateTime? SubscriptionStartDate { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// UTC timestamp of the teacher's most recent successful login, or null if the
    /// teacher has never logged in. Sourced from User.LastLoginAt (REQ-ADM-026).
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// UTC "last seen" — the teacher's most recent authenticated request, stamped by
    /// SessionActivitySlidingMiddleware (±5-minute throttle). Null until the account's
    /// first request after the LastActivityAt column shipped. Activity Monitor column.
    /// </summary>
    public DateTime? LastActivityAt { get; set; }

    /// <summary>
    /// Number of assistant accounts under this teacher — same population as
    /// GET /api/assistant/all?teacherId={Id}, so the Activity Monitor's collapsed row
    /// count always matches what expanding the row loads.
    /// </summary>
    public int AssistantCount { get; set; }

    /// <summary>
    /// Latest "seen" moment across the whole team: the max of the teacher's own
    /// LastActivityAt/LastLoginAt and every assistant's LastActivityAt/LastLoginAt.
    /// Answers "when did this teacher OR any of their assistants last use the app"
    /// without expanding the row. Null when nobody has ever logged in.
    /// </summary>
    public DateTime? TeamLastActivityAt { get; set; }

    /// <summary>
    /// True when <see cref="TeamLastActivityAt"/> came from an assistant rather than the
    /// teacher's own account — the UI annotates the value with "assistant" so the admin
    /// knows who was actually active.
    /// </summary>
    public bool TeamLastActivityIsAssistant { get; set; }

    /// <summary>
    /// The owning Center's id when this teacher is center-owned (operated by a Center account with no
    /// login of its own); null for a standalone teacher. Additive — lets the SuperAdmin list identify
    /// and filter center-owned teachers.
    /// </summary>
    public long? CenterId { get; set; }

    /// <summary>
    /// The Managerial/Full plan of a CENTER-OWNED teacher (which has no individual TeacherSubscription,
    /// so the <see cref="PlanType"/>/<see cref="SubscriptionStatus"/> above are null). Null for standalone.
    /// </summary>
    public SubscriptionPlanType? CenterPlanType { get; set; }
}