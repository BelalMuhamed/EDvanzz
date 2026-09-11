using Edvanz.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace Edvanz.Application.Dtos.AdminInsights;

// ════════════════════════════════════════════════════════════════════════════
// ADMIN INSIGHTS — WIRE CONTRACTS
// ════════════════════════════════════════════════════════════════════════════
//
// Every enum below serializes as a STRING (the global JsonStringEnumConverter), and module sets
// travel as string arrays rather than the stored bit mask — the admin UI renders "Attendance ·
// Payments" as chips, and a client should never have to know the bit layout.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One teacher on the usage grid — the three axes, the setup-health pairs, and enough identity to
/// act on the row without a second call.
/// </summary>
public class TeacherUsageListItemDto
{
    // ── Identity ───────────────────────────────────────────────────────────────
    public long TeacherId { get; set; }

    /// <summary>The teacher's login User id — what the admin password-reset endpoint takes.</summary>
    public long UserId { get; set; }

    public string FullName { get; set; } = null!;
    public string? Username { get; set; }
    public string TeacherCode { get; set; } = null!;
    public string? PhoneNumber { get; set; }

    /// <summary>Second way to reach them when the phone is dead.</summary>
    public string? Email { get; set; }

    public DateTime RegisteredAt { get; set; }
    public AccountStatus AccountStatus { get; set; }

    // ── Commercial ─────────────────────────────────────────────────────────────
    public string? SubscriptionStatus { get; set; }

    /// <summary>When the current subscription started — what "newly subscribed" is measured from.</summary>
    public DateTime? SubscriptionStartDate { get; set; }

    public DateTime? SubscriptionEndDate { get; set; }

    /// <summary>Full / Managerial / ManagerialPlus.</summary>
    public string? PlanType { get; set; }
    public long? SalesRepId { get; set; }
    public string? SalesRepName { get; set; }
    public string? AcquisitionSource { get; set; }

    // ── Axis 1: cadence ────────────────────────────────────────────────────────
    public UsageCadence Cadence { get; set; }
    public int ActiveDays7 { get; set; }
    public int ActiveDays30 { get; set; }
    public int ActiveDays90 { get; set; }
    public int TotalWrites30 { get; set; }

    // ── Axis 2: depth ──────────────────────────────────────────────────────────
    public UsageDepth Depth { get; set; }

    /// <summary>Modules used in the last 30 days, by name. The band alone is never enough —
    /// "Attendance only" and "Attendance + Payments + Exams" are different businesses.</summary>
    public IReadOnlyList<string> Modules { get; set; } = Array.Empty<string>();

    /// <summary>Modules used at any point. The difference from <see cref="Modules"/> separates
    /// "never adopted it" from "gave up on it".</summary>
    public IReadOnlyList<string> ModulesAllTime { get; set; } = Array.Empty<string>();

    // ── Axis 3: operator mix ───────────────────────────────────────────────────
    public OperatorMix Operators { get; set; }
    public DateTime? LastTeacherActivityAt { get; set; }
    public DateTime? LastAssistantActivityAt { get; set; }
    public int ActiveAssistantCount { get; set; }

    // ── Lifespan ───────────────────────────────────────────────────────────────
    public DateTime? FirstActivityAt { get; set; }
    public DateTime? LastActivityAt { get; set; }

    // ── Setup health (each pair: total, then the part that actually works) ──────
    public int StudentCount { get; set; }
    public int StudentsAssignedToSession { get; set; }
    public int SessionCount { get; set; }
    public int SessionsWithOccurrences { get; set; }
    public int LinkedAccountCount { get; set; }
    public int BoundAccountCount { get; set; }
    public bool HasEverMarkedAttendance { get; set; }
    public bool HasEverCollectedPayment { get; set; }

    /// <summary>True only when a roster is assigned to a session that has occurrences. Deliberately
    /// stricter than "has students".</summary>
    public bool HasRealData { get; set; }

    // ── Admin context ──────────────────────────────────────────────────────────
    public int NoteCount { get; set; }
    public DateTime? LastNoteAt { get; set; }

    /// <summary>Daily write totals for the last 30 days, oldest first, zero-filled. Drives the
    /// inline sparkline so a row shows its shape, not just its band.</summary>
    public IReadOnlyList<int> Sparkline30 { get; set; } = Array.Empty<int>();

    /// <summary>When the rollup last recomputed this teacher. Lets the UI distinguish stale numbers
    /// from genuinely quiet ones. Null = never rolled up.</summary>
    public DateTime? ComputedAt { get; set; }
}

