    using Edvanz.Domain.Constants;
    using Edvanz.Domain.Entities;
    using Edvanz.Domain.Enums;
    using Edvanz.Domain.Helpers;
    using Edvanz.Domain.Interfaces;
    using Edvanz.Infrastructure.Persistence;
    using Microsoft.EntityFrameworkCore;

    namespace Edvanz.Infrastructure.Repositories;

    /// <summary>
    /// Extended repository for the Payment Module (Module 4) and Event Payment Module (Module 5).
    /// Centralizes all domain-specific query logic for payment-related entities.
    ///
    /// ARCHITECTURAL NOTE:
    /// Inherits GenericRepo&lt;PaymentTransaction, long&gt; for basic CRUD on the primary entity.
    /// All other entities (PaymentPeriod, StudentPaymentCounter, AssistantWallet, etc.) are
    /// accessed via _context directly through named methods � keeping query logic in one place.
    ///
    /// QUERY PATTERNS:
    /// - Paged queries use CountAsync + Skip/Take (same as AttendanceRepo).
    /// - Aggregates use GroupBy + Sum projections for O(1) SQL operations.
    /// - ExecuteUpdateAsync for bulk FK nullification (same as AttendanceRepo Step 1.2).
    /// - All queries include teacherId guard for tenant isolation.
    /// </summary>
    public class PaymentRepo : GenericRepo<PaymentTransaction, long>, IPaymentRepo
    {
        public PaymentRepo(EdvanzDbContext context) : base(context)
        {
        }

        /// <inheritdoc />
        public async Task<int> CountEventsByTeacherAsync(long teacherId)
        {
            return await _context.PaymentEvents
                .CountAsync(e => e.TeacherId == teacherId && !e.IsDeleted);
        }

        // ----------------------------------------------
        // PAYMENT TRANSACTION QUERIES
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task<PaymentTransaction?> GetTransactionByIdAndTeacherAsync(
            long transactionId, long teacherId)
        {
            return await _context.PaymentTransactions
                .FirstOrDefaultAsync(t => t.Id == transactionId
                    && t.TeacherId == teacherId
                    && !t.IsDeleted);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<long>> GetReferencedSessionOccurrenceIdsAsync(
            IEnumerable<long> sessionOccurrenceIds)
        {
            var ids = sessionOccurrenceIds.Distinct().ToList();
            if (ids.Count == 0)
                return Array.Empty<long>();

            // Include soft-deleted (refunded) transactions: their SessionOccurrenceId is still a live FK,
            // so the occurrence must be preserved to keep the audit linkage intact.
            return await _context.PaymentTransactions
                .Where(t => t.SessionOccurrenceId.HasValue && ids.Contains(t.SessionOccurrenceId.Value))
                .Select(t => t.SessionOccurrenceId!.Value)
                .Distinct()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<PaymentTransaction> Items, int TotalCount)>
            GetStudentPaymentHistoryPagedAsync(
                long teacherId, long teacherStudentId,
                DateTime? startDate, DateTime? endDate,
                int page, int pageSize)
        {
            var query = _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && t.TeacherStudentId == teacherStudentId
                    && !t.IsDeleted);

            if (startDate.HasValue)
                query = query.Where(t => t.CollectedAt >= startDate.Value);
            if (endDate.HasValue)
                query = query.Where(t => t.CollectedAt <= endDate.Value.Date.AddDays(1));

            int totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(t => t.CollectedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking()
                .ToListAsync();

            return (items, totalCount);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentTransaction>> GetTransactionsByPeriodAsync(
            long paymentPeriodId)
        {
            return await _context.PaymentTransactions
                .Where(t => t.PaymentPeriodId == paymentPeriodId && !t.IsDeleted)
                .OrderBy(t => t.CollectedAt)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<PaymentTransaction?> GetLatestTransactionForPeriodViaAllocationsAsync(
            long paymentPeriodId)
        {
            return await _context.PaymentTransactionAllocations
                .Where(a => a.PaymentPeriodId == paymentPeriodId && !a.PaymentTransaction.IsDeleted)
                .OrderByDescending(a => a.PaymentTransaction.CollectedAt)
                .ThenByDescending(a => a.PaymentTransactionId)
                .Select(a => a.PaymentTransaction)
                .AsNoTracking()
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentTransaction>> GetSameDayTransactionsAsync(
            long teacherId, long teacherStudentId, DateTime localDate)
        {
            return await _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && t.TeacherStudentId == teacherStudentId
                    && t.LocalCollectedAt.Date == localDate.Date
                    && !t.IsDeleted)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<PaymentTransaction?> GetByClientEntryIdAsync(
            long teacherId, string clientEntryId)
        {
            // Deliberately ignores IsDeleted: a record the tutor deleted after
            // an earlier sync must still block a replay from re-recording it.
            return await _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && t.ClientEntryId == clientEntryId)
                .AsNoTracking()
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<PaymentTransaction> Items, int TotalCount)>
            GetCollectorTransactionsPagedAsync(
                long teacherId, long collectedByUserId,
                DateTime? startDate, DateTime? endDate,
                int page, int pageSize)
        {
            var query = _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && t.CollectedByUserId == collectedByUserId
                    && !t.IsDeleted);

            if (startDate.HasValue)
                query = query.Where(t => t.CollectedAt >= startDate.Value);
            if (endDate.HasValue)
                query = query.Where(t => t.CollectedAt <= endDate.Value.Date.AddDays(1));

            int totalCount = await query.CountAsync();
            var items = await query
                // CollectedAt is datetime2(0); ties are common within a batch collect.
                // Id is the deterministic tiebreaker that makes Skip/Take stable.
                .OrderByDescending(t => t.CollectedAt)
                .ThenByDescending(t => t.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking()
                .ToListAsync();

            return (items, totalCount);
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<PaymentTransaction> Items, int TotalCount)>
            GetSessionTransactionsPagedAsync(
                long teacherId, long sessionId,
                DateTime? startDate, DateTime? endDate,
                int page, int pageSize)
        {
            var query = _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && t.SessionId == sessionId
                    && !t.IsDeleted);

            if (startDate.HasValue)
                query = query.Where(t => t.CollectedAt >= startDate.Value);
            if (endDate.HasValue)
                query = query.Where(t => t.CollectedAt <= endDate.Value.Date.AddDays(1));

            int totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(t => t.CollectedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking()
                .ToListAsync();

            return (items, totalCount);
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<PaymentTransaction> Items, int TotalCount)>
            GetTransactionsByDateRangePagedAsync(
                long teacherId,
                DateTime startDate, DateTime endDate,
                long? sessionId, long? collectedByUserId,
                int page, int pageSize,
                string? search = null)
        {
            var query = _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && t.CollectedAt >= startDate
                    && t.CollectedAt <= endDate
                    && !t.IsDeleted);

            if (sessionId.HasValue)
                query = query.Where(t => t.SessionId == sessionId.Value);
            if (collectedByUserId.HasValue)
                query = query.Where(t => t.CollectedByUserId == collectedByUserId.Value);
            // Optional filter over the denormalized student name/code (case- AND
            // Arabic-variant-insensitive, provider-side via dbo.ArabicNormalize).
            var term = string.IsNullOrWhiteSpace(search) ? null : ArabicTextNormalizer.Normalize(search.Trim());
            if (!string.IsNullOrEmpty(term))
                query = query.Where(t =>
                    (t.StudentName != null && EF.Functions.Like(DbSearch.ArabicNormalize(t.StudentName), $"%{term}%"))
                    || (t.StudentCode != null && EF.Functions.Like(DbSearch.ArabicNormalize(t.StudentCode), $"%{term}%")));

            int totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(t => t.CollectedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                // Live session name for the collections ledger; the transaction's own SessionName is
                // a collection-time snapshot that goes stale when the session is renamed.
                .Include(t => t.Session)
                // Per-period settlement slices + their period → which month(s) this cash cleared.
                .Include(t => t.Allocations).ThenInclude(a => a.PaymentPeriod)
                // Amount-edit trail → surface "edited from X to Y" on the collection row.
                .Include(t => t.EditLogs)
                // Two collection includes (Allocations + EditLogs) → split to avoid a cartesian blow-up.
                .AsSplitQuery()
                .AsNoTracking()
                .ToListAsync();

            return (items, totalCount);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<(decimal Amount, int Count)>> GetCollectionAmountTiersAsync(
            long teacherId, DateTime startInclusive, DateTime endInclusive, long? collectedByUserId,
            string? search = null)
        {
            // Distribution of money collected by per-MONTH amount: group the settlement slices
            // (one per cleared month) by their applied amount, so a multi-month payment counts once
            // per month at its monthly amount rather than as one large lump. Scoped to non-deleted
            // transactions in [start, end] for the teacher (and one collector when set).
            var query = _context.Set<PaymentTransactionAllocation>()
                .Where(a => a.TeacherId == teacherId
                    && a.PaymentTransaction != null
                    && !a.PaymentTransaction.IsDeleted
                    && a.PaymentTransaction.CollectedAt >= startInclusive
                    && a.PaymentTransaction.CollectedAt <= endInclusive);

            if (collectedByUserId.HasValue)
                query = query.Where(a => a.PaymentTransaction.CollectedByUserId == collectedByUserId.Value);

            // Same student name/code predicate as the ledger rows (GetTransactionsByDateRangePagedAsync)
            // so the "how many paid X" cards narrow with the visible list instead of reporting the whole
            // scope. Case- AND Arabic-variant-insensitive, provider-side via dbo.ArabicNormalize.
            var term = string.IsNullOrWhiteSpace(search) ? null : ArabicTextNormalizer.Normalize(search.Trim());
            if (!string.IsNullOrEmpty(term))
                query = query.Where(a =>
                    (a.PaymentTransaction.StudentName != null
                        && EF.Functions.Like(DbSearch.ArabicNormalize(a.PaymentTransaction.StudentName), $"%{term}%"))
                    || (a.PaymentTransaction.StudentCode != null
                        && EF.Functions.Like(DbSearch.ArabicNormalize(a.PaymentTransaction.StudentCode), $"%{term}%")));

            var rows = await query
                .GroupBy(a => a.AmountApplied)
                .Select(g => new { Amount = g.Key, Count = g.Count() })
                .ToListAsync();

            return rows
                .Select(r => (r.Amount, r.Count))
                .OrderByDescending(r => r.Count)
                .ThenByDescending(r => r.Amount)
                .ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<DepartureRefundRow>> GetDepartureRefundsByDateRangeAsync(
            long teacherId, DateTime startInclusive, DateTime endExclusive, long? collectedByUserId = null)
        {
            var query = _context.StudentDepartures
                .Where(d => d.TeacherId == teacherId
                    && d.DepartureOutcome == DepartureOutcome.RefundDue
                    && d.FinalAmount > 0m
                    && d.DepartedAt >= startInclusive && d.DepartedAt < endExclusive);

            if (collectedByUserId.HasValue)
                query = query.Where(d => d.CollectedByUserId == collectedByUserId.Value);

            return await query
                .OrderByDescending(d => d.DepartedAt)
                .Select(d => new DepartureRefundRow
                {
                    Id = d.Id,
                    StudentId = d.TeacherStudentId,
                    StudentName = d.StudentName,
                    StudentCode = d.StudentCode,
                    // Live session name when the session still exists; else the departure-time snapshot.
                    SessionName = d.Session != null ? d.Session.SessionName : d.SessionName,
                    RefundAmount = d.FinalAmount,
                    RefundPeriodStart = d.RefundPeriodStart,
                    CollectedByUserId = d.CollectedByUserId,
                    DepartedAt = d.DepartedAt
                })
                .AsNoTracking()
                .ToListAsync();
        }

        // ----------------------------------------------
        // PAYMENT PERIOD QUERIES
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task<PaymentPeriod?> GetEarliestUnpaidPeriodAsync(
            long teacherId, long teacherStudentId, long? sessionId)
        {
            var query = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId == teacherStudentId
                    && p.PaymentStatus != PaymentStatus.Paid);

            if (sessionId.HasValue)
                query = query.Where(p => p.SessionId == sessionId.Value);

            return await query
                .OrderBy(p => p.PeriodSequence)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<List<PaymentPeriod>> GetUnpaidPeriodsThroughAsync(
            long teacherId, long teacherStudentId, long? sessionId, DateTime throughMonthEnd)
        {
            // Tracked (NOT AsNoTracking) � the caller mutates AmountPaid/PaymentStatus and saves.
            // Earliest-first, and only periods that start on/before the cutoff (current month, or
            // current+1 when paying one month in advance). Ordered so a payment fills the oldest
            // debt first and cascades forward.
            var query = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId == teacherStudentId
                    && p.PaymentStatus != PaymentStatus.Paid
                    && p.PeriodStart <= throughMonthEnd);

            if (sessionId.HasValue)
                query = query.Where(p => p.SessionId == sessionId.Value);

            // CHRONOLOGICAL, with PeriodSequence only as the tie-break (2026-09-09). PeriodSequence
            // restarts per session, so for a student who has been in more than one session ordering by
            // it alone interleaved the months - "oldest debt first" silently was not oldest-first, and
            // the note-required check walked a different prefix than the one the collect engine fills.
            // Same months, same set; only the order changes, and it changes to the one every caller
            // already assumes.
            return await query
                .OrderBy(p => p.PeriodStart)
                .ThenBy(p => p.PeriodSequence)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<PaymentPeriod>> GetRepriceableSessionDefaultPeriodsAsync(
            long teacherId, long sessionId, DateTime monthlyFromMonthStart, DateTime perSessionFromDate)
        {
            // Tracked � the caller rewrites AmountDue/PaymentStatus. Only periods that are still owed
            // (Unpaid/PartiallyPaid) � Paid/Overpaid are left settled. Students with their own
            // CustomPaymentAmount are excluded (BR-PAY-003): a session price change must not touch an
            // individually-priced student.
            // The lower bound is per PERIOD TYPE: a Monthly row re-prices over the caller's whole window
            // (every still-owed month, arrears included � the same scope a per-student price change
            // uses), while a PerSession row only re-prices from next month � a class already delivered
            // was delivered at the old price and is never re-priced.
            // CARRIED/MOVED rows are never re-priced: an IsCarriedForward or MovedFromSessionId row is
            // AGREED debt from somewhere else that merely lives under this SessionId, not a bill this
            // session generated (�7.4/�7.4b treat it as untouchable everywhere else).
            return await _context.PaymentPeriods
                .AsNoTracking()
                .Where(p => p.TeacherId == teacherId
                    && p.SessionId == sessionId
                    && p.TeacherStudentId != null
                    // MOVED months ARE re-priced (2026-09-09, teacher-confirmed): a month carried over
                    // by OnStudentMovedBetweenSessionsAsync is a real, month-shaped, still-unpaid
                    // obligation now billed by THIS session, so it follows this session's price like
                    // every other unpaid month. This also makes the two price-change paths agree - the
                    // per-student path (GetRepriceableStudentPeriodsAsync) has always re-priced them.
                    // A LEGACY COLLAPSED LUMP is the one exception and stays FROZEN: ConfirmTransferAsync
                    // writes ONE row holding the whole outstanding balance, with PeriodStart == PeriodEnd
                    // == the transfer day and NO MovedFromSessionId, so the months it stands for no
                    // longer exist and cannot be re-derived. Re-pricing it to a single month's amount is
                    // exactly what silently erased the rest of the debt. The discriminator is
                    // "IsCarriedForward AND MovedFromSessionId IS NULL": every month-shaped carried row
                    // carries MovedFromSessionId, only the lump does not.
                    && !(p.IsCarriedForward && p.MovedFromSessionId == null)
                    && ((p.PeriodType == PeriodType.Monthly && p.PeriodStart >= monthlyFromMonthStart)
                        || (p.PeriodType != PeriodType.Monthly && p.PeriodStart >= perSessionFromDate))
                    && (p.PaymentStatus == PaymentStatus.Unpaid
                        || p.PaymentStatus == PaymentStatus.PartiallyPaid)
                    && !_context.StudentPaymentCounters.Any(c =>
                        c.TeacherId == teacherId
                        && c.TeacherStudentId == p.TeacherStudentId
                        && c.CustomPaymentAmount != null))
                .OrderBy(p => p.TeacherStudentId)
                .ThenBy(p => p.PeriodSequence)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<PaymentPeriod>> GetRepriceableStudentPeriodsAsync(
            long teacherId, long teacherStudentId, DateTime fromMonthStart)
        {
            // Still-owed periods for one student (per-student price change).
            // A LEGACY COLLAPSED LUMP is excluded and stays FROZEN - identical rule to
            // GetRepriceableSessionDefaultPeriodsAsync above, deliberately the same predicate so the
            // two price-change paths can never drift apart again. Without it, a per-student price
            // change rewrote the single row holding a transferred student's ENTIRE carried balance
            // down to one month's amount and silently deleted the difference (2026-09-08 review).
            // Month-shaped moved rows (MovedFromSessionId set) stay in scope and re-price per month.
            return await _context.PaymentPeriods
                .AsNoTracking()
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId == teacherStudentId
                    && p.PeriodStart >= fromMonthStart
                    && !(p.IsCarriedForward && p.MovedFromSessionId == null)
                    && (p.PaymentStatus == PaymentStatus.Unpaid
                        || p.PaymentStatus == PaymentStatus.PartiallyPaid))
                .OrderBy(p => p.PeriodSequence)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<bool> TryRepricePeriodAsync(long teacherId, long periodId, decimal newAmountDue)
        {
            // The money guard lives in the WHERE, not in a prior read, so "is it still untouched?" and
            // "re-price it" are ONE statement and nothing can slip between them. A concurrent collect
            // sets AmountPaid before this runs (it loses the race, 0 rows) or after (it re-reads and
            // applies against the new AmountDue) - either way no payment is overwritten.
            // ExecuteUpdate bypasses the change tracker, which is why the callers load these periods
            // AsNoTracking: there is no tracked copy to go stale.
            int affected = await _context.PaymentPeriods
                .Where(p => p.Id == periodId
                    && p.TeacherId == teacherId
                    && p.AmountPaid == 0m
                    && (p.ForgivenAmount ?? 0m) == 0m)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.AmountDue, newAmountDue)
                    .SetProperty(p => p.PaymentStatus, PaymentStatus.Unpaid));

            return affected > 0;
        }

        /// <inheritdoc />
        public async Task<List<PaymentPeriod>> GetFirstMonthlyPeriodsForAssignedStudentsAsync(long teacherId)
        {
            // TRACKED — the caller re-prorates AmountDue/IsProRated/ProRatedFraction/PaymentStatus.
            // Returns, per CURRENTLY-assigned student, their earliest monthly period (the enrollment's
            // first month = min PeriodSequence, the ONLY period proration ever touches) — but ONLY when
            // it is still owed (Unpaid/PartiallyPaid). Fully-paid first months are excluded so a
            // proration toggle never rewrites money the student already settled (user-chosen scope).
            var assignedIds = _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && ts.SessionId != null)
                .Select(ts => ts.Id);

            // Min monthly PeriodSequence per assigned student (a plain GROUP BY + MIN), MATERIALIZED so
            // the candidate fetch stays a simple IN-filter — avoids a correlated .Any() over a grouped
            // subquery the provider might not translate.
            var minSeqPairs = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId != null
                    && assignedIds.Contains(p.TeacherStudentId!.Value)
                    && p.PeriodType == PeriodType.Monthly)
                .GroupBy(p => p.TeacherStudentId!.Value)
                .Select(g => new { StudentId = g.Key, MinSeq = g.Min(x => x.PeriodSequence) })
                .ToListAsync();
            if (minSeqPairs.Count == 0) return new List<PaymentPeriod>();

            var minSeqByStudent = minSeqPairs.ToDictionary(x => x.StudentId, x => x.MinSeq);
            var studentIds = minSeqByStudent.Keys.ToList();

            // Still-owed monthly periods for those students (TRACKED), then keep only each student's
            // min-sequence (first) period in memory.
            var candidates = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId != null
                    && studentIds.Contains(p.TeacherStudentId!.Value)
                    && p.PeriodType == PeriodType.Monthly
                    && (p.PaymentStatus == PaymentStatus.Unpaid
                        || p.PaymentStatus == PaymentStatus.PartiallyPaid))
                .ToListAsync();

            return candidates
                .Where(p => minSeqByStudent.TryGetValue(p.TeacherStudentId!.Value, out var min)
                    && p.PeriodSequence == min)
                .ToList();
        }

        /// <inheritdoc />
        public async Task<List<(long StudentId, long? SessionId, DateTime PeriodStart, decimal AmountDue, decimal ProRatedFraction, bool IsProRated, bool IsProrationManual, int? ClassesTotal, int? ClassesBilled)>>
            GetAnchorPeriodInfoByStudentIdsAsync(long teacherId, IReadOnlyCollection<long> studentIds)
        {
            if (studentIds.Count == 0)
                return new List<(long, long?, DateTime, decimal, decimal, bool, bool, int?, int?)>();
            // The proration-anchor month per student (one each) — its amount, fraction, month, manual
            // flag and frozen class-basis drive the "Joined {date} · {billed} of {total} classes →
            // {amount} of {full}" + "set by hand" transparency on the payment screens.
            var rows = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId != null
                    && studentIds.Contains(p.TeacherStudentId.Value)
                    && p.IsProrationAnchorMonth)
                .Select(p => new
                {
                    StudentId = p.TeacherStudentId!.Value,
                    p.SessionId, p.PeriodStart, p.AmountDue, p.ProRatedFraction, p.IsProRated,
                    p.IsProrationManual, p.ProrationClassesTotal, p.ProrationClassesBilled
                })
                .ToListAsync();
            return rows
                .GroupBy(r => r.StudentId)
                .Select(g => g.OrderBy(x => x.PeriodStart).First())
                .Select(r => (r.StudentId, r.SessionId, r.PeriodStart, r.AmountDue, r.ProRatedFraction,
                    r.IsProRated, r.IsProrationManual, r.ProrationClassesTotal, r.ProrationClassesBilled))
                .ToList();
        }

        /// <inheritdoc />
        public async Task<List<PaymentPeriod>> GetUnpaidAnchorMonthPeriodsAsync(long teacherId)
        {
            // TRACKED. Every still-owed proration-anchor month for the teacher. This INCLUDES a carried /
            // moved anchor: the never-paid first-month-move preservation (PaymentService) keeps
            // IsProrationAnchorMonth set on a student's genuine first month through a move/reassignment, so
            // the config reconcile can still adjust it. A PLAIN transfer month never carries the flag, so it
            // stays excluded. (Historically all carried rows were excluded — that hid preserved anchors.)
            return await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId != null
                    && p.IsProrationAnchorMonth
                    && (p.PaymentStatus == PaymentStatus.Unpaid
                        || p.PaymentStatus == PaymentStatus.PartiallyPaid))
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<DateTime?> GetEarliestAssignmentDateAsync(long teacherId, long teacherStudentId)
        {
            // Min across ALL sessions — the original enrollment day survives moves/reassignments
            // (the destination's assignment row carries the MOVE date, never the true join).
            return await _context.StudentSessionAssignments
                .Where(a => a.TeacherId == teacherId && a.TeacherStudentId == teacherStudentId)
                .MinAsync(a => (DateTime?)a.AssignedAt);
        }

        /// <inheritdoc />
        public async Task<Dictionary<long, DateTime>> GetEarliestAssignmentDatesForStudentsAsync(
            long teacherId, IReadOnlyCollection<long> teacherStudentIds)
        {
            if (teacherStudentIds.Count == 0)
                return new Dictionary<long, DateTime>();
            var rows = await _context.StudentSessionAssignments
                .Where(a => a.TeacherId == teacherId && a.TeacherStudentId != null
                    && teacherStudentIds.Contains(a.TeacherStudentId.Value))
                .GroupBy(a => a.TeacherStudentId!.Value)
                .Select(g => new { StudentId = g.Key, Earliest = g.Min(x => x.AssignedAt) })
                .ToListAsync();
            return rows.ToDictionary(r => r.StudentId, r => r.Earliest);
        }

        /// <inheritdoc />
        public async Task<List<PaymentPeriod>> GetPeriodsStartingBeforeByTeacherAsync(
            long teacherId, DateTime beforeDate)
        {
            // TRACKED — the billing-start reconcile deletes/updates the survivors in place. One cutoff
            // covers both period types: Monthly rows are first-of-month dated and the cutoff is itself
            // a first-of-month, PerSession rows carry the class date.
            return await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId != null
                    && p.PeriodStart < beforeDate)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<(long TeacherStudentId, long SessionId, DateTime AssignedAt)>>
            GetActiveAssignmentRowsByTeacherAsync(long teacherId)
        {
            // BUG-8 guard: a purge SET-NULLs TeacherStudentId but leaves IsActive=1 — the null filter
            // (which also hides soft-deleted students via the global filter on the navigation) keeps
            // ghosts out of the backfill population.
            var rows = await _context.StudentSessionAssignments
                .Where(a => a.TeacherId == teacherId && a.IsActive
                    && a.TeacherStudentId != null && a.TeacherStudent != null
                    && a.SessionId != null)
                .Select(a => new { StudentId = a.TeacherStudentId!.Value, SessionId = a.SessionId!.Value, a.AssignedAt })
                .ToListAsync();
            return rows.Select(r => (r.StudentId, r.SessionId, r.AssignedAt)).ToList();
        }

        /// <inheritdoc />
        public async Task<List<PaymentPeriod>> GetMonthlyPeriodsByStudentAndSessionTrackedAsync(
            long teacherId, long teacherStudentId, long sessionId)
        {
            // TRACKED on purpose (see interface doc) — the re-anchor mutates these in place.
            return await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == teacherStudentId
                    && p.SessionId == sessionId && p.PeriodType == PeriodType.Monthly)
                .OrderBy(p => p.PeriodStart)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<(decimal SuggestedAmount, decimal SetAmount, long? SetByUserId, DateTime SetAt)?>
            GetLatestProrationEditForPeriodAsync(long teacherId, long periodId)
        {
            // Tenant-checked through the period row (the log itself carries no TeacherId).
            var row = await (
                from e in _context.PaymentEditLogs.AsNoTracking()
                join p in _context.PaymentPeriods.AsNoTracking() on e.PaymentPeriodId equals p.Id
                where e.PaymentTransactionId == null && e.PaymentPeriodId == periodId
                    && p.TeacherId == teacherId
                orderby e.EditedAt descending
                select new { e.PreviousAmount, e.NewAmount, e.EditedByUserId, e.EditedAt })
                .FirstOrDefaultAsync();
            if (row is null) return null;
            return (row.PreviousAmount, row.NewAmount, row.EditedByUserId, row.EditedAt);
        }

        /// <inheritdoc />
        public async Task<PaymentPeriod?> GetProrationAnchorPeriodAsync(
            long teacherId, long teacherStudentId, long sessionId)
        {
            // TRACKED — the caller re-prices it. The single anchor month for this student+session. This
            // INCLUDES a carried / moved anchor: the never-paid first-month-move preservation
            // (PaymentService) keeps IsProrationAnchorMonth set on a student's genuine first month through a
            // move/reassignment, so the first-attendance re-anchor can still find and adjust it. A PLAIN
            // transfer month never carries the flag → still null. (Historically carried rows were excluded.)
            return await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == teacherStudentId
                    && p.SessionId == sessionId && p.PeriodType == PeriodType.Monthly
                    && p.IsProrationAnchorMonth)
                .OrderBy(p => p.PeriodSequence)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<List<(long PaymentTransactionId, decimal SuggestedAmount, decimal SetAmount, long? SetByUserId)>>
            GetProrationAuditByTransactionIdsAsync(long teacherId, IReadOnlyCollection<long> transactionIds)
        {
            if (transactionIds.Count == 0)
                return new List<(long, decimal, decimal, long?)>();

            // Join each transaction's settled periods (allocations) to any proration-decision edit log
            // (a log with a null PaymentTransactionId linked by PaymentPeriodId). Take the LATEST decision
            // per transaction. PreviousAmount = system suggestion, NewAmount = the human-set amount.
            var rows = await (
                from a in _context.PaymentTransactionAllocations.AsNoTracking()
                where a.TeacherId == teacherId
                    && transactionIds.Contains(a.PaymentTransactionId)
                    && a.PaymentPeriodId != null
                join el in _context.PaymentEditLogs.AsNoTracking()
                        .Where(e => e.PaymentTransactionId == null && e.PaymentPeriodId != null)
                    on a.PaymentPeriodId equals el.PaymentPeriodId
                select new
                {
                    a.PaymentTransactionId,
                    el.PreviousAmount,
                    el.NewAmount,
                    el.EditedByUserId,
                    el.EditedAt
                })
                .ToListAsync();

            return rows
                .GroupBy(r => r.PaymentTransactionId)
                .Select(g =>
                {
                    var latest = g.OrderByDescending(x => x.EditedAt).First();
                    return (g.Key, latest.PreviousAmount, latest.NewAmount, latest.EditedByUserId);
                })
                .ToList();
        }

        /// <inheritdoc />
        public async Task<List<StudentPaymentCounter>> GetPaymentCountersByStudentIdsAsync(
            long teacherId, IReadOnlyCollection<long> teacherStudentIds)
        {
            if (teacherStudentIds.Count == 0) return new List<StudentPaymentCounter>();
            // TRACKED — the proration reconcile reads CustomPaymentAmount and writes the counter deltas
            // in place, so all affected counters are fetched once (no per-student round-trips).
            return await _context.StudentPaymentCounters
                .Where(c => c.TeacherId == teacherId && teacherStudentIds.Contains(c.TeacherStudentId))
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<decimal> GetOverdueTotalThroughAsync(
            long teacherId, long teacherStudentId, long? sessionId, DateTime throughMonthEnd)
        {
            // Total arrears the student owes THROUGH the given month (sum of each unpaid month's
            // remaining due). This is the server-owned "amount due" for lookup + mark-paid, and it
            // never includes months in advance.
            var query = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId == teacherStudentId
                    && p.PaymentStatus != PaymentStatus.Paid
                    && p.PeriodStart <= throughMonthEnd);

            if (sessionId.HasValue)
                query = query.Where(p => p.SessionId == sessionId.Value);

            return await query.SumAsync(p => (decimal?)(p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m))) ?? 0m;
        }

        /// <inheritdoc />
        public async Task<PaymentPeriod?> GetLatestPaidPeriodAsync(
            long teacherId, long teacherStudentId, long? sessionId, DateTime throughMonthEnd)
        {
            var query = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId == teacherStudentId
                    && p.AmountPaid > 0
                    // Anchor the departure refund on the CURRENT month the student is actually leaving —
                    // never a FUTURE month that merely holds a stray advance / partial payment. Bounding by
                    // the teacher's current-month end (§7.4) and ordering by PeriodStart (NOT PeriodSequence)
                    // stops a later, partially-paid month (e.g. 6 EGP that spilled into September) from
                    // out-ranking the current FULLY-paid month (August). That mis-anchor reversed the wrong
                    // period and left an orphaned Aug/Paid period on a departed student (the session-84 bug).
                    && p.PeriodStart <= throughMonthEnd);

            if (sessionId.HasValue)
                query = query.Where(p => p.SessionId == sessionId.Value);

            return await query
                .OrderByDescending(p => p.PeriodStart)
                .ThenByDescending(p => p.PeriodSequence)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<PaymentPeriod?> GetPaymentPeriodByIdAsync(long paymentPeriodId)
        {
            return await _context.PaymentPeriods.FindAsync(paymentPeriodId);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentPeriod>> GetPaymentPeriodsByStudentAndSessionAsync(
            long teacherId, long teacherStudentId, long? sessionId)
        {
            var query = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId == teacherStudentId);

            if (sessionId.HasValue)
                query = query.Where(p => p.SessionId == sessionId.Value);

            return await query
                .OrderBy(p => p.PeriodSequence)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<long>> GetStudentIdsWithPeriodsInSessionAsync(long teacherId, long sessionId)
        {
            return await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.SessionId == sessionId
                    && p.TeacherStudentId != null)
                .Select(p => p.TeacherStudentId!.Value)
                .Distinct()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<(long TeacherId, long TeacherStudentId)>>
            GetOrphanedUnpaidPeriodOwnersAsync(long? teacherId)
        {
            // Legacy orphans: SessionId nulled by the OLD session-delete (never converted to a carried
            // pending debt), still UNPAID with no cash. The new lifecycle marks pending rows
            // IsCarriedForward, so !IsCarriedForward isolates exactly the leftovers to reconcile.
            var q = _context.PaymentPeriods
                .Where(p => p.SessionId == null && !p.IsCarriedForward && p.TeacherStudentId != null
                    && p.AmountPaid <= 0m && p.PaymentStatus != PaymentStatus.Paid
                    && (p.AmountDue - (p.ForgivenAmount ?? 0m)) > 0m);
            if (teacherId.HasValue)
                q = q.Where(p => p.TeacherId == teacherId.Value);
            var rows = await q
                .Select(p => new { p.TeacherId, StudentId = p.TeacherStudentId!.Value })
                .Distinct()
                .ToListAsync();
            return rows.Select(r => (r.TeacherId, r.StudentId)).ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentPeriod>> GetOrphanedPeriodsByStudentAsync(
            long teacherId, long teacherStudentId)
        {
            // All session-less, not-yet-carried periods for the student (paid orphans included — the
            // caller's void/consolidate rule ignores paid rows, keeping them as history).
            return await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == teacherStudentId
                    && p.SessionId == null && !p.IsCarriedForward)
                .OrderBy(p => p.PeriodSequence)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<DateTime?> GetFirstAttendanceDateAnyAsync(
            long teacherId, long teacherStudentId)
        {
            // Earliest date the student physically attended ANY session (Present or the linked-session
            // CrossSessionPresent). Session-agnostic — used by the never-paid first-month-move re-proration
            // remediation when a carried period's SessionId was nulled by a later session delete, so a
            // per-session lookup (GetFirstAttendanceDateAsync) is impossible.
            var attended = _context.AttendanceRecords
                .Where(a => a.TeacherId == teacherId && a.TeacherStudentId == teacherStudentId
                    && (a.Status == AttendanceStatus.Present
                        || a.Status == AttendanceStatus.CrossSessionPresent));
            if (!await attended.AnyAsync()) return null;
            return await attended.MinAsync(a => (DateTime?)a.OccurrenceDate);
        }

        /// <inheritdoc />
        public async Task<List<(long TeacherId, long TeacherStudentId)>>
            GetNeverPaidCarriedAnchorCandidateOwnersAsync(long? teacherId)
        {
            // Candidates for the never-paid first-month-move re-proration remediation: (teacher, student)
            // owners of a CARRIED / MOVED monthly period that is still fully UNPAID and currently NOT
            // prorated (the bug re-priced a prorated first-month anchor to full price and dropped its flag).
            // Over-selects cheaply; the service refines to the student's EARLIEST period + zero-paid history
            // + a first-attendance day that lands in a discounted tier.
            var q = _context.PaymentPeriods
                .Where(p => p.TeacherStudentId != null
                    && p.PeriodType == PeriodType.Monthly
                    && (p.IsCarriedForward || p.MovedFromSessionId != null)
                    && p.AmountPaid <= 0m
                    && p.PaymentStatus != PaymentStatus.Paid
                    && !p.IsProRated);
            if (teacherId.HasValue)
                q = q.Where(p => p.TeacherId == teacherId.Value);
            var rows = await q
                .Select(p => new { p.TeacherId, StudentId = p.TeacherStudentId!.Value })
                .Distinct()
                .ToListAsync();
            return rows.Select(r => (r.TeacherId, r.StudentId)).ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentPeriod>> GetAllPaymentPeriodsByStudentAsync(
            long teacherId, long teacherStudentId)
        {
            return await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == teacherStudentId)
                // Eager-load non-deleted transactions so the payment-view period rows can surface each
                // paid period's collection(s) � incl. the collector (CollectedByUserId) � without an N+1.
                .Include(p => p.PaymentTransactions.Where(t => !t.IsDeleted))
                // Eager-load the session so the service can display its LIVE name; the period's own
                // SessionName is a generation-time snapshot that goes stale on a rename.
                .Include(p => p.Session)
                .OrderBy(p => p.SessionName)
                .ThenBy(p => p.PeriodSequence)
                // Identity resolution (NOT plain AsNoTracking): sibling periods share ONE Session (and
                // transaction) instance instead of a fresh detached copy per row. Without it, when a
                // caller passes these entities to DeleteRangeAsync, EF's RemoveRange graph-attach tries to
                // track two Session instances with the same key and throws "cannot be tracked … already
                // being tracked" (broke confirm-reassign for any student with ≥2 periods in one session).
                .AsNoTrackingWithIdentityResolution()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentPeriod>> GetStudentPeriodsWithTransactionsAsync(
            long teacherId, long teacherStudentId)
        {
            // Eager-load only non-deleted transactions (filtered Include) so the tracking screen
            // can surface each paid period's settlement date without an N+1 per period. Ordered
            // by period start; the service classifies/re-orders into the Upcoming/Paid/Overdue
            // sections.
            return await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == teacherStudentId)
                .Include(p => p.PaymentTransactions.Where(t => !t.IsDeleted))
                // Live session name for display (the period's copy is a stale-on-rename snapshot).
                .Include(p => p.Session)
                .OrderBy(p => p.PeriodStart)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentPeriod>> GetPaymentPeriodsByStudentInRangeAsync(
            long teacherId, long teacherStudentId, DateTime? startDate, DateTime? endDate)
        {
            // Same as GetAllPaymentPeriodsByStudentAsync but honoring the optional date window
            // (by period start) for the history screen's startDate/endDate filter.
            var query = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == teacherStudentId);

            if (startDate.HasValue)
                query = query.Where(p => p.PeriodStart >= startDate.Value);
            if (endDate.HasValue)
                query = query.Where(p => p.PeriodStart <= endDate.Value);

            return await query
                // Eager-load non-deleted transactions so the history screen's period rows carry their
                // collection(s) � incl. the collector (CollectedByUserId) � without an N+1 per period.
                .Include(p => p.PaymentTransactions.Where(t => !t.IsDeleted))
                .OrderBy(p => p.SessionName)
                .ThenBy(p => p.PeriodSequence)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<decimal> GetCashCollectedInRangeAsync(
            long teacherId, long? sessionId, DateTime startInclusive, DateTime endExclusive)
        {
            // Actual cash physically collected in the window (by transaction date), regardless of
            // which month each payment settles. The global !IsDeleted filter excludes refunded/reverted
            // transactions and edits update AmountPaid, so the sum is already net of refunds.
            var query = _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && t.CollectedAt >= startInclusive && t.CollectedAt < endExclusive);

            if (sessionId.HasValue)
                // Per-session view = LIVE students only: exclude a departed (soft-deleted) student's
                // cash — they already left the session (the global soft-delete filter nulls the
                // navigation). Teacher-wide totals (sessionId == null) stay unscoped by design.
                query = query.Where(t => t.SessionId == sessionId.Value && t.TeacherStudent != null);

            return await query.SumAsync(t => (decimal?)t.AmountPaid) ?? 0m;
        }

        /// <inheritdoc />
        public async Task<int> CountDistinctPayingStudentsInRangeAsync(
            long teacherId, long? sessionId, DateTime startInclusive, DateTime endExclusive)
        {
            // Distinct students who physically paid in the window (by transaction date). The global
            // !IsDeleted filter excludes refunded/reverted transactions, so a student whose only
            // in-window payment was later refunded is not counted (mirrors GetCashCollectedInRange).
            var query = _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && t.CollectedAt >= startInclusive && t.CollectedAt < endExclusive
                    && t.TeacherStudentId != null);

            if (sessionId.HasValue)
                // Per-session view = LIVE students only: a departed (soft-deleted) student is not
                // counted among the session's payers (the global soft-delete filter nulls the
                // navigation). Teacher-wide counts (sessionId == null) stay unscoped by design.
                query = query.Where(t => t.SessionId == sessionId.Value && t.TeacherStudent != null);

            return await query
                .Select(t => t.TeacherStudentId!.Value)
                .Distinct()
                .CountAsync();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentTransaction>> GetCollectorTransactionsInRangeAsync(
            long teacherId, long collectorUserId, DateTime startInclusive, DateTime endExclusive)
        {
            // Money that came IN this window: collections the collector took, at the amount recorded.
            // IgnoreQueryFilters includes a collection that was later fully refunded (soft-deleted) �
            // its AmountPaid is preserved on delete, so pairing it with its negative refund entry nets
            // to zero for a same-window collect-then-refund.
            return await _context.PaymentTransactions
                .IgnoreQueryFilters()
                .Where(t => t.TeacherId == teacherId
                    && t.CollectedByUserId == collectorUserId
                    && t.CollectedAt >= startInclusive && t.CollectedAt < endExclusive)
                // Eager-load the session so the wallet rows can show its LIVE name. The
                // transaction's own SessionName is a collection-time snapshot, so collections taken
                // either side of a rename would otherwise list the SAME session under two names.
                .Include(t => t.Session)
                // Per-period slices + their period (which month(s) each collection settled) and the
                // amount-edit trail (so the wallet ledger can name the installment + show edits).
                .Include(t => t.Allocations).ThenInclude(a => a.PaymentPeriod)
                .Include(t => t.EditLogs)
                // This range is unbounded (whole collector history), so split the two collection
                // includes to avoid an Allocations×EditLogs cartesian row explosion.
                .AsSplitQuery()
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<CollectorRefundRow>> GetCollectorRefundsInRangeAsync(
            long teacherId, long collectorUserId, DateTime startInclusive, DateTime endExclusive,
            bool includeDeleted = true)
        {
            // A refund = money handed back. Two kinds, charged to DIFFERENT collectors:
            //   • Deleted — a CORRECTION of the original collection (no fresh cash moves): charged to
            //     the ORIGINAL collector (the transaction's CollectedByUserId) whose figure it corrects.
            //   • Reversed — a DEPARTURE payout: cash physically handed to the student by whoever
            //     CONFIRMED the departure, so it is charged to that PERFORMER (EditedByUserId), not the
            //     original collector — their held cash is untouched (decided 2026-08-24).
            // Partial amount-edits are treated as corrections to the collected figure (reflected in the
            // collection's own amount), not refund lines, so the month log never double-counts.
            // IgnoreQueryFilters because the transaction is soft-deleted.
            //
            // includeDeleted=false (the "view more" collections list) drops Deleted rows: a full delete
            // ALSO removes the collection's positive row from that !IsDeleted-filtered list, so surfacing
            // its refund line there is an orphaned negative with no positive counterpart. Reversed rows
            // (partial departure reversals) keep their still-visible positive row and are always shown.
            var rows = await _context.PaymentEditLogs
                .IgnoreQueryFilters()
                .Where(l => l.PaymentTransaction != null
                    && l.PaymentTransaction.TeacherId == teacherId
                    && l.EditedAt >= startInclusive && l.EditedAt < endExclusive
                    && ((l.EditAction == PaymentEditAction.Reversed
                            && l.EditedByUserId == collectorUserId)
                        || (includeDeleted && l.EditAction == PaymentEditAction.Deleted
                            && l.PaymentTransaction.CollectedByUserId == collectorUserId)))
                .Select(l => new CollectorRefundRow
                {
                    Id = l.Id,
                    StudentId = l.PaymentTransaction!.TeacherStudentId,
                    StudentName = l.PaymentTransaction.StudentName,
                    StudentCode = l.PaymentTransaction.StudentCode,
                    // Live session name; the transaction's copy is a stale-on-rename snapshot and is
                    // only the fallback for a session that no longer exists.
                    SessionName = l.PaymentTransaction.Session != null
                        ? l.PaymentTransaction.Session.SessionName
                        : l.PaymentTransaction.SessionName,
                    // The actually-refunded delta. PreviousAmount alone overstates a
                    // partial/prorated reversal (e.g. a prorated departure that only
                    // reverses part of the period); Deleted writes NewAmount=0 so a full
                    // delete still yields the full amount.
                    RefundAmount = l.PreviousAmount - l.NewAmount,
                    RefundedAt = l.EditedAt,
                    // Instant the reversed cash was originally collected — for reset-aware callers.
                    CollectedAt = l.PaymentTransaction!.CollectedAt,
                    // Who performed the refund/edit — may differ from the collector it is charged to.
                    PerformedByUserId = l.EditedByUserId
                })
                .AsNoTracking()
                .ToListAsync();

            return rows;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<(long SessionId, decimal CashCollected, int PaidStudents)>>
            GetSessionMonthCollectionAsync(long teacherId, DateTime startInclusive, DateTime endExclusive)
        {
            // "Collected by session" = per session, how many currently-assigned students have SETTLED
            // their obligation THROUGH the viewed month, and how much of that month's bill they paid.
            //
            // ONE SOURCE OF TRUTH with the roster / status breakdown: a student is "collected" here iff
            // the roster (GetStudentPaymentStatusCountsAsync) counts them "paid" — i.e. currently assigned
            // to a session (active; the !IsDeleted global filter applies) AND CAUGHT UP: no outstanding
            // (non-Paid) period whose PeriodStart is before the month rolls over. This is the SAME
            // "earliest outstanding through the month" rule the roster uses, so the per-session paid counts
            // always SUM to statusBreakdown.paid and this card can NEVER again diverge from the roster's
            // paid/unpaid split. Judged only through the month (PeriodStart < endExclusive) so pre-generated
            // future months are never counted as owed (§7.4).
            //
            // Why not the old rule (count any student with a month period whose AmountPaid > 0): it
            // over-counted. A student with a still-owed EARLIER month, or a DUPLICATE unpaid ladder for the
            // same session, reads "unpaid" on the roster yet had one this-month period paid > 0 — so the
            // card showed them "collected" while the roster showed 0 (the prod case: session-84 read 1/300
            // while the roster/paid-list read 0, from student 1594's duplicate Paid+Unpaid August periods).
            // It also double-counted an orphaned Paid period left behind by a departed student.
            //
            // "Not departed" is covered for free: a departure UNASSIGNS the student (TeacherStudent
            // .SessionId = null), so a departed student is simply absent from the assigned set below — no
            // separate StudentDepartures gate is needed (and the old one could not match anyway: a purge
            // NULLs StudentDeparture.TeacherStudentId/SessionId, so its correlated Any() never fired).

            // Currently-assigned, ACTIVE students as (student, session) pairs (the roster population).
            var assigned = await _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && ts.SessionId != null)
                .Select(ts => new { StudentId = ts.Id, SessionId = ts.SessionId!.Value })
                .ToListAsync();
            if (assigned.Count == 0)
                return new List<(long, decimal, int)>();

            // Students with ANY outstanding (non-Paid) period through the viewed month — the roster's
            // "not caught up" set (mirrors GetStudentPaymentStatusCountsAsync exactly).
            var outstanding = (await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId.HasValue
                    && p.PaymentStatus != PaymentStatus.Paid
                    && p.PeriodStart < endExclusive)
                .Select(p => p.TeacherStudentId!.Value)
                .Distinct()
                .ToListAsync())
                .ToHashSet();

            // Paid members = assigned AND caught up.
            var paidMembers = assigned.Where(a => !outstanding.Contains(a.StudentId)).ToList();
            if (paidMembers.Count == 0)
                return new List<(long, decimal, int)>();

            var paidStudentIds = paidMembers.Select(a => a.StudentId).ToHashSet();
            var paidPairs = paidMembers.Select(a => (a.StudentId, a.SessionId)).ToHashSet();

            // This-month cash for those paid members: sum of AmountPaid over the month's periods, per
            // (student, session). Restricted to the paid set so the cash and the count come from the SAME
            // students (never "0 students / N collected"). A caught-up student's month period is fully
            // settled, so this equals what they actually paid toward the viewed month.
            var monthCash = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId.HasValue
                    && p.SessionId.HasValue
                    && p.PeriodStart >= startInclusive && p.PeriodStart < endExclusive
                    && paidStudentIds.Contains(p.TeacherStudentId!.Value))
                .GroupBy(p => new { StudentId = p.TeacherStudentId!.Value, SessionId = p.SessionId!.Value })
                .Select(g => new { g.Key.StudentId, g.Key.SessionId, Cash = g.Sum(x => x.AmountPaid) })
                .ToListAsync();

            var cashBySession = monthCash
                // Attribute cash to the student's ASSIGNED session only, so it lines up with the count.
                .Where(mc => paidPairs.Contains((mc.StudentId, mc.SessionId)))
                .GroupBy(mc => mc.SessionId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Cash));

            return paidMembers
                .GroupBy(a => a.SessionId)
                .Select(g => (
                    g.Key,
                    cashBySession.TryGetValue(g.Key, out var c) ? c : 0m,
                    g.Select(a => a.StudentId).Distinct().Count()))
                .ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<(long SessionId, int TotalStudents)>>
            GetAssignedStudentCountsPerSessionAsync(long teacherId)
        {
            var rows = await _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && ts.SessionId != null)
                .GroupBy(ts => ts.SessionId!.Value)
                .Select(g => new { SessionId = g.Key, Total = g.Count() })
                .ToListAsync();

            return rows.Select(r => (r.SessionId, r.Total)).ToList();
        }

        /// <inheritdoc />
        public async Task<int> CountAssignedStudentsAsync(long teacherId)
        {
            // Active (non-deleted, global filter applies) students currently assigned to a session.
            return await _context.TeacherStudents
                .CountAsync(ts => ts.TeacherId == teacherId && ts.SessionId != null);
        }

        public async Task<int> CountUnpaidStudentsBySessionAsync(long teacherId, long sessionId)
        {
            return await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.SessionId == sessionId
                    && p.PaymentStatus != PaymentStatus.Paid
                    && p.TeacherStudentId.HasValue)
                .Select(p => p.TeacherStudentId)
                .Distinct()
                .CountAsync();
        }

        /// <inheritdoc />
        public async Task<(int Paid, int ProRated, int Unpaid)> GetStudentPaymentStatusCountsAsync(
            long teacherId, DateTime selectedMonthEnd)
        {
            // Buckets reconcile to the tracking screen's TotalStudents: only students CURRENTLY
            // assigned to a session (SessionId != null � same population as CountAssignedStudentsAsync)
            // are classified, so paid + prorated + unpaid always sums to that total. Formerly-assigned
            // students that still carry historical periods are intentionally excluded here (they would
            // otherwise inflate the buckets past the assigned headcount).
            var assignedStudentIds = _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && ts.SessionId != null)
                .Select(ts => ts.Id);

            int totalAssignedStudents = await assignedStudentIds.CountAsync();

            // Per assigned student, whether their earliest outstanding period (lowest PeriodSequence
            // among non-Paid periods THROUGH the selected month) is prorated. Same "earliest unpaid"
            // concept as GetEarliestUnpaidPeriodAsync, judged ONLY through the selected month so
            // pre-generated future periods never count as owed. An assigned student with no
            // outstanding period on/before the selected month (caught up, or no obligation generated
            // yet) is "paid".
            var earliestOutstandingIsProRated = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId.HasValue
                    && assignedStudentIds.Contains(p.TeacherStudentId!.Value)
                    && p.PaymentStatus != PaymentStatus.Paid
                    && p.PeriodStart <= selectedMonthEnd)
                .GroupBy(p => p.TeacherStudentId!.Value)
                .Select(g => g.OrderBy(p => p.PeriodSequence).First().IsProRated)
                .ToListAsync();

            int proRated = earliestOutstandingIsProRated.Count(isProRated => isProRated);
            int unpaid = earliestOutstandingIsProRated.Count - proRated;
            int paid = totalAssignedStudents - earliestOutstandingIsProRated.Count;

            return (paid, proRated, unpaid);
        }

        /// <inheritdoc />
        public async Task<int> CountPartiallyPaidStudentsInMonthAsync(
            long teacherId, DateTime monthStart, DateTime monthEnd)
        {
            // Same classification as GetStudentsByPaymentStatusPagedAsync(status: "partial"):
            // assigned students with a period THIS month that is partially settled.
            var assignedStudentIds = _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && ts.SessionId != null)
                .Select(ts => ts.Id);

            return await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId.HasValue
                    && assignedStudentIds.Contains(p.TeacherStudentId!.Value)
                    && p.PeriodStart >= monthStart && p.PeriodStart <= monthEnd
                    && p.PaymentStatus == PaymentStatus.PartiallyPaid)
                .Select(p => p.TeacherStudentId!.Value)
                .Distinct()
                .CountAsync();
        }

        /// <inheritdoc />
        public async Task<int> GetMaxPeriodSequenceAsync(
            long teacherId, long teacherStudentId, long sessionId)
        {
            var maxSeq = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId == teacherStudentId
                    && p.SessionId == sessionId)
                .MaxAsync(p => (int?)p.PeriodSequence);

            return maxSeq ?? 0;
        }

        /// <inheritdoc />
        public async Task AddPaymentPeriodAsync(PaymentPeriod period)
        {
            await _context.PaymentPeriods.AddAsync(period);
        }

        /// <inheritdoc />
        public async Task AddPaymentPeriodsRangeAsync(IEnumerable<PaymentPeriod> periods)
        {
            await _context.PaymentPeriods.AddRangeAsync(periods);
        }

        /// <inheritdoc />
        public async Task UpdatePaymentPeriodAsync(PaymentPeriod period)
        {
            _context.Entry(period).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<List<PaymentPeriod>> GetTrackedPaymentPeriodsByStudentAsync(long teacherStudentId)
        {
            // TRACKED (no AsNoTracking) — the admin backfill mutates AmountPaid/PaymentStatus + inserts a
            // new period. TeacherStudentId is globally unique so no teacher filter is needed.
            return await _context.PaymentPeriods
                .Where(p => p.TeacherStudentId == teacherStudentId)
                .OrderBy(p => p.PeriodSequence)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<PaymentTransactionAllocation>> GetAllocationsByPeriodAsync(long paymentPeriodId)
        {
            // TRACKED — the admin backfill repoints these to another period.
            return await _context.PaymentTransactionAllocations
                .Where(a => a.PaymentPeriodId == paymentPeriodId)
                .OrderBy(a => a.Id)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<long>> GetStudentIdsWithPeriodsAsync(long? teacherId)
        {
            var query = _context.PaymentPeriods.Where(p => p.TeacherStudentId != null);
            if (teacherId.HasValue)
                query = query.Where(p => p.TeacherId == teacherId.Value);

            return await query
                .Select(p => p.TeacherStudentId!.Value)
                .Distinct()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<HashSet<long>> GetReferencedPeriodIdsAsync(IReadOnlyCollection<long> periodIds)
        {
            if (periodIds.Count == 0) return new HashSet<long>();

            // Any transaction OR allocation pointing at a period (deleted rows included via IgnoreQueryFilters)
            // makes that period unsafe to delete — we must never orphan a cash record.
            var txRefs = await _context.PaymentTransactions
                .IgnoreQueryFilters()
                .Where(t => t.PaymentPeriodId != null && periodIds.Contains(t.PaymentPeriodId.Value))
                .Select(t => t.PaymentPeriodId!.Value)
                .Distinct()
                .ToListAsync();

            var allocRefs = await _context.PaymentTransactionAllocations
                .IgnoreQueryFilters()
                .Where(a => a.PaymentPeriodId != null && periodIds.Contains(a.PaymentPeriodId.Value))
                .Select(a => a.PaymentPeriodId!.Value)
                .Distinct()
                .ToListAsync();

            var referenced = new HashSet<long>(txRefs);
            referenced.UnionWith(allocRefs);
            return referenced;
        }

        /// <inheritdoc />
        public async Task<int> RepointTransactionsToPeriodAsync(long fromPeriodId, long toPeriodId)
        {
            // Bulk repoint of the denormalized display FK. Non-deleted only (active settlement); a
            // soft-deleted transaction's stale period pointer is harmless and left as history.
            return await _context.PaymentTransactions
                .Where(t => t.PaymentPeriodId == fromPeriodId && !t.IsDeleted)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.PaymentPeriodId, toPeriodId));
        }

        // ----------------------------------------------
        // PAYMENT TRANSACTION ALLOCATION LEDGER (PAY-1)
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task AddPaymentTransactionAllocationsRangeAsync(
            IEnumerable<PaymentTransactionAllocation> allocations)
        {
            await _context.PaymentTransactionAllocations.AddRangeAsync(allocations);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentTransactionAllocation>> GetAllocationsByTransactionAsync(
            long transactionId)
        {
            // TRACKED (no AsNoTracking) + eager PaymentPeriod so the caller can reverse each period's
            // AmountPaid in place. Oldest-first ordering lets a partial reversal walk newest-first (LIFO).
            return await _context.PaymentTransactionAllocations
                .Include(a => a.PaymentPeriod)
                .Where(a => a.PaymentTransactionId == transactionId)
                .OrderBy(a => a.PaymentPeriod!.PeriodStart)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task RemovePaymentTransactionAllocationsAsync(
            IEnumerable<PaymentTransactionAllocation> allocations)
        {
            _context.PaymentTransactionAllocations.RemoveRange(allocations);
            await Task.CompletedTask;
        }

        // ----------------------------------------------
        // STUDENT PAYMENT COUNTER QUERIES
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task<StudentPaymentCounter?> GetPaymentCounterAsync(
            long teacherId, long teacherStudentId)
        {
            return await _context.StudentPaymentCounters
                .FirstOrDefaultAsync(c => c.TeacherId == teacherId
                    && c.TeacherStudentId == teacherStudentId);
        }

        /// <inheritdoc />
        public async Task AddPaymentCounterAsync(StudentPaymentCounter counter)
        {
            await _context.StudentPaymentCounters.AddAsync(counter);
        }

        /// <inheritdoc />
        /// <inheritdoc />
        public async Task UpdatePaymentCounterAsync(StudentPaymentCounter counter)
        {
            // A counter created earlier in this same unit of work is still in the Added
            // state with a temporary key; forcing it to Modified throws. Leave Added entities
            // as-is � SaveChanges INSERTs them with the totals already set on the instance.
            // Only already-tracked/persisted rows need the explicit Modified flag.
            var entry = _context.Entry(counter);
            if (entry.State != EntityState.Added)
                entry.State = EntityState.Modified;

            await Task.CompletedTask;
        }

        /// <inheritdoc />
    public async Task<(IReadOnlyList<UnpaidStudentRow> Items, int TotalCount)>
            GetUnpaidStudentsPagedAsync(
                long teacherId,
                long? sessionId, long? sessionGroupId,
                PaymentType? paymentType,
                int? minConsecutiveUnpaid,
                string? search,
            DateTime throughMonthEnd,
                int page, int pageSize)
        {
        // Active (non-deleted) students only — the global !IsDeleted filter applies. A student in
        // the recycle bin cannot be collected from (PaymentStudentInRecycleBin), so listing their
        // arrears in a "go collect" view is noise. Previously such rows surfaced as "Unknown".
        var activeStudentIds = _context.TeacherStudents
            .Where(ts => ts.TeacherId == teacherId)
            .Select(ts => ts.Id);

        // Arrears are judged ONLY through the cutoff month (CLAUDE.md §7.4). Periods are
        // pre-generated to the session end, so an all-time scan reports every FUTURE month as
        // owed — the defect this replaces. Orphaned periods (TeacherStudentId nulled on student
        // purge) are excluded: they are nobody's obligation.
        // Backed by IX_PP_TeacherId_Status_PeriodStart (TeacherId, PaymentStatus, PeriodStart).
        var periods = _context.PaymentPeriods
            .Where(p => p.TeacherId == teacherId
                && p.TeacherStudentId != null
                && activeStudentIds.Contains(p.TeacherStudentId!.Value)
                && p.PaymentStatus != PaymentStatus.Paid
                && p.PeriodStart <= throughMonthEnd);

        // Scoped by the PERIOD's session/group — NOT the student's current assignment. A student
        // who transferred out of a session still owes that session's arrears and must appear
        // under it. Consistent with GetDashboardAggregatesAsync / GetDashboardPerSessionAsync.
            if (sessionId.HasValue)
            periods = periods.Where(p => p.SessionId == sessionId.Value);

            if (sessionGroupId.HasValue)
            periods = periods.Where(p =>
                p.Session != null && p.Session.SessionGroupId == sessionGroupId.Value);

        // PeriodType mirrors the session's PaymentType at generation time and shares its member
        // values (Monthly = 1, PerSession = 2), so the filter maps by a direct cast. This
        // parameter was previously accepted and silently ignored.
        if (paymentType.HasValue)
        {
            var periodType = (PeriodType)(byte)paymentType.Value;
            periods = periods.Where(p => p.PeriodType == periodType);
        }

            if (!string.IsNullOrWhiteSpace(search))
            {
                string searchLower = ArabicTextNormalizer.Normalize(search.Trim());
            periods = periods.Where(p =>
                p.TeacherStudent != null
                && (DbSearch.ArabicNormalize(p.TeacherStudent.StudentName).Contains(searchLower)
                    || DbSearch.ArabicNormalize(p.TeacherStudent.StudentCode).Contains(searchLower)));
            }

        // One row per student: arrears and unpaid-period count THROUGH the cutoff. The collection
        // engine settles oldest-first (cascade), so unpaid periods through a given month are
        // always a contiguous tail — the unpaid count IS the consecutive count (BR-PAY-006).
        var grouped = periods
            .GroupBy(p => p.TeacherStudentId!.Value)
            .Select(g => new
            {
                TeacherStudentId = g.Key,
                TotalOutstanding = g.Sum(p => p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m)),
                UnpaidPeriodCount = g.Count()
            });

        if (minConsecutiveUnpaid.HasValue)
            grouped = grouped.Where(x => x.UnpaidPeriodCount >= minConsecutiveUnpaid.Value);

        int totalCount = await grouped.CountAsync();

        var pageRows = await grouped
            .OrderByDescending(x => x.UnpaidPeriodCount)
            .ThenByDescending(x => x.TotalOutstanding)
            // Deterministic tiebreak — ties are common, and Skip/Take must be stable across pages.
            .ThenBy(x => x.TeacherStudentId)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
            .ToListAsync();

        if (pageRows.Count == 0)
            return (Array.Empty<UnpaidStudentRow>(), totalCount);

        var pageIds = pageRows.Select(r => r.TeacherStudentId).ToList();

        // Display fields for the PAGE's students only (bounded fan-out, no N+1). LastPaymentDate
        // is a historical fact, so it is still read from the counter — unlike the arrears totals,
        // it is not distorted by pre-generated future periods.
        var students = await _context.TeacherStudents
            .Where(ts => ts.TeacherId == teacherId && pageIds.Contains(ts.Id))
            .Select(ts => new
            {
                ts.Id,
                ts.StudentName,
                ts.StudentCode,
                ts.SessionId,
                SessionName = ts.Session != null ? ts.Session.SessionName : null,
                LastPaymentDate = _context.StudentPaymentCounters
                    .Where(c => c.TeacherId == teacherId && c.TeacherStudentId == ts.Id)
                    .Select(c => c.LastPaymentDate)
                    .FirstOrDefault()
            })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Id);

        // The individual unpaid periods behind each row (REQ-PAY-031 detail). Same filters and
        // cutoff as the aggregate above, so labels can never disagree with the counts. Formatting
        // stays in the Application layer — the repo returns dates, not display strings.
        var periodRefs = await periods
            .Where(p => pageIds.Contains(p.TeacherStudentId!.Value))
            .OrderBy(p => p.PeriodSequence)
            .Select(p => new
            {
                StudentId = p.TeacherStudentId!.Value,
                Ref = new UnpaidPeriodRef
                {
                    PeriodType = p.PeriodType,
                    PeriodStart = p.PeriodStart,
                    PeriodEnd = p.PeriodEnd,
                    AmountRemaining = p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m)
                }
            })
                .AsNoTracking()
                .ToListAsync();

        var refsByStudent = periodRefs
            .GroupBy(x => x.StudentId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UnpaidPeriodRef>)g.Select(x => x.Ref).ToList());

        var items = new List<UnpaidStudentRow>(pageRows.Count);
        foreach (var row in pageRows)
        {
            students.TryGetValue(row.TeacherStudentId, out var student);
            items.Add(new UnpaidStudentRow
            {
                TeacherStudentId = row.TeacherStudentId,
                StudentName = student?.StudentName ?? string.Empty,
                StudentCode = student?.StudentCode ?? string.Empty,
                SessionId = student?.SessionId,
                SessionName = student?.SessionName,
                UnpaidPeriodCount = row.UnpaidPeriodCount,
                TotalOutstanding = row.TotalOutstanding,
                LastPaymentDate = student?.LastPaymentDate,
                UnpaidPeriods = refsByStudent.TryGetValue(row.TeacherStudentId, out var refs)
                    ? refs
                    : Array.Empty<UnpaidPeriodRef>()
            });
        }

            return (items, totalCount);
        }

        /// <inheritdoc />
        public async Task<decimal> GetTotalOutstandingAmountAsync(long teacherId, long? sessionId)
        {
            var query = _context.StudentPaymentCounters
                .Where(c => c.TeacherId == teacherId);

            if (sessionId.HasValue)
                query = query.Where(c =>
                    c.TeacherStudent != null && c.TeacherStudent.SessionId == sessionId.Value);

            return await query.SumAsync(c => c.TotalOutstanding);
        }

        // ----------------------------------------------
        // SCREEN QUERIES (api/v1 � frontend payment.json)
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task<(IReadOnlyList<CollectStudentRow> Items, int TotalCount, int CountAll, int CountAssigned, int CountUnassigned)>
            GetCollectStudentsPagedAsync(
                long teacherId, string filter, string? search, int page, int pageSize,
                DateTime unpaidThroughMonthEnd, IReadOnlyCollection<long>? sessionScopeIds = null)
        {
            // Global !IsDeleted query filter on TeacherStudent applies automatically.
            var baseQuery = _context.TeacherStudents.Where(ts => ts.TeacherId == teacherId);

            // Session-scoped collect (this session + its linked sessions): restrict to students assigned
            // to one of those sessions BEFORE counts/filter, so per-tab counts reflect the scope. Null =
            // teacher-wide (unchanged). Mirrors the take-attendance roster population.
            if (sessionScopeIds is { Count: > 0 })
                baseQuery = baseQuery.Where(ts =>
                    ts.SessionId != null && sessionScopeIds.Contains(ts.SessionId.Value));

            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = ArabicTextNormalizer.Normalize(search.Trim());
                baseQuery = baseQuery.Where(ts =>
                    DbSearch.ArabicNormalize(ts.StudentName).Contains(s) || DbSearch.ArabicNormalize(ts.StudentCode).Contains(s));
            }

            // Per-tab counts reflect the current search.
            int countAll = await baseQuery.CountAsync();
            int countAssigned = await baseQuery.CountAsync(ts => ts.SessionId != null);
            int countUnassigned = countAll - countAssigned;

            var filtered = baseQuery;
            if (string.Equals(filter, "assigned", StringComparison.OrdinalIgnoreCase))
                filtered = filtered.Where(ts => ts.SessionId != null);
            else if (string.Equals(filter, "unassigned", StringComparison.OrdinalIgnoreCase))
                filtered = filtered.Where(ts => ts.SessionId == null);

            int totalCount = await filtered.CountAsync();

            // Page first, then LEFT-join each row to its counter (correlated TOP-1 subquery,
            // bounded to pageSize rows � no N+1 across the full set).
            var raw = await filtered
                .OrderBy(ts => ts.StudentName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(ts => new
                {
                    ts.Id,
                    ts.StudentName,
                    ts.StudentCode,
                    IsAssigned = ts.SessionId != null,
                    SessionName = ts.Session != null ? ts.Session.SessionName : null,
                    SessionAmount = ts.Session != null ? ts.Session.SessionAmount : (decimal?)null,
                    CustomAmount = _context.StudentPaymentCounters
                        .Where(c => c.TeacherId == teacherId && c.TeacherStudentId == ts.Id)
                        .Select(c => c.CustomPaymentAmount)
                        .FirstOrDefault(),
                    // Unpaid status is judged THROUGH the current month only � future pre-generated
                    // periods must not make an otherwise-caught-up student read as unpaid.
                    UnpaidMonths = _context.PaymentPeriods
                        .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == ts.Id
                            && p.PeriodStart <= unpaidThroughMonthEnd
                            && p.PaymentStatus != PaymentStatus.Paid)
                        .Count(),
                    // Full arrears through the current month (what "mark paid" actually collects), so the
                    // collect list can show the true owed (e.g. 600) with a counter, not a single month.
                    TotalOwed = _context.PaymentPeriods
                        .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == ts.Id
                            && p.PeriodStart <= unpaidThroughMonthEnd
                            && p.PaymentStatus != PaymentStatus.Paid)
                        .Sum(p => (decimal?)(p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m))) ?? 0m
                })
                .AsNoTracking()
                .ToListAsync();

            // One batched fetch of the page's unpaid periods (≤ pageSize students), grouped
            // per student oldest-first — the same itemization the collect lookup returns, so
            // the bulk multi-select can seed the SAME review queue as QR scanning.
            var pageIds = raw.Select(r => r.Id).ToList();
            var unpaidByStudent = (await _context.PaymentPeriods
                    .Where(p => p.TeacherId == teacherId && p.TeacherStudentId != null
                        && pageIds.Contains(p.TeacherStudentId!.Value)
                        && p.PeriodStart <= unpaidThroughMonthEnd
                        && p.PaymentStatus != PaymentStatus.Paid)
                    .OrderBy(p => p.PeriodSequence).ThenBy(p => p.PeriodStart)
                    .Select(p => new
                    {
                        StudentId = p.TeacherStudentId!.Value,
                        Month = new CollectLookupUnpaidMonth
                        {
                            PeriodId = p.Id,
                            PeriodStart = p.PeriodStart,
                            Remaining = p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m),
                            IsProRated = p.IsProRated,
                            ProRatedFraction = p.ProRatedFraction,
                            IsProrationAnchorMonth = p.IsProrationAnchorMonth,
                            IsProrationManual = p.IsProrationManual
                        }
                    })
                    .AsNoTracking()
                    .ToListAsync())
                .GroupBy(x => x.StudentId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Month).ToList());

            var items = raw.Select(r => new CollectStudentRow
            {
                TeacherStudentId = r.Id,
                StudentName = r.StudentName,
                StudentCode = r.StudentCode,
                IsAssigned = r.IsAssigned,
                SessionName = r.SessionName,
                Amount = r.CustomAmount ?? r.SessionAmount ?? 0m,
                IsUnpaid = r.UnpaidMonths > 0,
                UnpaidMonths = r.UnpaidMonths,
                TotalOwed = r.TotalOwed,
                UnpaidMonthsList = unpaidByStudent.TryGetValue(r.Id, out var months)
                    ? months
                    : new List<CollectLookupUnpaidMonth>()
            }).ToList();

            return (items, totalCount, countAll, countAssigned, countUnassigned);
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<StudentByStatusRow> Items, int TotalCount, decimal GroupCollected, decimal GroupExpected, decimal GroupUnpaid, decimal GroupExpectedRate)>
            GetStudentsByPaymentStatusPagedAsync(
                long teacherId, string? status,
                DateTime monthStart, DateTime monthEnd,
                int page, int pageSize,
                long? sessionId = null, string? search = null)
        {
            // Only students CURRENTLY assigned to a session (SessionId != null) are classified,
            // so these lists reconcile with GetStudentPaymentStatusCountsAsync / the tracking
            // screen's TotalStudents. Formerly-assigned students with lingering historical periods
            // are excluded from every bucket.
            //
            // B1: sessionId (optional) narrows the assigned scope to ONE session — powering a
            // per-session paid/unpaid roster — and search (optional) filters by name OR studentCode.
            // Both intersect with (never replace) the assigned-only gating above.
            var assignedQuery = _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && ts.SessionId != null);
            if (sessionId.HasValue)
                assignedQuery = assignedQuery.Where(ts => ts.SessionId == sessionId.Value);
            if (!string.IsNullOrWhiteSpace(search))
            {
                string searchLower = ArabicTextNormalizer.Normalize(search.Trim());
                assignedQuery = assignedQuery.Where(ts =>
                    DbSearch.ArabicNormalize(ts.StudentName).Contains(searchLower)
                    || (ts.StudentCode != null && DbSearch.ArabicNormalize(ts.StudentCode).Contains(searchLower)));
            }
            var assignedStudentIds = assignedQuery.Select(ts => ts.Id);

            // Everything is judged THROUGH the selected month � periods that start after the
            // month end (pre-generated future months) are excluded from every bucket and total.
            var withPeriods = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId.HasValue
                    && assignedStudentIds.Contains(p.TeacherStudentId!.Value)
                    && p.PeriodStart <= monthEnd);

            // Per-student classification by the earliest outstanding period (same rule as
            // GetStudentPaymentStatusCountsAsync). One row per student with an outstanding period.
            var earliestOutstanding = withPeriods
                .Where(p => p.PaymentStatus != PaymentStatus.Paid)
                .GroupBy(p => p.TeacherStudentId!.Value)
                .Select(g => new
                {
                    StudentId = g.Key,
                    IsProRated = g.OrderBy(p => p.PeriodSequence).First().IsProRated
                });

            // Materialize the target student-id set for the requested status (bounded to the
            // teacher's students; avoids deeply-nested subqueries the provider may not translate).
            //
            // B1: when status is null, the caller wants the WHOLE (assigned) scope with each
            // student carrying its own status — so target every scope student (including those
            // with no period yet, who read as "paid") and stamp each row's status below.
            List<long> targetIds;
            if (status is null)
            {
                targetIds = await assignedStudentIds.ToListAsync();
            }
            else if (string.Equals(status, "paid", StringComparison.OrdinalIgnoreCase))
            {
                var allIds = await withPeriods.Select(p => p.TeacherStudentId!.Value).Distinct().ToListAsync();
                var outstanding = (await earliestOutstanding.Select(e => e.StudentId).ToListAsync()).ToHashSet();
                targetIds = allIds.Where(id => !outstanding.Contains(id)).ToList();
            }
            else if (string.Equals(status, "prorated", StringComparison.OrdinalIgnoreCase))
            {
                targetIds = await earliestOutstanding.Where(e => e.IsProRated).Select(e => e.StudentId).ToListAsync();
            }

            else if (string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase))
            {
                // "Part Paid" chip: students with a period IN the requested month that is
                // partially settled (0 < AmountPaid < AmountDue). Month-scoped by design �
                // the screen header is month-relative ("monthly collected (march)").
                targetIds = await _context.PaymentPeriods
                    .Where(p => p.TeacherId == teacherId
                        && p.TeacherStudentId.HasValue
                        && assignedStudentIds.Contains(p.TeacherStudentId!.Value)
                        && p.PeriodStart >= monthStart && p.PeriodStart <= monthEnd
                        && p.PaymentStatus == PaymentStatus.PartiallyPaid)
                    .Select(p => p.TeacherStudentId!.Value)
                    .Distinct()
                    .ToListAsync();
            }
            else // unpaid
            {
                targetIds = await earliestOutstanding.Where(e => !e.IsProRated).Select(e => e.StudentId).ToListAsync();
            }

            int totalCount = targetIds.Count;

            // Group aggregates: month-scoped collected/expected, plus total outstanding.
            var groupMonthPeriods = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId.HasValue
                    && targetIds.Contains(p.TeacherStudentId!.Value)
                    && p.PeriodStart >= monthStart && p.PeriodStart <= monthEnd);

            decimal groupCollected = await groupMonthPeriods.SumAsync(p => (decimal?)p.AmountPaid) ?? 0m;
            // What this scope is actually BILLED for the month, NET OF FORGIVEN — the same basis as the
            // tracking card's "Expected · This month", so the session screens sum back to the card to the
            // cent (forgiving money must lower both, never just one). Distinct from GroupExpectedRate
            // below, which is the LIVE per-student rate and answers a different question.
            decimal groupExpected = await groupMonthPeriods
                .SumAsync(p => (decimal?)(p.AmountDue - (p.ForgivenAmount ?? 0m))) ?? 0m;
            // Outstanding is the arrears THROUGH the selected month only (not the all-time counter,
            // which includes pre-generated future months): sum of (due - paid) over unpaid periods
            // whose start is on/before the month end.
            decimal groupUnpaid = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId.HasValue
                    && targetIds.Contains(p.TeacherStudentId!.Value)
                    && p.PeriodStart <= monthEnd
                    && p.PaymentStatus != PaymentStatus.Paid)
                .SumAsync(p => (decimal?)(p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m))) ?? 0m;

            // Full-set expected revenue: each in-scope student's MONTHLY RATE — their custom override
            // (StudentPaymentCounter.CustomPaymentAmount) or the session default — summed over EVERY
            // targeted student, including those with no month period yet (absent from groupExpected).
            // Reuses the already-materialized targetIds and the SAME per-student rate projection as the
            // paged rows below; drives the session-detail "expected revenue" so a per-student custom
            // amount is reflected instead of the session default × student-count the client used to use.
            var expectedRates = await _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && targetIds.Contains(ts.Id))
                .Select(ts => (decimal?)(
                    (_context.StudentPaymentCounters
                        .Where(c => c.TeacherId == teacherId && c.TeacherStudentId == ts.Id)
                        .Select(c => c.CustomPaymentAmount).FirstOrDefault())
                    ?? (ts.Session != null ? ts.Session.SessionAmount : 0m)))
                .ToListAsync();
            decimal groupExpectedRate = expectedRates.Sum(r => r ?? 0m);

            // Page the students (name order) with their month amounts + counter fields.
            var items = await _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && targetIds.Contains(ts.Id))
                .OrderBy(ts => ts.StudentName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(ts => new StudentByStatusRow
                {
                    TeacherStudentId = ts.Id,
                    StudentName = ts.StudentName,
                    AmountPerMonth =
                        (_context.StudentPaymentCounters
                            .Where(c => c.TeacherId == teacherId && c.TeacherStudentId == ts.Id)
                            .Select(c => c.CustomPaymentAmount).FirstOrDefault())
                        ?? (ts.Session != null ? ts.Session.SessionAmount : 0m),
                    AmountPaid = _context.PaymentPeriods
                        .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == ts.Id
                            && p.PeriodStart >= monthStart && p.PeriodStart <= monthEnd)
                        .Sum(p => (decimal?)p.AmountPaid) ?? 0m,
                    // NET OF FORGIVEN, like the session header above it (monthAmount) and like the
                    // tracking card. Forgiving reduces what the student owes, so a gross figure here
                    // made the rows stop adding up to their own header the moment anything was
                    // forgiven. UnpaidAmount below has always subtracted it.
                    AmountDue = _context.PaymentPeriods
                        .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == ts.Id
                            && p.PeriodStart >= monthStart && p.PeriodStart <= monthEnd)
                        .Sum(p => (decimal?)(p.AmountDue - (p.ForgivenAmount ?? 0m))) ?? 0m,
                    // Arrears THROUGH the selected month only (not the all-time counter): sum of
                    // (due - paid) and count of unpaid periods whose start is on/before month end.
                    UnpaidAmount = _context.PaymentPeriods
                        .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == ts.Id
                            && p.PeriodStart <= monthEnd && p.PaymentStatus != PaymentStatus.Paid)
                        .Sum(p => (decimal?)(p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m))) ?? 0m,
                    UnpaidMonths = _context.PaymentPeriods
                        .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == ts.Id
                            && p.PeriodStart <= monthEnd && p.PaymentStatus != PaymentStatus.Paid)
                        .Count(),
                        StudentCode = ts.StudentCode,

                    // The student's currently-assigned session (never null here — the
                    // assigned-only gate above filters SessionId != null). Distinct from
                    // SessionName below, which reflects the session they PAID on.
                    SessionId = ts.SessionId,

                    // "Paid on" + "session he paid on" + "collected by": the student's latest paying
                    // transaction that SETTLED an in-month period. A month can be cleared as a
                    // non-oldest slice of a multi-month cascade, whose transaction FK points only at
                    // the OLDEST period — so we match on the allocation ledger (a transaction with an
                    // allocation to an in-month period) OR, for legacy transactions with no
                    // allocations, the single denormalized transaction→period FK. Same Where + tiebreak
                    // (Id) across the three subqueries so they always resolve to the SAME transaction.
                    // The global query filter already excludes soft-deleted transactions.
                    PaidOn = _context.PaymentTransactions
                        .Where(t => t.TeacherId == teacherId
                            && t.TeacherStudentId == ts.Id
                            && (t.Allocations.Any(a => a.PaymentPeriod != null
                                    && a.PaymentPeriod.PeriodStart >= monthStart
                                    && a.PaymentPeriod.PeriodStart <= monthEnd)
                                || (t.PaymentPeriod != null
                                    && t.PaymentPeriod.PeriodStart >= monthStart
                                    && t.PaymentPeriod.PeriodStart <= monthEnd)))
                        .OrderByDescending(t => t.CollectedAt)
                        .ThenByDescending(t => t.Id)
                        .Select(t => (DateTime?)t.CollectedAt)
                        .FirstOrDefault(),

                    CollectedByUserId = _context.PaymentTransactions
                        .Where(t => t.TeacherId == teacherId
                            && t.TeacherStudentId == ts.Id
                            && (t.Allocations.Any(a => a.PaymentPeriod != null
                                    && a.PaymentPeriod.PeriodStart >= monthStart
                                    && a.PaymentPeriod.PeriodStart <= monthEnd)
                                || (t.PaymentPeriod != null
                                    && t.PaymentPeriod.PeriodStart >= monthStart
                                    && t.PaymentPeriod.PeriodStart <= monthEnd)))
                        .OrderByDescending(t => t.CollectedAt)
                        .ThenByDescending(t => t.Id)
                        .Select(t => t.CollectedByUserId)
                        .FirstOrDefault(),

                    SessionName =
                        _context.PaymentTransactions
                            .Where(t => t.TeacherId == teacherId
                                && t.TeacherStudentId == ts.Id
                                && (t.Allocations.Any(a => a.PaymentPeriod != null
                                        && a.PaymentPeriod.PeriodStart >= monthStart
                                        && a.PaymentPeriod.PeriodStart <= monthEnd)
                                    || (t.PaymentPeriod != null
                                        && t.PaymentPeriod.PeriodStart >= monthStart
                                        && t.PaymentPeriod.PeriodStart <= monthEnd)))
                            .OrderByDescending(t => t.CollectedAt)
                            .ThenByDescending(t => t.Id)
                            .Select(t => t.SessionName)
                            .FirstOrDefault()
                        // Fallback (unpaid/prorated, no payment yet): the month's period session.
                        ?? _context.PaymentPeriods
                            .Where(p => p.TeacherId == teacherId
                                && p.TeacherStudentId == ts.Id
                                && p.PeriodStart >= monthStart && p.PeriodStart <= monthEnd)
                            .OrderBy(p => p.PeriodSequence)
                            .Select(p => p.SessionName)
                            .FirstOrDefault()
                })
                .AsNoTracking()
                .ToListAsync();

            // B1: when no status filter was supplied, stamp each row with its OWN computed status
            // (paid | prorated | unpaid) by the earliest-outstanding-period rule, so a single call
            // returns a mixed-status roster. A student with an outstanding period through the month
            // is prorated (when that earliest period is prorated) or unpaid; otherwise paid. With a
            // status filter, every row already matches it and the service uses the requested status.
            if (status is null && items.Count > 0)
            {
                var outstandingMap = (await earliestOutstanding.ToListAsync())
                    .ToDictionary(e => e.StudentId, e => e.IsProRated);
                foreach (var row in items)
                {
                    row.Status = outstandingMap.TryGetValue(row.TeacherStudentId, out bool isProRated)
                        ? (isProRated ? "prorated" : "unpaid")
                        : "paid";
                }
            }

            return (items, totalCount, groupCollected, groupExpected, groupUnpaid, groupExpectedRate);
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<YearlyStudentRow> Items, int TotalCount)>
            GetYearlyCollectionsPagedAsync(
                long teacherId, DateTime yearStart, DateTime yearEnd, int page, int pageSize)
        {
            // Students with at least one period in the year.
            var studentIdsInYear = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId.HasValue
                    && p.PeriodStart >= yearStart && p.PeriodStart <= yearEnd)
                .Select(p => p.TeacherStudentId!.Value)
                .Distinct();

            int totalCount = await _context.TeacherStudents
                .CountAsync(ts => ts.TeacherId == teacherId && studentIdsInYear.Contains(ts.Id));

            var pageStudents = await _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && studentIdsInYear.Contains(ts.Id))
                .OrderBy(ts => ts.StudentName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(ts => new { ts.Id, ts.StudentName })
                .AsNoTracking()
                .ToListAsync();

            var pageIds = pageStudents.Select(s => s.Id).ToList();

            // Aggregate the page students' year periods per (student, calendar month). Count-based
            // paid/prorated flags (not All/Any) so the whole thing translates to a single GROUP BY.
            var monthAgg = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId.HasValue
                    && pageIds.Contains(p.TeacherStudentId!.Value)
                    && p.PeriodStart >= yearStart && p.PeriodStart <= yearEnd)
                .GroupBy(p => new { StudentId = p.TeacherStudentId!.Value, Month = p.PeriodStart.Month })
                .Select(g => new
                {
                    g.Key.StudentId,
                    g.Key.Month,
                    AmountDue = g.Sum(x => x.AmountDue),
                    AmountPaid = g.Sum(x => x.AmountPaid),
                    Periods = g.Count(),
                    PaidCount = g.Sum(x => x.PaymentStatus == PaymentStatus.Paid ? 1 : 0),
                    ProRatedCount = g.Sum(x => x.IsProRated ? 1 : 0)
                })
                .AsNoTracking()
                .ToListAsync();

            var cellsByStudent = monthAgg
                .GroupBy(m => m.StudentId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(m => m.Month).Select(m => new YearlyMonthCell
                    {
                        Month = m.Month,
                        AmountDue = m.AmountDue,
                        AmountPaid = m.AmountPaid,
                        IsPaid = m.Periods == m.PaidCount,
                        IsProRated = m.ProRatedCount > 0
                    }).ToList());

            var items = pageStudents.Select(s => new YearlyStudentRow
            {
                TeacherStudentId = s.Id,
                StudentName = s.StudentName,
                Months = cellsByStudent.TryGetValue(s.Id, out var cells) ? cells : new List<YearlyMonthCell>()
            }).ToList();

            return (items, totalCount);
        }

        /// <inheritdoc />
        public async Task<CollectLookupRow?> ResolveCollectLookupAsync(
            long teacherId, string? qr, string? code, string? name, DateTime throughMonthEnd,
            DateTime? advanceCapEnd = null)
        {
            var q = _context.TeacherStudents.Where(ts => ts.TeacherId == teacherId);

            // Resolution priority: QR/barcode ? student code ? name (first match).
            if (!string.IsNullOrWhiteSpace(qr))
            {
                string barcode = qr.Trim();
                q = q.Where(ts => ts.Barcode == barcode || ts.StudentCode == barcode);
            }
            else if (!string.IsNullOrWhiteSpace(code))
            {
                string c = code.Trim().ToUpper(); // codes stored uppercase (REQ-STU-CODE-003)
                q = q.Where(ts => ts.StudentCode == c);
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                string n = ArabicTextNormalizer.Normalize(name.Trim());
                q = q.Where(ts => DbSearch.ArabicNormalize(ts.StudentName).Contains(n));
            }
            else
            {
                return null;
            }

            var student = await q
                .OrderBy(ts => ts.StudentName)
                .Select(ts => new
                {
                    ts.Id,
                    ts.StudentName,
                    ts.StudentCode,
                    Group = ts.Session != null ? ts.Session.SessionName : null,
                    SessionAmount = ts.Session != null ? ts.Session.SessionAmount : (decimal?)null,
                    // Per-student custom override (BR-PAY-003) wins over the session amount.
                    CustomAmount = _context.StudentPaymentCounters
                        .Where(c => c.TeacherId == teacherId && c.TeacherStudentId == ts.Id)
                        .Select(c => c.CustomPaymentAmount)
                        .FirstOrDefault()
                })
                .FirstOrDefaultAsync();

            if (student is null) return null;

            // AmountDue/totalOwed = the student's TOTAL arrears through the selected/current month (sum
            // of every unpaid month's remaining), not a single month. Excludes months in advance.
            // monthsOwed = how many unpaid months make up that total. One grouped query returns both.
            var arrears = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == student.Id
                    && p.PaymentStatus != PaymentStatus.Paid
                    && p.PeriodStart <= throughMonthEnd)
                .GroupBy(p => 1)
                .Select(g => new
                {
                    Total = g.Sum(p => (decimal?)(p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m))) ?? 0m,
                    Count = g.Count()
                })
                .FirstOrDefaultAsync();

            decimal overdueTotal = arrears?.Total ?? 0m;
            int monthsOwed = arrears?.Count ?? 0;

            // Itemized unpaid months (oldest-first = the cascade settlement order) so the collect UI can
            // label exactly which month each payment covers and offer a 1..N month counter. Only months
            // with a positive remaining are returned (a fully-forgiven month is Paid and excluded).
            var unpaidMonths = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == student.Id
                    && p.PaymentStatus != PaymentStatus.Paid
                    && p.PeriodStart <= throughMonthEnd)
                .OrderBy(p => p.PeriodSequence).ThenBy(p => p.PeriodStart)
                .Select(p => new CollectLookupUnpaidMonth
                {
                    PeriodId = p.Id,
                    PeriodStart = p.PeriodStart,
                    Remaining = p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m),
                    IsProRated = p.IsProRated,
                    ProRatedFraction = p.ProRatedFraction,
                    IsProrationAnchorMonth = p.IsProrationAnchorMonth,
                    IsProrationManual = p.IsProrationManual
                })
                .ToListAsync();

            // One-month-in-advance OFFER: when the student is fully caught up through the
            // selected/current month (no arrears), find the NEXT unpaid month within the advance window
            // (current + 1, the same cap CollectPaymentAsync enforces). It is returned in a SEPARATE
            // field — arrears/AmountDue/IsUnpaid stay untouched so the student keeps reading as PAID
            // everywhere (roster, attendance scan, status). The deliberate collect pop-up is the only
            // place that surfaces it. Null while arrears exist, no next-month period exists, or the
            // lookup is not scoped to the current month (advanceCapEnd is null then).
            CollectLookupUnpaidMonth? advanceMonth = null;
            if (overdueTotal == 0m && advanceCapEnd.HasValue)
            {
                advanceMonth = await _context.PaymentPeriods
                    .Where(p => p.TeacherId == teacherId && p.TeacherStudentId == student.Id
                        && p.PaymentStatus != PaymentStatus.Paid
                        && p.PeriodStart > throughMonthEnd
                        && p.PeriodStart <= advanceCapEnd.Value
                        && (p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m)) > 0m)
                    .OrderBy(p => p.PeriodSequence).ThenBy(p => p.PeriodStart)
                    .Select(p => new CollectLookupUnpaidMonth
                    {
                        PeriodId = p.Id,
                        PeriodStart = p.PeriodStart,
                        Remaining = p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m),
                        IsProRated = p.IsProRated,
                        ProRatedFraction = p.ProRatedFraction,
                        IsProrationAnchorMonth = p.IsProrationAnchorMonth,
                        IsProrationManual = p.IsProrationManual
                    })
                    .FirstOrDefaultAsync();
            }

            return new CollectLookupRow
            {
                AdvanceMonth = advanceMonth,
                TeacherStudentId = student.Id,
                StudentName = student.StudentName,
                StudentCode = student.StudentCode,
                Group = student.Group,
                AmountDue = overdueTotal,
                IsUnpaid = overdueTotal > 0m,
                // Per-month rate: custom override else the session amount else 0.
                MonthlyAmount = student.CustomAmount ?? student.SessionAmount ?? 0m,
                MonthsOwed = monthsOwed,
                UnpaidMonths = unpaidMonths
            };
        }

        // ----------------------------------------------
        // ATTENDANCE SCREEN PAYMENT ENRICHMENT (ShowPaymentInfoOnAttendanceScreen)
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task<Dictionary<long, AttendanceScreenPaymentInfoRow>> GetPaymentInfoForAttendanceBatchAsync(
            long teacherId, IReadOnlyCollection<long> teacherStudentIds,
            DateTime throughMonthEnd, DateTime lastMonthStart, DateTime lastMonthEnd)
        {
            var idList = teacherStudentIds.Distinct().ToList();
            if (idList.Count == 0)
                return new Dictionary<long, AttendanceScreenPaymentInfoRow>();

            // Same cutoff rule as GetUnpaidStudentsPagedAsync (CLAUDE.md Â§7.4): judge arrears only
            // through the cutoff month so pre-generated future periods are never counted as owed.
            var periodRefs = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId != null
                    && idList.Contains(p.TeacherStudentId!.Value)
                    && p.PaymentStatus != PaymentStatus.Paid
                    && p.PeriodStart <= throughMonthEnd)
                .OrderBy(p => p.PeriodSequence)
                .Select(p => new
                {
                    StudentId = p.TeacherStudentId!.Value,
                    Ref = new UnpaidPeriodRef
                    {
                        PeriodType = p.PeriodType,
                        PeriodStart = p.PeriodStart,
                        PeriodEnd = p.PeriodEnd,
                        AmountRemaining = p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m)
                    }
                })
                .AsNoTracking()
                .ToListAsync();

            // Both month flags are derived in-memory from periodRefs above (which already fetched
            // ALL unpaid periods with PeriodStart <= throughMonthEnd, so last-month and current-month
            // periods are both present). No extra query is needed. throughMonthEnd is the last day of
            // the teacher's current local month, so its first-of-month is the current-month window
            // start. Existence is judged against the exact period window (not the unpaid-tail count),
            // so it stays correct independent of the oldest-first collection assumption (BR-PAY-006).
            var currentMonthStart = new DateTime(throughMonthEnd.Year, throughMonthEnd.Month, 1);

            var result = new Dictionary<long, AttendanceScreenPaymentInfoRow>();
            foreach (var group in periodRefs.GroupBy(x => x.StudentId))
            {
                var refs = group.Select(x => x.Ref).ToList();
                result[group.Key] = new AttendanceScreenPaymentInfoRow
                {
                    TeacherStudentId = group.Key,
                    HasUnpaidLastMonth = refs.Any(r =>
                        r.PeriodStart >= lastMonthStart && r.PeriodStart <= lastMonthEnd),
                    HasUnpaidCurrentMonth = refs.Any(r =>
                        r.PeriodStart >= currentMonthStart && r.PeriodStart <= throughMonthEnd),
                    UnpaidMonthsCount = refs.Count,
                    UnpaidAmount = refs.Sum(r => r.AmountRemaining),
                    UnpaidPeriods = refs
                };
            }

            return result;
        }

        // ----------------------------------------------
        // ASSISTANT WALLET QUERIES
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task<AssistantWallet?> GetAssistantWalletAsync(
            long teacherId, long assistantId)
        {
            return await _context.AssistantWallets
                .Include(w => w.Assistant)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(w => w.TeacherId == teacherId
                    && w.AssistantId == assistantId);
        }

        /// <inheritdoc />
        public async Task<AssistantWallet?> GetAssistantWalletByUserIdAsync(
            long teacherId, long assistantUserId)
        {
            // BUGFIX (2026-08-01): Include added so assistant.name is populated on the AssistantWallet
            // screen for an assistant caller (this method -- not GetAssistantWalletAsync -- resolves
            // their own wallet; see PaymentScreenService.GetAssistantWalletScreenAsync). Deliberately
            // NOT AsNoTracking: this method also sits on the collect hot path
            // (UpdateAssistantWalletAfterCollectionAsync / AdjustAssistantWalletAsync), which needs the
            // returned entity tracked for the RowVersion concurrency-retry loop.
            return await _context.AssistantWallets
                .Include(w => w.Assistant)
                    .ThenInclude(a => a.User)
                .Include(w => w.CenterAssistant)
                    .ThenInclude(ca => ca.User)
                .FirstOrDefaultAsync(w => w.TeacherId == teacherId
                    && w.AssistantUserId == assistantUserId);
        }

        /// <inheritdoc />
        public async Task<AssistantWallet?> GetAssistantWalletByAssistantIdAsync(long assistantId)
        {
            // Cross-tenant (SuperAdmin recompute); tracked so the caller can write CurrentBalance.
            return await _context.AssistantWallets
                .Include(w => w.Assistant)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(w => w.AssistantId == assistantId);
        }

        /// <inheritdoc />
        public async Task<AssistantWallet?> GetAssistantWalletByCenterAssistantIdAsync(
            long teacherId, long centerAssistantId)
        {
            return await _context.AssistantWallets
                .Include(w => w.CenterAssistant)
                    .ThenInclude(ca => ca.User)
                .FirstOrDefaultAsync(w => w.TeacherId == teacherId
                    && w.CenterAssistantId == centerAssistantId);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AssistantWallet>> GetAllAssistantWalletsAsync(long teacherId)
        {
            // NOTE: no SQL ORDER BY here — a center-assistant wallet has no Assistant nav, and a
            // null-safe CASE across two optional navs is fragile to translate. The caller sorts by
            // the resolved name in memory (the per-teacher wallet list is tiny).
            return await _context.AssistantWallets
                .Where(w => w.TeacherId == teacherId)
                .Include(w => w.Assistant)
                    .ThenInclude(a => a.User)
                .Include(w => w.CenterAssistant)
                    .ThenInclude(ca => ca.User)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task AddAssistantWalletAsync(AssistantWallet wallet)
        {
            await _context.AssistantWallets.AddAsync(wallet);
        }

        /// <inheritdoc />
        public async Task UpdateAssistantWalletAsync(AssistantWallet wallet)
        {
            _context.Entry(wallet).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        // ----------------------------------------------
        // WALLET RESET LOG QUERIES
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task AddWalletResetLogAsync(WalletResetLog log)
        {
            await _context.WalletResetLogs.AddAsync(log);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<WalletResetLog>> GetWalletResetLogsAsync(
            long teacherId, long assistantId)
        {
            return await _context.WalletResetLogs
                .Where(l => l.TeacherId == teacherId && l.AssistantId == assistantId)
                .OrderByDescending(l => l.ResetAt)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<WalletResetLog?> GetWalletResetLogByIdAsync(long walletResetLogId)
        {
            return await _context.WalletResetLogs
                .FirstOrDefaultAsync(l => l.Id == walletResetLogId);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<WalletResetLog>> GetWalletResetLogsForCenterAssistantAsync(
            long teacherId, long centerAssistantId)
        {
            return await _context.WalletResetLogs
                .Where(l => l.TeacherId == teacherId && l.CenterAssistantId == centerAssistantId)
                .OrderByDescending(l => l.ResetAt)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<DateTime?> GetLastWalletResetAtByWalletIdAsync(long teacherId, long assistantWalletId)
        {
            // Keyed by AssistantWalletId so it works for both a normal-assistant and a center-assistant
            // wallet (both write it). Max ResetAt = the most recent hand-over (full reset OR partial
            // withdrawal).
            return await _context.WalletResetLogs
                .Where(l => l.TeacherId == teacherId && l.AssistantWalletId == assistantWalletId)
                .MaxAsync(l => (DateTime?)l.ResetAt);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<WalletResetLog>> GetWalletResetLogsForCollectorInRangeAsync(
            long teacherId, long collectorUserId, DateTime startInclusive, DateTime endExclusive)
        {
            // The collector's teacher-scoped Assistant record; a tutor collecting their own cash has no
            // Assistant/wallet, so this resolves to nothing and no withdrawal lines are produced. A
            // CenterAssistant collector resolves via CenterAssistant instead.
            var assistantIds = _context.Set<Assistant>()
                .Where(a => a.UserId == collectorUserId && a.TeacherAccountId == teacherId)
                .Select(a => (long?)a.Id);
            var centerAssistantIds = _context.Set<CenterAssistant>()
                .Where(a => a.UserId == collectorUserId)
                .Select(a => (long?)a.Id);

            return await _context.WalletResetLogs
                .Where(l => l.TeacherId == teacherId
                    && ((l.AssistantId != null && assistantIds.Contains(l.AssistantId))
                        || (l.CenterAssistantId != null && centerAssistantIds.Contains(l.CenterAssistantId)))
                    && l.ResetAt >= startInclusive && l.ResetAt < endExclusive)
                .OrderByDescending(l => l.ResetAt)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<DateTime?> GetLastWalletResetAtAsync(long teacherId, long assistantId)
        {
            return await _context.WalletResetLogs
                .Where(l => l.TeacherId == teacherId && l.AssistantId == assistantId)
                .OrderByDescending(l => l.ResetAt)
                .Select(l => (DateTime?)l.ResetAt)
                .FirstOrDefaultAsync();
        }

        // ----------------------------------------------
        // PAYMENT EDIT LOG QUERIES
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task AddPaymentEditLogAsync(PaymentEditLog log)
        {
            await _context.PaymentEditLogs.AddAsync(log);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentEditLog>> GetPaymentEditLogsAsync(
            long paymentTransactionId)
        {
            return await _context.PaymentEditLogs
                .Where(l => l.PaymentTransactionId == paymentTransactionId)
                .OrderBy(l => l.EditedAt)
                .AsNoTracking()
                .ToListAsync();
        }

        // ----------------------------------------------
        // FORGIVENESS QUERIES (waive outstanding balance — reversible)
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task AddPaymentForgivenessAsync(PaymentForgiveness forgiveness)
        {
            await _context.PaymentForgivenesses.AddAsync(forgiveness);
        }

        /// <inheritdoc />
        public async Task AddPaymentForgivenessAllocationsRangeAsync(
            IEnumerable<PaymentForgivenessAllocation> allocations)
        {
            await _context.PaymentForgivenessAllocations.AddRangeAsync(allocations);
        }

        /// <inheritdoc />
        public async Task<PaymentForgiveness?> GetForgivenessByIdAndTeacherAsync(
            long teacherId, long forgivenessId)
        {
            // TRACKED + eager allocations + each allocation's PaymentPeriod (also tracked) so the
            // reversal can restore the exact per-period ForgivenAmount in place.
            return await _context.PaymentForgivenesses
                .Include(f => f.Allocations)
                    .ThenInclude(a => a.PaymentPeriod)
                .Where(f => f.Id == forgivenessId && f.TeacherId == teacherId)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PaymentForgiveness>> GetForgivenessesByStudentAsync(
            long teacherId, long teacherStudentId)
        {
            return await _context.PaymentForgivenesses
                .Where(f => f.TeacherId == teacherId && f.TeacherStudentId == teacherStudentId)
                .OrderByDescending(f => f.ForgivenAt)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<decimal> GetStudentMonthlyRateAsync(long teacherId, long teacherStudentId)
        {
            // Custom per-student override wins (BR-PAY-003); else the current session's amount; else 0.
            var custom = await _context.StudentPaymentCounters
                .Where(c => c.TeacherId == teacherId && c.TeacherStudentId == teacherStudentId)
                .Select(c => c.CustomPaymentAmount)
                .FirstOrDefaultAsync();
            if (custom.HasValue) return custom.Value;

            return await _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && ts.Id == teacherStudentId && ts.Session != null)
                .Select(ts => ts.Session!.SessionAmount)
                .FirstOrDefaultAsync();
        }

        // ----------------------------------------------
        // DEPARTURE QUERIES
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task AddStudentDepartureAsync(StudentDeparture departure)
        {
            await _context.StudentDepartures.AddAsync(departure);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<StudentDeparture>> GetStudentDeparturesAsync(
            long teacherId, long teacherStudentId)
        {
            return await _context.StudentDepartures
                .Where(d => d.TeacherId == teacherId && d.TeacherStudentId == teacherStudentId)
                .OrderByDescending(d => d.DepartedAt)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<DepartureListRow> Items, int TotalCount)> GetDeparturesPagedAsync(
            long teacherId, string? search, int page, int pageSize,
            DateTime? fromInclusive = null, DateTime? toExclusive = null)
        {
            var query = BuildDeparturesQuery(teacherId, search, fromInclusive, toExclusive);

            int total = await query.CountAsync();

            var items = await query
                // Id is the tiebreak: DepartedAt is datetime2(0), so same-second departures would
                // otherwise order non-deterministically and duplicate/drop rows across pages.
                .OrderByDescending(d => d.DepartedAt)
                .ThenByDescending(d => d.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(d => new DepartureListRow
                {
                    Id = d.Id,
                    TeacherStudentId = d.TeacherStudentId,
                    StudentName = d.StudentName,
                    StudentCode = d.StudentCode,
                    // Live session name when the session still exists; else the snapshot.
                    SessionName = d.Session != null ? d.Session.SessionName : d.SessionName,
                    DepartedAt = d.DepartedAt,
                    DepartureOutcome = d.DepartureOutcome,
                    FinalAmount = d.FinalAmount,
                    PaymentStatusAtDeparture = d.PaymentStatusAtDeparture,
                    IsTutorOverride = d.IsTutorOverride,
                    AttendedOccurrences = d.AttendedOccurrences,
                    TotalOccurrencesInPeriod = d.TotalOccurrencesInPeriod,
                    FullPeriodAmount = d.FullPeriodAmount,
                    ProRatedAmount = d.ProRatedAmount,
                    OriginalCalculatedAmount = d.OriginalCalculatedAmount,
                    AnchorPeriodStart = d.AnchorPeriodStart,
                    PaidAmountAtDeparture = d.PaidAmountAtDeparture,
                })
                .AsNoTracking()
                .ToListAsync();

            return (items, total);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<DepartureDayTotalRow>> GetDepartureDayTotalsAsync(
            long teacherId, string? search, DateTime? fromInclusive = null, DateTime? toExclusive = null)
        {
            // Grouped over the WHOLE filtered scope (never one page) so the day-separator figures are
            // final and do not shift as the client pages in more rows — same contract as the
            // collections ledger's daily nets.
            return await BuildDeparturesQuery(teacherId, search, fromInclusive, toExclusive)
                .GroupBy(d => d.DepartedAt.Date)
                .Select(g => new DepartureDayTotalRow
                {
                    Date = g.Key,
                    DepartedCount = g.Count(),
                    // The SETTLED figure, so a waived refund correctly contributes 0 to the day.
                    RefundedTotal = g.Sum(d =>
                        d.DepartureOutcome == DepartureOutcome.RefundDue ? d.FinalAmount : 0m),
                    OwedTotal = g.Sum(d =>
                        d.DepartureOutcome == DepartureOutcome.AmountOwed ? d.FinalAmount : 0m),
                })
                .OrderByDescending(x => x.Date)
                .AsNoTracking()
                .ToListAsync();
        }

        /// <summary>
        /// The one filter definition shared by the departures page and its day totals — keeping them
        /// in a single place is what guarantees the separators sum to the rows on screen.
        /// </summary>
        private IQueryable<StudentDeparture> BuildDeparturesQuery(
            long teacherId, string? search, DateTime? fromInclusive, DateTime? toExclusive)
        {
            var query = _context.StudentDepartures.Where(d => d.TeacherId == teacherId);

            if (fromInclusive.HasValue)
                query = query.Where(d => d.DepartedAt >= fromInclusive.Value);
            if (toExclusive.HasValue)
                query = query.Where(d => d.DepartedAt < toExclusive.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = ArabicTextNormalizer.Normalize(search.Trim());
                query = query.Where(d =>
                    (d.StudentName != null && DbSearch.ArabicNormalize(d.StudentName).Contains(s))
                    || (d.StudentCode != null && DbSearch.ArabicNormalize(d.StudentCode).Contains(s)));
            }

            return query;
        }

        /// <inheritdoc />
        public async Task<(int Total, int RefundDue, int AmountOwed)> CountDeparturesInRangeAsync(
            long teacherId, DateTime startInclusive, DateTime endExclusive)
        {
            var query = _context.StudentDepartures
                .Where(d => d.TeacherId == teacherId
                    && d.DepartedAt >= startInclusive && d.DepartedAt < endExclusive);

            int total = await query.CountAsync();
            int refundDue = await query.CountAsync(d => d.DepartureOutcome == DepartureOutcome.RefundDue);
            int amountOwed = await query.CountAsync(d => d.DepartureOutcome == DepartureOutcome.AmountOwed);

            return (total, refundDue, amountOwed);
        }

        // ----------------------------------------------
        // SESSION TRANSFER QUERIES
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task AddSessionTransferEventAsync(SessionTransferEvent transferEvent)
        {
            await _context.SessionTransferEvents.AddAsync(transferEvent);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SessionTransferEvent>> GetStudentTransferEventsAsync(
            long teacherId, long teacherStudentId)
        {
            return await _context.SessionTransferEvents
                .Where(t => t.TeacherId == teacherId && t.TeacherStudentId == teacherStudentId)
                .OrderByDescending(t => t.TransferredAt)
                .AsNoTracking()
                .ToListAsync();
        }

        // ----------------------------------------------
        // DASHBOARD AGGREGATES
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task<(decimal Expected, decimal Collected, decimal Remaining)>
            GetDashboardAggregatesAsync(
                long teacherId,
                long? sessionId, long? sessionGroupId,
                PaymentType? paymentType,
                DateTime? startDate, DateTime? endDate)
        {
            // Exclude orphaned periods (TeacherStudentId nulled when a student is permanently purged) �
            // they are no active student's obligation and must never inflate expected/collected.
            var periodQuery = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.TeacherStudentId != null);

            if (sessionId.HasValue)
                periodQuery = periodQuery.Where(p => p.SessionId == sessionId.Value);

            if (sessionGroupId.HasValue)
                periodQuery = periodQuery.Where(p =>
                    p.Session != null && p.Session.SessionGroupId == sessionGroupId.Value);

            if (startDate.HasValue)
                periodQuery = periodQuery.Where(p => p.PeriodEnd >= startDate.Value);

            if (endDate.HasValue)
                periodQuery = periodQuery.Where(p => p.PeriodStart <= endDate.Value);

            var aggregates = await periodQuery
                .GroupBy(p => 1)
                .Select(g => new
                {
                    Expected = g.Sum(p => p.AmountDue),
                    Collected = g.Sum(p => p.AmountPaid)
                })
                .FirstOrDefaultAsync();

            decimal expected = aggregates?.Expected ?? 0;
            decimal collected = aggregates?.Collected ?? 0;
            return (expected, collected, expected - collected);
        }

        /// <inheritdoc />
        public async Task<(decimal ExpectedThisMonth, decimal CollectedThisMonth, decimal RemainingThisMonth, decimal ExpectedTotal, decimal CollectedTotal, decimal OutstandingTotal, decimal CollectedAdvance)>
            GetAssignedObligationAggregatesAsync(long teacherId, DateTime monthStart, DateTime monthEnd)
        {
            var monthStartD = monthStart.Date;
            var monthEndD = monthEnd.Date;

            // Currently-assigned active students (SessionId != null) — the same population as the
            // status headcounts, so this card's figures reconcile with paid/prorated/unpaid.
            var assignedStudentIds = _context.TeacherStudents
                .Where(ts => ts.TeacherId == teacherId && ts.SessionId != null)
                .Select(ts => ts.Id);

            // Base set: this teacher's non-orphaned periods owned by an assigned student.
            var basePeriods = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId != null
                    && assignedStudentIds.Contains(p.TeacherStudentId!.Value));

            // ── This month: periods overlapping [monthStart, monthEnd] (the current installment). ──
            var thisMonth = basePeriods.Where(p => p.PeriodEnd >= monthStartD && p.PeriodStart <= monthEndD);

            var thisMonthAgg = await thisMonth
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Expected = g.Sum(p => p.AmountDue),
                    Collected = g.Sum(p => p.AmountPaid)
                })
                .FirstOrDefaultAsync();

            // Remaining as an explicit filtered SUM (WHERE not-Paid + SUM) rather than a CASE inside the
            // grouped projection — the provider translates it unambiguously (mirrors
            // GetOverdueTotalThroughAsync).
            decimal remainingThisMonth = await thisMonth
                .Where(p => p.PaymentStatus != PaymentStatus.Paid)
                .SumAsync(p => (decimal?)(p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m))) ?? 0m;
            if (remainingThisMonth < 0m) remainingThisMonth = 0m;

            // ── Through this month: current + every earlier month (PeriodStart ≤ month end). ──
            var throughMonth = basePeriods.Where(p => p.PeriodStart <= monthEndD);

            // ExpectedTotal is the full bill of this month + every earlier month (gross AmountDue),
            // so it is always ≥ ExpectedThisMonth and the Total column reads bigger than This month.
            // Reconciles as ExpectedTotal = CollectedTotal + OutstandingTotal (+ forgiven).
            decimal expectedTotal = await throughMonth.SumAsync(p => (decimal?)p.AmountDue) ?? 0m;

            decimal collectedTotal = await throughMonth.SumAsync(p => (decimal?)p.AmountPaid) ?? 0m;

            decimal outstandingTotal = await throughMonth
                .Where(p => p.PaymentStatus != PaymentStatus.Paid)
                .SumAsync(p => (decimal?)(p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m))) ?? 0m;
            if (outstandingTotal < 0m) outstandingTotal = 0m;

            // ── Paid ahead: money already paid toward FUTURE months (PeriodStart > month end). ──
            // Kept OUTSIDE the through-this-month totals so those reconcile; shown on its own line so
            // advance payments always have a home.
            decimal collectedAdvance = await basePeriods
                .Where(p => p.PeriodStart > monthEndD)
                .SumAsync(p => (decimal?)p.AmountPaid) ?? 0m;

            return (
                thisMonthAgg?.Expected ?? 0m,
                thisMonthAgg?.Collected ?? 0m,
                remainingThisMonth,
                expectedTotal,
                collectedTotal,
                outstandingTotal,
                collectedAdvance);
        }

        /// <inheritdoc />
        public async Task<(decimal Total, decimal ThisMonth, decimal Earlier, decimal Ahead)>
            GetCashCollectedBreakdownAsync(long teacherId, DateTime monthStart, DateTime monthEnd)
        {
            // Mirror GetDashboardPerCollectorAsync's window EXACTLY so this breakdown's Total ties to the
            // collectors' cash total to the cent: cash by CollectedAt in [monthStart, monthEnd + 1 day],
            // NET of StudentDeparture refunds by DepartedAt in the same window. Buckets are computed with
            // explicit filtered SUMs (not a GROUP BY over a CASE key) so translation is never in doubt.
            var windowEnd = monthEnd.Date.AddDays(1);

            // 1) Gross cash physically collected this month (already net of soft-deleted/edited transactions
            //    via the global !IsDeleted filter) — same figure as GetCashCollectedInRangeAsync.
            decimal grossCash = await _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && t.CollectedAt >= monthStart && t.CollectedAt <= windowEnd)
                .SumAsync(t => (decimal?)t.AmountPaid) ?? 0m;

            // 2) Gross cash that settled EARLIER / AHEAD months, from the PAY-1 allocation ledger (multi-month
            //    cascades split across their periods) PLUS legacy rows with no ledger (whole AmountPaid on
            //    their own period). "This month" is the remainder, so any payment with no period at all still
            //    lands somewhere and the three buckets always sum to grossCash.
            async Task<decimal> AllocSumAsync(bool earlierSide) => await _context.PaymentTransactionAllocations
                .Where(a => a.TeacherId == teacherId
                    && !a.PaymentTransaction.IsDeleted
                    && a.PaymentTransaction.CollectedAt >= monthStart && a.PaymentTransaction.CollectedAt <= windowEnd
                    && a.PaymentPeriod != null
                    && (earlierSide ? a.PaymentPeriod!.PeriodStart < monthStart : a.PaymentPeriod!.PeriodStart > monthEnd))
                .SumAsync(a => (decimal?)a.AmountApplied) ?? 0m;

            async Task<decimal> LegacySumAsync(bool earlierSide) => await _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && t.CollectedAt >= monthStart && t.CollectedAt <= windowEnd
                    && !t.Allocations.Any() && t.PaymentPeriod != null
                    && (earlierSide ? t.PaymentPeriod!.PeriodStart < monthStart : t.PaymentPeriod!.PeriodStart > monthEnd))
                .SumAsync(t => (decimal?)t.AmountPaid) ?? 0m;

            decimal grossEarlier = await AllocSumAsync(true) + await LegacySumAsync(true);
            decimal grossAhead = await AllocSumAsync(false) + await LegacySumAsync(false);
            decimal grossThis = grossCash - grossEarlier - grossAhead;   // this month + any no-period remainder

            // 3) Departure refunds (they do NOT soft-delete their transaction) by the month they refunded.
            var refundBase = _context.StudentDepartures
                .Where(d => d.TeacherId == teacherId
                    && d.DepartureOutcome == DepartureOutcome.RefundDue
                    && d.FinalAmount > 0m
                    && d.CollectedByUserId.HasValue
                    && d.DepartedAt >= monthStart && d.DepartedAt <= windowEnd);
            decimal refundTotal = await refundBase.SumAsync(d => (decimal?)d.FinalAmount) ?? 0m;
            decimal refundEarlier = await refundBase
                .Where(d => d.RefundPeriodStart != null && d.RefundPeriodStart < monthStart)
                .SumAsync(d => (decimal?)d.FinalAmount) ?? 0m;
            decimal refundAhead = await refundBase
                .Where(d => d.RefundPeriodStart != null && d.RefundPeriodStart > monthEnd)
                .SumAsync(d => (decimal?)d.FinalAmount) ?? 0m;
            decimal refundThis = refundTotal - refundEarlier - refundAhead;   // this-month + null-period refunds

            // Net cash per bucket. total == grossCash − refundTotal == the collectors' cash total.
            decimal earlier = grossEarlier - refundEarlier;
            decimal thisMonth = grossThis - refundThis;
            decimal ahead = grossAhead - refundAhead;
            return (earlier + thisMonth + ahead, thisMonth, earlier, ahead);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<(long SessionId, string SessionName, long? SessionGroupId, string? SessionGroupName, decimal Expected, decimal Collected, decimal Remaining)>>
            GetDashboardPerSessionAsync(
                long teacherId,
                long? sessionGroupId,
                PaymentType? paymentType,
                DateTime? startDate, DateTime? endDate)
        {
            // Exclude orphaned periods (student purged ? TeacherStudentId nulled) from per-session totals.
            var query = _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId && p.SessionId.HasValue && p.TeacherStudentId != null);

            if (sessionGroupId.HasValue)
                query = query.Where(p =>
                    p.Session != null && p.Session.SessionGroupId == sessionGroupId.Value);

            if (startDate.HasValue)
                query = query.Where(p => p.PeriodEnd >= startDate.Value);
            if (endDate.HasValue)
                query = query.Where(p => p.PeriodStart <= endDate.Value);

            // Group on the LIVE session name, not the denormalized copy on the period.
            // PaymentPeriod.SessionName is a snapshot taken when the period was generated, so a
            // session renamed afterwards kept showing its OLD name on the payment screens while the
            // sessions screen showed the new one. Worse, periods written either side of a rename
            // carry DIFFERENT names for the same SessionId, which split one session into two rows.
            // The snapshot survives only as the fallback for a session that no longer exists
            // (SessionId is SET NULL-ed / the row was hard-deleted), which is what it is for.
            var result = await query
                .GroupBy(p => new
                {
                    p.SessionId,
                    SessionName = p.Session != null ? p.Session.SessionName : p.SessionName,
                    // Group the session belongs to (null = ungrouped). Lets the app render
                    // groups + ungrouped sessions with per-group roll-ups.
                    SessionGroupId = p.Session != null ? p.Session.SessionGroupId : (long?)null,
                    SessionGroupName = p.Session != null && p.Session.SessionGroup != null
                        ? p.Session.SessionGroup.GroupName
                        : null
                })
                .Select(g => new
                {
                    SessionId = g.Key.SessionId!.Value,
                    SessionName = g.Key.SessionName,
                    g.Key.SessionGroupId,
                    g.Key.SessionGroupName,
                    // Expected is NET OF FORGIVEN so the per-session figures sum back to the tracking
                    // card's Expected, which is Sum(AmountDue - ForgivenAmount). A gross Expected here
                    // overstated every session the moment a teacher forgave anything.
                    Expected = g.Sum(p => p.AmountDue - (p.ForgivenAmount ?? 0m)),
                    Collected = g.Sum(p => p.AmountPaid)
                })
                .OrderBy(r => r.SessionName)
                .ToListAsync();

            return result
                .Select(r => (r.SessionId, r.SessionName, r.SessionGroupId, r.SessionGroupName,
                    r.Expected, r.Collected, r.Expected - r.Collected))
                .ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<ActiveSessionCollectionSummaryRow>> GetActiveSessionsCollectionSummaryAsync(
            long teacherId, DateTime asOfLocalDate)
        {
            // The day comes from the caller so it is the TEACHER's — see IPaymentRepo.
            var today = asOfLocalDate.Date;

            var sessions = await _context.Sessions
                .Where(s => s.TeacherId == teacherId && s.EndDate >= today)
                .OrderBy(s => s.StartTime)
                .Select(s => new
                {
                    s.Id,
                    s.SessionName,
                    s.OccurrenceType,
                    s.SelectedDays,
                    s.MonthlyDayOfMonth,
                    s.StartTime,
                    TotalStudents = s.TeacherStudents.Count
                })
                .ToListAsync();

            if (sessions.Count == 0)
                return Array.Empty<ActiveSessionCollectionSummaryRow>();

            var sessionIds = sessions.Select(s => s.Id).ToList();

            var financials = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.SessionId.HasValue && sessionIds.Contains(p.SessionId.Value))
                .GroupBy(p => p.SessionId!.Value)
                .Select(g => new
                {
                    SessionId = g.Key,
                    Expected = g.Sum(p => p.AmountDue),
                    Collected = g.Sum(p => p.AmountPaid)
                })
                .ToDictionaryAsync(x => x.SessionId, x => x);

            // Distinct-student unpaid count per session (same pattern as
            // CountUnpaidStudentsBySessionAsync). PaidStudents = TotalStudents - unpaid.
            var unpaidCounts = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.SessionId.HasValue && sessionIds.Contains(p.SessionId.Value)
                    && p.TeacherStudentId.HasValue
                    && p.PaymentStatus != PaymentStatus.Paid)
                .Select(p => new { SessionId = p.SessionId!.Value, p.TeacherStudentId })
                .Distinct()
                .GroupBy(p => p.SessionId)
                .Select(g => new { SessionId = g.Key, UnpaidCount = g.Count() })
                .ToDictionaryAsync(x => x.SessionId, x => x.UnpaidCount);

            return sessions.Select(s =>
            {
                financials.TryGetValue(s.Id, out var fin);
                unpaidCounts.TryGetValue(s.Id, out var unpaidCount);

                return new ActiveSessionCollectionSummaryRow
                {
                    SessionId = s.Id,
                    SessionName = s.SessionName,
                    OccurrenceType = s.OccurrenceType,
                    SelectedDays = s.SelectedDays,
                    MonthlyDayOfMonth = s.MonthlyDayOfMonth,
                    StartTime = s.StartTime,
                    TotalStudents = s.TotalStudents,
                    PaidStudents = Math.Max(0, s.TotalStudents - unpaidCount),
                    ExpectedAmount = fin?.Expected ?? 0,
                    CollectedAmount = fin?.Collected ?? 0
                };
            }).ToList();
        }

        /// <inheritdoc />
        public async Task<Dictionary<long, int>> GetLiveCollectionCountsByCollectorUserAsync(long teacherId)
        {
            return await _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && !t.IsDeleted
                    && t.CollectedByUserId.HasValue)
                .GroupBy(t => t.CollectedByUserId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<(long UserId, string? UserName, decimal Collected, int TransactionCount)>>
            GetDashboardPerCollectorAsync(
                long teacherId,
                DateTime? startDate, DateTime? endDate)
        {
            var query = _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                    && !t.IsDeleted
                    && t.CollectedByUserId.HasValue);

            if (startDate.HasValue)
                query = query.Where(t => t.CollectedAt >= startDate.Value);
            if (endDate.HasValue)
                query = query.Where(t => t.CollectedAt <= endDate.Value.Date.AddDays(1));

            var result = await query
                .GroupBy(t => t.CollectedByUserId!.Value)
                .Select(g => new
                {
                    UserId = g.Key,
                    Collected = g.Sum(t => t.AmountPaid),
                    TransactionCount = g.Count()
                })
                .ToListAsync();

            // Departure refunds hand cash back to the student but do NOT soft-delete the underlying
            // transaction, so the !IsDeleted sum above still counts the collected cash. Subtract each
            // collector's departure payouts (RefundDue, authoritative FinalAmount; CollectedByUserId =
            // the CONFIRMING performer whose drawer the cash left — §7.4) in the same window so the
            // total reflects the money returned — for an assistant OR the tutor. A collector who ONLY
            // paid out refunds in the window still surfaces here, as a net-negative total.
            var refundQuery = _context.StudentDepartures
                .Where(d => d.TeacherId == teacherId
                    && d.DepartureOutcome == DepartureOutcome.RefundDue
                    && d.FinalAmount > 0m
                    && d.CollectedByUserId.HasValue);
            if (startDate.HasValue)
                refundQuery = refundQuery.Where(d => d.DepartedAt >= startDate.Value);
            if (endDate.HasValue)
                refundQuery = refundQuery.Where(d => d.DepartedAt <= endDate.Value.Date.AddDays(1));

            var refunds = await refundQuery
                .GroupBy(d => d.CollectedByUserId!.Value)
                .Select(g => new { UserId = g.Key, Refunded = g.Sum(d => d.FinalAmount) })
                .ToListAsync();

            var byUser = result.ToDictionary(
                r => r.UserId, r => (Collected: r.Collected, Count: r.TransactionCount));
            foreach (var rf in refunds)
            {
                byUser[rf.UserId] = byUser.TryGetValue(rf.UserId, out var agg)
                    ? (agg.Collected - rf.Refunded, agg.Count)
                    : (-rf.Refunded, 0);
            }

            return byUser
                .Select(kv => (kv.Key, (string?)null, kv.Value.Collected, kv.Value.Count))
                .ToList();
        }

        // ----------------------------------------------
        // EVENT QUERIES (Module 5)
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task AddPaymentEventAsync(PaymentEvent paymentEvent)
        {
            await _context.PaymentEvents.AddAsync(paymentEvent);
        }

        /// <inheritdoc />
        public async Task<PaymentEvent?> GetPaymentEventByIdAndTeacherAsync(
            long eventId, long teacherId)
        {
            return await _context.PaymentEvents
                .FirstOrDefaultAsync(e => e.Id == eventId
                    && e.TeacherId == teacherId
                    && !e.IsDeleted);
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<PaymentEvent> Items, int TotalCount)>
            GetPaymentEventsPagedAsync(long teacherId, int page, int pageSize)
        {
            var query = _context.PaymentEvents
                .Where(e => e.TeacherId == teacherId && !e.IsDeleted);

            int totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(e => e.EventDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking()
                .ToListAsync();

            return (items, totalCount);
        }

        /// <inheritdoc />
        public async Task UpdatePaymentEventAsync(PaymentEvent paymentEvent)
        {
            _context.Entry(paymentEvent).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task AddEventObligationsRangeAsync(
            IEnumerable<EventStudentObligation> obligations)
        {
            await _context.EventStudentObligations.AddRangeAsync(obligations);
        }

        /// <inheritdoc />
        public async Task<EventStudentObligation?> GetEventObligationAsync(
            long eventId, long teacherStudentId)
        {
            return await _context.EventStudentObligations
                .FirstOrDefaultAsync(o => o.PaymentEventId == eventId
                    && o.TeacherStudentId == teacherStudentId);
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<EventStudentObligation> Items, int TotalCount)>
            GetEventObligationsPagedAsync(
                long eventId, long teacherId,
                PaymentStatus? statusFilter,
                string? search,
                int page, int pageSize)
        {
            var query = _context.EventStudentObligations
                .Where(o => o.PaymentEventId == eventId && o.TeacherId == teacherId);

            if (statusFilter.HasValue)
            {
                if (statusFilter.Value == PaymentStatus.Paid)
                    query = query.Where(o => o.PaymentStatus == PaymentStatus.Paid);
                else
                    query = query.Where(o => o.PaymentStatus != PaymentStatus.Paid);
            }

            // REQ-EVT-016: Search by student name or student code
            if (!string.IsNullOrWhiteSpace(search))
            {
                string searchLower = ArabicTextNormalizer.Normalize(search.Trim());
                query = query.Where(o =>
                    (o.StudentName != null && DbSearch.ArabicNormalize(o.StudentName).Contains(searchLower))
                    || (o.StudentCode != null && DbSearch.ArabicNormalize(o.StudentCode).Contains(searchLower)));
            }

            int totalCount = await query.CountAsync();
            var items = await query
                .OrderBy(o => o.StudentName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking()
                .ToListAsync();

            return (items, totalCount);
        }

        /// <inheritdoc />
        public async Task UpdateEventObligationAsync(EventStudentObligation obligation)
        {
            _context.Entry(obligation).State = EntityState.Modified;
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task DeleteEventObligationAsync(EventStudentObligation obligation)
        {
            _context.EventStudentObligations.Remove(obligation);
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<(IReadOnlyList<PaymentEvent> Items, int TotalCount)>
            GetPaymentEventsFilteredPagedAsync(
                long teacherId,
                string? searchName,
                EventTargetScopeType? scopeTypeFilter,
                string? completionStatus,
                int page, int pageSize)
        {
            var query = _context.PaymentEvents
                .Where(e => e.TeacherId == teacherId && !e.IsDeleted);

            if (!string.IsNullOrWhiteSpace(searchName))
            {
                string search = ArabicTextNormalizer.Normalize(searchName.Trim());
                query = query.Where(e => DbSearch.ArabicNormalize(e.EventName).Contains(search));
            }

            if (scopeTypeFilter.HasValue)
                query = query.Where(e => e.TargetScopeType == scopeTypeFilter.Value);

            if (!string.IsNullOrWhiteSpace(completionStatus))
            {
                query = completionStatus switch
                {
                    "FullyCollected" => query.Where(e => e.TotalCollectedRevenue >= e.TotalExpectedRevenue),
                    "PartiallyCollected" => query.Where(e => e.TotalCollectedRevenue > 0 && e.TotalCollectedRevenue < e.TotalExpectedRevenue),
                    "NotStarted" => query.Where(e => e.TotalCollectedRevenue == 0),
                    _ => query
                };
            }

            int totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(e => e.EventDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking()
                .ToListAsync();

            return (items, totalCount);
        }

        /// <inheritdoc />
        public async Task AddEventPaymentTransactionAsync(EventPaymentTransaction transaction)
        {
            await _context.EventPaymentTransactions.AddAsync(transaction);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<EventPaymentTransaction>> GetEventPaymentTransactionsAsync(
            long eventId, long teacherId)
        {
            return await _context.EventPaymentTransactions
                .Where(t => t.PaymentEventId == eventId && t.TeacherId == teacherId)
                .OrderByDescending(t => t.CollectedAt)
                .AsNoTracking()
                .ToListAsync();
        }

        // ----------------------------------------------
        // TARGET SCOPE RESOLUTION (Module 5)
        // ----------------------------------------------

        /// <inheritdoc />
        public async Task<List<long>> GetStudentIdsBySessionAsync(long teacherId, long sessionId)
        {
            return await _context.TeacherStudents
                .Where(s => s.TeacherId == teacherId && s.SessionId == sessionId && !s.IsDeleted)
                .Select(s => s.Id)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<long>> GetStudentIdsByGroupAsync(long teacherId, long sessionGroupId)
        {
            var sessionIds = await _context.Sessions
                .Where(s => s.TeacherId == teacherId && s.SessionGroupId == sessionGroupId)
                .Select(s => s.Id)
                .ToListAsync();

            return await _context.TeacherStudents
                .Where(s => s.TeacherId == teacherId && s.SessionId.HasValue
                    && sessionIds.Contains(s.SessionId.Value) && !s.IsDeleted)
                .Select(s => s.Id)
                .ToListAsync();
        }

        /// <inheritdoc />
        public async Task<List<long>> GetAllStudentIdsAsync(long teacherId)
        {
            return await _context.TeacherStudents
                .Where(s => s.TeacherId == teacherId && !s.IsDeleted)
                .Select(s => s.Id)
                .ToListAsync();
        }

        // ----------------------------------------------
        // INTEGRATION HOOKS (bulk FK nullification)
        // ----------------------------------------------

        /// <inheritdoc />
        /// Uses ExecuteUpdateAsync � single SQL UPDATE, no in-memory loading.
        /// Same pattern as AttendanceRepo.NullifySessionIdOnRecordsForSessionAsync (Step 1.2).
        public async Task<long?> GetLatestCollectorUserIdForStudentSessionAsync(
            long teacherId, long teacherStudentId, long sessionId)
        {
            return await _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                         && t.TeacherStudentId == teacherStudentId
                         && t.SessionId == sessionId
                         && !t.IsDeleted)
                .OrderByDescending(t => t.Id)
                .Select(t => t.CollectedByUserId)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task<DateTime?> GetLatestCollectionInstantForStudentSessionAsync(
            long teacherId, long teacherStudentId, long sessionId)
        {
            // Same ordering as GetLatestCollectorUserIdForStudentSessionAsync (newest by Id) so the
            // instant and the collector describe the SAME transaction.
            return await _context.PaymentTransactions
                .Where(t => t.TeacherId == teacherId
                         && t.TeacherStudentId == teacherStudentId
                         && t.SessionId == sessionId
                         && !t.IsDeleted)
                .OrderByDescending(t => t.Id)
                .Select(t => (DateTime?)t.CollectedAt)
                .FirstOrDefaultAsync();
        }

        /// <inheritdoc />
        public async Task NullifySessionIdOnPaymentRecordsAsync(long sessionId)
        {
            // IgnoreQueryFilters so SOFT-DELETED records are nullified too. A refunded/deleted payment
            // transaction (IsDeleted=true) still points at the session via a NO-ACTION FK; if it isn't
            // nullified here, the session's hard delete fails with a 409 conflict.
            // Nullify on PaymentTransactions
            await _context.PaymentTransactions
                .IgnoreQueryFilters()
                .Where(t => t.SessionId == sessionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.SessionId, (long?)null));

            // Nullify on PaymentPeriods
            await _context.PaymentPeriods
                .IgnoreQueryFilters()
                .Where(p => p.SessionId == sessionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.SessionId, (long?)null));

            // Nullify on StudentDepartures
            await _context.StudentDepartures
                .IgnoreQueryFilters()
                .Where(d => d.SessionId == sessionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.SessionId, (long?)null));
        }

        /// <inheritdoc />
        /// Uses ExecuteUpdateAsync � single SQL UPDATE, no in-memory loading.
        /// Same pattern as AttendanceRepo.NullifyStudentReferencesOnRecordsAsync (Step 1.1).
        /// Denormalized StudentName and StudentCode remain intact for historical display.
        public async Task NullifyStudentReferencesOnPaymentRecordsAsync(long teacherStudentId)
        {
            // PaymentTransactions
            await _context.PaymentTransactions
                .Where(t => t.TeacherStudentId == teacherStudentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.TeacherStudentId, (long?)null)
                    .SetProperty(t => t.StudentSessionAssignmentId, (long?)null));

            // PaymentPeriods: DELETE the purged student's billing obligations rather than nulling their
            // student FK. A period is a specific student's monthly bill; once the student is gone the
            // obligation is meaningless, and leaving it as an orphaned (null-student) row let its
            // AmountDue leak into dashboard aggregates. The PaymentTransaction -> PaymentPeriod FK is
            // ON DELETE SET NULL, so audit transactions keep their denormalized data and simply lose the
            // period link (their own student FK is already nulled above).
            await _context.PaymentPeriods
                .Where(p => p.TeacherStudentId == teacherStudentId)
                .ExecuteDeleteAsync();

            // StudentDepartures
            await _context.StudentDepartures
                .Where(d => d.TeacherStudentId == teacherStudentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.TeacherStudentId, (long?)null));

            // SessionTransferEvents
            await _context.SessionTransferEvents
                .Where(t => t.TeacherStudentId == teacherStudentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.TeacherStudentId, (long?)null));

            // EventStudentObligations
            await _context.EventStudentObligations
                .Where(o => o.TeacherStudentId == teacherStudentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.TeacherStudentId, (long?)null));

            // EventPaymentTransactions
            await _context.EventPaymentTransactions
                .Where(t => t.TeacherStudentId == teacherStudentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.TeacherStudentId, (long?)null));

            // Delete counter (no longer needed after purge)
            await _context.StudentPaymentCounters
                .Where(c => c.TeacherStudentId == teacherStudentId)
                .ExecuteDeleteAsync();
        }

        /// <inheritdoc />
        public async Task<int> RecalculateConsecutiveUnpaidAsync(
            long teacherId, long teacherStudentId)
        {
            var recentPeriods = await _context.PaymentPeriods
                .Where(p => p.TeacherId == teacherId
                    && p.TeacherStudentId == teacherStudentId)
                .OrderByDescending(p => p.PeriodSequence)
                .Take(PaymentConstants.MaxConsecutiveUnpaidScanDepth)
                .AsNoTracking()
                .ToListAsync();

            int consecutive = 0;
            foreach (var period in recentPeriods)
            {
                if (period.PaymentStatus == PaymentStatus.Paid)
                    break;
                consecutive++;
            }

            return consecutive;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<StrandedStudentRow>> GetStudentsWithStrandedUnpaidPeriodsAsync()
        {
            // Currently-assigned students (SessionId != null; soft-deleted excluded by the global filter)
            // that have at least one still-owed period (AmountDue > AmountPaid) under a DIFFERENT session.
            var rows = await _context.TeacherStudents
                .Where(ts => ts.SessionId != null
                    && _context.PaymentPeriods.Any(p =>
                        p.TeacherStudentId == ts.Id
                        && p.SessionId != null
                        && p.SessionId != ts.SessionId
                        && p.AmountDue > p.AmountPaid))
                .Select(ts => new StrandedStudentRow
                {
                    TeacherId = ts.TeacherId,
                    TeacherStudentId = ts.Id,
                    CurrentSessionId = ts.SessionId!.Value,
                    StudentName = ts.StudentName,
                    StudentCode = ts.StudentCode
                })
                .AsNoTracking()
                .ToListAsync();

            return rows;
        }
    }