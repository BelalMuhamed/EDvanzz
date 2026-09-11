using System.Text.Json.Serialization;

namespace Edvanz.Domain.Enums;

/// <summary>
/// AXIS 2 of the admin usage model — HOW MUCH OF THE PRODUCT a teacher actually touches.
///
/// Derived from the number of distinct modules with at least one real human write in the window
/// (the popcount of <c>TeacherUsageSnapshot.ModulesUsedMask</c>, see <see cref="UsageModules"/>).
///
/// The BAND is only a coarse sort key — the admin UI always shows the module LIST alongside it,
/// because "Attendance only" and "Attendance + Payments + Exams" are different businesses even
/// though both are just a number here. Never render the band without the modules.
///
/// Stored as tinyint. Thresholds live in <c>AdminInsightsConstants</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageDepth : byte
{
    /// <summary>No module used at all in the window.</summary>
    None = 0,

    /// <summary>Exactly one module — most often attendance. The classic onboarding/upsell target.</summary>
    Single = 1,

    /// <summary>2-3 modules. Typically attendance plus payments.</summary>
    Core = 2,

    /// <summary>4-5 modules.</summary>
    Broad = 3,

    /// <summary>6 or more modules. Using the platform as a whole.</summary>
    Full = 4
}
