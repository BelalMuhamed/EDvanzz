using System;
using System.Collections.Generic;

namespace Edvanz.Application.Dtos.Payment;

// ══════════════════════════════════════════════════════════════════════════
// PAYMENT "SCREENS" DTOs  (frontend payment.json spec — api/v1/*)
//
// These map 1:1 to the shapes the frontend designed in payment.json. They are
// intentionally separate from the existing PaymentDtos so the screen contract can
// evolve without disturbing the current PaymentController DTOs.
//
// CONVENTIONS:
//  - PascalCase C# properties serialize to camelCase JSON (MvcJsonDefaults.Web),
//    which is exactly what the frontend expects (monthLabel, studentId, ...).
//  - Entity ids are exposed as STRINGS per the frontend contract (backend uses long).
//  - DateTimes are returned as-is (System.Text.Json emits ISO-8601).
// ══════════════════════════════════════════════════════════════════════════

// ── Screen: SessionPaymentCollectedByMonth ─────────────────────────────────

/// <summary>Paginated ledger of collected student payments for a given month + year.</summary>
public class CollectionsByMonthResponse
{
    public int Month { get; set; }
    public int Year { get; set; }
    public string MonthLabel { get; set; } = string.Empty;
    public int Page { get; set; }
    public int Limit { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }

    /// <summary>
    /// Echo of an applied date-range filter (inclusive). Populated ONLY when the caller passed
    /// <c>from</c>/<c>to</c>; null on the month/year path so existing consumers are unaffected.
    /// </summary>
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }

    public List<CollectionRow> Items { get; set; } = new();

    /// <summary>
    /// Distribution of the money collected in this scope by per-month amount — one entry per distinct
    /// amount (e.g. 300 → 12, 200 → 7, 100 → 20), highest count first. Derived from the per-period
    /// settlement slices so a multi-month payment (e.g. 600 = 2×300) counts once per month at its
    /// monthly amount. Covers the whole scope (not just the current page); withdrawals are excluded.
    /// </summary>
    public List<CollectionAmountTier> AmountTiers { get; set; } = new();

    /// <summary>
    /// Per-day money totals for the whole collector-scoped scope (not just the current page), newest
    /// day first — powers the ledger's day-separator headers ("31 Aug 2026 · +1,250 · −300"). Computed
    /// over the full in-memory set so each day's net is authoritative regardless of pagination. Empty on
    /// the teacher-wide (account) path; only populated when the ledger is scoped to one collector.
    /// </summary>
    public List<CollectionDailyNet> DailyNets { get; set; } = new();
}

/// <summary>One "how many paid X" bucket for the collections summary cards.</summary>
public class CollectionAmountTier
{
    /// <summary>The per-month amount collected (a settlement slice's applied amount).</summary>
    public decimal Amount { get; set; }
    /// <summary>How many month-payments were collected at this amount in scope.</summary>
    public int Count { get; set; }
}

/// <summary>
/// One calendar day's money totals in a collector-scoped ledger — the source for the ledger's
/// day-separator headers. Computed over the whole scope (all pages), so <see cref="Net"/> is final.
/// </summary>
public class CollectionDailyNet
{
    /// <summary>Stable calendar-day key ("yyyy-MM-dd") — matches <see cref="CollectionRow.DayKey"/>.</summary>
    public string DateKey { get; set; } = string.Empty;
    /// <summary>The calendar day (date component only).</summary>
    public DateTime Date { get; set; }
    /// <summary>Total money collected (positive lines) on this day.</summary>
    public decimal Collected { get; set; }
    /// <summary>Total money returned/handed-over on this day (refunds + withdrawals), as a positive number.</summary>
    public decimal Deducted { get; set; }
    /// <summary>Net for the day = <see cref="Collected"/> − <see cref="Deducted"/> (may be negative).</summary>
    public decimal Net { get; set; }
    /// <summary>How many collection (positive) lines landed on this day.</summary>
    public int CollectionsCount { get; set; }
}

/// <summary>One row in the collected-payments ledger.</summary>
public class CollectionRow
{
    public string Id { get; set; } = string.Empty;
    public int Index { get; set; }
    public string? StudentId { get; set; }
    public string? StudentName { get; set; }
    /// <summary>The teacher's roster code for the student (e.g. "123"); null for a withdrawal line.</summary>
    public string? StudentCode { get; set; }
    /// <summary>Positive for a collection; NEGATIVE for a departure refund (money returned).</summary>
    public decimal Amount { get; set; }
    /// <summary>collected | pending | refund | withdrawal. Refund/withdrawal are negative-amount lines.</summary>
    public string Status { get; set; } = "collected";
    public string? SessionName { get; set; }
    public DateTime? CollectedAt { get; set; }

    /// <summary>
    /// The collector's free-text note captured for a custom/partial collect (from
    /// <c>PaymentTransaction.CollectionNote</c>). Null for whole-month collects and for
    /// refund/withdrawal lines (which carry no transaction).
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Stable calendar-day key ("yyyy-MM-dd") for this row, from the raw stored <see cref="CollectedAt"/>
    /// — the SAME day notion the collections date-filter uses. The client groups the ledger into
    /// day-separator sections by this string (never by re-deriving a date from <see cref="CollectedAt"/>,
    /// which would drift at the UTC/local boundary) and matches it to a <see cref="CollectionDailyNet"/>.
    /// Populated on the collector-scoped path; null on the teacher-wide path.
    /// </summary>
    public string? DayKey { get; set; }

    /// <summary>True when this row is a student-departure refund (negative amount).</summary>
    public bool IsRefund { get; set; }

    /// <summary>
    /// True when this row is a cash withdrawal/hand-over taken from this collector's wallet
    /// (negative amount, no student). Rendered as a hand-over line, not a student row.
    /// </summary>
    public bool IsWithdrawal { get; set; }

    /// <summary>
    /// For a money-out line (withdrawal / refund) on a COLLECTOR-scoped ledger: the full name of the
    /// user who PERFORMED the action, set only when that user is someone OTHER than the collector the
    /// ledger is scoped to (e.g. the tutor taking a hand-over from an assistant's wallet, or the tutor
    /// departing a student whose refund is charged to the original collector). Null on collections,
    /// on the collector's own actions, and on teacher-wide ledgers — so the label reads exactly as
    /// "someone else did this", which is the case that was being misread.
    /// </summary>
    public string? PerformedByName { get; set; }

