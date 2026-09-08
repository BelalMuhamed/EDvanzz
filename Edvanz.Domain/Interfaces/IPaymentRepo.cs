using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;

namespace Edvanz.Domain.Interfaces;

/// <summary>
/// Extended repository interface for the Payment Module (Module 4) and Event Payment Module (Module 5).
/// Centralizes all domain-specific query methods for payment-related entities:
/// PaymentTransaction, PaymentPeriod, StudentPaymentCounter, AssistantWallet,
/// WalletResetLog, PaymentEditLog, StudentDeparture, SessionTransferEvent,
/// PaymentEvent, EventStudentObligation, EventPaymentTransaction.
///
/// ARCHITECTURAL NOTE (same rationale as IUserRepo, ITeacherStudentRepo, IAttendanceRepo):
/// All expression-based queries are encapsulated here in named methods.
/// The Application layer never builds raw predicates. If a query changes,
/// you edit ONE method here — not every service that uses it.
///
/// Inherits from IGenericRepo&lt;PaymentTransaction, long&gt; for basic CRUD on the
/// primary entity. Other entities are accessed via named methods below.
/// </summary>
public interface IPaymentRepo : IGenericRepo<PaymentTransaction, long>
{
    // ══════════════════════════════════════════════
    // PAYMENT TRANSACTION QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets a payment transaction by ID scoped to a teacher.
    /// Returns null if not found or belongs to different teacher.
    /// </summary>
    Task<PaymentTransaction?> GetTransactionByIdAndTeacherAsync(long transactionId, long teacherId);

    /// <summary>
    /// From a candidate set of <c>SessionOccurrence</c> ids, returns the subset referenced by any
    /// <c>PaymentTransaction</c> (its per-session billing anchor). Consumed by the Attendance module
    /// before it rebuilds a session's occurrences on a recurrence edit (SES-1) so payment-anchored
    /// occurrences are preserved rather than SET NULL. Returns an empty list for an empty input.
    /// </summary>
    Task<IReadOnlyList<long>> GetReferencedSessionOccurrenceIdsAsync(IEnumerable<long> sessionOccurrenceIds);

    /// <summary>
    /// Gets a paginated list of payment transactions for a student across all sessions.
    /// REQ-PAY-052: Student payment history with period grouping.
    /// </summary>
    Task<(IReadOnlyList<PaymentTransaction> Items, int TotalCount)> GetStudentPaymentHistoryPagedAsync(
        long teacherId, long teacherStudentId,
        DateTime? startDate, DateTime? endDate,
        int page, int pageSize);

    /// <summary>
    /// Gets all transactions for a specific payment period.
    /// Used for period balance calculation and display.
    /// </summary>
    Task<IReadOnlyList<PaymentTransaction>> GetTransactionsByPeriodAsync(long paymentPeriodId);

    /// <summary>
    /// The most recent non-deleted transaction that settled the period via the PAY-1 allocation
    /// ledger. Fallback for months settled inside a multi-month cascade: the ONE transaction's
    /// direct PaymentPeriodId points at a single period, so the OTHER months it cleared have no
    /// row in <see cref="GetTransactionsByPeriodAsync"/> — without this fallback a departure
    /// refund on such a month writes an edit log with no transaction and vanishes from every
    /// collector ledger.
    /// </summary>
    Task<PaymentTransaction?> GetLatestTransactionForPeriodViaAllocationsAsync(long paymentPeriodId);

    /// <summary>
    /// Returns the CollectedByUserId of the most recent non-deleted payment the student made for the
    /// given session — i.e. who is holding the cash. Used to attribute a departure refund back to the
    /// correct assistant wallet. Null when there is no such payment (or it was collected by the tutor).
    /// </summary>
    Task<long?> GetLatestCollectorUserIdForStudentSessionAsync(long teacherId, long teacherStudentId, long sessionId);

    /// <summary>
    /// CollectedAt (UTC) of the most recent non-deleted payment the student made for the given session.
    /// Pairs with <see cref="GetLatestCollectorUserIdForStudentSessionAsync"/> so a departure refund can
    /// be tested against the collector's last cash hand-over (reset-aware wallet reversal). Null when
    /// there is no such payment.
    /// </summary>
    Task<DateTime?> GetLatestCollectionInstantForStudentSessionAsync(long teacherId, long teacherStudentId, long sessionId);

    /// <summary>
    /// Gets transactions by a student on a specific local date.
    /// REQ-PAY-020: Same-day duplicate detection.
    /// </summary>
    Task<IReadOnlyList<PaymentTransaction>> GetSameDayTransactionsAsync(
        long teacherId, long teacherStudentId, DateTime localDate);

    /// <summary>
    /// Finds an offline-synced transaction by its client-generated id.
    /// Exactly-once replay guard for POST api/Payment/sync.
    /// </summary>
    Task<PaymentTransaction?> GetByClientEntryIdAsync(
        long teacherId, string clientEntryId);

    /// <summary>
    /// Gets transactions collected by a specific user in a date range.
    /// REQ-PAY-013: Collector view. REQ-PAY-058: Collector performance report.
    /// </summary>
    Task<(IReadOnlyList<PaymentTransaction> Items, int TotalCount)> GetCollectorTransactionsPagedAsync(
        long teacherId, long collectedByUserId,
        DateTime? startDate, DateTime? endDate,
        int page, int pageSize);

    /// <summary>
    /// Gets all transactions for a session in a date range.
    /// REQ-PAY-053: Session payment report.
    /// </summary>
    Task<(IReadOnlyList<PaymentTransaction> Items, int TotalCount)> GetSessionTransactionsPagedAsync(
        long teacherId, long sessionId,
        DateTime? startDate, DateTime? endDate,
        int page, int pageSize);

    /// <summary>
    /// Gets all transactions across all sessions for a date range.
    /// REQ-PAY-060: Daily collection report. REQ-PAY-061: Period revenue report.
    /// </summary>
    Task<(IReadOnlyList<PaymentTransaction> Items, int TotalCount)> GetTransactionsByDateRangePagedAsync(
        long teacherId,
        DateTime startDate, DateTime endDate,
        long? sessionId, long? collectedByUserId,
        int page, int pageSize,
        string? search = null);

