namespace Edvanz.Domain.Interfaces;

// ════════════════════════════════════════════════════════════════════════════
// PAYMENT MODULE (MODULE 4) — REPOSITORY PROJECTION TYPES
// ════════════════════════════════════════════════════════════════════════════
//
// Query projections returned by IPaymentRepo. Same convention as
// VideoRepoProjections.cs: projections live in the Domain layer alongside the
// repo interface so the Application service maps them to client-facing DTOs
// without the repo needing to know about the Application layer's DTO types.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Projection row for the "Collected by Sessions" summary — one row per
/// currently active session (<c>EndDate &gt;= today</c>). Combines the
/// session's schedule fields with payment aggregates so the service layer can
/// build the display label and progress percentage without a second query.
/// </summary>
public sealed class ActiveSessionCollectionSummaryRow
{
    public long SessionId { get; set; }
    public string SessionName { get; set; } = null!;
    public Enums.OccurrenceType OccurrenceType { get; set; }
    public string? SelectedDays { get; set; }
    public byte? MonthlyDayOfMonth { get; set; }
    public TimeSpan StartTime { get; set; }
    public int TotalStudents { get; set; }
    public int PaidStudents { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal CollectedAmount { get; set; }
}

/// <summary>
/// Projection row for the CollectPayment student list (api/v1 screens). One row per
/// student with the payment-status fields the screen needs, LEFT-joined to the
/// student's <c>StudentPaymentCounter</c> (a student with no counter → paid, 0 unpaid).
/// </summary>
public sealed class CollectStudentRow
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public bool IsAssigned { get; set; }
    /// <summary>The session the student is currently assigned to (null when unassigned). Surfaced so a
    /// session-scoped collect (this session + its linked sessions) can label which session each row belongs to.</summary>
    public string? SessionName { get; set; }
    /// <summary>The student's per-month rate (custom override else session amount).</summary>
    public decimal Amount { get; set; }
    public bool IsUnpaid { get; set; }
    public int UnpaidMonths { get; set; }

    /// <summary>
    /// Total arrears through the current month (Σ remaining over unpaid periods) — what a full "mark
    /// paid" would collect. Distinct from <see cref="Amount"/> (a single month): a 2-months-behind
    /// student has <c>Amount = 300</c> but <c>TotalOwed = 600</c>, so the collect UI can default to the
    /// full owed with a month counter instead of silently under-showing one month.
    /// </summary>
    public decimal TotalOwed { get; set; }

    /// <summary>
    /// Itemized unpaid months (oldest-first = the cascade settlement order) making up
    /// <see cref="TotalOwed"/> — same shape the collect lookup returns, so the multi-select bulk
    /// collect can seed the SAME review queue as QR scanning (per-month counter labels, a
    /// prorated/partial oldest month billed at its TRUE remaining).
    /// </summary>
    public List<CollectLookupUnpaidMonth> UnpaidMonthsList { get; set; } = new();
}

/// <summary>
/// Projection row for the one-time "reconcile moved students" cleanup: a student who is CURRENTLY
/// assigned to a session yet still carries an UNPAID/PARTIAL PaymentPeriod under a DIFFERENT session
/// — i.e. arrears stranded in an old session by the pre-carry-over reassign flow. One row per such
/// student. Soft-deleted students are excluded by the TeacherStudent global query filter.
/// </summary>
public sealed class StrandedStudentRow
{
    public long TeacherId { get; set; }
    public long TeacherStudentId { get; set; }
    public long CurrentSessionId { get; set; }
    public string? StudentName { get; set; }
    public string? StudentCode { get; set; }
}