    /// <summary>
    /// Number of months (settlement slices) this collection cleared — 1 for a normal single-month
    /// payment, &gt;1 when one cash event paid several months at once (e.g. 600 = 2 months). Lets the
    /// client show "N months" so a large amount isn't mistaken for a single month. 0/1 for refund and
    /// withdrawal lines.
    /// </summary>
    public int PeriodsCovered { get; set; }

    /// <summary>
    /// For a refund only: display label of the month the departure applies to (e.g. "March 2026").
    /// Null for a normal collection.
    /// </summary>
    public string? RefundedForMonthLabel { get; set; }

    /// <summary>
    /// For a collection: exactly which month(s) this cash settled, oldest-first, from the allocation
    /// ledger (e.g. a 600 payment → [{August, 300}, {July, 300}]). Empty for refund/withdrawal lines
    /// and for legacy collections with no allocation ledger. Lets the ledger name the installment(s)
    /// instead of showing a bare amount.
    /// </summary>
    public List<CollectionMonthSlice> AppliedMonths { get; set; } = new();

    /// <summary>True when this collection's amount was later edited (an <c>AmountChanged</c> edit log).
    /// Pair with <see cref="OriginalAmount"/> to render "edited from X to Y".</summary>
    public bool IsEdited { get; set; }

    /// <summary>The amount originally collected, before the first amount-edit. Null when never edited;
    /// the current (edited) value is <see cref="Amount"/>.</summary>
    public decimal? OriginalAmount { get; set; }

    // ── Prorated-first-month transparency (REQ-PAY-021/022 §2b, 2026-09-02) ──
    // So a teacher can always see what an assistant did on a joining month: that it was prorated, the
    // system's suggestion, and who set the amount.

    /// <summary>True when this collection settled a student's prorated JOINING (anchor) month.</summary>
    public bool IsProratedFirstMonth { get; set; }

    /// <summary>The system-suggested joining amount at set time (from the proration audit log), when the
    /// human-set amount differed from it. Null when never manually overridden (auto suggestion == amount).</summary>
    public decimal? SystemSuggestedProratedAmount { get; set; }

    /// <summary>Display name of the user who SET the manual joining amount (from the proration audit log).
    /// Null when the joining month was never manually overridden.</summary>
    public string? ProrationSetByName { get; set; }

    // ── Joining-month STORY (rev 2 increment 2, 2026-09-03) — the same "why this price" facts the
    // student cards show, so the collector-scoped ledgers explain a prorated first-month collection
    // identically: "Joined {date} · {billed} of {total} classes left · {amount} of {full}".

    /// <summary>The student's enrollment (earliest assignment) date. Null when unavailable.</summary>
    public DateTime? ProrationJoinedAt { get; set; }

    /// <summary>ByClasses basis frozen at pricing time (see PaymentPeriod). Null for percentage/manual.</summary>
    public int? ProrationClassesTotal { get; set; }

    /// <inheritdoc cref="ProrationClassesTotal"/>
    public int? ProrationClassesBilled { get; set; }

    /// <summary>The joining month's (reduced) billed amount — the "160" of "160 of 300".</summary>
    public decimal? ProratedFirstMonthAmount { get; set; }

    /// <summary>The full month price the reduction was taken from — the "300". Reconstructed from the
    /// anchor's stored amount ÷ fraction (display-only).</summary>
    public decimal? ProrationFullMonthAmount { get; set; }

    /// <summary>True when the joining amount was set BY HAND (sticky override) — renders the
    /// name-free "Set by hand" piece in the story line.</summary>
    public bool IsProrationManual { get; set; }
}

/// <summary>One month a collection settled: the month key/label and how much of the cash landed on it.</summary>
public class CollectionMonthSlice
{
    /// <summary>The month, as "YYYY-MM".</summary>
    public string Month { get; set; } = string.Empty;
    /// <summary>Human label, e.g. "August 2026".</summary>
    public string MonthLabel { get; set; } = string.Empty;
    /// <summary>Amount of this cash event applied to this month.</summary>
    public decimal Amount { get; set; }

    /// <summary>True when the month this slice settled was the prorated anchor month — lets the
    /// collections/wallet ledger keep showing "prorated" as history, even after the money was collected.</summary>
    public bool IsProRated { get; set; }

    /// <summary>The proration fraction (e.g. 0.6685) when this settled month was prorated; null otherwise.</summary>
    public decimal? ProRatedFraction { get; set; }
}

// ── Screen: Collections summary (date-filtered) ────────────────────────────

/// <summary>
/// Period summary for the collections/payment screens when a date filter is applied. Composed
/// entirely from existing repo aggregates (reuse-first). IMPORTANT — mixed time semantics, by design:
/// the money/activity and departure/collector metrics honour the exact <c>[from,to]</c> range
/// (keyed on transaction/departure date), while the student status counts are anchored to
/// <c>asOfMonth</c> because payment status ("paid / partial / prorated / unpaid") is defined
/// per calendar month, not over an arbitrary range. The frontend labels each group accordingly.
/// </summary>
public class CollectionsSummaryResponse
{
    // Echo of the applied filter.
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    /// <summary>The "YYYY-MM" the status counts are anchored to (defaults to the month of <c>To</c>).</summary>
    public string AsOfMonth { get; set; } = string.Empty;
    public string AsOfMonthLabel { get; set; } = string.Empty;

    // ── Money / activity — TRUE [from,to] range ──
    /// <summary>Cash physically collected in the range, net of departure refunds.</summary>
    public decimal NetCashCollected { get; set; }
    /// <summary>Departure refunds (money returned) confirmed in the range.</summary>
    public decimal RefundsTotal { get; set; }
    /// <summary>Number of collection transactions in the range.</summary>
    public int TransactionCount { get; set; }
    /// <summary>Distinct students who made at least one payment in the range.</summary>
    public int StudentsPaidCount { get; set; }

    // ── Student payment status — anchored to AsOfMonth ──
    /// <summary>Assigned students with no outstanding period through the month (fully settled).</summary>
    public int PaidInFullCount { get; set; }
    /// <summary>Students with a period this month partially settled (0 &lt; paid &lt; due).</summary>
    public int PartialCount { get; set; }
    public int ProratedCount { get; set; }
    public int UnpaidCount { get; set; }

