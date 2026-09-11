using Edvanz.Domain.Constants;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Helpers;

/// <summary>
/// Maps a teacher's GRANTED modules onto the usage flags the rollup measures, so the admin can ask
/// the only question that matters commercially: <b>is this teacher using what they pay for?</b>
///
/// Entitlement is a grant row in <c>TutorModuleAccess</c> joined to <c>Models</c> — the presence of
/// the row IS the grant, and it is the same source the runtime gate reads
/// (<c>UserRepo</c> builds <c>UserAuthSnapshot.Modules</c> from it), so this can never disagree with
/// what the teacher can actually open.
///
/// TWO SPECIAL CASES, both of which would otherwise produce a wrong phone call:
///
/// 1. <see cref="UsageModules.Sessions"/> — the <c>Session</c> grant EXISTS but is enforced NOWHERE
///    (zero <c>[ModulePermission]</c> sites reference it). A teacher without the grant can still
///    create classes, so treating the missing grant as "not entitled" would hide a genuine gap.
///    Sessions is therefore ALWAYS entitled.
///
/// 2. <see cref="UsageModules.ParentPortal"/> — has no module row at all. It is gated purely by the
///    subscription plan, so its entitlement is passed in rather than read from a grant.
/// </summary>
public static class UsageModuleEntitlement
{
    /// <summary>
    /// Grantable <c>Module.Name</c> → the usage flag it entitles. Names are the exact seeded
    /// strings; the constants are used where one exists (there is none for Session, Messaging or
    /// Exams And Homework — those are string literals throughout the API too).
    /// </summary>
    private static readonly (string ModuleName, UsageModules Flag)[] Map =
    {
        (StudentConstants.ModuleName,        UsageModules.Students),      // "Student"
        ("Session",                          UsageModules.Sessions),
        (AttendanceConstants.ModuleName,     UsageModules.Attendance),    // "Attendance"
        (PaymentConstants.ModuleName,        UsageModules.Payments),      // "Payment"
        (PaymentConstants.EventModuleName,   UsageModules.EventPayments), // "Event-Based Payment"
        ("Exams And Homework",               UsageModules.ExamsHomework),
        ("Messaging",                        UsageModules.Messaging),
        (VideoConstants.ModuleName,          UsageModules.Videos),        // "Videos"
        (OnlineExamConstants.ModuleName,     UsageModules.OnlineExams)    // "OnlineExam"
    };

    /// <summary>
    /// Flags that are entitled regardless of any grant row. See case 1 above — the Session grant is
    /// stored and shown in the admin UI but never enforced, so everyone can use classes.
    /// </summary>
    public const int AlwaysEntitled = (int)UsageModules.Sessions;

    /// <summary>
    /// Builds the entitlement mask for one teacher.
    /// </summary>
    /// <param name="grantedModuleNames">Module names from that teacher's grant rows.</param>
    /// <param name="parentFollowUpAllowed">
    /// Whether the plan permits the public parent follow-up portal. Plan-derived, not a grant —
    /// blocked only for an ACTIVE plain Managerial plan.
    /// </param>
    public static int Build(IEnumerable<string> grantedModuleNames, bool parentFollowUpAllowed)
    {
        int mask = AlwaysEntitled;

        foreach (var name in grantedModuleNames)
            foreach (var (moduleName, flag) in Map)
                if (string.Equals(name, moduleName, StringComparison.OrdinalIgnoreCase))
                    mask |= (int)flag;

        if (parentFollowUpAllowed)
            mask |= (int)UsageModules.ParentPortal;

        return mask;
    }

    /// <summary>
    /// Features they have and have NEVER touched — the gap, and the reason to call. Computed
    /// against EVER-used rather than the 30-day window: a feature used once in March is adopted,
    /// not missing.
    /// </summary>
    public static int NeverUsed(int entitledMask, int everUsedMask) => entitledMask & ~everUsedMask;

    /// <summary>
    /// Features they adopted and then stopped. Distinct from never-used because it is a different
    /// conversation: something made them give up, rather than never starting.
    /// </summary>
    public static int Lapsed(int everUsedMask, int usedMask30) => everUsedMask & ~usedMask30;

    /// <summary>Features they have and have used at least once.</summary>
    public static int Adopted(int entitledMask, int everUsedMask) => entitledMask & everUsedMask;

    /// <summary>Number of bits set, for "using 4 of 7".</summary>
    public static int Count(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);
}
