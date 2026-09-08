using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Payment;
using Edvanz.Domain.Enums;

namespace Edvanz.Application.ServiceContract;

/// <summary>
/// Defines the contract for Payment Module operations (Module 4).
/// Covers: payment collection, editing, deletion, unpaid overview,
/// custom amounts, assistant wallets, dashboard, departure, transfer,
/// offline sync, and student/parent views.
///
/// All methods return Result&lt;T&gt; for consistent error handling.
/// All methods are async per system architecture requirements.
///
/// TRANSACTION SAFETY:
/// All write operations use the ownsTransaction pattern:
///   bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
/// This makes them safe for both standalone calls and nested calls.
/// </summary>
public interface IPaymentService
{
    // ══════════════════════════════════════════════
    // PAYMENT COLLECTION (REQ-PAY-001 through 010)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Collects a payment from a student.
    /// REQ-PAY-001/002: All four collection methods produce a unified record.
    /// REQ-PAY-015/016: Amount pre-filled from session or custom amount.
    /// REQ-PAY-018/019: Automatically assigned to earliest unpaid period (BR-PAY-001).
    /// REQ-PAY-020/026: Duplicate detection and already-paid warning.
    /// REQ-PAY-021/025: Pro-rating for first period (BR-PAY-005).
    /// </summary>
    Task<Result<CollectPaymentResultDto>> CollectPaymentAsync(CollectPaymentDto dto);

    /// <summary>
    /// Gets the payment status for a student on the collection screen.
    /// REQ-PAY-NFR-006: Displayed immediately after student identification.
    /// </summary>
    Task<Result<PaymentStatusDto>> GetStudentPaymentStatusAsync(
        long teacherId, long teacherStudentId);

    /// <summary>
    /// Checks for duplicate payments (same day or already paid).
    /// REQ-PAY-020/026: Pre-collection check.
    /// </summary>
    Task<Result<DuplicateCheckResultDto>> CheckDuplicatePaymentAsync(
        long teacherId, long teacherStudentId);

    // ══════════════════════════════════════════════
    // PAYMENT EDIT/DELETE (REQ-PAY-027, BR-PAY-002)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Edits an existing payment transaction.
    /// BR-PAY-002: Only the tutor role may edit.
    /// </summary>
    Task<Result<PaymentTransactionDto>> EditPaymentAsync(EditPaymentDto dto);

    /// <summary>
    /// Soft-deletes a payment transaction.
    /// BR-PAY-002: Only the tutor role may delete.
    /// </summary>
    Task<Result<bool>> DeletePaymentAsync(long teacherId, long transactionId, long deletedByUserId);

    /// <summary>
    /// Gets the edit history for a payment transaction.
    /// </summary>
    Task<Result<List<PaymentEditLogDto>>> GetPaymentEditHistoryAsync(
        long teacherId, long transactionId);

    // ══════════════════════════════════════════════
    // STUDENT PAYMENT HISTORY (REQ-PAY-052)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets the complete payment history for a student.
    /// REQ-PAY-052: Single Student Payment History.
    /// REQ-PAY-092: Unified timeline across all sessions.
    /// </summary>
    Task<Result<PaginatedResponse<PaymentHistoryDto>>> GetStudentPaymentHistoryAsync(
        long teacherId, long teacherStudentId,
        DateTime? startDate, DateTime? endDate,
        int page, int pageSize);

    // ══════════════════════════════════════════════
    // CUSTOM AMOUNT (REQ-PAY-016, BR-PAY-003)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets the custom payment amount for a student (null = use session default).
    /// </summary>
    Task<Result<decimal?>> GetCustomAmountAsync(long teacherId, long teacherStudentId);

    /// <summary>
    /// Sets a custom payment amount for a student.
    /// REQ-PAY-016: Overrides session default for all future collections.
    /// BR-PAY-003: Not affected by session amount changes.
    /// </summary>
    Task<Result<bool>> SetCustomAmountAsync(SetCustomAmountDto dto);