    // ── Departures — TRUE [from,to] range ──
    public int DepartedCount { get; set; }
    public int DepartedRefundDueCount { get; set; }
    public int DepartedAmountOwedCount { get; set; }

    // ── Per-collector breakdown — TRUE [from,to] range ──
    public List<CollectionsSummaryCollectorDto> ByCollector { get; set; } = new();
}

/// <summary>One collector's collected total in the summary range (you or an assistant).</summary>
public class CollectionsSummaryCollectorDto
{
    /// <summary>The collector's user id (matches <c>collectedByUserId</c> on the collections list).</summary>
    public string UserId { get; set; } = string.Empty;
    public string? Name { get; set; }
    /// <summary>Teacher | Assistant.</summary>
    public string Role { get; set; } = "Collector";
    public decimal CollectedAmount { get; set; }
    public int TransactionCount { get; set; }
}

// ── Screen: AssistantWallet ────────────────────────────────────────────────

/// <summary>An assistant's wallet card + paginated recent collections.</summary>
public class AssistantWalletScreenResponse
{
    public AssistantWalletAssistantDto Assistant { get; set; } = new();
    public AssistantWalletInfoDto Wallet { get; set; } = new();
    public AssistantWalletCollectionsDto Collections { get; set; } = new();
}

public class AssistantWalletAssistantDto
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string Role { get; set; } = "Assistant";
    /// <summary>No backing column exists yet — always null (documented gap).</summary>
    public string? AvatarUrl { get; set; }
    public int TransactionCount { get; set; }
}

public class AssistantWalletInfoDto
{
    /// <summary>Gross collections since the last handover (reset or withdraw).</summary>
    public decimal TotalCashCollected { get; set; }
    /// <summary>Gross refunds taken back from this collector since the last handover.</summary>
    public decimal TotalRefunded { get; set; }
    public decimal WalletBalance { get; set; }
    /// <summary>Lifetime collected -- never reduced by a reset/withdraw. REQ-PAY-035.</summary>
    public decimal TotalCollectedAllTime { get; set; }
    /// <summary>Lifetime transaction count -- NOT scoped to the Collections window below.</summary>
    public int CollectionsCount { get; set; }
    public DateTime? LastActivityAt { get; set; }
}

public class AssistantWalletCollectionsDto
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int Limit { get; set; }
    /// <summary>
    /// Start of the window below -- the last handover, or null if there has never been one
    /// (in which case this is the assistant's full lifetime history). NOTE: when the balance
    /// was just fully handed over, this falls back to the PREVIOUS handover so the listed
    /// window (the just-closed period) is not blank -- legacy display contract, keep as-is.
    /// </summary>
    public DateTime? SinceAt { get; set; }

    /// <summary>
    /// STRICT anchor of the current holdings: the instant of the last full hand-over
    /// (running-balance zero crossing), with NO just-closed-period fallback. Everything
    /// strictly after this sums exactly to <c>walletBalance</c> -- so right after a full
    /// withdrawal the window is honestly empty. Null = the balance never crossed zero
    /// (nothing ever handed over). The merged wallet/ledger screen anchors its
    /// "in hand now" scope on this, never on <see cref="SinceAt"/>.
    /// </summary>
    public DateTime? HeldSinceAt { get; set; }
    public List<AssistantWalletCollectionItemDto> Items { get; set; } = new();
}

public class AssistantWalletCollectionItemDto
{
    public string Id { get; set; } = string.Empty;
    public string? StudentId { get; set; }
    public string? StudentName { get; set; }
    public string? StudentCode { get; set; }
    public string? SessionName { get; set; }
    public decimal Amount { get; set; }
    public DateTime CollectedAt { get; set; }

    /// <summary>
    /// The collector's free-text note captured for a custom/partial collect (from
    /// <c>PaymentTransaction.CollectionNote</c>). Null for whole-month collects and for
    /// refund/withdrawal lines (which carry no transaction).
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Ledger line type so the client can label/render it: "collection" (positive, a student
    /// payment), "refund" (negative, cash handed back — e.g. a departure), or "withdrawal"
    /// (negative, a cash hand-over the tutor took from this wallet). The three together sum to
    /// the held balance since the last full hand-over.
    /// </summary>
    public string Kind { get; set; } = "collection";

    /// <summary>
    /// For a "collection": which month(s) this cash settled, oldest-first (from the allocation ledger),
    /// so the assistant sees the installment paid ("August + July"), not a vague amount. Empty for
    /// refund/withdrawal lines and legacy collections without an allocation ledger.
    /// </summary>
    public List<CollectionMonthSlice> AppliedMonths { get; set; } = new();

    /// <summary>True when this collection's amount was later edited. Pair with
    /// <see cref="OriginalAmount"/> to render "edited from X to Y".</summary>
    public bool IsEdited { get; set; }

    /// <summary>The amount originally collected, before the first amount-edit. Null when never edited.</summary>
    public decimal? OriginalAmount { get; set; }
}

// ── Screen: CollectPayment (student list) ──────────────────────────────────

/// <summary>Searchable, filterable, paginated student list for collecting payments.</summary>
public class CollectStudentsResponse
{
    public CollectStudentsCountsDto Counts { get; set; } = new();
    public int Page { get; set; }
    public int Limit { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }
    public List<CollectStudentDto> Students { get; set; } = new();
}

public class CollectStudentsCountsDto
{
    public int All { get; set; }
    public int Assigned { get; set; }
    public int Unassigned { get; set; }
}

public class CollectStudentDto
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    /// <summary>Student code shown on the card (e.g. "#223"); also matched by search.</summary>
    public string? StudentCode { get; set; }
    public string? AvatarUrl { get; set; }
    /// <summary>The student's per-month rate (a single month).</summary>
    public decimal Amount { get; set; }
    /// <summary>assigned | unassigned</summary>
    public string Assignment { get; set; } = "unassigned";
    /// <summary>paid | unpaid</summary>
    public string Status { get; set; } = "paid";
    public int UnpaidMonths { get; set; }

    /// <summary>
    /// Total arrears through the current month (what a full collection settles). For a 2-months-behind
    /// student <c>Amount = 300</c> but <c>TotalOwed = 600</c> — the collect UI defaults to this with a
    /// month counter (oldest-first), instead of silently showing a single month.
    /// </summary>
    public decimal TotalOwed { get; set; }

