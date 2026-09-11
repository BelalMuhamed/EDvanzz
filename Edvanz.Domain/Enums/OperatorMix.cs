using System.Text.Json.Serialization;

namespace Edvanz.Domain.Enums;

/// <summary>
/// AXIS 3 of the admin usage model — WHO is actually working the account.
///
/// Resolved from two independent evidence sources, both already in the schema:
///  • ATTRIBUTED WORK — <c>AttendanceRecord.RecordedByUserId</c> and
///    <c>PaymentTransaction.CollectedByUserId</c>. These are the two highest-volume writes and
///    exactly the ones assistants perform, so they carry most of the signal.
///  • PRESENCE — <c>User.LastLoginAt</c> / <c>User.LastActivityAt</c> on the teacher's own user row
///    and on each assistant's, used as a fallback for modules that carry no actor column.
///
/// <see cref="AssistantsOnly"/> is the single highest-value signal in the whole model: the owner has
/// disengaged while their staff keep the lights on. That account is at risk even though its raw
/// activity numbers look healthy, and no screen before this one could surface it.
///
/// Stored as tinyint.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperatorMix : byte
{
    /// <summary>No human write from anyone in the window.</summary>
    Nobody = 0,

    /// <summary>Only the teacher's own user account did the work. No assistants, or none active.</summary>
    TeacherOnly = 1,

    /// <summary>Only assistants worked the account — the teacher themself was absent. CHURN WARNING.</summary>
    AssistantsOnly = 2,

    /// <summary>Both the teacher and at least one assistant were active. The healthiest shape.</summary>
    TeacherAndAssistants = 3
}