/// <summary>
/// Projection row for the PaymentTracking "students by status" list (api/v1 screens).
/// One row per student in the requested status group, with that month's paid/due amounts
/// and the student's outstanding balance/unpaid-month count from their counter.
/// </summary>
public sealed class StudentByStatusRow
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public decimal AmountPerMonth { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal AmountDue { get; set; }
    public decimal UnpaidAmount { get; set; }
    public int UnpaidMonths { get; set; }

    /// <summary>Live student code from the TeacherStudent record.</summary>
    public string? StudentCode { get; set; }

    /// <summary>
    /// The student's CURRENTLY-assigned session id (<c>TeacherStudent.SessionId</c>).
    /// Non-null for every row by construction — the list only classifies assigned
    /// students. Drives client flows that need the session context of a picked
    /// student (e.g. the assistant's global student-leaving picker, which has no
    /// session screen to fall back on).
    /// </summary>
    public long? SessionId { get; set; }

    /// <summary>
    /// The session the student actually paid on this month (from the latest paying
    /// transaction). Falls back to the month's period session name when no payment
    /// exists, so unpaid/prorated rows still show a session.
    /// </summary>
    public string? SessionName { get; set; }

    /// <summary>
    /// Collection date of the student's latest paying transaction for the month
    /// (<c>PaymentTransaction.CollectedAt</c>). Null when nothing was paid.
    /// Derived from the allocation ledger (any transaction that settled an in-month period),
    /// with a legacy fallback to the single denormalized transaction→period FK — so a month
    /// cleared as a NON-oldest slice of a multi-month cascade still surfaces its paid-on date
    /// instead of null (fixes the "paid student, blank paid-on" bug).
    /// </summary>
    public DateTime? PaidOn { get; set; }

    /// <summary>
    /// User id of the collector of the student's latest in-month settling transaction (the same
    /// transaction that drives <see cref="PaidOn"/>). Null when nothing was collected this month
    /// (or the month was settled purely by forgiveness). Resolved to a display name + role
    /// (you / assistant name) by the service so the paid card can show "collected by X".
    /// </summary>
    public long? CollectedByUserId { get; set; }

    /// <summary>
    /// The student's own computed status (<c>paid | prorated | unpaid</c>) by the
    /// earliest-outstanding-period rule. Populated ONLY when the caller omits a status filter
    /// (the per-session mixed-status roster); when a status filter is supplied every row already
    /// matches it, so the service uses the requested status instead.
    /// </summary>
    public string? Status { get; set; }
}

/// <summary>
/// Projection for the SessionPaymentCollectedByYear matrix (api/v1 screens). One row per
/// student with their per-month cells for the selected year (months with no period are
/// simply absent from <see cref="Months"/>).
/// </summary>
public sealed class YearlyStudentRow
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public List<YearlyMonthCell> Months { get; set; } = new();
}

/// <summary>One (student, month) cell of the yearly matrix, aggregated across the student's
/// periods in that calendar month.</summary>
public sealed class YearlyMonthCell
{
    public int Month { get; set; }        // 1-12
    public decimal AmountDue { get; set; }
    public decimal AmountPaid { get; set; }
    public bool IsPaid { get; set; }      // every period that month is fully Paid
    public bool IsProRated { get; set; }  // any period that month is prorated
}

/// <summary>
/// Projection for the CollectPaymentSession QR/code/name lookup (api/v1 screens): the resolved
/// student plus the amount they should pay and their current paid/unpaid state.
/// </summary>
public sealed class CollectLookupRow
{
    public long TeacherStudentId { get; set; }
    public string StudentName { get; set; } = null!;
    public string StudentCode { get; set; } = null!;
    public string? Group { get; set; }
    public decimal AmountDue { get; set; }
    public bool IsUnpaid { get; set; }

    /// <summary>The student's per-month rate: custom override else session amount else 0.</summary>
    public decimal MonthlyAmount { get; set; }

    /// <summary>Count of unpaid months making up <see cref="AmountDue"/> (arrears through the month).</summary>
    public int MonthsOwed { get; set; }

    /// <summary>
    /// The unpaid months making up <see cref="AmountDue"/>, OLDEST-FIRST (the order the collect cascade
    /// settles them). Lets the collect UI show a per-month counter and label exactly which month(s) a
    /// payment covers — accurate even when a month is prorated or partially paid.
    /// </summary>
    public List<CollectLookupUnpaidMonth> UnpaidMonths { get; set; } = new();

    /// <summary>
    /// One-month-in-advance OFFER: the next unpaid month (current + 1, the cap CollectPaymentAsync
    /// accepts), populated ONLY when the student has no arrears through the current month (fully paid).
    /// It is kept SEPARATE from <see cref="AmountDue"/>/<see cref="IsUnpaid"/>/<see cref="UnpaidMonths"/>
    /// on purpose: the student must keep reading as PAID everywhere (roster, attendance scan, status) —
    /// the advance is only an option the deliberate collect pop-up may surface. Null when arrears exist,
    /// no next-month period exists, or the lookup is not scoped to the current month.
    /// </summary>
    public CollectLookupUnpaidMonth? AdvanceMonth { get; set; }
}

/// <summary>One unpaid month in a collect lookup: the period, the month it falls in, and the
/// remaining amount owed for it (AmountDue − AmountPaid − ForgivenAmount).</summary>
public sealed class CollectLookupUnpaidMonth
{
    public long PeriodId { get; set; }
    /// <summary>First day of the month this period bills (drives the month label + ordering).</summary>
    public DateTime PeriodStart { get; set; }
    /// <summary>Remaining amount owed for this month (what a single-month collection would settle).</summary>
    public decimal Remaining { get; set; }
    /// <summary>True when this month is the prorated anchor month — justifies a reduced amount.</summary>
    public bool IsProRated { get; set; }
    /// <summary>The proration fraction (e.g. 0.6685) when prorated; 1.0 otherwise.</summary>
    public decimal ProRatedFraction { get; set; }
}