    /// <summary>True when the required amount is 0 (free/scholarship) → "Not paying" label.</summary>
    public bool IsExempt { get; set; }

    /// <summary>The student's FULL per-month rate (same value as <see cref="Amount"/>; named explicitly
    /// so the collect UI can show "full amount" beside a prorated first-month figure).</summary>
    public decimal MonthlyAmount { get; set; }

    /// <summary>
    /// Itemized unpaid months (oldest-first) making up <see cref="TotalOwed"/> — the SAME shape the
    /// collect lookup returns, so the multi-select bulk collect can seed the QR-scan review queue
    /// with exact per-month labels (a prorated/partial oldest month billed at its true remaining).
    /// </summary>
    public List<CollectLookupMonthDto> UnpaidMonthsBreakdown { get; set; } = new();

    /// <summary>The session the student belongs to (this session, or the linked session they come from
    /// on a session-scoped collect). Null when unassigned. Lets the collect list label each row's session.</summary>
    public string? SessionName { get; set; }

    // ── Proration transparency (populated only when IsProrated) — mirrors StudentByStatusDto ──

    /// <summary>True when the student's first (anchor) month is prorated — justifies the reduced amount.</summary>
    public bool IsProrated { get; set; }

    /// <summary>The proration fraction of the anchor month (e.g. 0.6685 = ⅔). Null when not prorated.</summary>
    public decimal? ProRatedFraction { get; set; }

    /// <summary>The anchor month's prorated amount owed (e.g. 200.55). Null when not prorated.</summary>
    public decimal? ProratedAmount { get; set; }

    /// <summary>The date the proration is anchored to — the ENROLLMENT (assignment) date since rev 2
    /// (2026-09-03; attendance never prices the joining month). Null when not prorated.</summary>
    public DateTime? JoinedAt { get; set; }

    /// <summary>LEGACY wire flag (rev 1) — always false since rev 2: <see cref="JoinedAt"/> is always
    /// the enrollment date. Kept so older clients keep rendering "joined {date}".</summary>
    public bool JoinedAtIsFirstAttendance { get; set; }

    /// <summary>True when the joining amount was set BY HAND (sticky override) — the row can badge
    /// "set by hand" so an overridden price is visible before opening the editor.</summary>
    public bool IsProrationManual { get; set; }

    /// <summary>ByClasses basis FROZEN at pricing time: total classes in the joining month / classes
    /// remaining from the join day. Lets the row say "7 of 13 classes → 160 of 300" forever —
    /// including after the month is paid. Null for percentage/manual pricing.</summary>
    public int? ProrationClassesTotal { get; set; }

    /// <inheritdoc cref="ProrationClassesTotal"/>
    public int? ProrationClassesBilled { get; set; }
}

// ── Screen: PaymentTracking (students by status) ───────────────────────────

/// <summary>Paginated student list filtered by payment status for a month.</summary>
public class StudentsByStatusResponse
{
    public string Month { get; set; } = string.Empty;
    public string MonthLabel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal TotalCollected { get; set; }
    public decimal MonthAmount { get; set; }
    public decimal AmountPerMonth { get; set; }
    /// <summary>
    /// Full-set expected revenue for this scope: Σ over every in-scope student of their monthly
    /// rate (custom override ?? session default), including students with no month period yet.
    /// The session-detail screen renders this directly so per-student custom amounts are reflected
    /// (instead of the client computing session-default × student-count).
    /// </summary>
    public decimal ExpectedAmount { get; set; }
    public decimal TotalUnpaidAmount { get; set; }
    public int Total { get; set; }
    public int Page { get; set; }
    public int Limit { get; set; }
    public List<StudentByStatusDto> Students { get; set; } = new();
}

