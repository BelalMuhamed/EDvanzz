using Edvanz.Domain.Entities;

namespace Edvanz.Domain.Helpers;

/// <summary>
/// SINGLE source of truth for the one rule that ties a teacher's two capacity limits together.
/// Every write path that sets <see cref="Teacher.LinkedStudentCapacity"/> — admin approve,
/// admin direct set, subscription-request approve, teacher creation — calls
/// <see cref="EnforceInvariant"/> instead of re-implementing the comparison, so the two limits
/// can never drift into an impossible state.
/// </summary>
public static class TeacherCapacityRules
{
    /// <summary>
    /// Enforces <c>LinkedStudentCapacity &lt;= StudentCapacity</c>. A linked student APP ACCOUNT
    /// always needs a student record behind it, so a higher linked limit would simply be
    /// unreachable. Rather than fail the caller, this RAISES <see cref="Teacher.StudentCapacity"/>
    /// to match — students-in-the-account is a free operational quota, so widening it costs
    /// nobody anything, whereas rejecting the write would block a legitimate plan change.
    /// Never LOWERS either value, and is a no-op when the invariant already holds.
    /// </summary>
    /// <param name="teacher">The teacher whose limits were just changed. Mutated in place.</param>
    public static void EnforceInvariant(Teacher teacher)
    {
        if (teacher.LinkedStudentCapacity > teacher.StudentCapacity)
            teacher.StudentCapacity = teacher.LinkedStudentCapacity;
    }
}