/// <summary>Filters for the usage grid. Every one is optional and they compose.</summary>
public class TeacherUsageQueryRequest
{
    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 20;

    /// <summary>Matches name, username, teacher code or phone. Arabic-variant insensitive.</summary>
    public string? Search { get; set; }

    public UsageCadence? Cadence { get; set; }
    public UsageDepth? Depth { get; set; }
    public OperatorMix? Operators { get; set; }

    /// <summary>Only teachers who used this specific module in the last 30 days.</summary>
    public UsageModules? UsingModule { get; set; }

    /// <summary>True = only set-up accounts; false = only accounts with nothing that works.</summary>
    public bool? HasRealData { get; set; }

    public long? SalesRepId { get; set; }

    /// <summary>Only teachers with no sales rep assigned — the attribution backlog.</summary>
    public bool? UnassignedSalesRep { get; set; }

    public string? SubscriptionStatus { get; set; }
    public DateTime? RegisteredFrom { get; set; }
    public DateTime? RegisteredTo { get; set; }

    /// <summary>
    /// Only teachers whose current subscription started within this many days. Setting it also
    /// forces newest-subscription-first ordering, because that is the question being asked.
    /// </summary>
    public int? SubscribedWithinDays { get; set; }

    public TeacherUsageSortBy SortBy { get; set; } = TeacherUsageSortBy.LastActivity;
    public SortDirection SortDirection { get; set; } = SortDirection.Desc;
}

/// <summary>Sort keys for the usage grid.</summary>
public enum TeacherUsageSortBy
{
    LastActivity = 0,
    ActiveDays30 = 1,
    TotalWrites30 = 2,
    StudentCount = 3,
    RegisteredAt = 4,
    Name = 5
}

// ════════════════════════════════════════════════════════════════════════════
// OVERVIEW
// ════════════════════════════════════════════════════════════════════════════

/// <summary>The admin landing page in one call.</summary>
public class AdminOverviewDto
{
    public DateTime GeneratedAt { get; set; }

    /// <summary>Oldest <c>ComputedAt</c> across all snapshots — if the rollup stalled, this is how
    /// the admin finds out rather than quietly trusting stale figures.</summary>
    public DateTime? OldestSnapshotAt { get; set; }

    public AdminOverviewTotalsDto Totals { get; set; } = new();

    /// <summary>Distribution across each axis, for the three headline charts.</summary>
    public IReadOnlyList<BandCountDto> CadenceBreakdown { get; set; } = Array.Empty<BandCountDto>();
    public IReadOnlyList<BandCountDto> DepthBreakdown { get; set; } = Array.Empty<BandCountDto>();
    public IReadOnlyList<BandCountDto> OperatorBreakdown { get; set; } = Array.Empty<BandCountDto>();

    /// <summary>How many teachers actually use each module. The honest adoption picture.</summary>
    public IReadOnlyList<BandCountDto> ModuleAdoption { get; set; } = Array.Empty<BandCountDto>();

    /// <summary>The actionable lists. Each NAMES teachers — a count alone cannot be worked.</summary>
    public IReadOnlyList<AdminInsightCardDto> Insights { get; set; } = Array.Empty<AdminInsightCardDto>();
}

/// <summary>Headline counts. "Live" means real activity in the last 30 days, not a login.</summary>
public class AdminOverviewTotalsDto
{
    public int Teachers { get; set; }
    public int Live { get; set; }
    public int Dormant { get; set; }
    public int NeverStarted { get; set; }
    public int WithRealData { get; set; }
    public int AssistantOnly { get; set; }

    /// <summary>Teachers live in the PREVIOUS 30-day window, so the UI can show a real delta rather
    /// than a number with no context.</summary>
    public int LivePrevious { get; set; }
}

/// <summary>One bar/slice: a band or module name and how many teachers fall in it.</summary>
public class BandCountDto
{
    public string Key { get; set; } = null!;
    public int Count { get; set; }
}

/// <summary>
/// One insight card: a named problem, how many teachers have it, and the first few of them by name.
/// </summary>
public class AdminInsightCardDto
{
    /// <summary>Stable identifier the UI keys its copy and its "see all" filter off — never the title.</summary>
    public string Key { get; set; } = null!;

    /// <summary>Why this card matters, in one line. Shipped from the backend so the definition and
    /// its explanation can never drift apart.</summary>
    public string Description { get; set; } = null!;

    /// <summary>attention | warning | info — drives colour only.</summary>
    public string Severity { get; set; } = "info";

    public int TotalCount { get; set; }

