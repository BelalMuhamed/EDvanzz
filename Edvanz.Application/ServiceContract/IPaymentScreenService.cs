using System;
using System.Threading.Tasks;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Payment;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Screen-oriented (BFF-style) payment endpoints backing the frontend payment.json spec
/// (api/v1/*). Additive and reuse-first: this service composes existing IPaymentRepo named
/// methods and (for money movement, Phase 2) IPaymentService — it never re-implements
/// payment mutation logic. <c>teacherId</c> is always supplied by the controller from the JWT.
/// </summary>
public interface IPaymentScreenService
{
    /// <summary>
    /// Screen: SessionPaymentCollectedByMonth. Paginated ledger of collected payments for the
    /// selected month. DASH-1: <paramref name="month"/> accepts EITHER the unified <c>"YYYY-MM"</c>
    /// string (same value <c>tracking</c>/<c>students</c> take) OR the legacy integer month string
    /// (1-12) paired with <paramref name="year"/>; both omitted → the teacher's current local
    /// (Africa/Cairo) month/year. Reuses <c>IPaymentRepo.GetTransactionsByDateRangePagedAsync</c>.
    /// Returns 422 for an invalid month/year.
    ///
    /// <para>DATE-RANGE (additive): when BOTH <paramref name="from"/> and <paramref name="to"/> are
    /// supplied they take precedence over <paramref name="month"/>/<paramref name="year"/> — the
    /// ledger is scoped to the inclusive <c>[from,to]</c> day range instead of a calendar month, and
    /// <c>FromDate</c>/<c>ToDate</c> are echoed on the response. When they are null the behaviour is
    /// unchanged (fully backward-compatible with the month path).</para>
    ///
    /// <para>SEARCH (additive): <paramref name="search"/> filters both the collection rows and the
    /// negative refund/edit lines by student name/code (case-insensitive substring); null/blank →
    /// no filter.</para>
    ///
    /// <para>ORDERING (collector-scoped path): when <paramref name="collectedByUserId"/> is set the
    /// whole ledger is ordered by calendar day (newest first) and, within a day, money-OUT lines
    /// (refunds/withdrawals) before collections — then paginated in memory so the ordering holds
    /// across pages. The response also carries <c>DailyNets</c> (per-day totals for the whole scope).</para>
    ///
    /// <para><paramref name="includeAdjustments"/> (additive; default <c>true</c>): when <c>false</c>
    /// the negative refund/withdrawal lines are omitted entirely ("collections only" view), so the list
    /// and its pagination cover collections alone. No effect on the amount tiers (already collections-only).</para>
    ///
    /// <para><paramref name="exactRange"/> (drawer scope): <c>true</c> ⇒ <paramref name="from"/>/
    /// <paramref name="to"/> are precise instants bounding <c>[from, to)</c>; <c>false</c> ⇒ an
    /// inclusive whole-day range. The caller (controller) decides from the RAW query text — a
    /// midnight-to-midnight exact window parses to the same <see cref="DateTime"/>s as a date-only
    /// one, so the values alone cannot carry the intent. <c>null</c> falls back to inferring from
    /// <c>TimeOfDay</c> (legacy behaviour, kept for callers without access to the raw text).</para>
    /// </summary>
    Task<Result<CollectionsByMonthResponse>> GetCollectionsByMonthAsync(
        long teacherId, string? month, int? year, int page, int limit,
        long? collectedByUserId = null,
        DateTime? from = null, DateTime? to = null,
        string? search = null,
        bool includeAdjustments = true,
        bool? exactRange = null);