    // ══════════════════════════════════════════════
    // UNPAID OVERVIEW (REQ-PAY-028 through 033)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets the paginated unpaid students overview.
    /// REQ-PAY-031/032/033: Filterable with search.
    /// </summary>
    Task<Result<PaginatedResponse<List<UnpaidStudentDto>>>> GetUnpaidStudentsAsync(
        long teacherId, UnpaidStudentsFilterDto filter);

    /// <summary>
    /// Gets the unpaid student count badge for a session.
    /// REQ-PAY-028: Notification badge on session view.
    /// </summary>
    Task<Result<int>> GetUnpaidCountBySessionAsync(long teacherId, long sessionId);

    /// <summary>
    /// Gets the Paid / Pro-rated / Unpaid students overview counts.
    /// REQ-PAY-031: Overview counts alongside the filterable unpaid list.
    /// </summary>
    Task<Result<StudentPaymentStatusCountsDto>> GetStudentPaymentStatusCountsAsync(long teacherId);

    // ══════════════════════════════════════════════
    // COLLECTOR VIEW (REQ-PAY-013/014)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets the User Collection View.
    /// REQ-PAY-013: Per-user collection breakdown.
    /// REQ-PAY-014: The full view (all collectors) is tutor-only; when
    /// <paramref name="scopeToCollectorUserId"/> is supplied (assistant caller) the result is
    /// filtered to that single collector's own records.
    /// TODO(assistant-dashboard): interim own-scoping; the dedicated assistant collection view
    /// is to be built end-to-end by frontend + backend.
    /// </summary>
    Task<Result<List<CollectorSummaryDto>>> GetCollectorSummaryAsync(
        long teacherId, DateTime? startDate, DateTime? endDate, long? scopeToCollectorUserId = null);

    // ══════════════════════════════════════════════
    // WALLET MANAGEMENT (REQ-PAY-034 through 038)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets all assistant wallets for a teacher, plus the combined total currently
    /// held across all assistants.
    /// REQ-PAY-035: View current wallet balance of each assistant.
    /// When <paramref name="scopeToAssistantUserId"/> is supplied (assistant caller) the result
    /// contains ONLY that assistant's own wallet (never peers'), and the combined total equals
    /// their own balance.
    /// TODO(assistant-dashboard): interim own-scoping; proper assistant dashboard TBD (frontend + backend).
    /// </summary>
    Task<Result<AssistantWalletsSummaryDto>> GetAllWalletsAsync(long teacherId, long? scopeToAssistantUserId = null);

    /// <summary>
    /// Gets detailed wallet for a specific assistant.
    /// REQ-PAY-035: Itemized collection list.
    /// When <paramref name="restrictToAssistantUserId"/> is supplied (assistant caller) the
    /// requested <paramref name="assistantId"/> is IGNORED and the caller's OWN wallet (resolved
    /// by their user id) is returned — an assistant can never read a peer's wallet.
    /// TODO(assistant-dashboard): interim own-scoping; proper assistant dashboard TBD (frontend + backend).
    /// </summary>
    Task<Result<AssistantWalletDto>> GetWalletDetailAsync(
        long teacherId, long assistantId, long? restrictToAssistantUserId = null);

    /// <summary>
    /// Resets an assistant's wallet to zero.
    /// REQ-PAY-036: Tutor confirms cash handover.
    /// REQ-PAY-037: Permanent ledger event.
    /// REQ-PAY-038: Fresh accumulation begins from zero.
    /// </summary>
    Task<Result<WalletResetLogDto>> ResetWalletAsync(WalletResetDto dto);

    /// <summary>
    /// Partial assistant-wallet withdrawal (api/v1 screens) — the tutor takes an amount of
    /// collected cash from an assistant. Generalizes <see cref="ResetWalletAsync"/> to a partial
    /// amount (defaults to the full balance): validates funds (409 on insufficient), decrements
    /// the balance, logs a WalletResetLog ledger entry, and is RowVersion-concurrency-safe
    /// (clean 409 on a concurrent update). Tutor-only at the controller. No schema change.
    /// </summary>
    Task<Result<WalletWithdrawalResult>> WithdrawFromWalletAsync(
        long teacherId, long assistantId, decimal? amount, long withdrawnByUserId);

