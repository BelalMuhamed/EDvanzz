using System.Text.Json.Serialization;

namespace Edvanz.Domain.Enums;

/// <summary>
/// Bit flags naming every module the admin usage rollup measures. Stored as an int mask on
/// <c>TeacherUsageSnapshot.ModulesUsedMask</c> (last 30 days) and <c>ModulesUsedAllTimeMask</c>,
/// so "which modules does this teacher use" is one column, one index, and one cheap filter.
///
/// A flag is set when the module has at least ONE REAL HUMAN WRITE in the window. Two exclusions
/// are load-bearing and must never be dropped:
///  • Attendance ignores <c>AttendanceRecord.IsAutoAbsent</c> rows — the nightly 02:30 sweep writes
///    those with no human involved, so counting them would make every idle teacher look active.
///  • Payments ignores <c>IsDeleted</c> transactions — a reversed collection is not usage.
///
/// Adding a module = add a flag here, add its source to the rollup service, and add its label to the
/// admin UI. The mask is additive, so existing stored values keep their meaning.
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageModules
{
    None = 0,

    /// <summary>Roster rows created (TeacherStudent).</summary>
    Students = 1 << 0,

    /// <summary>Classes created or scheduled (Session / SessionOccurrence).</summary>
    Sessions = 1 << 1,

    /// <summary>Attendance marked by a human (AttendanceRecord where IsAutoAbsent = false).</summary>
    Attendance = 1 << 2,

    /// <summary>Money collected (PaymentTransaction where !IsDeleted).</summary>
    Payments = 1 << 3,

    /// <summary>Videos uploaded (VideoAsset).</summary>
    Videos = 1 << 4,

    /// <summary>Online exams created (OnlineExam).</summary>
    OnlineExams = 1 << 5,

    /// <summary>Paper exams / homework created (AssignmentTemplate).</summary>
    ExamsHomework = 1 << 6,

    /// <summary>Manual or templated messages sent (MessageLog).</summary>
    Messaging = 1 << 7,

    /// <summary>Parent follow-up portal grants (ParentPortalAccess).</summary>
    ParentPortal = 1 << 8
}