    /// <summary>
    /// Distribution of money collected by per-month amount in [start, end] for the teacher (optionally
    /// one collector): groups the per-period settlement slices by applied amount so a multi-month
    /// payment counts once per month. Drives the "how many paid X" summary cards.
    /// <para>
    /// <paramref name="search"/> narrows to the same student name/code filter the ledger rows use
    /// (<see cref="GetTransactionsByDateRangePagedAsync"/>), so the cards follow the visible list
    /// instead of reporting the whole scope. Null/blank ⇒ unfiltered (the original behaviour).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<(decimal Amount, int Count)>> GetCollectionAmountTiersAsync(
        long teacherId, DateTime startInclusive, DateTime endInclusive, long? collectedByUserId,
        string? search = null);

    // ══════════════════════════════════════════════
    // PAYMENT PERIOD QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets the earliest unpaid payment period for a student in a session.
    /// BR-PAY-001: Core payment assignment logic.
    /// Uses index on (TeacherId, TeacherStudentId, PaymentStatus, PeriodSequence).
    /// </summary>
    Task<PaymentPeriod?> GetEarliestUnpaidPeriodAsync(long teacherId, long teacherStudentId, long? sessionId);

    /// <summary>
    /// All unpaid periods (PaymentStatus != Paid) for the student/session whose PeriodStart is on
    /// or before <paramref name="throughMonthEnd"/>, earliest-first (by PeriodSequence). Tracked
    /// so the caller can apply a cascading payment across them. The cutoff enforces the
    /// "pay overdue up to this month, at most one month in advance" rule.
    /// </summary>
    Task<List<PaymentPeriod>> GetUnpaidPeriodsThroughAsync(
        long teacherId, long teacherStudentId, long? sessionId, DateTime throughMonthEnd);

    /// <summary>
    /// Re-priceable periods for a SESSION (a session-price change): periods of
    /// <paramref name="sessionId"/> that are still Unpaid or PartiallyPaid, EXCLUDING students who
    /// carry an individual CustomPaymentAmount (BR-PAY-003 — their price is not affected by session
    /// changes) and EXCLUDING carried/moved rows (<c>IsCarriedForward</c> or
    /// <c>MovedFromSessionId</c>) — agreed debt from elsewhere is never re-priced by this session.
    /// Tracked (the caller rewrites AmountDue/PaymentStatus and saves). Ordered by student then
    /// sequence.
    /// <para>The window is per PERIOD TYPE because the two bill different things: a Monthly row is a
    /// month's bill that is still being collected, so it re-prices over the caller's whole window
    /// (<c>DateTime.MinValue</c> = every still-owed month, arrears included — the same scope a
    /// per-student price change uses); a PerSession row is one class, and a class already delivered
    /// was delivered at the old price, so it re-prices only from
    /// <paramref name="perSessionFromDate"/> (next month).</para>
    /// </summary>
    /// <param name="teacherId">Tenant scope.</param>
    /// <param name="sessionId">The session whose default-priced periods are re-priced.</param>
    /// <param name="monthlyFromMonthStart">Inclusive lower bound for <c>PeriodType.Monthly</c> rows.</param>
    /// <param name="perSessionFromDate">Inclusive lower bound for <c>PeriodType.PerSession</c> rows.</param>
    Task<List<PaymentPeriod>> GetRepriceableSessionDefaultPeriodsAsync(
        long teacherId, long sessionId, DateTime monthlyFromMonthStart, DateTime perSessionFromDate);

    /// <summary>
    /// FUTURE re-priceable periods for one STUDENT (a per-student price change): the student's
    /// periods whose PeriodStart is on/after <paramref name="fromMonthStart"/> and are still Unpaid
    /// or PartiallyPaid. Tracked so the caller can rewrite AmountDue/PaymentStatus and save.
    /// </summary>
    Task<List<PaymentPeriod>> GetRepriceableStudentPeriodsAsync(
        long teacherId, long teacherStudentId, DateTime fromMonthStart);

    /// <summary>
    /// Total arrears (sum of each unpaid month's remaining due) the student owes through
    /// <paramref name="throughMonthEnd"/> for the session. Server-owned "amount due" for the
    /// collect lookup and mark-paid; never includes months in advance.
    /// </summary>
    Task<decimal> GetOverdueTotalThroughAsync(
        long teacherId, long teacherStudentId, long? sessionId, DateTime throughMonthEnd);

    /// <summary>
    /// Returns the most recent period the student actually paid into (AmountPaid &gt; 0) for the
    /// session — i.e. the current month if paid, otherwise the previous paid month. Used for the
    /// departure refund when proration is DISABLED (refund the full paid amount of that single
    /// period, not a pro-rated or cumulative amount). Null if the student has paid nothing.
    /// </summary>
    Task<PaymentPeriod?> GetLatestPaidPeriodAsync(long teacherId, long teacherStudentId, long? sessionId, DateTime throughMonthEnd);

    /// <summary>
    /// Gets a payment period by ID.
    /// </summary>
    Task<PaymentPeriod?> GetPaymentPeriodByIdAsync(long paymentPeriodId);

    /// <summary>
    /// Gets all payment periods for a student in a session, ordered by sequence.
    /// REQ-PAY-052: Student payment history timeline.
    /// </summary>
    Task<IReadOnlyList<PaymentPeriod>> GetPaymentPeriodsByStudentAndSessionAsync(
        long teacherId, long teacherStudentId, long? sessionId);

    /// <summary>
    /// Gets all payment periods for a student across all sessions.
    /// REQ-PAY-092: Unified timeline across all sessions.
    /// </summary>
    Task<IReadOnlyList<PaymentPeriod>> GetAllPaymentPeriodsByStudentAsync(
        long teacherId, long teacherStudentId);

    /// <summary>
    /// Gets ALL of a student's payment periods (across sessions) with their non-deleted
    /// <c>PaymentTransactions</c> eager-loaded, ordered by period start. Backs the
    /// student/parent payment tracking screen, where each paid period needs its settlement
    /// date (the latest transaction's local collection date).
    /// </summary>
    Task<IReadOnlyList<PaymentPeriod>> GetStudentPeriodsWithTransactionsAsync(
        long teacherId, long teacherStudentId);

    /// <summary>
    /// Gets a student's payment periods filtered to an optional date window (by period start),
    /// ordered by session then sequence. Backs the payment-history startDate/endDate filter.
    /// </summary>
    Task<IReadOnlyList<PaymentPeriod>> GetPaymentPeriodsByStudentInRangeAsync(
        long teacherId, long teacherStudentId, DateTime? startDate, DateTime? endDate);

    /// <summary>
    /// Gets unpaid payment periods for a specific session.
    /// REQ-PAY-028: Unpaid badge count per session.
    /// </summary>
    Task<int> CountUnpaidStudentsBySessionAsync(long teacherId, long sessionId);

    /// <summary>
    /// Count of active students currently assigned to a session (SessionId not null).
    /// Used as the dashboard "total students" — the roster a teacher expects to collect from.
    /// </summary>
    Task<int> CountAssignedStudentsAsync(long teacherId);

    /// <summary>
    /// Per-session collection for the "collected by session" tracking card, scoped BY PERIOD to
    /// [startInclusive, endExclusive): <c>CashCollected</c> = sum of <c>PaymentPeriod.AmountPaid</c>
    /// on the session's periods whose <c>PeriodStart</c> falls in the window; <c>PaidStudents</c> =
    /// distinct students with <c>AmountPaid &gt; 0</c> on their period(s) for that session in the
    /// window. This answers "how much of THIS session's THIS-month bill was collected, and how many
    /// students paid their this-month bill" — so a payment toward a previous month reflects under
    /// that previous month's card, NOT the current month's (reconciles with the roster's paid/unpaid
    /// split). Deliberately DIFFERENT from the transaction-date/cash-based dashboard + assistant
    /// "collected this month" figures (<see cref="GetCashCollectedInRangeAsync"/> /
    /// <see cref="GetDashboardPerCollectorAsync"/>), which are unchanged. Excludes soft-deleted /
    /// purge-orphaned students. §7.4.
    /// </summary>
    Task<IReadOnlyList<(long SessionId, decimal CashCollected, int PaidStudents)>>
        GetSessionMonthCollectionAsync(long teacherId, DateTime startInclusive, DateTime endExclusive);

    /// <summary>
    /// Per-session count of students currently assigned to it — the per-session card's
    /// "total students" denominator.
    /// </summary>
    Task<IReadOnlyList<(long SessionId, int TotalStudents)>>
        GetAssignedStudentCountsPerSessionAsync(long teacherId);

    /// <summary>
    /// Actual cash collected (by transaction date) in [startInclusive, endExclusive), optionally
    /// for one session — the dashboard's "collected this month". Net of refunds (soft-deleted /
    /// edited transactions), and independent of which month each payment settles.
    /// </summary>
    Task<decimal> GetCashCollectedInRangeAsync(
        long teacherId, long? sessionId, DateTime startInclusive, DateTime endExclusive);

    /// <summary>
    /// Count of DISTINCT students who made at least one (non-deleted) payment in
    /// [startInclusive, endExclusive), optionally scoped to one session. Backs the date-filtered
    /// collections summary ("how many students paid" over the range).
    /// </summary>
    Task<int> CountDistinctPayingStudentsInRangeAsync(
        long teacherId, long? sessionId, DateTime startInclusive, DateTime endExclusive);

    /// <summary>
    /// LIVE (!IsDeleted) collection counts per collector user id, all-time. The wallet screens used
    /// to show the denormalized <c>AssistantWallet.TransactionCount</c> counter, which is incremented
    /// on collect but never decremented when a collection is later deleted — so it drifts above the
    /// live row count the tracking collectors card shows (the "45 outside / 53 inside" report).
    /// Display counts should come from HERE so every surface counts the same thing.
    /// </summary>
    Task<Dictionary<long, int>> GetLiveCollectionCountsByCollectorUserAsync(long teacherId);

    /// <summary>
    /// All collections a collector took in [startInclusive, endExclusive), unpaged —
    /// merged with refunds into a single chronological month log.
    /// </summary>
    Task<IReadOnlyList<PaymentTransaction>> GetCollectorTransactionsInRangeAsync(
        long teacherId, long collectorUserId, DateTime startInclusive, DateTime endExclusive);

    /// <summary>
    /// Refunds taken back from a collector in [startInclusive, endExclusive), from the payment
    /// edit-log trail (delete/reversal = full amount; amount reduction = the drop).
    /// <para>
    /// <paramref name="includeDeleted"/> (default <c>true</c>) keeps the historical behaviour used by
    /// the assistant-wallet CARD, which reads collections with <c>IgnoreQueryFilters()</c> and so
    /// PAIRS a fully-deleted collection's positive row with its negative <c>Deleted</c> refund line
    /// (they net to zero). Pass <c>false</c> ONLY on the "view more" collections list
    /// (<c>GetCollectionsByMonthAsync</c>), which reads collections with the <c>!IsDeleted</c> filter:
    /// there the deleted collection's positive row is already gone, so its <c>Deleted</c> refund line
    /// would show as an ORPHANED negative with no positive counterpart. <c>Reversed</c> rows (partial
    /// departure reversals whose transaction stays visible) are ALWAYS included — their positive
    /// counterpart is still on the list.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<CollectorRefundRow>> GetCollectorRefundsInRangeAsync(
        long teacherId, long collectorUserId, DateTime startInclusive, DateTime endExclusive,
        bool includeDeleted = true);

    /// <summary>
    /// Student-departure refunds (RefundDue) confirmed within [startInclusive, endExclusive), sourced
    /// directly from <c>StudentDeparture</c> (authoritative <c>FinalAmount</c>). Teacher-wide, or
    /// narrowed to one collector. Drives the negative refund lines on the collections ledger and the
    /// per-collector refund subtraction.
    /// </summary>
    Task<IReadOnlyList<DepartureRefundRow>> GetDepartureRefundsByDateRangeAsync(
        long teacherId, DateTime startInclusive, DateTime endExclusive, long? collectedByUserId = null);

    /// <summary>
    /// Gets the maximum period sequence for a student in a session.
    /// Used when generating new periods to determine the next sequence number.
    /// </summary>
    Task<int> GetMaxPeriodSequenceAsync(long teacherId, long teacherStudentId, long sessionId);

    /// <summary>
    /// Gets student payment status counts for the Paid / Pro-rated / Unpaid
    /// overview card. Classification is per student, based on the same
    /// "earliest outstanding period" concept as <see cref="GetEarliestUnpaidPeriodAsync"/>
    /// (BR-PAY-001): no outstanding period → Paid; earliest outstanding period
    /// is prorated (REQ-PAY-021/022 first-period proration) → Pro-rated;
    /// otherwise → Unpaid. Students with no payment periods at all are not counted.
    /// </summary>
    Task<(int Paid, int ProRated, int Unpaid)> GetStudentPaymentStatusCountsAsync(
        long teacherId, DateTime selectedMonthEnd);

    /// <summary>
    /// Count of assigned students with a period in [monthStart, monthEnd] that is
    /// <c>PartiallyPaid</c> (0 &lt; AmountPaid &lt; AmountDue) — the "part paid this month" bucket,
    /// matching the <c>status="partial"</c> classification in
    /// <see cref="GetStudentsByPaymentStatusPagedAsync"/>. Backs the collections summary.
    /// </summary>
    Task<int> CountPartiallyPaidStudentsInMonthAsync(
        long teacherId, DateTime monthStart, DateTime monthEnd);

    /// <summary>
    /// Adds a new payment period.
    /// </summary>
    Task AddPaymentPeriodAsync(PaymentPeriod period);

    /// <summary>
    /// Adds multiple payment periods in bulk.
    /// Used when generating all periods for a newly assigned student.
    /// </summary>
    Task AddPaymentPeriodsRangeAsync(IEnumerable<PaymentPeriod> periods);

    /// <summary>
    /// Updates an existing payment period.
    /// </summary>
    Task UpdatePaymentPeriodAsync(PaymentPeriod period);

    /// <summary>
    /// ADMIN one-off support: every <see cref="PaymentPeriod"/> for a student, TRACKED (the caller
    /// mutates AmountPaid/PaymentStatus and adds a new period) and NOT teacher-scoped —
    /// <c>TeacherStudentId</c> is globally unique, so this reads a single student's whole timeline for a
    /// cross-tenant SuperAdmin op. Ordered by PeriodSequence.
    /// </summary>
    Task<List<PaymentPeriod>> GetTrackedPaymentPeriodsByStudentAsync(long teacherStudentId);

    /// <summary>
    /// ADMIN one-off support: the settlement-ledger rows that point at ONE period, TRACKED so the caller
    /// can repoint them to another period (move an advance payment). Ordered by id.
    /// </summary>
    Task<List<PaymentTransactionAllocation>> GetAllocationsByPeriodAsync(long paymentPeriodId);

    /// <summary>
    /// ADMIN one-off support (duplicate-period cleanup): distinct <c>TeacherStudentId</c>s that own at least
    /// one payment period, optionally scoped to one teacher (null = every teacher). Nulls excluded.
    /// </summary>
    Task<List<long>> GetStudentIdsWithPeriodsAsync(long? teacherId);

    /// <summary>
    /// ADMIN one-off support (duplicate-period cleanup): of the given period ids, the subset referenced by
    /// ANY <c>PaymentTransaction.PaymentPeriodId</c> or <c>PaymentTransactionAllocation.PaymentPeriodId</c>
    /// (deleted rows included — a reference of any kind makes a period unsafe to delete).
    /// </summary>
    Task<HashSet<long>> GetReferencedPeriodIdsAsync(IReadOnlyCollection<long> periodIds);

    /// <summary>
    /// ADMIN one-off support: repoints the denormalized <c>PaymentTransaction.PaymentPeriodId</c> of every
    /// NON-deleted transaction currently pointing at <paramref name="fromPeriodId"/> to
    /// <paramref name="toPeriodId"/> (bulk ExecuteUpdate). Returns the number of rows changed.
    /// </summary>
    Task<int> RepointTransactionsToPeriodAsync(long fromPeriodId, long toPeriodId);

    // ══════════════════════════════════════════════
    // PAYMENT TRANSACTION ALLOCATION LEDGER (PAY-1)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds per-period settlement rows for a transaction (one per period a cascade collection
    /// cleared). Allocations may reference a not-yet-persisted transaction via the navigation
    /// property; EF fixes up the FK on save.
    /// </summary>
    Task AddPaymentTransactionAllocationsRangeAsync(IEnumerable<PaymentTransactionAllocation> allocations);

    /// <summary>
    /// Returns the settlement ledger for a transaction, TRACKED and with each allocation's
    /// <see cref="PaymentPeriod"/> eagerly loaded (also tracked), so a caller can reverse each
    /// period's amount in place. Ordered by the period's start date (oldest first).
    /// </summary>
    Task<IReadOnlyList<PaymentTransactionAllocation>> GetAllocationsByTransactionAsync(long transactionId);

    /// <summary>
    /// Removes fully-consumed allocation rows (a slice whose amount has been entirely reversed).
    /// </summary>
    Task RemovePaymentTransactionAllocationsAsync(IEnumerable<PaymentTransactionAllocation> allocations);

    // ══════════════════════════════════════════════
    // STUDENT PAYMENT COUNTER QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets the payment counter for a student under a teacher.
    /// O(1) lookup via unique index.
    /// </summary>
    Task<StudentPaymentCounter?> GetPaymentCounterAsync(long teacherId, long teacherStudentId);

    /// <summary>
    /// Adds a new payment counter.
    /// </summary>
    Task AddPaymentCounterAsync(StudentPaymentCounter counter);

    /// <summary>
    /// Updates an existing payment counter.
    /// Uses RowVersion for optimistic concurrency.
    /// </summary>
    Task UpdatePaymentCounterAsync(StudentPaymentCounter counter);

    /// <summary>
    /// Unpaid Students Overview rows (REQ-PAY-031/032/033), derived from <c>PaymentPeriods</c>
    /// and judged ONLY through <paramref name="throughMonthEnd"/> (<c>PeriodStart &lt;= cutoff</c>)
    /// per CLAUDE.md §7.4 — periods are pre-generated to the session end, so a counter-derived
    /// read counts months that are not yet owed.
    ///
    /// Scoped by the PERIOD's session/group, not the student's current assignment: a student who
    /// transferred out still owes the old session's arrears. Only ACTIVE (non-recycled) students
    /// are returned. <paramref name="paymentType"/> maps onto <c>PaymentPeriod.PeriodType</c>.
    /// <paramref name="minConsecutiveUnpaid"/> filters on the unpaid-period count, which — because
    /// collection cascades oldest-first — is also the consecutive count (BR-PAY-006).
    /// </summary>
    Task<(IReadOnlyList<UnpaidStudentRow> Items, int TotalCount)> GetUnpaidStudentsPagedAsync(
        long teacherId,
        long? sessionId, long? sessionGroupId,
        PaymentType? paymentType,
        int? minConsecutiveUnpaid,
        string? search,
        DateTime throughMonthEnd,
        int page, int pageSize);

    /// <summary>
    /// Gets the sum of total outstanding across all students for a teacher.
    /// REQ-PAY-NFR-008: Live total outstanding in Unpaid Students View.
    /// </summary>
    Task<decimal> GetTotalOutstandingAmountAsync(long teacherId, long? sessionId);

    // ══════════════════════════════════════════════
    // SCREEN QUERIES (api/v1 — frontend payment.json)
    // ══════════════════════════════════════════════

    /// <summary>
    /// CollectPayment screen student list: students filtered by assignment
    /// (all | assigned | unassigned) + name/code search, paginated, each with
    /// payment status/amount/unpaid-months from their counter. Also returns the
    /// per-tab counts (all/assigned/unassigned) for the current search.
    /// <para><paramref name="sessionScopeIds"/> (optional) restricts the population to students assigned
    /// to one of the given sessions — this session plus its linked sessions, mirroring the take-attendance
    /// roster. When null the query is teacher-wide (existing behavior, unchanged).</para>
    /// </summary>
    Task<(IReadOnlyList<CollectStudentRow> Items, int TotalCount, int CountAll, int CountAssigned, int CountUnassigned)>
        GetCollectStudentsPagedAsync(
            long teacherId, string filter, string? search, int page, int pageSize,
            DateTime unpaidThroughMonthEnd, IReadOnlyCollection<long>? sessionScopeIds = null);

    /// <summary>
    /// PaymentTracking "students by status" list: students whose current status
    /// (by the earliest-outstanding-period rule, same as
    /// <see cref="GetStudentPaymentStatusCountsAsync"/>) matches
    /// <paramref name="status"/> (paid | prorated | unpaid | partial), paginated, each with that
    /// month's paid/due amounts and their counter's outstanding/unpaid-months. Also
    /// returns the group's month-scoped collected/expected totals and total outstanding.
    /// <c>GroupExpected</c> is what the scope is BILLED for the month (Σ AmountDue − Forgiven, the
    /// tracking card's basis); <c>GroupExpectedRate</c> is the same scope at TODAY's per-student
    /// rates. The two diverge whenever a bill was written at a price that has since changed.
    ///
    /// <para><paramref name="status"/> is OPTIONAL: when null, ALL of the (assigned) scope's
    /// students are returned, each carrying its OWN computed status on
    /// <see cref="StudentByStatusRow.Status"/> (a per-session mixed-status roster in one call).
    /// When supplied, behaviour is unchanged (rows are filtered to that status).</para>
    ///
    /// <para><paramref name="sessionId"/> (optional) restricts the assigned-scope to a single
    /// session; <paramref name="search"/> (optional) filters by student name OR studentCode
    /// (case-insensitive). Both intersect with — never replace — the assigned-only gating.</para>
    /// </summary>
    Task<(IReadOnlyList<StudentByStatusRow> Items, int TotalCount, decimal GroupCollected, decimal GroupExpected, decimal GroupUnpaid, decimal GroupExpectedRate)>
        GetStudentsByPaymentStatusPagedAsync(
            long teacherId, string? status,
            DateTime monthStart, DateTime monthEnd,
            int page, int pageSize,
            long? sessionId = null, string? search = null);

    /// <summary>
    /// SessionPaymentCollectedByYear matrix: students who have at least one payment period in
    /// the given year, paginated by student name, each with their per-calendar-month cells
    /// (aggregated across sessions). Returns the total student count for paging.
    /// </summary>
    Task<(IReadOnlyList<YearlyStudentRow> Items, int TotalCount)>
        GetYearlyCollectionsPagedAsync(
            long teacherId, DateTime yearStart, DateTime yearEnd, int page, int pageSize);

    /// <summary>
    /// CollectPaymentSession lookup: resolves a single student for collection by QR/barcode,
    /// then student code, then name (first match by name), returning the amount they should pay
    /// (custom amount → earliest-unpaid-period remaining → session amount) and paid/unpaid state.
    /// Returns null when nothing matches or no lookup key is supplied.
    ///
    /// <paramref name="advanceCapEnd"/> enables the one-month-in-advance option: when it is
    /// non-null AND the student has NO arrears through <paramref name="throughMonthEnd"/> (fully
    /// caught up), the earliest unpaid month in the window
    /// (<paramref name="throughMonthEnd"/>, <paramref name="advanceCapEnd"/>] — i.e. the current
    /// month + 1, the same cap CollectPaymentAsync enforces — is surfaced as the collectable month
    /// so the assistant can take next month's fee ahead. Null (or arrears present) keeps the
    /// arrears-only behaviour. Never surfaces more than one month ahead.
    /// </summary>
    Task<CollectLookupRow?> ResolveCollectLookupAsync(
        long teacherId, string? qr, string? code, string? name, DateTime throughMonthEnd,
        DateTime? advanceCapEnd = null);

    // ══════════════════════════════════════════════
    // ASSISTANT WALLET QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets the wallet for an assistant under a teacher.
    /// </summary>
    Task<AssistantWallet?> GetAssistantWalletAsync(long teacherId, long assistantId);

    /// <summary>
    /// ADMIN one-off support: resolves an assistant wallet by its <c>AssistantId</c> alone (NOT
    /// teacher-scoped — for the cross-tenant SuperAdmin recompute). Tracked, with the assistant's user
    /// eager-loaded for the display name. Null when no wallet has that AssistantId (e.g. a
    /// center-assistant wallet, which keys on CenterAssistantId).
    /// </summary>
    Task<AssistantWallet?> GetAssistantWalletByAssistantIdAsync(long assistantId);

    /// <summary>
    /// Gets the wallet by assistant user ID.
    /// Used during payment collection to find the collector's wallet.
    /// </summary>
    Task<AssistantWallet?> GetAssistantWalletByUserIdAsync(long teacherId, long assistantUserId);

    /// <summary>Finds a center-assistant's wallet under a teacher by CenterAssistantId (its unique key).</summary>
    Task<AssistantWallet?> GetAssistantWalletByCenterAssistantIdAsync(long teacherId, long centerAssistantId);

    /// <summary>
    /// Gets all assistant wallets for a teacher.
    /// REQ-PAY-035: View current wallet balance of each assistant.
    /// </summary>
    Task<IReadOnlyList<AssistantWallet>> GetAllAssistantWalletsAsync(long teacherId);

    /// <summary>
    /// Adds a new assistant wallet.
    /// </summary>
    Task AddAssistantWalletAsync(AssistantWallet wallet);

    /// <summary>
    /// Updates an existing assistant wallet.
    /// Uses RowVersion for optimistic concurrency.
    /// </summary>
    Task UpdateAssistantWalletAsync(AssistantWallet wallet);

    // ══════════════════════════════════════════════
    // WALLET RESET LOG QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds a new wallet reset log entry.
    /// REQ-PAY-037: Permanent ledger event — never deleted.
    /// </summary>
    Task AddWalletResetLogAsync(WalletResetLog log);

    /// <summary>
    /// Gets all wallet reset logs for an assistant.
    /// REQ-PAY-059: Assistant Wallet History Report.
    /// </summary>
    Task<IReadOnlyList<WalletResetLog>> GetWalletResetLogsAsync(long teacherId, long assistantId);

    /// <summary>One reset/withdrawal log by id, TRACKED — the admin adjust-withdrawal op mutates
    /// <see cref="WalletResetLog.AmountReset"/> and saves.</summary>
    Task<WalletResetLog?> GetWalletResetLogByIdAsync(long walletResetLogId);

    /// <summary>Reset/withdrawal ledger entries for a center-assistant's wallet (keyed by CenterAssistantId).</summary>
    Task<IReadOnlyList<WalletResetLog>> GetWalletResetLogsForCenterAssistantAsync(long teacherId, long centerAssistantId);

    /// <summary>
    /// Instant (UTC) of the collector's MOST RECENT cash hand-over — the max <c>ResetAt</c> across every
    /// <c>WalletResetLog</c> for the given wallet (both full resets and partial withdrawals write one,
    /// keyed by <c>AssistantWalletId</c>). Null when the wallet has never been reset/withdrawn. Drives
    /// the reset-aware wallet reversal: cash collected on/before this instant was already handed to the
    /// tutor, so reversing it must NOT drive <c>CurrentBalance</c> negative.
    /// (Distinct from the AssistantId-keyed overload — this one works for center-assistant wallets too.)
    /// </summary>
    Task<DateTime?> GetLastWalletResetAtByWalletIdAsync(long teacherId, long assistantWalletId);

    /// <summary>
    /// Cash withdrawals/hand-overs taken from a collector's OWN wallet within [start, endExclusive),
    /// resolved from the collector's User id (→ their teacher-scoped Assistant record). Empty for a
    /// collector without a wallet (e.g. the tutor). Drives the negative withdrawal lines on a
    /// collector-scoped collections ledger.
    /// </summary>
    Task<IReadOnlyList<WalletResetLog>> GetWalletResetLogsForCollectorInRangeAsync(
        long teacherId, long collectorUserId, DateTime startInclusive, DateTime endExclusive);

    // ══════════════════════════════════════════════
    // PAYMENT EDIT LOG QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds a new payment edit log entry.
    /// </summary>
    Task AddPaymentEditLogAsync(PaymentEditLog log);

    /// <summary>
    /// Gets all edit logs for a payment transaction.
    /// </summary>
    Task<IReadOnlyList<PaymentEditLog>> GetPaymentEditLogsAsync(long paymentTransactionId);

    // ══════════════════════════════════════════════
    // FORGIVENESS QUERIES (waive outstanding balance — reversible)
    // ══════════════════════════════════════════════

    /// <summary>Adds a forgiveness header row (per-period allocations are added separately).</summary>
    Task AddPaymentForgivenessAsync(PaymentForgiveness forgiveness);

    /// <summary>Adds per-period waiver slices for a forgiveness (nav-linked so EF fixes the FK on save).</summary>
    Task AddPaymentForgivenessAllocationsRangeAsync(IEnumerable<PaymentForgivenessAllocation> allocations);

    /// <summary>
    /// Gets one forgiveness by id (tenant-scoped), TRACKED, with its allocations and each allocation's
    /// <c>PaymentPeriod</c> eagerly loaded (also tracked), so a reversal can restore the exact per-period
    /// <c>ForgivenAmount</c>. Null when not found / not this teacher's.
    /// </summary>
    Task<PaymentForgiveness?> GetForgivenessByIdAndTeacherAsync(long teacherId, long forgivenessId);

    /// <summary>All forgivenesses for a student (any status), newest first — the history surface.</summary>
    Task<IReadOnlyList<PaymentForgiveness>> GetForgivenessesByStudentAsync(long teacherId, long teacherStudentId);

    /// <summary>
    /// The student's per-month rate: their <c>CustomPaymentAmount</c> override, else the current
    /// session's <c>SessionAmount</c>, else 0. Backs whole-month-multiple note validation
    /// (collect/edit) and the collect-lookup <c>monthlyAmount</c>.
    /// </summary>
    Task<decimal> GetStudentMonthlyRateAsync(long teacherId, long teacherStudentId);

    // ══════════════════════════════════════════════
    // DEPARTURE QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds a new student departure record.
    /// REQ-PAY-073: Departure event recorded permanently.
    /// </summary>
    Task AddStudentDepartureAsync(StudentDeparture departure);

    /// <summary>
    /// Gets departure records for a student.
    /// </summary>
    Task<IReadOnlyList<StudentDeparture>> GetStudentDeparturesAsync(long teacherId, long teacherStudentId);

    /// <summary>Teacher-wide paged list of departed students (search by name/code), newest first.</summary>
    Task<(IReadOnlyList<DepartureListRow> Items, int TotalCount)> GetDeparturesPagedAsync(
        long teacherId, string? search, int page, int pageSize,
        DateTime? fromInclusive = null, DateTime? toExclusive = null);

    /// <summary>
    /// Per-day departure totals over the whole filtered scope (all pages) — the source for the
    /// departed-students list's day-separator headers. Takes the same filters as
    /// <see cref="GetDeparturesPagedAsync"/> so the totals always reconcile with the rows shown.
    /// </summary>
    Task<IReadOnlyList<DepartureDayTotalRow>> GetDepartureDayTotalsAsync(
        long teacherId, string? search, DateTime? fromInclusive = null, DateTime? toExclusive = null);

    /// <summary>
    /// Counts student departures confirmed in [startInclusive, endExclusive), split by outcome
    /// (RefundDue / AmountOwed; the remainder is NoObligation). Backs the collections summary's
    /// "departed" metric over the true date range.
    /// </summary>
    Task<(int Total, int RefundDue, int AmountOwed)> CountDeparturesInRangeAsync(
        long teacherId, DateTime startInclusive, DateTime endExclusive);

    // ══════════════════════════════════════════════
    // SESSION TRANSFER QUERIES
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds a new session transfer event.
    /// REQ-PAY-089: Permanently retained, never deleted.
    /// </summary>
    Task AddSessionTransferEventAsync(SessionTransferEvent transferEvent);

    /// <summary>
    /// Gets transfer events for a student.
    /// REQ-PAY-092: Unified timeline display.
    /// </summary>
    Task<IReadOnlyList<SessionTransferEvent>> GetStudentTransferEventsAsync(
        long teacherId, long teacherStudentId);

    // ══════════════════════════════════════════════
    // DASHBOARD AGGREGATES (REQ-PAY-039 through 044)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets the expected, collected, and remaining revenue for a teacher.
    /// REQ-PAY-040/041/042: Dashboard views.
    /// Filterable by session, session group, payment type, date range.
    /// </summary>
    Task<(decimal Expected, decimal Collected, decimal Remaining)> GetDashboardAggregatesAsync(
        long teacherId,
        long? sessionId, long? sessionGroupId,
        PaymentType? paymentType,
        DateTime? startDate, DateTime? endDate);

    /// <summary>
    /// Assigned-gated obligation aggregates for the tracking summary card's two perspectives
    /// (This month vs Total-through-this-month). Everything here is <b>period-based</b> — attributed
    /// to the month each installment BELONGS to, never the calendar date the cash arrived — and scoped
    /// to students currently assigned to a session (<c>SessionId != null</c>, the same population as
    /// <see cref="CountAssignedStudentsAsync"/> / <see cref="GetStudentPaymentStatusCountsAsync"/>), so
    /// the figures reconcile with the paid/prorated/unpaid headcounts. Orphaned periods
    /// (<c>TeacherStudentId == null</c>, purged students) are excluded. Amounts already respect
    /// proration and per-student custom price (both live in <c>AmountDue</c>). Two windows, each of which
    /// reconciles internally as <c>Expected = Collected + Remaining (+ Forgiven)</c>:
    /// <list type="bullet">
    /// <item><b>This month</b> — periods overlapping <c>[monthStart, monthEnd]</c>:
    ///   <c>ExpectedThisMonth</c> = Σ <c>AmountDue</c>; <c>CollectedThisMonth</c> = Σ <c>AmountPaid</c>;
    ///   <c>RemainingThisMonth</c> = Σ(<c>AmountDue − AmountPaid − ForgivenAmount</c>) over not-Paid periods.</item>
    /// <item><b>Through this month</b> — periods with <c>PeriodStart ≤ monthEnd</c> (current + every earlier
    ///   month): <c>ExpectedTotal</c> = Σ <c>AmountDue</c> (the full bills of this month + all earlier months,
    ///   so it is always ≥ <c>ExpectedThisMonth</c>); <c>CollectedTotal</c> = Σ <c>AmountPaid</c>;
    ///   <c>OutstandingTotal</c> = Σ(<c>AmountDue − AmountPaid − ForgivenAmount</c>) over not-Paid periods —
    ///   all money still owed up to and including this month (arrears + current).</item>
    /// <item><b>Paid ahead</b> — <c>CollectedAdvance</c> = Σ <c>AmountPaid</c> on FUTURE periods
    ///   (<c>PeriodStart &gt; monthEnd</c>): money students paid early for months after the selected one.
    ///   Deliberately OUTSIDE the through-this-month totals (so those still reconcile), surfaced on its
    ///   own "paid ahead" line so advance payments always have somewhere to show.</item>
    /// </list>
    /// Both remaining figures are clamped at 0. Indexed SUM round-trips, no row materialization.
    /// </summary>
    Task<(decimal ExpectedThisMonth, decimal CollectedThisMonth, decimal RemainingThisMonth,
          decimal ExpectedTotal, decimal CollectedTotal, decimal OutstandingTotal, decimal CollectedAdvance)>
        GetAssignedObligationAggregatesAsync(long teacherId, DateTime monthStart, DateTime monthEnd);

    /// <summary>
    /// DATE-BASED cash view of the tracking card's "Collected" section (teacher-requested 2026-08-19).
    /// The actual cash physically collected DURING the selected calendar month (by <c>CollectedAt</c>,
    /// NET of refunds — the same money the "collected by you/assistants" section shows), decomposed by
    /// WHICH installment month each payment settled. This is a pure cash-flow lens and is INTENTIONALLY
    /// independent of the obligation view (<see cref="GetAssignedObligationAggregatesAsync"/>): a July
    /// arrear paid in August lands in August's <c>Earlier</c> slice, never on July's card.
    /// <list type="bullet">
    /// <item><c>ThisMonth</c> — cash collected this month that settled THIS month's installments.</item>
    /// <item><c>Earlier</c> — cash collected this month that settled EARLIER months' installments
    ///   (arrears collected now).</item>
    /// <item><c>Ahead</c> — cash collected this month that settled FUTURE months' installments (advance).</item>
    /// </list>
    /// A payment's month(s) come from the <c>PaymentTransactionAllocation</c> ledger (multi-month cascades
    /// split across their periods), falling back to the transaction's own <c>PaymentPeriod</c> for legacy
    /// rows and to the ThisMonth bucket for a payment with no period at all. Departure refunds
    /// (<c>StudentDeparture.RefundDue</c>, by <c>DepartedAt</c>) are subtracted from the bucket of the month
    /// they refunded (<c>RefundPeriodStart</c>), so <c>Total = ThisMonth + Earlier + Ahead</c> and equals the
    /// collectors' cash total to the cent. A bucket can be net-negative in a window that refunded more than
    /// it collected for that month.
    /// </summary>
    Task<(decimal Total, decimal ThisMonth, decimal Earlier, decimal Ahead)>
        GetCashCollectedBreakdownAsync(long teacherId, DateTime monthStart, DateTime monthEnd);

    /// <summary>
    /// TRACKED. Per currently-assigned student, their earliest monthly period (min PeriodSequence — the
    /// enrollment's first month, the only period proration ever applies to) when it is still owed
    /// (Unpaid/PartiallyPaid). Drives the retroactive proration re-price on a settings toggle; fully-paid
    /// first months are excluded so already-settled money is never rewritten.
    /// </summary>
    Task<List<PaymentPeriod>> GetFirstMonthlyPeriodsForAssignedStudentsAsync(long teacherId);

    /// <summary>Batch-loads the (tracked) payment counters for a set of students — the proration
    /// reconcile reads each CustomPaymentAmount (the full base rate) and writes its deltas in place,
    /// so all affected counters are fetched in ONE query instead of per-student round-trips.</summary>
    Task<List<StudentPaymentCounter>> GetPaymentCountersByStudentIdsAsync(
        long teacherId, IReadOnlyCollection<long> teacherStudentIds);

    /// <summary>TRACKED. The single proration-anchor month (a new enrollment's first month) for this
    /// student+session, or null for a transfer / once cleared. Re-priced on first attendance.</summary>
    Task<PaymentPeriod?> GetProrationAnchorPeriodAsync(long teacherId, long teacherStudentId, long sessionId);

    /// <summary>TRACKED. Every still-owed proration-anchor month for the teacher (a new enrollment's
    /// first month) — the population the settings reconcile re-prices; transfers are excluded by design.</summary>
    Task<List<PaymentPeriod>> GetUnpaidAnchorMonthPeriodsAsync(long teacherId);

    /// <summary>The proration-anchor month info (month, amount, fraction, prorated?, manually set?,
    /// frozen class-basis) per student — feeds the "Joined {date} · {billed} of {total} classes →
    /// {amount} of {full}" + "set by hand" transparency on the payment screens, INCLUDING paid rows.</summary>
    Task<List<(long StudentId, long? SessionId, DateTime PeriodStart, decimal AmountDue, decimal ProRatedFraction, bool IsProRated, bool IsProrationManual, int? ClassesTotal, int? ClassesBilled)>>
        GetAnchorPeriodInfoByStudentIdsAsync(long teacherId, IReadOnlyCollection<long> studentIds);

    /// <summary>Distinct student ids that have any payment period in the given session — the population
    /// the session-delete lifecycle iterates to preserve history / carry arrears.</summary>
    Task<List<long>> GetStudentIdsWithPeriodsInSessionAsync(long teacherId, long sessionId);

    /// <summary>(teacher, student) owners of LEGACY orphaned unpaid periods (SessionId null, NOT
    /// carried-forward, still owed) left by the old session-delete — the population the orphan-cleanup
    /// tool reconciles. Optionally scoped to one teacher.</summary>
    Task<List<(long TeacherId, long TeacherStudentId)>> GetOrphanedUnpaidPeriodOwnersAsync(long? teacherId);

    /// <summary>A student's session-less, not-yet-carried periods (the orphan-cleanup input).</summary>
    Task<IReadOnlyList<PaymentPeriod>> GetOrphanedPeriodsByStudentAsync(long teacherId, long teacherStudentId);

    /// <summary>Earliest AssignedAt (UTC) across ALL of a student's session assignments — the student's
    /// ORIGINAL enrollment day, the single proration anchor under the enrollment-anchored model
    /// (REQ-PAY-021/022 rev 2, 2026-09-03: attendance never moves the bill). Survives session moves
    /// (a preserved first-month anchor keeps pricing from the original join, not the move date).
    /// Null when the student has no assignment rows.</summary>
    Task<DateTime?> GetEarliestAssignmentDateAsync(long teacherId, long teacherStudentId);

    /// <summary>Earliest AssignedAt (UTC) per student (min across ALL their sessions) for a batch —
    /// the reconcile / list-transparency companion of
    /// <see cref="GetEarliestAssignmentDateAsync"/>. One query for the whole set.</summary>
    Task<Dictionary<long, DateTime>> GetEarliestAssignmentDatesForStudentsAsync(
        long teacherId, IReadOnlyCollection<long> teacherStudentIds);

    /// <summary>TRACKED. Every payment period of the teacher whose <c>PeriodStart</c> is BEFORE the
    /// given date (Monthly rows are first-of-month dated, PerSession rows are class-dated, so one
    /// cutoff covers both) — the candidate set the billing-start reconcile trims. Includes rows of
    /// deleted sessions and session-less rows; the service applies the keep rules (cash / manual /
    /// carried debt). Orphaned rows (<c>TeacherStudentId</c> null) are excluded.</summary>
    Task<List<PaymentPeriod>> GetPeriodsStartingBeforeByTeacherAsync(long teacherId, DateTime beforeDate);

    /// <summary>The teacher's ACTIVE session assignments — (student, session, AssignedAt UTC) — the
    /// population the billing-start reconcile backfills. Ghost rows whose student was purged
    /// (BUG-8: TeacherStudent nulled but IsActive kept) are excluded.</summary>
    Task<List<(long TeacherStudentId, long SessionId, DateTime AssignedAt)>>
        GetActiveAssignmentRowsByTeacherAsync(long teacherId);

    /// <summary>TRACKED. One student's MONTHLY periods in one session, ordered by month — the
    /// billing-start reconcile's re-anchor input (identity-mapped, so rows already tracked by the
    /// same reconcile pass come back as the same instances, never attach-conflicting copies).</summary>
    Task<List<PaymentPeriod>> GetMonthlyPeriodsByStudentAndSessionTrackedAsync(
        long teacherId, long teacherStudentId, long sessionId);

    /// <summary>Latest proration-decision audit for ONE period (the <see cref="Edvanz.Domain.Entities.PaymentEditLog"/>
    /// with a null <c>PaymentTransactionId</c> linked by <c>PaymentPeriodId</c>): the suggested vs set
    /// amount, who set it and when — the "set by hand · by whom · when" transparency on the collect
    /// lookup. Null when the period was never manually priced.</summary>
    Task<(decimal SuggestedAmount, decimal SetAmount, long? SetByUserId, DateTime SetAt)?>
        GetLatestProrationEditForPeriodAsync(long teacherId, long periodId);

    /// <summary>Batch proration-audit for the collections ledger: for each collection transaction id that
    /// settled a manually-overridden joining month, returns the suggestion vs the set amount and who set it
    /// (from the proration-decision <see cref="Edvanz.Domain.Entities.PaymentEditLog"/> — the one with a
    /// null <c>PaymentTransactionId</c> linked by <c>PaymentPeriodId</c> to a settled allocation). Latest
    /// decision per transaction. Only transactions with such an audit row are returned.</summary>
    Task<List<(long PaymentTransactionId, decimal SuggestedAmount, decimal SetAmount, long? SetByUserId)>>
        GetProrationAuditByTransactionIdsAsync(long teacherId, IReadOnlyCollection<long> transactionIds);

    /// <summary>Earliest date the student physically attended ANY session (Present or the linked
    /// CrossSessionPresent) — the SESSION-AGNOSTIC first-attendance anchor used by the never-paid
    /// first-month-move re-proration remediation when a carried period's SessionId was nulled by a later
    /// session delete. Null if never attended.</summary>
    Task<DateTime?> GetFirstAttendanceDateAnyAsync(long teacherId, long teacherStudentId);

    /// <summary>(teacher, student) owners of a CARRIED / MOVED, still fully-unpaid, NON-prorated monthly
    /// period — the over-selected candidate population for the never-paid first-month-move re-proration
    /// remediation. The service refines to the student's earliest period + zero-paid history + a
    /// first-attendance day in a discounted tier. Optionally scoped to one teacher.</summary>
    Task<List<(long TeacherId, long TeacherStudentId)>>
        GetNeverPaidCarriedAnchorCandidateOwnersAsync(long? teacherId);

    /// <summary>
    /// Gets per-session breakdown of expected/collected/remaining.
    /// REQ-PAY-043: Filterable by session for drill-down.
    /// </summary>
    Task<IReadOnlyList<(long SessionId, string SessionName, long? SessionGroupId, string? SessionGroupName, decimal Expected, decimal Collected, decimal Remaining)>>
        GetDashboardPerSessionAsync(
            long teacherId,
            long? sessionGroupId,
            PaymentType? paymentType,
            DateTime? startDate, DateTime? endDate);

    /// <summary>
    /// Gets the collection summary for every currently active session
    /// (<c>EndDate &gt;= today</c>): collected/expected amounts and
    /// paid/total student counts, alongside the schedule fields needed to
    /// build the display label.
    /// REQ-PAY-043: "Collected by Sessions" card — per-session collection
    /// progress while the session is active.
    /// </summary>
    /// <param name="teacherId">The owning teacher's Id (multi-tenant scope).</param>
    /// <param name="asOfLocalDate">
    /// The TEACHER's current local date, from <c>ITimeZoneService.GetTeacherLocalDate</c>.
    /// Required rather than derived here: <c>EndDate</c> is a calendar day, and
    /// <c>DateTime.UtcNow.Date</c> is still yesterday between midnight and 2-3 AM Cairo, so a
    /// session dropped off the card for the first hours of the day it actually ended.
    /// </param>
    Task<IReadOnlyList<ActiveSessionCollectionSummaryRow>> GetActiveSessionsCollectionSummaryAsync(
        long teacherId, DateTime asOfLocalDate);

    /// <summary>
    /// Gets per-collector breakdown of collected amounts.
    /// REQ-PAY-041: Collected revenue broken down by collecting users.
    /// </summary>
    Task<IReadOnlyList<(long UserId, string? UserName, decimal Collected, int TransactionCount)>>
        GetDashboardPerCollectorAsync(
            long teacherId,
            DateTime? startDate, DateTime? endDate);

    // ══════════════════════════════════════════════
    // EVENT QUERIES (Module 5)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Adds a new payment event.
    /// REQ-EVT-001: Create one-time payment events.
    /// </summary>
    Task AddPaymentEventAsync(PaymentEvent paymentEvent);

    /// <summary>
    /// Gets a payment event by ID scoped to a teacher.
    /// </summary>
    Task<PaymentEvent?> GetPaymentEventByIdAndTeacherAsync(long eventId, long teacherId);

    /// <summary>
    /// Gets all active (non-deleted) events for a teacher.
    /// REQ-EVT-014: Event list display.
    /// </summary>
    Task<(IReadOnlyList<PaymentEvent> Items, int TotalCount)> GetPaymentEventsPagedAsync(
        long teacherId, int page, int pageSize);

    /// <summary>
    /// Updates an existing payment event.
    /// </summary>
    Task UpdatePaymentEventAsync(PaymentEvent paymentEvent);

    /// <summary>
    /// Adds event student obligations in bulk.
    /// BR-EVT-001: Created at event creation time for target scope.
    /// </summary>
    Task AddEventObligationsRangeAsync(IEnumerable<EventStudentObligation> obligations);

    /// <summary>
    /// Gets a specific event obligation for a student.
    /// </summary>
    Task<EventStudentObligation?> GetEventObligationAsync(long eventId, long teacherStudentId);

    /// <summary>
    /// Gets all obligations for an event with paging, paid/unpaid separation, and search.
    /// REQ-EVT-015: Paid and unpaid student lists.
    /// REQ-EVT-016: Searchable by student name or student code.
    /// </summary>
    Task<(IReadOnlyList<EventStudentObligation> Items, int TotalCount)> GetEventObligationsPagedAsync(
        long eventId, long teacherId,
        PaymentStatus? statusFilter,
        string? search,
        int page, int pageSize);

    /// <summary>
    /// Updates an event student obligation (e.g., after payment collection).
    /// </summary>
    Task UpdateEventObligationAsync(EventStudentObligation obligation);

    /// <summary>
    /// Deletes an event student obligation.
    /// REQ-EVT-021: Remove student from event if they haven't paid.
    /// </summary>
    Task DeleteEventObligationAsync(EventStudentObligation obligation);

    /// <summary>
    /// Gets filtered and paginated events for a teacher.
    /// REQ-EVT-017/018: Searchable by name, filterable by scope and completion.
    /// </summary>
    Task<(IReadOnlyList<PaymentEvent> Items, int TotalCount)> GetPaymentEventsFilteredPagedAsync(
        long teacherId,
        string? searchName,
        EventTargetScopeType? scopeTypeFilter,
        string? completionStatus,
        int page, int pageSize);

    /// <summary>
    /// Adds an event payment transaction.
    /// REQ-EVT-009: Collect event payment.
    /// </summary>
    Task AddEventPaymentTransactionAsync(EventPaymentTransaction transaction);

    /// <summary>
    /// Gets event payment transactions for reporting.
    /// REQ-EVT-023: Single Event Payment Report.
    /// </summary>
    Task<IReadOnlyList<EventPaymentTransaction>> GetEventPaymentTransactionsAsync(
        long eventId, long teacherId);

    // ══════════════════════════════════════════════
    // INTEGRATION HOOKS (called before delete/purge)
    // ══════════════════════════════════════════════

    // ══════════════════════════════════════════════
    // TARGET SCOPE RESOLUTION (Module 5 event creation)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets all active (non-deleted) student IDs assigned to a specific session.
    /// REQ-EVT-004: Session-scoped event target resolution.
    /// </summary>
    Task<List<long>> GetStudentIdsBySessionAsync(long teacherId, long sessionId);

    /// <summary>
    /// Gets all active (non-deleted) student IDs across all sessions in a session group.
    /// REQ-EVT-005: Session group-scoped event target resolution.
    /// </summary>
    Task<List<long>> GetStudentIdsByGroupAsync(long teacherId, long sessionGroupId);

    /// <summary>
    /// Gets all active (non-deleted) student IDs under a teacher's account.
    /// REQ-EVT-006: All students scope event target resolution.
    /// </summary>
    Task<List<long>> GetAllStudentIdsAsync(long teacherId);

    // ══════════════════════════════════════════════
    // INTEGRATION HOOKS (called before delete/purge)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Nullifies SessionId on all payment transactions and periods for a session
    /// before the session is hard-deleted. Uses ExecuteUpdateAsync for bulk operation.
    /// Same pattern as AttendanceRepo.NullifySessionIdOnRecordsForSessionAsync.
    /// </summary>
    Task NullifySessionIdOnPaymentRecordsAsync(long sessionId);

    /// <summary>
    /// Severs a student's payment records during permanent purge: nullifies TeacherStudentId on the
    /// audit records that must survive (transactions, departures, transfers, event obligations —
    /// denormalized name/code fields preserved), DELETEs the student's PaymentPeriods (billing
    /// obligations are meaningless once the student is gone and would otherwise orphan into
    /// dashboard aggregates), and deletes the StudentPaymentCounter.
    /// Same pattern as AttendanceRepo.NullifyStudentReferencesOnRecordsAsync.
    /// </summary>
    Task NullifyStudentReferencesOnPaymentRecordsAsync(long teacherStudentId);

    /// <summary>
    /// Recalculates the consecutive unpaid count from payment period records.
    /// BR-PAY-006: Based on sequential unpaid periods with no paid period in between.
    /// Called after payment edits/deletions where simple increment/decrement won't work.
    /// </summary>
    Task<int> RecalculateConsecutiveUnpaidAsync(long teacherId, long teacherStudentId);

    /// <summary>
    /// One-time cleanup support: every CURRENTLY-assigned student (across all teachers) that still
    /// carries an UNPAID/PARTIAL PaymentPeriod under a session OTHER than their current one — arrears
    /// stranded in an old session by the pre-carry-over reassign. Soft-deleted students are excluded by
    /// the global query filter; students with no current session are excluded by the SessionId != null
    /// predicate. See <see cref="StrandedStudentRow"/>.
    /// </summary>
    Task<IReadOnlyList<StrandedStudentRow>> GetStudentsWithStrandedUnpaidPeriodsAsync();

    /// <summary>Counts the (non-deleted) payment events owned by a teacher (free-tier quota).</summary>
    Task<int> CountEventsByTeacherAsync(long teacherId);
    Task<DateTime?> GetLastWalletResetAtAsync(long teacherId, long assistantId);
    // ══════════════════════════════════════════════
    // ATTENDANCE SCREEN PAYMENT ENRICHMENT (ShowPaymentInfoOnAttendanceScreen)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Batched, per-student payment/debt snapshot for the Take/Edit Attendance list's optional
    /// payment enrichment — bounded to the caller's page of <paramref name="teacherStudentIds"/>,
    /// never the full roster. Reuses the exact same arrears rules as the Unpaid Students Overview
    /// (<see cref="UnpaidStudentRow"/>) per CLAUDE.md §7.4.
    /// </summary>
    Task<Dictionary<long, AttendanceScreenPaymentInfoRow>> GetPaymentInfoForAttendanceBatchAsync(
        long teacherId, IReadOnlyCollection<long> teacherStudentIds,
        DateTime throughMonthEnd, DateTime lastMonthStart, DateTime lastMonthEnd);

}