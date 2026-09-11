namespace Edvanz.Application.Dtos.AdminInsights;

/// <summary>
/// The landing page: how the SUBSCRIBED base is doing.
///
/// Scoped to subscribers by default because that is the question actually being asked. Counting the
/// free and expired accounts alongside them buries the paying base — a hundred accounts that never
/// subscribed made every platform figure look like a failure when the subscribers were fine.
/// </summary>
public class AdminNumbersDto
{
    public DateTime GeneratedAt { get; set; }

    /// <summary>False when free and expired accounts are folded in too.</summary>
    public bool SubscribedOnly { get; set; }

    /// <summary>Teachers in scope.</summary>
    public int Teachers { get; set; }

    /// <summary>Of those, how many did something real in the last 30 days.</summary>
    public int Active { get; set; }

    /// <summary>Same window, previous period — so the headline carries a real delta.</summary>
    public int ActivePrevious { get; set; }

    /// <summary>Properly set up: students in a class that has class days.</summary>
    public int SetUp { get; set; }

    /// <summary>On the call list for any reason — the link between this page and the work.</summary>
    public int NeedAttention { get; set; }

    /// <summary>
    /// The centrepiece: for every feature, how many are entitled, how many use it, and how many
    /// have never opened it. This is "is he using all the features or not", aggregated — and the
    /// clearest read on where the product is failing to land.
    /// </summary>
    public IReadOnlyList<FeatureAdoptionDto> FeatureAdoption { get; set; } = Array.Empty<FeatureAdoptionDto>();

    /// <summary>Subscribers broken down by plan, each with its own adoption.</summary>
    public IReadOnlyList<PlanBreakdownDto> ByPlan { get; set; } = Array.Empty<PlanBreakdownDto>();
}

/// <summary>One row of the feature-adoption table.</summary>
public class FeatureAdoptionDto
{
    /// <summary>Stable feature key — the UI turns it into a label and a filter link.</summary>
    public string Feature { get; set; } = null!;

    /// <summary>How many teachers in scope are entitled to it.</summary>
    public int Entitled { get; set; }

    /// <summary>Of those, how many used it in the last 30 days.</summary>
    public int UsingNow { get; set; }

    /// <summary>Of those, how many have used it at least once, ever.</summary>
    public int EverUsed { get; set; }

    /// <summary>Entitled and never opened. The upsell and onboarding surface.</summary>
    public int NeverUsed { get; set; }
}

/// <summary>Subscribers on one plan.</summary>
public class PlanBreakdownDto
{
    /// <summary>Full / Managerial / ManagerialPlus.</summary>
    public string Plan { get; set; } = null!;

    public int Teachers { get; set; }
    public int Active { get; set; }

    /// <summary>Average share of entitled features actually adopted, 0-100.</summary>
    public int AdoptionPercent { get; set; }
}