    /// <summary>
    /// Screen: Collections date-filtered SUMMARY. Period overview for the payment/collections
    /// screens: money + activity + departures + per-collector honour the exact <c>[from,to]</c>
    /// range; the paid/partial/prorated/unpaid student counts are anchored to <paramref name="asOfMonth"/>
    /// (defaults to the month of <c>to</c>) because payment status is defined per calendar month.
    /// Composed from existing repo aggregates. Both dates omitted → the teacher's current local month.
    /// 422 on a malformed <paramref name="asOfMonth"/>.
    /// </summary>
    /// <param name="collectedByUserId">Optional: scope the money/activity/departure figures to ONE
    /// collector's own ledger (the drill-in screens' day strip). Null → account-wide (unchanged).
    /// The per-month status buckets are always account-wide.</param>
    /// <param name="exactRange">Mirrors the collections-ledger contract: <c>true</c> ⇒ treat
    /// <paramref name="from"/>/<paramref name="to"/> as PRECISE INSTANTS bounding <c>[from, to)</c>
    /// (the wallet's "in drawer now" window); <c>false</c>/null ⇒ inclusive whole days, byte-identical
    /// to the original behaviour. The caller derives this from the RAW query text — never from the
    /// parsed value's TimeOfDay, which cannot distinguish a midnight-to-midnight exact window from a
    /// date-only day filter. Without it this summary widened both bounds to whole days while the rows
    /// endpoint honoured the instants, so the day-insight cards disagreed with the list they sit above.</param>
    Task<Result<CollectionsSummaryResponse>> GetCollectionsSummaryAsync(
        long teacherId, DateTime? from, DateTime? to, string? asOfMonth, long? sessionId = null,
        long? collectedByUserId = null, bool? exactRange = null,
        string? search = null, bool includeAdjustments = true);

    /// <summary>
    /// Withdrawal/reset history for one assistant's wallet (newest first) — the record of every
    /// cash hand-over the teacher took from that assistant. Reuses
    /// <c>IPaymentRepo.GetWalletResetLogsAsync</c>.
    /// </summary>
    Task<Result<List<WalletResetLogDto>>> GetWalletWithdrawalHistoryAsync(
        long teacherId, long assistantId);

    /// <summary>
    /// Screen: AssistantWallet. Wallet card + paginated recent collections for one assistant.
    /// Reuses <c>GetAssistantWalletAsync</c> (tenant-scoped → 404 if not this teacher's) and
    /// <c>GetCollectorTransactionsInRangeAsync</c> / <c>GetCollectorRefundsInRangeAsync</c>.
    /// BUGFIX (2026-08-01): Collections is scoped SINCE THE ASSISTANT'S LAST WALLET HANDOVER
    /// (reset or withdraw -- <c>GetLastWalletResetAtAsync</c>), not the current calendar month.
    /// The old month-based scope went empty on every month rollover even with a real balance held,
    /// and drifted on local/UTC boundaries near midnight. The new window reconciles by
    /// construction: TotalCashCollected − TotalRefunded == WalletBalance.
    /// When <paramref name="restrictToAssistantUserId"/> is supplied (assistant caller) the
    /// requested <paramref name="assistantId"/> is IGNORED and the caller's OWN wallet (resolved by
    /// their user id) is returned — an assistant can only open their own wallet, never a peer's.
    /// TODO(assistant-dashboard): interim own-scoping; the dedicated assistant dashboard is to be
    /// built end-to-end by frontend + backend.
    /// <para>B2: <paramref name="search"/> (optional) filters the returned
    /// <c>collections.items</c> by student name OR studentCode (case-insensitive). The wallet
    /// totals stay authoritative (unfiltered); only the listed rows are narrowed.</para>
    /// </summary>
    Task<Result<AssistantWalletScreenResponse>> GetAssistantWalletScreenAsync(
        long teacherId, long assistantId, int page, int limit,
        long? restrictToAssistantUserId = null, string? search = null);

    /// <summary>
    /// Screen: CollectPayment. Searchable/filterable (all|assigned|unassigned) paginated
    /// student list with payment status + per-tab counts. Reuses <c>GetCollectStudentsPagedAsync</c>.
    /// Returns 422 for an invalid filter.
    /// <para><paramref name="sessionId"/> (optional) scopes the list to that session PLUS its linked
    /// sessions (mirroring the take-attendance roster) so a session-launched collect shows only the
    /// relevant students. When null the list is teacher-wide (existing behavior, unchanged).</para>
    /// </summary>
    Task<Result<CollectStudentsResponse>> GetCollectStudentsAsync(
        long teacherId, string? filter, string? search, int page, int limit, long? sessionId = null);