    // ══════════════════════════════════════════════
    // DASHBOARD (REQ-PAY-039 through 044)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets the Payment Overview Dashboard.
    /// REQ-PAY-039/040/041/042: Expected, collected, remaining revenue.
    /// REQ-PAY-043: Filterable by session, group, payment type, date range.
    /// When <paramref name="scopeToCollectorUserId"/> is supplied (assistant caller) the result is
    /// scoped to that assistant's OWN collections: <c>CollectedRevenue</c> = their own collected,
    /// while the teacher-wide figures (<c>ExpectedRevenue</c>/<c>RemainingRevenue</c>/per-session)
    /// come back <c>null</c> so the client shows an assistant-scoped view without confusing null and 0.
    /// TODO(assistant-dashboard): interim own-scoping; a real assistant dashboard (its own expected/
    /// target semantics) is to be designed and built end-to-end by frontend + backend.
    /// </summary>
    Task<Result<PaymentDashboardDto>> GetDashboardAsync(
        long teacherId, PaymentDashboardFilterDto filter, long? scopeToCollectorUserId = null);

    /// <summary>
    /// Gets the "Collected by Sessions" card data: one row per currently
    /// active session, with collected amount, paid/total student counts,
    /// and progress percentage.
    /// REQ-PAY-043: Per-session collection progress while the session is active.
    /// </summary>
    Task<Result<List<SessionCollectionSummaryDto>>> GetSessionsCollectionSummaryAsync(long teacherId);

    // ══════════════════════════════════════════════
    // DEPARTURE (REQ-PAY-066 through 075)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Calculates and returns the departure summary before confirmation.
    /// REQ-PAY-067/068: Pro-rated obligation calculation.
    /// REQ-PAY-072: Departure summary screen.
    /// </summary>
    Task<Result<DepartureSummaryDto>> GetDepartureSummaryAsync(
        long teacherId, long teacherStudentId);

    /// <summary>
    /// Confirms a student departure.
    /// REQ-PAY-073: Unassign, record departure event, handle refund/charge.
    /// REQ-PAY-075: Optional tutor override.
    /// </summary>
    Task<Result<StudentDepartureDto>> ConfirmDepartureAsync(ConfirmDepartureDto dto);

    /// <summary>
    /// Teacher-wide paged list of departed students (search by name/code), newest first, with the
    /// per-day totals that drive the list's day-separator headers.
    /// </summary>
    /// <param name="from">Inclusive lower bound on DepartedAt (UTC instant). Null = unbounded.</param>
    /// <param name="to">EXCLUSIVE upper bound on DepartedAt (UTC instant). Null = unbounded.</param>
    Task<Result<DeparturesResponse>> GetDeparturesAsync(
        long teacherId, string? search, int page, int limit,
        DateTime? from = null, DateTime? to = null);

    // ══════════════════════════════════════════════
    // SESSION TRANSFER (REQ-PAY-085 through 092)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Calculates and returns the pre-transfer financial summary.
    /// REQ-PAY-088: Pre-transfer summary shown before confirmation.
    /// </summary>
    Task<Result<TransferSummaryDto>> GetTransferSummaryAsync(
        long teacherId, long teacherStudentId,
        long sourceSessionId, long destinationSessionId);

    /// <summary>
    /// Confirms a session transfer with payment data continuity.
    /// REQ-PAY-085/086/089/090/091: Full payment history preserved.
    /// </summary>
    Task<Result<SessionTransferEventDto>> ConfirmTransferAsync(ConfirmTransferDto dto);

    // ══════════════════════════════════════════════
    // OFFLINE SYNC (REQ-PAY-076 through 084)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Syncs offline-collected payment records to the server.
    /// REQ-PAY-080/081: Background sync on reconnection.
    /// REQ-PAY-082: Conflict detection and resolution.
    /// </summary>
    Task<Result<PaymentSyncResultDto>> SyncOfflinePaymentsAsync(OfflinePaymentSyncRequestDto dto);

    // ══════════════════════════════════════════════
    // STUDENT/PARENT VIEW
    // ══════════════════════════════════════════════

    /// <summary>
    /// Gets payment data for a student/parent viewing their own payments.
    /// Gated by TeacherConfiguration visibility settings.
    /// </summary>
    Task<Result<StudentPaymentViewDto>> GetStudentPaymentViewAsync(
        long teacherId, long teacherStudentId);

