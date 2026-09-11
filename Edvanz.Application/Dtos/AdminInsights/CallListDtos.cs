namespace Edvanz.Application.Dtos.AdminInsights;

/// <summary>
/// ONE teacher to call, with the reason and what to do about it.
///
/// WHY THIS REPLACED THE CARDS: the overview used to show nine insight lists side by side. Across
/// 171 teachers they held 241 entries, the same teacher appeared on several of them, nothing said
/// which to work first, and no card said what to actually DO. That is a report, not a worklist.
///
/// Here every teacher appears EXACTLY ONCE, under their single most urgent reason, ranked so the
/// top of the list is the first call of the day.
/// </summary>
public class CallListItemDto
{
    public long TeacherId { get; set; }
    public string FullName { get; set; } = null!;
    public string TeacherCode { get; set; } = null!;

    /// <summary>The number to ring. The whole list is useless without it.</summary>
    public string? PhoneNumber { get; set; }

    public string? SalesRepName { get; set; }

    /// <summary>Rank, 1 = call first. Ties are broken by how much is at stake.</summary>
    public int Priority { get; set; }

    /// <summary>Stable key for the UI's filter chips and colour. Never shown to a person.</summary>
    public string ReasonKey { get; set; } = null!;

    /// <summary>Short label for the chip, e.g. "Paid, not started".</summary>
    public string ReasonLabel { get; set; } = null!;

    /// <summary>
    /// The evidence, in plain words with the real numbers in it — "Paid 3 days ago and still has
    /// no students". This is what makes the row believable rather than something to double-check.
    /// </summary>
    public string Why { get; set; } = null!;

    /// <summary>
    /// What to DO, as an instruction. The single thing the old cards were missing: a count and a
    /// name tell you there is a problem, not how to end the call having solved it.
    /// </summary>
    public string Action { get; set; } = null!;

    /// <summary>attention | warning | info — colour only.</summary>
    public string Severity { get; set; } = "info";

    public DateTime? LastActivityAt { get; set; }
    public int StudentCount { get; set; }

    /// <summary>Notes already written about this teacher, so nobody calls twice about the same thing.</summary>
    public int NoteCount { get; set; }
}

/// <summary>The call list plus the one-line context above it.</summary>
public class CallListDto
{
    public DateTime GeneratedAt { get; set; }

    /// <summary>How many teachers need a call at all — the list may be capped below this.</summary>
    public int TotalNeedingContact { get; set; }

    public int TotalTeachers { get; set; }

    /// <summary>Live in the last 30 days, for the single line of context above the list.</summary>
    public int Live { get; set; }

    /// <summary>Ranked, deduplicated, most urgent first.</summary>
    public IReadOnlyList<CallListItemDto> Items { get; set; } = Array.Empty<CallListItemDto>();

    /// <summary>How many fall under each reason, for the filter chips. Sums to the list length.</summary>
    public IReadOnlyList<BandCountDto> ReasonCounts { get; set; } = Array.Empty<BandCountDto>();
}