    /// <summary>
    /// Screen: PaymentTracking "View" cards. Paginated students filtered by status
    /// (paid|prorated|unpaid|partial) for a month (YYYY-MM), with per-student month amounts and
    /// group totals. Reuses <c>GetStudentsByPaymentStatusPagedAsync</c>. 422 on bad month/status.
    ///
    /// <para>B1: <paramref name="status"/> is OPTIONAL — when omitted, ALL of the (session-scoped)
    /// roster is returned with each student carrying its OWN status. <paramref name="sessionId"/>
    /// (optional) restricts to one session's assigned students; <paramref name="search"/> (optional)
    /// filters by student name OR studentCode (case-insensitive).</para>
    /// </summary>
    Task<Result<StudentsByStatusResponse>> GetStudentsByStatusAsync(
        long teacherId, string? month, string? status, int page, int limit,
        long? sessionId = null, string? search = null);

    /// <summary>
    /// Screen: SessionPaymentCollectedByYear. Per-student month-by-month collection matrix for a
    /// year, paginated over students. DASH-1: the year is derived from EITHER the unified
    /// <c>"YYYY-MM"</c> <paramref name="month"/> selector (its year component) OR the legacy
    /// <paramref name="year"/> integer; both omitted → the teacher's current local (Africa/Cairo)
    /// year. Reuses <c>GetYearlyCollectionsPagedAsync</c>. 422 on bad month/year.
    /// </summary>
    Task<Result<YearlyCollectionsResponse>> GetYearlyCollectionsAsync(
        long teacherId, string? month, int? year, int page, int limit);

    /// <summary>
    /// Screen: CollectPaymentSession. Resolves a student by QR/code/name → student + amount owed
    /// + paid state. Reuses <c>ResolveCollectLookupAsync</c>. 422 when no key given; 404 when unmatched.
    /// <para>Optional <paramref name="month"/> (<c>YYYY-MM</c>): amounts/breakdown are the arrears
    /// THROUGH that month (what a month-scoped card displayed) instead of through the current
    /// month. 422 when malformed.</para>
    /// </summary>
    Task<Result<CollectLookupResponse>> ResolveLookupAsync(
        long teacherId, string? qr, string? code, string? name, string? month = null);

    /// <summary>
    /// Screen: PaymentTracking. Month aggregate (summary revenue, status breakdown, collected
    /// by assistant, collected by sessions) for the given YYYY-MM. Composed entirely from
    /// existing dashboard/collector/status repo methods. 422 on bad month.
    /// </summary>
    Task<Result<TrackingResponse>> GetTrackingAsync(long teacherId, string? month);

    /// <summary>
    /// Screen: CollectPayment "Mark N as Paid" (MONEY). Marks each student paid by routing through
    /// the existing <c>CollectPaymentAsync</c> (clears their earliest unpaid period). Idempotent via
    /// <paramref name="idempotencyKey"/>. 422 when no students selected.
    /// <para>Optional <paramref name="month"/> (<c>YYYY-MM</c>): each student is charged their
    /// arrears THROUGH that month only (what the month-scoped card displayed) instead of through
    /// the current month. 422 when malformed.</para>
    /// </summary>
    /// <para>Issue 1 (2026-09-02): a student already collected-from today is returned as
    /// <c>needs_confirmation</c> (with attribution) rather than a hard fail; the client re-submits with
    /// <paramref name="duplicateConfirmed"/> = true to proceed.</para>
    Task<Result<MarkPaidResponse>> MarkPaidAsync(
        long teacherId, long actingUserId, List<long> studentIds, string? idempotencyKey,
        string? month = null, bool duplicateConfirmed = false);