public class StudentByStatusDto
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? AvatarUrl { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal AmountPerMonth { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal AmountDue { get; set; }
    public decimal UnpaidAmount { get; set; }
    public int UnpaidMonths { get; set; }
    /// <summary>Student code shown on the card (e.g. "#223").</summary>
    public string? StudentCode { get; set; }

    /// <summary>True when the required amount is 0 (free/scholarship) → "Not paying" label.</summary>
    public bool IsExempt { get; set; }

    /// <summary>
    /// The student's CURRENTLY-assigned session id — always present (only assigned students are
    /// listed). The mobile app's global student-leaving picker reads this to open the departure
    /// sheet without a session screen to fall back on.
    /// </summary>
    public long? SessionId { get; set; }

    /// <summary>Session the student paid on (falls back to the month's period session).</summary>
    public string? SessionName { get; set; }

    /// <summary>Date of the latest paying transaction this month; null when unpaid.</summary>
    public DateTime? PaidOn { get; set; }

    /// <summary>
    /// Display name of who collected the money for this month (the tutor or the assistant), from the
    /// same transaction that drives <see cref="PaidOn"/>. Null when unpaid or settled by forgiveness.
    /// Lets the paid card show "collected by X".
    /// </summary>
    public string? CollectedByName { get; set; }

    /// <summary>Role of the collector: <c>Teacher</c> | <c>Assistant</c>. Null when no collector.</summary>
    public string? CollectedByRole { get; set; }

    // ── Proration transparency (shown only when IsProrated) ──

    /// <summary>True when the student's first (anchor) month is prorated — justifies the reduced amount.</summary>
    public bool IsProrated { get; set; }

    /// <summary>The proration fraction of the anchor month (e.g. 0.6685 = ⅔). Null when not prorated.</summary>
    public decimal? ProRatedFraction { get; set; }

    /// <summary>The anchor month's prorated amount owed (e.g. 200.55). Null when not prorated.</summary>
    public decimal? ProratedAmount { get; set; }

    /// <summary>
    /// The date the proration is anchored to — the ENROLLMENT (assignment) date since rev 2
    /// (2026-09-03; attendance never prices the joining month). Justifies the prorated value on screen
    /// ("joined {date}"). Null when not prorated.
    /// </summary>
    public DateTime? JoinedAt { get; set; }

    /// <summary>LEGACY wire flag (rev 1) — always false since rev 2: <see cref="JoinedAt"/> is always
    /// the enrollment date. Kept so older clients keep rendering "joined {date}".</summary>
    public bool JoinedAtIsFirstAttendance { get; set; }

    /// <summary>True when the joining amount was set BY HAND (sticky override) — the row can badge
    /// "set by hand" so an overridden price is visible before opening the editor.</summary>
    public bool IsProrationManual { get; set; }

    /// <summary>ByClasses basis FROZEN at pricing time: total classes in the joining month / classes
    /// remaining from the join day. Lets the row say "7 of 13 classes → 160 of 300" forever —
    /// including after the month is paid. Null for percentage/manual pricing.</summary>
    public int? ProrationClassesTotal { get; set; }

    /// <inheritdoc cref="ProrationClassesTotal"/>
    public int? ProrationClassesBilled { get; set; }
}

// ── Screen: SessionPaymentCollectedByYear ──────────────────────────────────

/// <summary>Per-student month-by-month collection matrix for a selected year.</summary>
public class YearlyCollectionsResponse
{
    public int Year { get; set; }
    public int Page { get; set; }
    public int Limit { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }
    public List<YearlyStudentDto> Items { get; set; } = new();
}

public class YearlyStudentDto
{
    public string Id { get; set; } = string.Empty;
    public int Index { get; set; }
    public string? StudentName { get; set; }
    public YearlyStudentSummaryDto Summary { get; set; } = new();
    /// <summary>Only months the student has a period for; months with no period are absent.</summary>
    public List<YearlyMonthDto> Months { get; set; } = new();
}

public class YearlyStudentSummaryDto
{
    public int PaidMonths { get; set; }
    public int UnpaidMonths { get; set; }
    public decimal TotalCollected { get; set; }
    public string Label { get; set; } = string.Empty;
}

public class YearlyMonthDto
{
    public int Month { get; set; }
    /// <summary>paid | unpaid | prorated</summary>
    public string Status { get; set; } = "unpaid";
    public decimal Amount { get; set; }
}

// ── Screen: CollectPaymentSession (lookup) ─────────────────────────────────

/// <summary>Resolves a scanned/typed student to a student + amount owed + paid state.</summary>
public class CollectLookupResponse
{
    public CollectLookupStudentDto Student { get; set; } = new();
    public decimal AmountDue { get; set; }
    /// <summary>paid | unpaid</summary>
    public string PaymentStatus { get; set; } = "unpaid";

    // ── Feature C additions (additive — existing fields above are unchanged) ──

    /// <summary>The student's per-month rate: custom override (else session amount, else 0).</summary>
    public decimal MonthlyAmount { get; set; }

    /// <summary>Number of unpaid months making up <see cref="TotalOwed"/> (arrears through the month).</summary>
    public int MonthsOwed { get; set; }

    /// <summary>Total arrears through the current teacher-local month — equal to <see cref="AmountDue"/>.</summary>
    public decimal TotalOwed { get; set; }

    /// <summary>
    /// The unpaid months making up <see cref="TotalOwed"/>, OLDEST-FIRST (the order money is applied).
    /// The collect UI defaults to the full owed amount and uses this to (a) power a 1..N month counter
    /// and (b) label exactly which month(s) a payment covers — the teacher/assistant sees "August +
    /// July", not a vague total. Because settlement is oldest-first, selecting N months always pays the
    /// first N entries (the student must clear older months before newer ones).
    /// </summary>
    public List<CollectLookupMonthDto> UnpaidMonthsBreakdown { get; set; } = new();

    /// <summary>
    /// One-month-in-advance OFFER (nullable): the next month the student MAY pay ahead — populated
    /// only when they are fully paid through the current month and a next-month period (current + 1)
    /// exists. It does NOT affect <see cref="AmountDue"/>/<see cref="PaymentStatus"/> (the student
    /// stays "paid" everywhere); the deliberate collect pop-up is the only surface that offers it.
    /// Older clients ignore this field and keep showing the student as already paid.
    /// </summary>
    public CollectLookupMonthDto? AdvanceMonth { get; set; }

    // ── Joining-month proration TOP-LEVEL mirror (2026-09-02) ──
    // Convenience copy of the anchor month's proration enrichment (also present on the matching
    // UnpaidMonthsBreakdown[] item) so the mobile collect popup can read it at the response root.
    // Populated ONLY when the student has a still-unpaid joining/anchor month; all null/false otherwise.

    /// <summary>True when the student's joining/anchor month is still owed and proration is enabled.</summary>
    public bool IsJoiningMonthProrated { get; set; }
    /// <summary>System-suggested joining amount, already rounded to the nearest 5 (the field pre-fill).</summary>
    public decimal? SuggestedProratedAmount { get; set; }
    /// <summary>ByClasses: classes attended so far this month (the "attended 3").</summary>
    public int? ClassesAttendedThisMonth { get; set; }
    /// <summary>ByClasses: total scheduled classes in the joining month (the "of 6").</summary>
    public int? ClassesTotalThisMonth { get; set; }
    /// <summary>ByClasses: scheduled classes billed from the JOIN date through month-end (the "4" of "4 of 6").</summary>
    public int? ClassesBilledThisMonth { get; set; }
    /// <summary>LEGACY (rev 1 attendance anchor) — no longer populated; kept null for wire-compat.</summary>
    public DateTime? FirstClassDate { get; set; }
    /// <summary>The student's enrollment (assignment) date — the basis of the suggestion (rev 2).</summary>
    public DateTime? JoinDate { get; set; }
    /// <summary>Plain reason string, e.g. "7 of 13 classes billed from joining 14 Sep".</summary>
    public string? ProratedReason { get; set; }
    /// <summary>True when a person set a sticky manual joining amount (auto-suggest will not overwrite it).</summary>
    public bool IsProrationManual { get; set; }
    /// <summary>When manually set: who set the joining amount (display name, from the audit log).</summary>
    public string? ProrationSetByName { get; set; }
    /// <summary>When manually set: when the joining amount was set (UTC).</summary>
    public DateTime? ProrationSetAt { get; set; }

    // ── Same-day cross-collector soft-confirm (Issue 1, 2026-09-02) ──
    // When someone already collected from THIS student today, the mobile collect popup shows a soft
    // confirm ("{name} collected {amount} for {month} today — collect anyway?") BEFORE recording; the
    // collect still proceeds. Populated only when a same-day payment exists; all null/false otherwise.

    /// <summary>True when a payment was already recorded for this student today (any collector).</summary>
    public bool HasSameDayPayment { get; set; }
    /// <summary>Display name of who collected today's payment (most recent), for the soft-confirm copy.</summary>
    public string? TodayPaidByName { get; set; }
    /// <summary>Total amount already collected from this student today.</summary>
    public decimal? TodayPaidAmount { get; set; }
    /// <summary>Human month label the most-recent today's payment settled, e.g. "September 2026".</summary>
    public string? TodayPaidMonthLabel { get; set; }
}

/// <summary>One unpaid month in a collect lookup breakdown.</summary>
public class CollectLookupMonthDto
{
    /// <summary>The payment-period id (string per the api/v1 contract).</summary>
    public string PeriodId { get; set; } = string.Empty;
    /// <summary>The month this installment bills, as "YYYY-MM".</summary>
    public string Month { get; set; } = string.Empty;
    /// <summary>Human label, e.g. "August 2026".</summary>
    public string MonthLabel { get; set; } = string.Empty;
    /// <summary>Remaining amount owed for this month (what paying this one month settles).</summary>
    public decimal Amount { get; set; }
    /// <summary>True when this is the prorated anchor month — the collect UI can show "prorated ⅔".</summary>
    public bool IsProrated { get; set; }
    /// <summary>The proration fraction (e.g. 0.6685) when prorated; null otherwise.</summary>
    public decimal? ProRatedFraction { get; set; }

    // ── Joining-month proration enrichment (REQ-PAY-021/022, 2026-09-02) ──
    // Populated ONLY on the joining/anchor month entry (a NEW enrollment's still-owed first month) so the
    // collect popup can render the "prorated joining month" box in ONE call (no N+1). All null otherwise.

    /// <summary>True when THIS month is the student's joining/anchor month and proration is enabled.</summary>
    public bool IsJoiningMonthProrated { get; set; }

    /// <summary>System-suggested joining amount, already rounded to the nearest 5 (the pre-fill for the field).</summary>
    public decimal? SuggestedProratedAmount { get; set; }

    /// <summary>ByClasses: classes the student has attended so far this month (informational "attended 3").</summary>
    public int? ClassesAttendedThisMonth { get; set; }

    /// <summary>ByClasses: total scheduled classes in the joining month (the "of 6").</summary>
    public int? ClassesTotalThisMonth { get; set; }

    /// <summary>ByClasses: scheduled classes billed (from the JOIN date through month-end) — the "4" in "4 of 6".</summary>
    public int? ClassesBilledThisMonth { get; set; }

    /// <summary>LEGACY (rev 1 attendance anchor) — no longer populated; kept null for wire-compat.</summary>
    public DateTime? FirstClassDate { get; set; }

    /// <summary>The student's enrollment (assignment) date — the basis of the suggestion (rev 2).</summary>
    public DateTime? JoinDate { get; set; }

    /// <summary>Plain reason string, e.g. "7 of 13 classes billed from joining 14 Sep". Null when not the joining month.</summary>
    public string? ProratedReason { get; set; }

    /// <summary>True when a person already set a sticky manual joining amount (auto-suggest will not overwrite it).</summary>
    public bool IsProrationManual { get; set; }

    /// <summary>When manually set: who set the joining amount (display name, from the audit log).</summary>
    public string? ProrationSetByName { get; set; }

    /// <summary>When manually set: when the joining amount was set (UTC).</summary>
    public DateTime? ProrationSetAt { get; set; }
}

public class CollectLookupStudentDto
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Code { get; set; }
    public string? Group { get; set; }
    public string? AvatarUrl { get; set; }
}