    /// <summary>
    /// Builds the full student/parent "Payment" tracking screen in one call: header, the
    /// single Upcoming month, the Paid section, and the Overdue section — pivoted on the
    /// teacher's local current month. Gated by the caller-specific visibility flag
    /// (<see cref="Domain.Enums.PaymentViewerType"/> → StudentVisibilityPayment /
    /// ParentVisibilityPayment); returns 403 when disabled (fail-closed on missing config).
    /// </summary>
    Task<Result<StudentPaymentTrackingDto>> GetStudentPaymentTrackingAsync(
        long teacherId, long teacherStudentId, PaymentViewerType viewer);

    // ══════════════════════════════════════════════
    // INTEGRATION HOOKS (called by other modules)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Called by SessionService when a student is assigned to a session.
    /// Generates initial payment periods and creates the payment counter.
    /// </summary>
    Task<Result<bool>> OnStudentAssignedToSessionAsync(
        long teacherId, long teacherStudentId, long sessionId,
        string sessionName, DateTime assignedAt);

    /// <summary>
    /// Called by SessionService when a session's <c>SessionAmount</c> changes. Re-prices the
    /// session's still-owed periods to <paramref name="newAmount"/>, EXCEPT students who carry their
    /// own custom price (BR-PAY-003).
    /// <para>Window: EVERY still-owed Monthly bill — arrears, the current month and future months —
    /// the same scope a per-student price change uses; PerSession bills only from next month onward,
    /// since a class already delivered was delivered at the old price. Never rewritten: paid periods,
    /// carried/moved debt, hand-set joining months (<c>IsProrationManual</c>), and past/current bills
    /// that already carry collected or forgiven money.</para>
    /// <para>Runs on the caller's transaction (does not open/commit its own). Returns what it did so
    /// the app can report the recalculation instead of leaving it silent.</para>
    /// </summary>
    Task<Result<SessionRepriceSummary>> OnSessionAmountChangedAsync(
        long teacherId, long sessionId, decimal newAmount);

    /// <summary>
    /// Retroactively reconciles EXISTING students' first month to the teacher's CURRENT proration config
    /// (req 3): prorates first months when proration is enabled, restores full price when disabled, and
    /// re-applies changed tiers/method. Anchored to each student's ENROLLMENT (earliest assignment) date
    /// (rev 2, 2026-09-03) — deterministic and idempotent. Only still-owed, never-collected, non-manual
    /// first months are touched. Returns WHAT IT DID (re-priced / kept counts) so the settings screen can
    /// report the recalculation instead of running silently. Runs on the caller's transaction (no own
    /// commit); call it from <c>TeacherService.SaveConfigurationAsync</c> after the config + tiers are
    /// saved, when the proration config actually changed.
    /// </summary>
    Task<ProrationReconcileSummary> ReconcileProrationForExistingStudentsAsync(long teacherId);

    /// <summary>
    /// Reconciles the teacher's existing obligations to a billing floor (<c>BillingStartDate</c>,
    /// §7.4b — set during onboarding so data entered in August bills from September):
    /// (1) TRIM — deletes every period dated before the floor that carries NO cash, is not
    /// manually priced and is not carried/moved debt (kept rows are reported, never touched);
    /// (2) BACKFILL — for currently-assigned students, (re)generates months/class dates the floor
    /// newly allows (floor moved earlier), via the same generation pipeline as assignment so
    /// pricing/sequencing never diverge; (3) RE-ANCHOR — when a student's enrollment first-month
    /// anchor was trimmed (or an earlier month appeared), the first surviving never-paid month
    /// becomes the anchor, re-priced per the current proration method with the floor-clamped join
    /// day, and each affected student's counter is fully recomputed. Idempotent. With
    /// <paramref name="dryRun"/> nothing is written — the summary reports what WOULD happen.
    /// Runs on the caller's transaction (does not open/commit its own).
    /// </summary>
    Task<BillingStartReconcileSummary> ReconcileBillingStartAsync(
        long teacherId, DateTime billingStart, bool dryRun);