    /// <summary>
    /// Screen: CollectPaymentSession "Submit N students" (MONEY). Collects each {studentId, amount}
    /// via <c>CollectPaymentAsync</c>. Idempotent via <paramref name="idempotencyKey"/>. 409 on empty batch.
    /// <para>Feature C: each transaction stores its item's note (<c>SubmitCollectionItem.Note</c>,
    /// how the mobile app sends it) falling back to the batch-level <paramref name="note"/>. A note
    /// (either level) is REQUIRED (else 400 <c>CollectNoteRequired</c>) for any item whose amount is
    /// a partial/custom value — not a whole-month multiple of that student's monthly rate.</para>
    /// </summary>
    Task<Result<SubmitCollectionResponse>> SubmitCollectionAsync(
        long teacherId, long actingUserId, string? month, long? classSessionId,
        List<SubmitCollectionItem> students, string? note, string? idempotencyKey);

    /// <summary>
    /// Screen: AssistantWallet "Withdraw" (MONEY, tutor-only). Partial wallet withdrawal delegating
    /// to <c>IPaymentService.WithdrawFromWalletAsync</c>. Idempotent via <paramref name="idempotencyKey"/>.
    /// 404 unknown wallet, 409 insufficient balance / concurrent update, 422 non-positive amount.
    /// </summary>
    Task<Result<WalletWithdrawResponse>> WithdrawAsync(
        long teacherId, long assistantId, decimal? amount, long actingUserId, string? idempotencyKey);

    /// <summary>
    /// FORGIVE (waive) part of a student's outstanding balance (MONEY, TEACHER-ONLY — assistants are
    /// blocked at the API gate). Forgiving is NOT cash: no wallet change, no transaction, no collector
    /// attribution — it only reduces what the student owes, applied oldest-unpaid-month first (cascade,
    /// same month-scoping as §7.4) by incrementing each period's <c>ForgivenAmount</c>. Records a
    /// reversible <c>PaymentForgiveness</c> audit row. 404 unknown student; 422 when
    /// <paramref name="amount"/> ≤ 0 (<c>ForgiveAmountInvalid</c>) or exceeds the outstanding through
    /// the current teacher-local month (<c>ForgiveAmountExceedsOutstanding</c>). Returns the created
    /// forgiveness + the updated student payment summary row.
    /// </summary>
    Task<Result<ForgiveBalanceResponse>> ForgiveBalanceAsync(
        long teacherId, long actingUserId, long teacherStudentId, decimal amount, string? note);

    /// <summary>
    /// REVERSE a forgiveness (TEACHER-ONLY): restores the exact per-period balance it waived and audits
    /// the reversal (who/when/note). 404 <c>ForgivenessNotFound</c>; 409 <c>ForgivenessAlreadyReversed</c>.
    /// Returns the reversed forgiveness + the updated student payment summary row.
    /// </summary>
    Task<Result<ForgiveBalanceResponse>> ReverseForgivenessAsync(
        long teacherId, long actingUserId, long forgivenessId, string? note);

    /// <summary>
    /// Screen: CollectPayment — set/clear a student's JOINING-MONTH proration amount (REQ-PAY-021/022).
    /// Allowed for the teacher AND assistants (the acting user is recorded). Delegates to
    /// <c>IPaymentService.SetStudentProrationAmountAsync</c>: <paramref name="amount"/> is snapped to the
    /// nearest 5 and clamped to [0, full month]; <c>null</c> clears the sticky override and reverts to the
    /// method's auto suggestion. Guard: only a new enrollment's still-unpaid anchor month. Returns the
    /// updated period so the popup refreshes in place. 404 unknown student; 400 not assigned / no anchor;
    /// 422 amount out of range or cash already collected on the joining month.
    /// </summary>
    Task<Result<ProrationUpdateResultDto>> SetProrationAmountAsync(
        long teacherId, long actingUserId, long teacherStudentId, decimal? amount);
}