// ── Screen: per-student joining-month proration override (REQ-PAY-021/022, 2026-09-02) ─────────────

/// <summary>
/// Body for <c>PUT /api/v1/payments/students/{teacherStudentId}/proration</c>. Sets (or clears) the
/// student's joining-month amount. <c>amount</c> is snapped to the nearest 5 and must be in
/// [0, full month]; <c>null</c> CLEARS the manual override and reverts to the method's auto suggestion.
/// </summary>
public class SetProrationRequest
{
    /// <summary>The exact joining-month amount to set (snapped to nearest 5, 0..full month). Null = clear override.</summary>
    public decimal? Amount { get; set; }
}

/// <summary>Result of setting/clearing a student's joining-month proration — the popup refreshes in place.</summary>
public class ProrationUpdateResultDto
{
    public string TeacherStudentId { get; set; } = string.Empty;
    /// <summary>The anchor payment-period id (string per the api/v1 contract).</summary>
    public string PeriodId { get; set; } = string.Empty;
    /// <summary>The joining month, as "YYYY-MM".</summary>
    public string Month { get; set; } = string.Empty;
    /// <summary>Human label, e.g. "September 2026".</summary>
    public string MonthLabel { get; set; } = string.Empty;
    /// <summary>The new joining-month amount now due.</summary>
    public decimal AmountDue { get; set; }
    /// <summary>The full month base (custom amount ?? session amount).</summary>
    public decimal FullBase { get; set; }
    /// <summary>Whether the joining month is prorated (AmountDue &lt; FullBase).</summary>
    public bool IsProrated { get; set; }
    /// <summary>AmountDue ÷ FullBase (display only).</summary>
    public decimal ProRatedFraction { get; set; }
    /// <summary>True when a human set the amount (sticky — auto-suggest will not overwrite it).</summary>
    public bool IsProrationManual { get; set; }
    /// <summary>The current system suggestion (rounded to 5) for reference beside the set amount.</summary>
    public decimal SuggestedProratedAmount { get; set; }
    /// <summary>Plain reason string for the suggestion, e.g. "4 of 6 classes from first class 17 Sep".</summary>
    public string? ProratedReason { get; set; }
}

// ── Screen: PaymentTracking (aggregate) ────────────────────────────────────

/// <summary>Everything the PaymentTracking screen renders on load for a selected month.</summary>
public class TrackingResponse
{
    public string Month { get; set; } = string.Empty;
    public string MonthLabel { get; set; } = string.Empty;
    public TrackingSummaryDto Summary { get; set; } = new();
    public TrackingStatusBreakdownDto StatusBreakdown { get; set; } = new();
    public TrackingByAssistantDto CollectedByAssistant { get; set; } = new();
    public TrackingBySessionsDto CollectedBySessions { get; set; } = new();
}

public class TrackingSummaryDto
{
    public int TotalStudents { get; set; }
    public int TotalSessions { get; set; }
    public int SessionsCollected { get; set; }
    public int SessionsTotal { get; set; }