    /// <summary>
    /// SUPER-ADMIN: sets (or previews, with <paramref name="dryRun"/>) a teacher's
    /// <c>BillingStartDate</c> from the support side — normalizes to first-of-month, stamps
    /// <c>BillingStartDateSetAt</c> (re-locking the teacher's one-time self-service change), clears
    /// the re-grant flag, and runs <see cref="ReconcileBillingStartAsync"/> in one transaction.
    /// Dry runs write NOTHING (config keeps its stored value).
    /// </summary>
    Task<Result<BillingStartAdminResult>> SetBillingStartForTeacherAsync(
        long teacherId, DateTime billingStart, bool dryRun);

    /// <summary>
    /// SUPER-ADMIN: re-grants the teacher ONE more self-service change of their
    /// <c>BillingStartDate</c> (sets <c>BillingStartDateChangeAllowed</c>; consumed by the next
    /// teacher-side change). Support flow for the yearly onboarding reset or a mistaken first set.
    /// </summary>
    Task<Result<bool>> AllowBillingStartChangeAsync(long teacherId);

    /// <summary>
    /// Computes the SYSTEM-SUGGESTED joining-month amount for a student in a session, per the teacher's
    /// chosen <see cref="Edvanz.Domain.Enums.ProrationMethod"/> (REQ-PAY-021/022 rev 2). Anchored to the
    /// student's ENROLLMENT (earliest assignment) date — attendance never prices the joining month.
    /// Recomputed from the join date + CURRENT settings on every call (never echoed from the stored
    /// period). Rounded to the nearest 5, clamped to [0, full month]. Pure read — never persists.
    /// Returns <c>Applicable == false</c> when there is nothing to prorate (proration off, no still-owed
    /// anchor month, transfer, or already paid). Feeds the collect-lookup enrichment + the per-student
    /// proration endpoint (no N+1 — one student per call).
    /// </summary>
    Task<ProrationSuggestionResult> ComputeProrationSuggestionAsync(
        long teacherId, long teacherStudentId, long sessionId);

    /// <summary>
    /// Sets (amount != null) or CLEARS (amount == null) a student's joining-month proration amount
    /// (REQ-PAY-021/022). Allowed for the teacher AND assistants — the acting user is recorded. The amount
    /// is snapped to the nearest 5 and must be in [0, full month]; setting marks the anchor
    /// <c>IsProrationManual</c> (sticky — auto re-proration/price-change never overwrites it) and ALWAYS
    /// writes a proration-decision <see cref="Edvanz.Domain.Entities.PaymentEditLog"/> (actor · suggested
    /// · set — rev 2: unconditional, so "set by hand · by whom · when" is always attributable). Clearing
    /// reverts to the method's auto suggestion. Guard: only the still-unpaid anchor month of a new
    /// enrollment. Reuses the reprice + counter-delta + consecutive-unpaid path.
    /// </summary>
    Task<Result<ProrationUpdateResultDto>> SetStudentProrationAmountAsync(
        long teacherId, long actingUserId, long teacherStudentId, decimal? amount);

    /// <summary>
    /// Called by SessionService after a session's date window changed. Billing periods are only
    /// generated ONCE — when a student is assigned — and only as far as the session's end date AT
    /// THAT MOMENT. Extending the end date afterwards therefore left the already-assigned students
    /// with no obligation for the newly-covered months: the session silently vanished from the
    /// payment screens for those months and its students read as "paid" with nothing due.
    ///
    /// This BACKFILLS the gap: for every currently assigned student of a <c>Monthly</c> session it
    /// creates the missing monthly periods, from the month after their latest existing period through
    /// the session's end month, at their effective price (custom amount, else the session amount).
    /// Idempotent and additive — existing periods are never touched, nothing is created past the end
    /// date, and a student who is already covered gets nothing. Safe to call on every session update,
    /// so re-saving a session also REPAIRS historical gaps.
    /// Runs on the caller's transaction (does not open/commit its own).
    /// </summary>
    Task<Result<bool>> BackfillSessionPeriodsThroughEndDateAsync(long teacherId, long sessionId);