/// <summary>
/// Projection for a refund taken back from a collector, derived from the payment edit-log trail
/// (a delete/reversal or an amount reduction) and attributed to the original collector. Surfaced
/// in the assistant/collector month log alongside collections (as a negative-amount entry).
/// </summary>
public sealed class CollectorRefundRow
{
    public long Id { get; set; }
    public long? StudentId { get; set; }
    public string? StudentName { get; set; }
    public string? StudentCode { get; set; }
    public string? SessionName { get; set; }
    public decimal RefundAmount { get; set; }
    public DateTime RefundedAt { get; set; }

    /// <summary>
    /// CollectedAt (UTC) of the UNDERLYING collection this refund reverses. Lets a reset-aware caller
    /// (the wallet recompute) tell whether the reversed cash was collected before or after the last
    /// hand-over. Zero-default for callers that ignore it.
    /// </summary>
    public DateTime CollectedAt { get; set; }

    /// <summary>User who performed the refund/edit (PaymentEditLog.EditedByUserId) — lets the ledger
    /// label a refund charged to a collector but performed by someone else (e.g. the tutor).</summary>
    public long? PerformedByUserId { get; set; }
}

/// <summary>
/// Projection for a student-departure refund (RefundDue), sourced directly from
/// <c>StudentDeparture</c> — the authoritative refund amount (<c>FinalAmount</c>), the anchored
/// month it applies to, and the collector the cash was taken back from. Surfaced as a
/// negative-amount entry on the collections ledger and subtracted from the collector's total.
/// </summary>
public sealed class DepartureRefundRow
{
    public long Id { get; set; }
    public long? StudentId { get; set; }
    public string? StudentName { get; set; }
    public string? StudentCode { get; set; }
    public string? SessionName { get; set; }
    public decimal RefundAmount { get; set; }
    /// <summary>First day of the anchored month the refund applies to (the "departed month").</summary>
    public DateTime? RefundPeriodStart { get; set; }
    /// <summary>Collector (assistant or tutor) whose cash was returned; null for historical rows.</summary>
    public long? CollectedByUserId { get; set; }
    /// <summary>When the departure/refund was confirmed.</summary>
    public DateTime DepartedAt { get; set; }
}

/// <summary>
/// One row of the teacher-wide "departed students" list (GET api/Payment/departures).
/// Sourced from the permanent, denormalized StudentDeparture record — no joins required.
/// </summary>
public sealed class DepartureListRow
{
    public long Id { get; set; }
    public long? TeacherStudentId { get; set; }
    public string? StudentName { get; set; }
    public string? StudentCode { get; set; }
    public string? SessionName { get; set; }
    public DateTime DepartedAt { get; set; }
    public Edvanz.Domain.Enums.DepartureOutcome DepartureOutcome { get; set; }
    public decimal FinalAmount { get; set; }
    public Edvanz.Domain.Enums.PaymentStatus PaymentStatusAtDeparture { get; set; }
    public bool IsTutorOverride { get; set; }

    /// <summary>Sessions the student attended in the anchored month (the "3" in "3/15").</summary>
    public int AttendedOccurrences { get; set; }
    /// <summary>Total sessions scheduled in the anchored month (the "15" in "3/15").</summary>
    public int TotalOccurrencesInPeriod { get; set; }
    /// <summary>The month's full price before attendance proration.</summary>
    public decimal FullPeriodAmount { get; set; }
    /// <summary>The attendance-prorated amount (what the student's attended sessions are worth).</summary>
    public decimal ProRatedAmount { get; set; }

    /// <summary>What the system calculated BEFORE the tutor overrode it (the "should have been 200").</summary>
    public decimal OriginalCalculatedAmount { get; set; }
    /// <summary>First day of the anchored month — by construction the last month the student paid for.</summary>
    public DateTime? AnchorPeriodStart { get; set; }
    /// <summary>Cash the student had paid for the anchored month at departure time.</summary>
    public decimal? PaidAmountAtDeparture { get; set; }
}

/// <summary>
/// One calendar day's departure totals — the source for the departed-list day-separator headers.
/// Computed over the whole filtered scope (all pages), so the figures are final, mirroring
/// <c>CollectionDailyNet</c> on the collections ledger.
/// </summary>
public sealed class DepartureDayTotalRow
{
    /// <summary>The calendar day (UTC date component of DepartedAt).</summary>
    public DateTime Date { get; set; }
    /// <summary>How many students departed on this day.</summary>
    public int DepartedCount { get; set; }
    /// <summary>Total actually refunded to students on this day (settled amounts, waived ones contribute 0).</summary>
    public decimal RefundedTotal { get; set; }
    /// <summary>Total students were still recorded as owing on this day.</summary>
    public decimal OwedTotal { get; set; }
}