    /// <summary>The first few teachers, by name. The rest sit behind the card's "see all" filter.</summary>
    public IReadOnlyList<InsightTeacherDto> Teachers { get; set; } = Array.Empty<InsightTeacherDto>();
}

/// <summary>A teacher as named on an insight card — just enough to recognise and act on them.</summary>
public class InsightTeacherDto
{
    public long TeacherId { get; set; }
    public string FullName { get; set; } = null!;
    public string TeacherCode { get; set; } = null!;
    public string? PhoneNumber { get; set; }
    public string? SalesRepName { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime RegisteredAt { get; set; }
    public int StudentCount { get; set; }

    /// <summary>The one number that explains why this teacher is on this card — days assigned to a
    /// session, days silent, and so on. Card-specific by design.</summary>
    public string? Detail { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
// TEACHER 360 — USAGE TAB
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Everything the Teacher 360 usage tab draws.</summary>
public class TeacherUsageDetailDto
{
    public TeacherUsageListItemDto Summary { get; set; } = new();

    /// <summary>90 days of daily totals, oldest first, zero-filled so the chart has no gaps.</summary>
    public IReadOnlyList<UsageDayPointDto> DailySeries { get; set; } = Array.Empty<UsageDayPointDto>();

    /// <summary>Writes per module over the last 30 days — which parts of the product they lean on.</summary>
    public IReadOnlyList<BandCountDto> ModuleBreakdown30 { get; set; } = Array.Empty<BandCountDto>();

    /// <summary>The people on the account and when each was last seen. This is the operator axis
    /// made concrete: a name to call, not just a band.</summary>
    public IReadOnlyList<TeacherOperatorDto> Operators { get; set; } = Array.Empty<TeacherOperatorDto>();
}

/// <summary>One day on the activity chart.</summary>
public class UsageDayPointDto
{
    /// <summary>Teacher-local calendar day. A day, so it is a <c>DateOnly</c> on the wire
    /// (CLAUDE.md §11b) — a DateTime here would carry a meaningless Z.</summary>
    public DateOnly Date { get; set; }

    public int TotalWrites { get; set; }
    public int TeacherWrites { get; set; }
    public int AssistantWrites { get; set; }
    public IReadOnlyList<string> Modules { get; set; } = Array.Empty<string>();
}

/// <summary>A person who works the account: the teacher themself, or one of their assistants.</summary>
public class TeacherOperatorDto
{
    public long UserId { get; set; }
    public string FullName { get; set; } = null!;
    public string? Username { get; set; }

    /// <summary>Teacher | Assistant.</summary>
    public string Role { get; set; } = null!;

    /// <summary>False for a removed assistant, whose past work still counts but who cannot log in.</summary>
    public bool IsActive { get; set; }

    public DateTime? LastLoginAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
// SALES
// ════════════════════════════════════════════════════════════════════════════

/// <summary>A sales rep plus how their accounts are actually doing.</summary>
public class SalesRepDto
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;
    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }

    // ── Performance. Accounts sold is the vanity number; the rest is the truth. ──
    public int TeachersAssigned { get; set; }
    public int TeachersLive { get; set; }
    public int TeachersDormant { get; set; }
    public int TeachersNeverStarted { get; set; }
    public int TeachersWithRealData { get; set; }
}

/// <summary>Create/update payload for a sales rep.</summary>
public class SaveSalesRepRequest
{
    [Required, MaxLength(128)]
    public string Name { get; set; } = null!;

    [MaxLength(32)]
    public string? PhoneNumber { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Assigns (or clears) a teacher's sales attribution.</summary>
public class AssignSalesRepRequest
{
    /// <summary>Null clears the attribution.</summary>
    public long? SalesRepId { get; set; }

    [MaxLength(128)]
    public string? AcquisitionSource { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
// NOTES
// ════════════════════════════════════════════════════════════════════════════

/// <summary>An internal admin note about a teacher.</summary>
public class AdminNoteDto
{
    public long Id { get; set; }
    public long TeacherId { get; set; }
    public long AuthorUserId { get; set; }
    public string AuthorName { get; set; } = null!;
    public string Body { get; set; } = null!;
    public bool IsPinned { get; set; }
    public DateOnly? FollowUpDate { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Create payload for a note.</summary>
public class CreateAdminNoteRequest
{
    [Required, MaxLength(4000)]
    public string Body { get; set; } = null!;

    public bool IsPinned { get; set; }

    /// <summary>Optional date to come back to this teacher.</summary>
    public DateOnly? FollowUpDate { get; set; }
}