    /// <summary>
    /// Called by TeacherStudentService when a student is MOVED between two sessions (A → B) — i.e. both
    /// the previous and the new session are non-null. Carries the student's billing over in one
    /// transaction (§7.4): PAID months stay in A as history; UNPAID months still owed (past arrears +
    /// the current month) MOVE to B keeping the original amount, tagged MovedFrom*/IsCarriedForward; a
    /// PARTIALLY-paid month is settled in A to what was paid and only its REMAINING balance is billed in
    /// B; UNPAID FUTURE months in A are CANCELLED and B generates its own future months (never
    /// re-billing a moved or already-paid month). A <c>SessionTransferEvent</c> audit row is written and
    /// the student's counter is recomputed from the resulting periods. PerSession sessions fall back to
    /// the unassign+assign behavior. Runs on the caller's transaction when one is active, else owns its own.
    /// </summary>
    Task<Result<bool>> OnStudentMovedBetweenSessionsAsync(
        long teacherId, long teacherStudentId,
        long fromSessionId, string fromSessionName,
        long toSessionId, string toSessionName,
        DateTime movedAt);

    /// <summary>
    /// Called by SessionService when a student is unassigned from a session.
    /// Preserves complete payment history.
    /// </summary>
    Task<Result<bool>> OnStudentUnassignedFromSessionAsync(
        long teacherId, long teacherStudentId);

    /// <summary>
    /// Called by SessionService before a session is hard-deleted.
    /// Nullifies SessionId on payment records — denormalized data preserved.
    /// </summary>
    Task<Result<bool>> OnSessionDeletingAsync(long teacherId, long sessionId);

    /// <summary>
    /// Called by TeacherStudentService during permanent student purge.
    /// Nullifies TeacherStudentId on all payment records.
    /// </summary>
    Task<Result<bool>> OnStudentPermanentlyDeletedAsync(long teacherStudentId);

    /// <summary>
    /// ONE-TIME OPS CLEANUP (SuperAdmin). Finds every currently-assigned student whose UNPAID/PARTIAL
    /// billing was STRANDED under a session other than their current one (by the pre-carry-over reassign)
    /// and applies the same carry-over as a live move — stranded unpaid-due → current session, stranded
    /// future cancelled, partials settled (paid part kept, remainder re-billed), paid kept — recomputing
    /// each student's counter. Does NOT regenerate the current session's schedule (it already exists; an
    /// overlap guard prevents double-billing a month the current session already bills). Monthly only —
    /// PerSession students are reported and skipped. <paramref name="dryRun"/>=true writes NOTHING and
    /// returns exactly what WOULD change; false applies it (each student in its own transaction).
    /// </summary>
    Task<Result<MovedStudentsReconcileReport>> ReconcileMovedStudentsAsync(bool dryRun);

    /// <summary>
    /// ADMIN one-off (SuperAdmin). Backfills a PAID month for a student by moving the cash of a
    /// fully-paid ADVANCE month back onto an earlier, currently-non-existent month — used to correct a
    /// student who paid ahead but whose earlier month has no billing period. Inside one transaction:
    /// (1) creates the <paramref name="targetMonth"/> period (Monthly, same session/denormalized fields
    /// as the advance period, AmountDue = the student's monthly rate, AmountPaid = the advance month's
    /// AmountPaid, status Paid, sequence sorting before the earliest existing period); (2) moves the
    /// advance period's <c>PaymentTransactionAllocation</c>s to the new period; (3) un-settles the
    /// advance month (AmountPaid 0 / Unpaid); (4) repoints any transaction whose denormalized
    /// PaymentPeriodId was the advance period to the new one; (5) recomputes the student's counter. Total
    /// cash paid is invariant. Guards: NO period may exist at <paramref name="targetMonth"/>, and the
    /// <paramref name="fromAdvanceMonth"/> period must EXIST, be Monthly and be fully cash-paid.
    /// <paramref name="dryRun"/>=true (default) writes NOTHING and returns exactly what WOULD change.
    /// Months are "YYYY-MM".
    /// </summary>
    Task<Result<BackfillPaidMonthReport>> BackfillPaidMonthAsync(
        long teacherStudentId, string targetMonth, string fromAdvanceMonth, bool dryRun);

