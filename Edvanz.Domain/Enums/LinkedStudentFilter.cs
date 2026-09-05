using System.Text.Json.Serialization;

namespace Edvanz.Domain.Enums;

/// <summary>
/// Which slice of a teacher's connected student accounts to return from the linked-students list.
///
/// "Connected" and "linked" are SEPARATE axes (see the Connection-vs-Link split): every row here is
/// already an Active link, and this only narrows by whether that link is BOUND to a student record.
/// The headcounts returned alongside the page are deliberately NOT narrowed by this filter — they
/// describe the whole (searched) set so the client's filter chips can show real totals.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LinkedStudentFilter : byte
{
    /// <summary>Every connected account, bound or not. The default.</summary>
    All = 0,

    /// <summary>Only accounts bound to a student record (TeacherStudentId is set).</summary>
    Linked = 1,

    /// <summary>Only accounts that are connected but not yet bound to a student record.</summary>
    NotLinked = 2
}