    /// <summary>Student-based collection progress (0–100): assigned students who are caught up
    /// (<c>statusBreakdown.paid</c>) ÷ all currently-assigned students (<see cref="TotalStudents"/>).
    /// Drives both the progress bar fill AND the "X of Y students paid" label, so the two now match.</summary>
    public decimal ProgressPercent { get; set; }

    // ── TWO SEPARATE LENSES (redesigned 2026-08-19, teacher-requested) ──
    // (1) OBLIGATION — Expected / Remaining: what is owed vs still owed, scoped to currently-assigned
    //     students, net of forgiven. "This month" = the current bill; "Total" = current bill + arrears
    //     (fully-collected closed months drop off). Each column reconciles as Expected = Collected(period)
    //     + Remaining, and Expected moves down when money is forgiven.
    // (2) CASH — Collected*: the real money physically collected THIS calendar month (by transaction date,
    //     net of refunds), split by the installment month it settled. This lens is INTENTIONALLY
    //     independent of the obligation lens — a July arrear paid in August shows in August's "earlier"
    //     slice and does NOT change July's card. So Collected does NOT tie to Expected − Remaining.

    /// <summary>OBLIGATION · THIS MONTH — Expected: the current month's bill across assigned students, net
    /// of forgiven (Σ(AmountDue − ForgivenAmount) on this month's periods). Equals
    /// <see cref="CollectedThisMonth"/>'s period-settled counterpart + <see cref="RemainingAmount"/>.</summary>
    public decimal ExpectedRevenue { get; set; }

    /// <summary>CASH · money physically collected this calendar month that settled THIS month's installments
    /// (the "collected this month" breakdown line). Because it is date-based, a payment taken this month for
    /// a past month is NOT here (it is in <see cref="CollectedPreviousMonths"/>); and this month's bill paid
    /// in an earlier month is NOT here either.</summary>
    public decimal CollectedThisMonth { get; set; }

    /// <summary>OBLIGATION · THIS MONTH — Remaining: still owed for the current month only (forgiven-aware:
    /// Σ(AmountDue − AmountPaid − ForgivenAmount) over not-Paid periods overlapping the month).</summary>
    public decimal RemainingAmount { get; set; }

    /// <summary>OBLIGATION · TOTAL — Expected: the current month's net bill PLUS every earlier month's still
    /// unpaid balance (this month + arrears only — already-collected closed months are excluded). Equals
    /// <see cref="RemainingTotal"/> + the current month's period-settled amount.</summary>
    public decimal ExpectedTotal { get; set; }

    /// <summary>CASH · TOTAL — the real money physically collected THIS calendar month (by transaction date,
    /// net of refunds); ties to <c>CollectedByAssistant.TotalCollected</c> to the cent. Equals
    /// <see cref="CollectedThisMonth"/> + <see cref="CollectedPreviousMonths"/> + <see cref="CollectedInAdvance"/>.</summary>
    public decimal CollectedTotal { get; set; }

    /// <summary>CASH · money physically collected this calendar month that settled EARLIER months'
    /// installments (arrears collected now) — the "collected for earlier months" breakdown line.</summary>
    public decimal CollectedPreviousMonths { get; set; }

    /// <summary>OBLIGATION · TOTAL — Remaining: still owed across the current month + every earlier month
    /// (forgiven-aware). Equals <see cref="ExpectedTotal"/> − the current month's period-settled amount.</summary>
    public decimal RemainingTotal { get; set; }

    /// <summary>CASH · money physically collected this calendar month that settled FUTURE months'
    /// installments (advance, e.g. a September bill paid in August) — the "collected ahead" breakdown line.
    /// Part of <see cref="CollectedTotal"/> (it is real cash collected this month). Often 0.</summary>
    public decimal CollectedInAdvance { get; set; }
}

public class TrackingStatusBreakdownDto
{
    public int Paid { get; set; }
    public int Prorated { get; set; }
    public int Unpaid { get; set; }
}

public class TrackingByAssistantDto
{
    public decimal TotalCollected { get; set; }
    public List<TrackingAssistantDto> Assistants { get; set; } = new();
}

public class TrackingAssistantDto
{
    /// <summary>The collector's user id (kept for back-compat / identity).</summary>
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = "Collector";
    public int TransactionCount { get; set; }
    public decimal CollectedAmount { get; set; }

    /// <summary>The assistant id to open the wallet drill-in (route matches on AssistantId,
    /// NOT the user id). Null for the teacher row, which has no assistant wallet.</summary>
    public string? AssistantId { get; set; }

    /// <summary>Cash this assistant currently holds for the teacher (their wallet balance).
    /// 0 for the teacher row.</summary>
    public decimal WalletBalance { get; set; }

    /// <summary>True when this collector's account has been REMOVED (soft-deleted) from the
    /// teacher. The row survives only in months up to and including the removal month (see
    /// <c>GetTrackingAsync</c>), so clients mark it as history instead of showing a card for
    /// someone who is no longer on the account. Always false for the teacher row.</summary>
    public bool IsRemoved { get; set; }
}

public class TrackingBySessionsDto
{
    public List<TrackingSessionDto> Sessions { get; set; } = new();
}

public class TrackingSessionDto
{
    public string Id { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Day { get; set; }
    public string? Time { get; set; }
    public string? Grade { get; set; }
    public decimal CollectedAmount { get; set; }
    public int StudentsCollected { get; set; }
    public int StudentsTotal { get; set; }
    public decimal ProgressPercent { get; set; }
    /// <summary>Group the session belongs to (null = ungrouped). Lets the app render
    /// groups + ungrouped sessions with per-group roll-ups and a group drill-in.</summary>
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
}

// ── Money: bulk mark-paid (CollectPayment screen) ──────────────────────────

public class MarkPaidRequest
{
    public List<long> StudentIds { get; set; } = new();

    /// <summary>
    /// Optional month scope (<c>YYYY-MM</c>) — the month the initiating screen was opened on.
    /// When supplied, each student is charged their arrears THROUGH THIS month only (what the
    /// month-scoped card displayed), not through the current month. Omitted → current-month
    /// behavior, unchanged for older clients.
    /// </summary>
    public string? Month { get; set; }