    /// <summary>
    /// ADMIN one-off (SuperAdmin). Recomputes an assistant wallet's <c>CurrentBalance</c> using the same
    /// reset-aware rule as the wallet-reversal fix, to repair a balance corrupted by pre-reset reversals
    /// (e.g. salma −2700 → 0). Anchors at the last FULL cash hand-over — the latest reset whose
    /// RECONSTRUCTED balance (collections collected by then, incl. later-deleted, minus refunds taken by
    /// then, minus cash handed over by then) is exactly zero; a partial withdrawal leaves it &gt; 0 and is
    /// skipped. Held cash = collections taken after the anchor − partial (departure) reversals of those
    /// collections − partial withdrawals after it. Cash collected before the hand-over — and its later
    /// refunds — is excluded. Reports old vs new balance and the components. <paramref name="dryRun"/>=true
    /// (default) writes NOTHING; false sets <c>CurrentBalance</c> only (TotalCollected/lifetime untouched).
    /// <paramref name="assistantId"/> is the Assistant.Id.
    /// </summary>
    Task<Result<RecomputeAssistantWalletReport>> RecomputeAssistantWalletAsync(long assistantId, bool dryRun);

    /// <summary>
    /// ADMIN one-off (SuperAdmin). Corrects a withdrawal (<c>WalletResetLog</c>) whose RECORDED
    /// amount differs from the cash physically handed over — e.g. a withdrawal logged before the
    /// 2026-08-24 performer-attribution change swept up refund cash the assistant had already paid
    /// out to a departing student. Applying sets <c>AmountReset</c> then re-runs the reset-aware
    /// balance recompute for the wallet's assistant. <paramref name="dryRun"/>=true (default)
    /// writes nothing and reports old vs new.
    /// </summary>
    Task<Result<AdjustWithdrawalReport>> AdjustWithdrawalAmountAsync(long walletResetLogId, decimal newAmount, bool dryRun);

    /// <summary>
    /// ADMIN one-off (SuperAdmin). Repairs students left with a DUPLICATE period ladder for the same
    /// session (root cause: a non-idempotent assign that regenerated a full parallel ladder, only
    /// skipping PAID months — now fixed in <c>OnStudentAssignedToSessionAsync</c>). For each group of
    /// periods sharing (TeacherStudentId, SessionId, month) it KEEPS one — a money/meaning period
    /// (paid/partial/forgiven/carried, or referenced by a transaction/allocation) if present, else the
    /// lowest-sequence empty one — and DELETES the other empty, UNREFERENCED Unpaid twins, then
    /// resyncs the student's counter. A twin holding cash or a reference is never deleted; it is
    /// reported as a conflict for manual review. <paramref name="teacherId"/> null = every teacher.
    /// <paramref name="dryRun"/>=true (default) writes NOTHING and reports exactly what WOULD change.
    /// </summary>
    Task<Result<DuplicatePeriodsReconcileReport>> ReconcileDuplicatePeriodsAsync(long? teacherId, bool dryRun);

    /// <summary>
    /// ADMIN one-off (SuperAdmin). Applies the session-delete money lifecycle to LEGACY orphaned periods
    /// left by the OLD session-delete (which only nulled <c>SessionId</c>, leaving unpaid periods — monthly
    /// OR per-session — to linger as inflated obligations, e.g. student 134A's four Aug @35 per-session
    /// rows). Per student with orphaned unpaid periods: VOIDs the future-unpaid orphans, collapses the
    /// unpaid arrears through the current month into ONE pending monthly carry-forward debt per month
    /// (which re-prices to the next session on reassignment), and KEEPS paid orphans as history — then
    /// resyncs the counter. <paramref name="teacherId"/> null = every teacher.
    /// <paramref name="dryRun"/>=true writes NOTHING and reports exactly what WOULD change.
    /// </summary>
    Task<Result<OrphanedPeriodsReconcileReport>> ReconcileOrphanedPeriodsAsync(long? teacherId, bool dryRun);

