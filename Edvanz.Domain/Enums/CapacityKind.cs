using System.Text.Json.Serialization;

namespace Edvanz.Domain.Enums;

/// <summary>
/// Which per-teacher limit a <see cref="Entities.CapacityIncreaseRequest"/> targets.
///
/// The two limits are SEPARATE axes:
///   • <see cref="AccountStudents"/> — <c>Teacher.StudentCapacity</c>: how many student
///     records may exist in the teacher's account (a free operational quota).
///   • <see cref="LinkedStudents"/> — <c>Teacher.LinkedStudentCapacity</c>: how many of
///     those students may have a LINKED student app account. This is the PRICED limit
///     (LinkedStudentCapacity × PricePerStudentEGP).
///
/// <see cref="AccountStudents"/> is 1 (the NOT NULL default) so every pre-existing row
/// keeps its original meaning after the column was added.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapacityKind
{
    /// <summary>Student records in the teacher's account (<c>Teacher.StudentCapacity</c>).</summary>
    AccountStudents = 1,

    /// <summary>Linked student app accounts (<c>Teacher.LinkedStudentCapacity</c>) — the priced limit.</summary>
    LinkedStudents = 2
}