    /// <summary>
    /// Issue 1 (2026-09-02): the client re-submits with this = true to CONFIRM a same-day collection
    /// warning and proceed. Omitted/false → a student already collected-from today is returned as
    /// <c>needs_confirmation</c> (with attribution) instead of being hard-failed. The money engine still
    /// cannot double-charge the same month, so confirming only takes a real unpaid/advance month or no-ops.
    /// </summary>
    public bool DuplicateConfirmed { get; set; } = false;
}

public class MarkPaidResponse
{
    public int MarkedPaidCount { get; set; }
    public List<MarkPaidResultDto> Results { get; set; } = new();
}

public class MarkPaidResultDto
{
    public string StudentId { get; set; } = string.Empty;
    /// <summary>paid | failed | needs_confirmation</summary>
    public string Status { get; set; } = "failed";
    public string? Reason { get; set; }

    // ── Same-day soft-confirm (Issue 1, 2026-09-02) ──
    // Set when Status == "needs_confirmation": a payment already exists for this student today. The client
    // shows the attribution + a "Collect anyway" action, then re-submits with duplicateConfirmed = true.

    /// <summary>True when this student was NOT collected because a same-day payment exists — confirmable.</summary>
    public bool NeedsConfirmation { get; set; }

    /// <summary>Display name of the user who recorded the same-day payment (attribution). Null when unresolved.</summary>
    public string? TodayPaidByName { get; set; }

    /// <summary>Total already collected from this student today.</summary>
    public decimal? TodayPaidAmount { get; set; }

    /// <summary>The installment month/period label the same-day payment settled (e.g. "September 2026").</summary>
    public string? TodayPaidMonthLabel { get; set; }
}

// ── Money: submit batch collection (CollectPaymentSession screen) ──────────

public class SubmitCollectionRequest
{
    /// <summary>Informational (YYYY-MM); collection still applies to the earliest unpaid period.</summary>
    public string? Month { get; set; }
    public long? ClassSessionId { get; set; }
    public List<SubmitCollectionItem> Students { get; set; } = new();

    /// <summary>
    /// Feature C: optional batch-level note applied to every collection in this submit that has no
    /// per-item <see cref="SubmitCollectionItem.Note"/>. A note (item-level or this) is REQUIRED
    /// (else 400 <c>CollectNoteRequired</c>) for any item whose amount is a partial/custom value —
    /// not a whole-month multiple of that student's monthly rate. Stored on each transaction and
    /// shown in history.
    /// </summary>
    public string? Note { get; set; }
}

public class SubmitCollectionItem
{
    public long StudentId { get; set; }
    public decimal Amount { get; set; }

    /// <summary>
    /// Feature C: per-student note for THIS collection (the mobile app sends the note here, per
    /// item — not at the batch level). Overrides the batch-level
    /// <see cref="SubmitCollectionRequest.Note"/> for this item and satisfies the
    /// partial/custom-amount note requirement on its own.
    /// </summary>
    public string? Note { get; set; }
}

public class SubmitCollectionResponse
{
    public int SubmittedCount { get; set; }
    public decimal TotalCollected { get; set; }
    public List<SubmitCollectionResultDto> Results { get; set; } = new();
}

public class SubmitCollectionResultDto
{
    public string StudentId { get; set; } = string.Empty;
    /// <summary>committed | failed</summary>
    public string Status { get; set; } = "failed";
    public string? Reason { get; set; }
}

// ── Money: forgive balance (TEACHER-ONLY; assistants 403) ──────────────────

/// <summary>
/// Request to FORGIVE (waive) part of a student's outstanding balance. Forgiving is NOT cash: no
/// wallet change, no transaction, no collector attribution — it only reduces what the student owes,
/// applied oldest-unpaid-month first (cascade). <c>amount</c> must be &gt; 0 and ≤ the student's
/// outstanding through the current teacher-local month.
/// </summary>
public class ForgiveBalanceRequest
{
    public long TeacherStudentId { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Optional free-text reason recorded on the audit row.</summary>
    public string? Note { get; set; }
}

/// <summary>Optional body for reversing a forgiveness (restores the waived balance).</summary>
public class ReverseForgivenessRequest
{
    /// <summary>Optional note recorded on the reversal audit.</summary>
    public string? Note { get; set; }
}

/// <summary>Audit view of a single forgiveness (ids as strings, per the api/v1 contract).</summary>
public class ForgivenessDto
{
    public string Id { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    /// <summary>Display name of the tutor who forgave.</summary>
    public string? ByName { get; set; }
    /// <summary>User id of the tutor who forgave.</summary>
    public string? ByUserId { get; set; }
    public DateTime Date { get; set; }
    /// <summary>active | reversed</summary>
    public string Status { get; set; } = "active";
}

/// <summary>The student's payment summary AFTER a forgive/reverse — the row the frontend re-renders.</summary>
public class ForgiveStudentSummaryDto
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? StudentCode { get; set; }
    public string? SessionName { get; set; }
    /// <summary>Arrears through the current teacher-local month AFTER the forgive/reverse.</summary>
    public decimal Outstanding { get; set; }
    /// <summary>Number of unpaid months through the current month AFTER the forgive/reverse.</summary>
    public int MonthsOwed { get; set; }
    /// <summary>paid | unpaid — whether any outstanding remains through the current month.</summary>
    public string Status { get; set; } = "paid";
}

/// <summary>Response of forgive / reverse — the created (or reversed) forgiveness + updated summary row.</summary>
public class ForgiveBalanceResponse
{
    public ForgivenessDto Forgiveness { get; set; } = new();
    public ForgiveStudentSummaryDto Student { get; set; } = new();
}

// ── Money: assistant wallet withdraw (AssistantWallet screen) ──────────────

public class WalletWithdrawRequest
{
    /// <summary>Defaults to the full current balance when omitted.</summary>
    public decimal? Amount { get; set; }
}

/// <summary>Frontend response for a wallet withdrawal (ids as strings).</summary>
public class WalletWithdrawResponse
{
    public string WithdrawalId { get; set; } = string.Empty;
    /// <summary>Always "completed" — withdrawal is an instant cash handover (no pending state).</summary>
    public string Status { get; set; } = "completed";
    public decimal Amount { get; set; }
    public decimal WalletBalanceAfter { get; set; }
    public DateTime RequestedAt { get; set; }
}

/// <summary>Service-level result of a wallet withdrawal (from IPaymentService).</summary>
public class WalletWithdrawalResult
{
    public long WithdrawalId { get; set; }
    public decimal Amount { get; set; }
    public decimal WalletBalanceAfter { get; set; }
    public DateTime RequestedAt { get; set; }
}