    /// <summary>
    /// ADMIN one-off (SuperAdmin). REMEDIATION for the never-paid FIRST-MONTH-MOVE proration WIPE (root
    /// cause fixed going-forward in <c>ApplyCarryOverPlanAsync</c> + the DB2a fold-in): a student moved /
    /// reassigned between sessions within their first month, before paying anything, had their genuine
    /// prorated first month re-priced to FULL price with its anchor flag dropped (prod: student 8990
    /// 300×0.3333 → 300). For each AFFECTED student — the earliest period is a carried / moved, never-paid,
    /// non-prorated monthly anchor, the student has zero paid periods, proration is enabled, and the
    /// first-attendance day lands in a discounted tier — the anchor is re-priced to
    /// round(sessionOrCustom × first-attendance fraction), its IsProRated / fraction / anchor flag restored,
    /// and its counter resynced. Non-qualifying candidates are reported (with a reason) and left untouched.
    /// <paramref name="teacherId"/> null = every teacher. <paramref name="dryRun"/>=true (default at the
    /// controller) writes NOTHING and reports exactly what WOULD change.
    /// </summary>
    Task<Result<CarriedAnchorReprorationReport>> ReprorateCarriedAnchorsAsync(long? teacherId, bool dryRun);

    /// <summary>
    /// Ensures an AssistantWallet record exists for the given assistant.
    /// Should be called when an assistant is created (from AssistantService)
    /// or as a safety check during payment collection.
    /// Creates the wallet if it doesn't already exist.
    /// </summary>
    /// <param name="teacherId">The owning teacher's ID.</param>
    /// <param name="assistantId">The Assistant entity's ID.</param>
    /// <param name="assistantUserId">The assistant's User.Id for wallet lookup.</param>
    Task<Result<bool>> EnsureAssistantWalletExistsAsync(
        long teacherId, long assistantId, long assistantUserId);
    /// <summary>
    /// Collects payments for multiple students in a single request ("Mark N students as Paid").
    /// Best-effort partial success: each student is processed independently via
    /// <see cref="CollectPaymentAsync"/> — the single source of truth for all payment
    /// business rules — and reported as Collected, NeedsConfirmation, or Failed.
    /// No explicit REQ-PAY covers batch collection (it is UI-derived); per-student behavior
    /// is governed by REQ-PAY-001/002/018/019/020/026 and BR-PAY-001 via the reused path.
    /// <paramref name="teacherId"/> and <paramref name="collectedByUserId"/> are supplied
    /// by the presentation layer from the JWT, never from the request body.
    /// </summary>
    Task<Result<BatchCollectResultDto>> BatchCollectPaymentAsync(
        BatchCollectPaymentDto dto, long teacherId, long collectedByUserId);

    /// <summary>
    /// Batch-edits multiple payment transactions in a single request (D2 — UI "Saved N changes").
    /// Best-effort partial success: each transaction is edited independently via
    /// <see cref="EditPaymentAsync"/> — the single source of truth for all edit business rules
    /// (PaymentEditLog, period/counter reversal, transaction ownership) — and reported as
    /// Succeeded or Failed. One item's failure never rolls back the others.
    /// BR-PAY-002: tutor-only; <paramref name="teacherId"/> and <paramref name="editedByUserId"/>
    /// are supplied by the presentation layer from the JWT, never from the request body.
    /// </summary>
    Task<Result<BatchEditResultDto>> BatchEditPaymentAsync(
        BatchEditPaymentDto dto, long teacherId, long editedByUserId);
    /// <summary>
    /// Batch-reverts multiple payment transactions in a single request (D1 — UI "Revert (N students)").
    /// A revert is expressed as an edit that zeroes the collected amount and marks the transaction
    /// Unpaid, applied independently per item via <see cref="EditPaymentAsync"/> — the single source
    /// of truth for PaymentEditLog, period reversal, counter reversal, ownership, and transaction
    /// ownership. No new reversal logic is introduced. Best-effort partial success: one item's
    /// failure never rolls back the others. BR-PAY-002: tutor-only; <paramref name="teacherId"/> and
    /// <paramref name="editedByUserId"/> come from the JWT, never the request body.
    /// </summary>
    Task<Result<BatchRevertResultDto>> BatchRevertPaymentAsync(
        BatchRevertPaymentDto dto, long teacherId, long editedByUserId);
}