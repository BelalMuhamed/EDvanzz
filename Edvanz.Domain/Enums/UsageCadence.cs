using System.Text.Json.Serialization;

namespace Edvanz.Domain.Enums;

/// <summary>
/// AXIS 1 of the admin usage model — HOW OFTEN a teacher account is worked.
///
/// Derived from the count of ACTIVE DAYS in the last 30 (see <c>TeacherUsageSnapshot.ActiveDays30</c>).
/// An "active day" is a day — in the TEACHER'S local calendar, never UTC (CLAUDE.md §11b) — on which
/// at least one real human write happened, by the teacher or by any of their assistants.
///
/// This is deliberately NOT "did they log in". A login proves nothing: someone who opens the app once
/// and marks nothing looks identical to someone running their whole business on it. Only writes count,
/// and system-generated writes (the 02:30 auto-absent sweep) are excluded at the source.
///
/// Stored as tinyint. Band thresholds live in <c>AdminInsightsConstants</c> so they can be tuned in one
/// place without touching the enum or the stored values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageCadence : byte
{
    /// <summary>No human write EVER. Registered, never started.</summary>
    Never = 0,

    /// <summary>Was active at some point, but nothing in the last 30 days. The churn-risk band.</summary>
    Dormant = 1,

    /// <summary>1-3 active days in the last 30. Opens it now and then.</summary>
    Rarely = 2,

    /// <summary>4-9 active days in the last 30. Roughly weekly.</summary>
    Weekly = 3,

    /// <summary>10-19 active days in the last 30. Several times a week.</summary>
    MostDays = 4,

    /// <summary>20+ active days in the last 30. Runs the business on it.</summary>
    Daily = 5
}
