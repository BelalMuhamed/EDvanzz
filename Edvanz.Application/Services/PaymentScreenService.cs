using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Edvanz.Application.Dtos;
using Edvanz.Application.Dtos.Payment;
using Edvanz.Application.ServiceContract;
using Edvanz.Domain.Constants;
using Edvanz.Domain.Entities;
using Edvanz.Domain.Enums;
using Edvanz.Domain.Helpers;
using Edvanz.Domain.Interfaces;
using Microsoft.Extensions.Localization;

namespace Edvanz.Application.Services;

/// <summary>
/// Implements the screen-oriented payment endpoints (api/v1/*). Reuse-first: delegates to
/// existing <see cref="IPaymentRepo"/> named methods (and, for money movement in Phase 2,
/// <c>IPaymentService</c>) — no payment mutation logic is duplicated here. All reads are
/// tenant-scoped by the <c>teacherId</c> the controller resolves from the JWT.
/// </summary>
public class PaymentScreenService : IPaymentScreenService
{
    private static readonly JsonSerializerOptions IdempotencyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IStringLocalizer<Domain.Resources.Messages> _localizer;
    private readonly IPaymentService _paymentService;
    private readonly IIdempotencyService _idempotency;
    private readonly ITimeZoneService _timeZoneService;

    public PaymentScreenService(
        IUnitOfWork unitOfWork,
        IStringLocalizer<Domain.Resources.Messages> localizer,
        IPaymentService paymentService,
        IIdempotencyService idempotency,
        ITimeZoneService timeZoneService)
    {
        _unitOfWork = unitOfWork;
        _localizer = localizer;
        _paymentService = paymentService;
        _idempotency = idempotency;
        _timeZoneService = timeZoneService;
    }

    /// <summary>
    /// Last day of the teacher's current local (Africa/Cairo) month — the default
    /// "through this month" boundary for screens that carry no explicit month.
    /// </summary>
    private DateTime CurrentMonthEnd(long teacherId)
    {
        var today = _timeZoneService.GetTeacherLocalDate(teacherId);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        return monthStart.AddMonths(1).AddDays(-1);
    }

    /// <summary>
    /// Resolves an optional screen month (<c>YYYY-MM</c>) to its month-end "through" boundary.
    /// Null/empty → the teacher's current local month (the pre-existing behavior); malformed →
    /// false (callers 422 with <c>PaymentInvalidMonthFormat</c>). Lets month-scoped screens collect
    /// exactly what they display — arrears through the month they were opened on (see MarkPaid /
    /// collect lookup) — instead of always charging through the current month.
    /// </summary>
    private bool TryResolveThroughMonthEnd(long teacherId, string? month, out DateTime monthEnd)
    {
        if (!TryResolveMonth(teacherId, month, out int year, out int mon))
        {
            monthEnd = default;
            return false;
        }
        monthEnd = new DateTime(year, mon, 1).AddMonths(1).AddDays(-1);
        return true;
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<Result<List<WalletResetLogDto>>> GetWalletWithdrawalHistoryAsync(
        long teacherId, long assistantId)
    {
        // Every withdrawal/reset the teacher took from this assistant's wallet — the "receipt"
        // trail (amount + when + who) that answers "where did the money I took go?". The cash was
        // already counted as revenue at collection time; this ledger records the hand-over.
        // assistantId is an Assistant.Id OR a CenterAssistant.Id (surfaced identically in the wallet
        // DTO). Try the teacher-assistant ledger, then fall back to the center-assistant ledger.
        var logs = await _unitOfWork.PaymentsRepo.GetWalletResetLogsAsync(teacherId, assistantId);
        if (logs.Count == 0)
            logs = await _unitOfWork.PaymentsRepo.GetWalletResetLogsForCenterAssistantAsync(teacherId, assistantId);

        var dtos = logs
            .OrderByDescending(l => l.ResetAt)
            .Select(l => new WalletResetLogDto
            {
                Id = l.Id,
                AssistantName = l.AssistantName,
                AmountReset = l.AmountReset,
                ResetAt = l.ResetAt,
                ResetByUserId = l.ResetByUserId
            })
            .ToList();

        return Result<List<WalletResetLogDto>>.Success(
            dtos, _localizer, PaymentConstants.Messages.Success);
    }

    public async Task<Result<CollectionsByMonthResponse>> GetCollectionsByMonthAsync(
        long teacherId, string? month, int? year, int page, int limit,
        long? collectedByUserId = null,
        DateTime? from = null, DateTime? to = null,
        string? search = null,
        bool includeAdjustments = true,
        bool? exactRange = null)
    {
        (page, limit) = NormalizePaging(page, limit);

        int resolvedYear, resolvedMonth;
        DateTime startDate, endDate, endExclusive;
        DateTime? fromEcho = null, toEcho = null;
        string monthLabel;

        if (from.HasValue && to.HasValue)
        {
            // DATE-RANGE path (additive): an inclusive [from,to] day window, taking precedence over
            // month/year. Boundaries mirror the month path — startDate inclusive, endDate the
            // inclusive end-of-day for the paged query's `<=` filter, endExclusive for refunds.
            //
            // EXACT-INSTANT variant (2026-09-03, drawer scope): precise instants [from, to)
            // instead of whole days — the merged wallet/ledger screen's "in drawer now" scope
            // starts at the exact last hand-over moment, so the listed rows sum EXACTLY to the
            // held balance (a day floor would leak same-day pre-hand-over rows in). Date-only
            // callers (the existing day filter) are byte-identical to before.
            //
            // The intent comes from `exactRange` (controller reads the RAW query text): the old
            // TimeOfDay inference below misread a midnight-to-midnight exact window (the wallet
            // day filter, "…T00:00:00" → next-day "…T00:00:00") as a date-only range and served
            // the whole next day inclusively. Kept only as a null-fallback.
            var f = from.Value;
            var t = to.Value;
            if (t < f) (f, t) = (t, f);               // tolerate a reversed range
            bool timed = exactRange
                ?? (f.TimeOfDay != TimeSpan.Zero || t.TimeOfDay != TimeSpan.Zero);
            startDate = timed ? f : f.Date;
            endExclusive = timed ? t : t.Date.AddDays(1);
            endDate = endExclusive.AddTicks(-1);
            resolvedYear = f.Year;
            resolvedMonth = f.Month;
            fromEcho = f;
            toEcho = t;
            monthLabel = f.Date == t.Date
                ? f.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                : $"{f.ToString("d MMM", CultureInfo.InvariantCulture)} – {t.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
        }
        else
        {
            // MONTH path (unchanged): DASH-1 unified "YYYY-MM" OR legacy ?month=<int 1-12>&year=<int>;
            // default to the teacher's current local (Africa/Cairo) month/year (no screen-load 422).
            var errorKey = ResolveCollectionsMonthYear(
                teacherId, month, year, out resolvedYear, out resolvedMonth);
            if (errorKey is not null)
                return Result<CollectionsByMonthResponse>.Failure(
                    _localizer, errorKey, HttpStatusCode.UnprocessableEntity);

            startDate = new DateTime(resolvedYear, resolvedMonth, 1);
            endExclusive = startDate.AddMonths(1);
            // End-of-day of the last day (Aug 31 23:59:59.9999999), NOT the last day at midnight.
            // `GetTransactionsByDateRangePagedAsync` filters `CollectedAt <= endDate` and CollectedAt
            // carries a real time-of-day, so `AddMonths(1).AddDays(-1)` (= last day 00:00:00) silently
            // dropped every collection made after midnight on the last calendar day of the month —
            // i.e. the whole last day (the "no collections on Aug 31" bug). Mirrors the date-range path.
            endDate = endExclusive.AddTicks(-1);
            monthLabel = startDate.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        }

        // Collector-scoped ledger ("collected by me" / an assistant's own collections): materialize the
        // whole scope so the list can be ordered by day (newest first; money-OUT before collections
        // within a day) and paginated consistently across pages, and so per-day nets are authoritative.
        if (collectedByUserId is long collectorId)
        {
            return await BuildCollectorScopedCollectionsAsync(
                teacherId, collectorId, page, limit,
                startDate, endDate, endExclusive,
                resolvedYear, resolvedMonth, monthLabel, fromEcho, toEcho, search, includeAdjustments);
        }

        // ── TEACHER-WIDE (account) path — collectedByUserId is null here (the collector-scoped path
        // returned above). Keeps SQL pagination: the few departure-refund lines are surfaced on page 1
        // and counted into the totals on every page. ──
        var (items, totalCount) = await _unitOfWork.PaymentsRepo
            .GetTransactionsByDateRangePagedAsync(
                teacherId, startDate, endDate,
                sessionId: null, collectedByUserId: null,
                page: page, pageSize: limit, search: search);

        // Arabic-variant-insensitive (أ/ا, ة/ه, ى/ي …): both sides folded in memory, mirroring
        // the SQL dbo.ArabicNormalize path. Normalize already lower-cases, so Ordinal suffices.
        var term = string.IsNullOrWhiteSpace(search) ? null : ArabicTextNormalizer.Normalize(search.Trim());
        bool MatchesSearch(string? name, string? code) =>
            string.IsNullOrEmpty(term)
            || (name != null && ArabicTextNormalizer.Normalize(name).Contains(term, StringComparison.Ordinal))
            || (code != null && ArabicTextNormalizer.Normalize(code).Contains(term, StringComparison.Ordinal));

        var refundRows = new List<CollectionRow>();
        int refundCount = 0;
        // Student-departure refunds confirmed in-range (money OUT). Omitted when includeAdjustments=false.
        if (includeAdjustments)
        {
            var refunds = (await _unitOfWork.PaymentsRepo
                    .GetDepartureRefundsByDateRangeAsync(teacherId, startDate, endExclusive))
                .Where(r => MatchesSearch(r.StudentName, r.StudentCode))
                .ToList();
            refundCount = refunds.Count;
            if (page == 1)
            {
                refundRows.AddRange(refunds.Select(r => new CollectionRow
                {
                    Id = $"departure-refund-{r.Id.ToString(CultureInfo.InvariantCulture)}",
                    Index = 0,
                    StudentId = r.StudentId?.ToString(CultureInfo.InvariantCulture),
                    StudentName = r.StudentName,
                    StudentCode = r.StudentCode,
                    Amount = -r.RefundAmount,
                    Status = "refund",
                    IsRefund = true,
                    SessionName = r.SessionName,
                    RefundedForMonthLabel = r.RefundPeriodStart.HasValue
                        ? r.RefundPeriodStart.Value.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
                        : null,
                    CollectedAt = r.DepartedAt
                }));
            }
        }

        int baseIndex = (page - 1) * limit;
        var rows = new List<CollectionRow>(items.Count + refundRows.Count);
        rows.AddRange(refundRows);
        for (int i = 0; i < items.Count; i++)
            rows.Add(BuildCollectionRowFromTransaction(items[i], baseIndex + i + 1));

        // "How many paid X" distribution across the whole scope (not just this page), by per-month amount.
        // `search` is threaded so the cards narrow with the visible list — without it they kept
        // reporting the unfiltered scope while the rows below were filtered ("cards keep showing total").
        var amountTiers = (await _unitOfWork.PaymentsRepo
                .GetCollectionAmountTiersAsync(teacherId, startDate, endDate, null, search))
            .Select(t => new CollectionAmountTier { Amount = t.Amount, Count = t.Count })
            .ToList();

        // §2b transparency: fill system-suggested + set-by name on this page's prorated-first-month rows.
        await EnrichProrationTransparencyAsync(teacherId, rows);

        int transactionPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)limit);
        var response = new CollectionsByMonthResponse
        {
            Month = resolvedMonth,
            Year = resolvedYear,
            MonthLabel = monthLabel,
            Page = page,
            Limit = limit,
            TotalItems = totalCount + refundCount,
            TotalPages = transactionPages == 0 ? (refundCount > 0 ? 1 : 0) : transactionPages,
            FromDate = fromEcho,
            ToDate = toEcho,
            Items = rows,
            AmountTiers = amountTiers
        };

        return Result<CollectionsByMonthResponse>.Success(
            response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <summary>
    /// Builds the collector-scoped collections ledger ("collected by me" / an assistant's own
    /// collections). The flat list is ordered by calendar day (newest first) with money-OUT lines
    /// (refunds/withdrawals) before collections within each day.
    ///
    /// Only the requested PAGE of collections is fetched from SQL. The money-out lines are read whole
    /// (a bounded handful per collector per window) and the day counts that decide where the page
    /// falls come from a GROUP BY, so the ledger costs one page of rows however deep the history is —
    /// the old form pulled EVERY transaction the collector ever made, with Session, Allocations,
    /// Allocations.PaymentPeriod and EditLogs eager-loaded, because the app's default "collections in
    /// wallet" scope starts in 2020 when there has never been a hand-over.
    ///
    /// Every figure the screen shows — TotalItems, the per-day nets, the amount tiers — is computed
    /// over the WHOLE filtered scope on the server, never summed from the loaded page.
    /// </summary>
    private async Task<Result<CollectionsByMonthResponse>> BuildCollectorScopedCollectionsAsync(
        long teacherId, long collectorId, int page, int limit,
        DateTime startDate, DateTime endDate, DateTime endExclusive,
        int resolvedYear, int resolvedMonth, string monthLabel,
        DateTime? fromEcho, DateTime? toEcho, string? search, bool includeAdjustments)
    {
        var repo = _unitOfWork.PaymentsRepo;
        var term = string.IsNullOrWhiteSpace(search) ? null : ArabicTextNormalizer.Normalize(search.Trim());
        bool MatchesSearch(string? name, string? code) =>
            string.IsNullOrEmpty(term)
            || (name != null && ArabicTextNormalizer.Normalize(name).Contains(term, StringComparison.Ordinal))
            || (code != null && ArabicTextNormalizer.Normalize(code).Contains(term, StringComparison.Ordinal));

        // ── Positives: per-day counts + money totals over the WHOLE scope (one GROUP BY, no rows). ──
        // The teacher's UTC offset for THIS window, resolved once. Every day bucket below - the SQL
        // GROUP BY, the money-out grouping, and each row's DayKey - must use the SAME local day, or a
        // client matching a row's dayKey to a header's dateKey finds nothing for a late-night
        // collection (01:00 Cairo is the previous day in UTC).
        var offsetReference = startDate == DateTime.MinValue ? DateTime.UtcNow : startDate;
        int localOffsetHours =
            (int)Math.Round((_timeZoneService.ConvertUtcToLocal(offsetReference) - offsetReference).TotalHours);

        var dayTotals = await repo.GetTransactionDayTotalsAsync(
            teacherId, startDate, endDate, sessionId: null, collectedByUserId: collectorId,
            search: search, localOffsetHours: localOffsetHours);

        // ── Negatives (money OUT) — refunds + wallet withdrawals — unless "collections only".
        // Read in full: they are a handful per collector per window, they interleave with the
        // collections by day, and the day nets need every one of them. ──
        var moneyOut = new List<CollectionRow>();
        var moneyOutPerformers = new List<(CollectionRow Row, long PerformerId)>();
        if (includeAdjustments)
        {
            // includeDeleted:false — a fully-DELETED collection's positive row is already excluded, so its
            // Deleted refund line would be an orphaned −amount (the Omar bug). Reversed (partial departure)
            // refunds stay: their transaction is still visible above to net against.
            var refunds = (await repo.GetCollectorRefundsInRangeAsync(
                    teacherId, collectorId, startDate, endExclusive, includeDeleted: false))
                .Where(r => r.RefundAmount > 0m && MatchesSearch(r.StudentName, r.StudentCode))
                .ToList();
            foreach (var r in refunds)
            {
                var row = new CollectionRow
                {
                    Id = $"collector-refund-{r.Id.ToString(CultureInfo.InvariantCulture)}",
                    Index = 0,
                    StudentId = r.StudentId?.ToString(CultureInfo.InvariantCulture),
                    StudentName = r.StudentName,
                    StudentCode = r.StudentCode,
                    Amount = -r.RefundAmount,
                    Status = "refund",
                    IsRefund = true,
                    SessionName = r.SessionName,
                    RefundedForMonthLabel = null,
                    CollectedAt = r.RefundedAt
                };
                moneyOut.Add(row);
                if (r.PerformedByUserId is long performerId && performerId != collectorId)
                    moneyOutPerformers.Add((row, performerId));
            }

            // Cash withdrawals (hand-overs FROM this collector's wallet). A withdrawal carries no student,
            // so it's skipped while a student search is active. The tutor's own view has no wallet → empty.
            if (string.IsNullOrEmpty(term))
            {
                var withdrawals = await repo.GetWalletResetLogsForCollectorInRangeAsync(
                    teacherId, collectorId, startDate, endExclusive);
                foreach (var w in withdrawals)
                {
                    var row = new CollectionRow
                    {
                        Id = $"withdrawal-{w.Id.ToString(CultureInfo.InvariantCulture)}",
                        Index = 0,
                        StudentId = null,
                        StudentName = null,
                        StudentCode = null,
                        Amount = -w.AmountReset,
                        Status = "withdrawal",
                        IsWithdrawal = true,
                        SessionName = null,
                        RefundedForMonthLabel = null,
                        CollectedAt = w.ResetAt
                    };
                    moneyOut.Add(row);
                    if (w.ResetByUserId != collectorId)
                        moneyOutPerformers.Add((row, w.ResetByUserId));
                }
            }
        }

        // Money-out lines grouped by the same day key the collections are grouped by, newest time
        // first inside a day — the within-day order the merged list used to produce by sorting.
        var moneyOutByDay = moneyOut
            .GroupBy(r => (r.CollectedAt ?? DateTime.MinValue).AddHours(localOffsetHours).Date)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.CollectedAt ?? DateTime.MinValue).ToList());

        // ── Per-day nets over the FULL scope (authoritative regardless of pagination): collections
        // from the GROUP BY, money-out from the rows above, merged on the day. ──
        var positivesByDay = dayTotals.ToDictionary(d => d.Day);
        var allDays = positivesByDay.Keys
            .Concat(moneyOutByDay.Keys)
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        var dailyNets = allDays
            .Select(day =>
            {
                positivesByDay.TryGetValue(day, out var p);
                decimal outAmount = moneyOutByDay.TryGetValue(day, out var outs)
                    ? outs.Sum(r => -r.Amount)
                    : 0m;
                decimal collected = p.Collected;
                decimal deducted = p.Deducted + outAmount;
                return new CollectionDailyNet
                {
                    DateKey = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Date = day,
                    Collected = collected,
                    Deducted = deducted,
                    Net = collected - deducted,
                    CollectionsCount = p.CollectionsCount
                };
            })
            .ToList();

        int totalPositives = dayTotals.Sum(d => d.RowCount);
        int totalItems = totalPositives + moneyOut.Count;
        int totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)limit);

        // ── Locate the requested page inside the interleaved order WITHOUT materializing it.
        // Walking the days newest-first, each day contributes its money-out lines and then its
        // collections; the collections keep their global "newest CollectedAt first" sequence across
        // days, so the page's collections are always one contiguous slice of that sequence. The walk
        // records which money-out rows land on the page and where that slice starts/ends. ──
        int windowStart = (page - 1) * limit;
        int windowEnd = windowStart + limit;
        int globalIndex = 0;        // position of the next row in the full interleaved list
        int positiveCursor = 0;     // position of the next collection in the collections-only sequence
        int positiveSkip = 0, positiveTake = 0;
        bool positiveSkipSet = false;
        // null = "the next collection from the fetched slice"; non-null = that money-out row.
        var slots = new List<CollectionRow?>(limit);

        foreach (var day in allDays)
        {
            if (globalIndex >= windowEnd) break;

            if (moneyOutByDay.TryGetValue(day, out var outs))
            {
                foreach (var row in outs)
                {
                    if (globalIndex >= windowStart && globalIndex < windowEnd) slots.Add(row);
                    globalIndex++;
                }
            }

            int dayPositives = positivesByDay.TryGetValue(day, out var p) ? p.RowCount : 0;
            if (dayPositives > 0)
            {
                // This day's collections occupy [globalIndex, globalIndex + dayPositives) globally.
                int from = Math.Max(globalIndex, windowStart);
                int to = Math.Min(globalIndex + dayPositives, windowEnd);
                if (to > from)
                {
                    if (!positiveSkipSet)
                    {
                        positiveSkip = positiveCursor + (from - globalIndex);
                        positiveSkipSet = true;
                    }
                    positiveTake += to - from;
                    for (int i = 0; i < to - from; i++) slots.Add(null);
                }
                globalIndex += dayPositives;
                positiveCursor += dayPositives;
            }
        }

        // ── The one row-bearing read: exactly the collections this page shows. ──
        var pageTxns = positiveTake > 0
            ? await repo.GetTransactionsByDateRangeSliceAsync(
                teacherId, startDate, endDate, sessionId: null, collectedByUserId: collectorId,
                skip: positiveSkip, take: positiveTake, search: search)
            : (IReadOnlyList<PaymentTransaction>)Array.Empty<PaymentTransaction>();

        var pageRows = new List<CollectionRow>(slots.Count);
        int taken = 0;
        foreach (var slot in slots)
        {
            if (slot is not null) { pageRows.Add(slot); continue; }
            // Defensive: a concurrent collection/refund inside the window can shift the slice by a
            // row between the count and the fetch. Stopping short is a truthful short page; indexing
            // past the end would be a 500.
            if (taken >= pageTxns.Count) break;
            pageRows.Add(BuildCollectionRowFromTransaction(pageTxns[taken++], 0));
        }

        // Name every foreign performer (tutor taking a hand-over / departing a student) on the rows
        // this page actually returns — tiny set.
        var pagePerformers = moneyOutPerformers
            .Where(mp => pageRows.Contains(mp.Row))
            .ToList();
        if (pagePerformers.Count > 0)
        {
            var performerNames = await _unitOfWork.Users.GetUserFullNamesByUserIdsAsync(
                pagePerformers.Select(mp => mp.PerformerId).Distinct().ToList());
            foreach (var (row, performerId) in pagePerformers)
                if (performerNames.TryGetValue(performerId, out var name))
                    row.PerformedByName = name;
        }

        // Stable per-row day key (invariant "yyyy-MM-dd") — the client groups the ledger into day
        // sections by this string.
        foreach (var r in pageRows)
            // TEACHER-LOCAL day, not the raw UTC instant (2026-09-09). CollectedAt is UTC, so cash taken
            // at 01:00 Cairo on the 8th is 2026-09-07T22:00Z and filed under a "7 September" header -
            // directly above a row rendering 08 Sep 01:00, and above a receipt whose LocalCollectedAt
            // also says the 8th. The filter was self-consistent, so nothing was lost; the heading simply
            // contradicted the rows it headed. A business day is the tenant's (CLAUDE.md 11b).
            r.DayKey = (r.CollectedAt is DateTime ca
                    ? _timeZoneService.ConvertUtcToLocal(ca)
                    : DateTime.MinValue)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        for (int i = 0; i < pageRows.Count; i++)
            pageRows[i].Index = windowStart + i + 1;

        // "How many paid X" across the WHOLE scope, by per-month settlement amount — computed in SQL
        // over the same collector/date/search filter the rows use. It used to be folded from the fully
        // materialized row set, which is exactly what this method no longer loads. Money-out lines are
        // excluded by construction: refunds and withdrawals belong to DailyNets.
        var amountTiers = (await repo.GetCollectionAmountTiersAsync(
                teacherId, startDate, endDate, collectorId, search))
            .Select(t => new CollectionAmountTier { Amount = t.Amount, Count = t.Count })
            .ToList();

        // §2b transparency: fill system-suggested + set-by name on this page's prorated-first-month rows.
        await EnrichProrationTransparencyAsync(teacherId, pageRows);

        var response = new CollectionsByMonthResponse
        {
            Month = resolvedMonth,
            Year = resolvedYear,
            MonthLabel = monthLabel,
            Page = page,
            Limit = limit,
            TotalItems = totalItems,
            TotalPages = totalPages,
            FromDate = fromEcho,
            ToDate = toEcho,
            Items = pageRows,
            AmountTiers = amountTiers,
            DailyNets = dailyNets
        };

        return Result<CollectionsByMonthResponse>.Success(
            response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <summary>Maps a collection <see cref="PaymentTransaction"/> to its ledger row (shared by both paths).</summary>
    private CollectionRow BuildCollectionRowFromTransaction(PaymentTransaction tx, int index)
    {
        var (appliedMonths, isEdited, originalAmount) = BuildCollectionLedgerMeta(tx);
        return new CollectionRow
        {
            Id = tx.Id.ToString(CultureInfo.InvariantCulture),
            Index = index,
            StudentId = tx.TeacherStudentId?.ToString(CultureInfo.InvariantCulture),
            StudentName = tx.StudentName,
            StudentCode = tx.StudentCode,
            Amount = tx.AmountPaid,
            Status = "collected",
            // Months this one cash event cleared (settlement slices); legacy rows w/o allocations → 1.
            PeriodsCovered = tx.Allocations != null && tx.Allocations.Count > 0
                ? tx.Allocations.Count : 1,
            AppliedMonths = appliedMonths,
            IsEdited = isEdited,
            OriginalAmount = originalAmount,
            // §2b transparency: this collection settled a prorated JOINING (anchor) month. The
            // system-suggested amount + who set it are batch-filled by EnrichProrationTransparencyAsync.
            IsProratedFirstMonth = appliedMonths.Any(m => m.IsProRated),
            // Live session name; the transaction's copy is a stale-on-rename snapshot.
            SessionName = ResolveSessionName(tx.Session?.SessionName, tx.SessionName),
            CollectedAt = tx.CollectedAt,
            // Collector's free-text note for a custom/partial collect (null for whole-month collects).
            Note = tx.CollectionNote
        };
    }

    /// <summary>
    /// §2b transparency (REQ-PAY-021/022): for the prorated-first-month collection rows on the page, fill
    /// (a) the joining-month STORY — the same facts the student cards show ("Joined {date} · {billed} of
    /// {total} classes left · {amount} of {full}"), from the anchor period's frozen basis + the student's
    /// enrollment date — and (b) the SYSTEM-suggested amount and the NAME of whoever set a manual joining
    /// amount, from the proration-decision audit logs. All batched (three repo queries + one name lookup,
    /// no N+1). Rows with no override (auto-prorated) keep null suggested/setBy — the amount IS the story.
    /// </summary>
    private async Task EnrichProrationTransparencyAsync(long teacherId, List<CollectionRow> rows)
    {
        var flagged = rows
            .Where(r => r.IsProratedFirstMonth && r.Status == "collected")
            .ToList();
        if (flagged.Count == 0) return;

        // ── (a) The joining-month story, keyed by student (the anchor month is per student). ──
        var storyStudentIds = flagged
            .Where(r => long.TryParse(r.StudentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(r => long.Parse(r.StudentId!, CultureInfo.InvariantCulture))
            .Distinct()
            .ToList();
        if (storyStudentIds.Count > 0)
        {
            // A HAND-SET joining month qualifies even when it is NOT prorated: it is sticky (every
            // automatic re-price skips it) and can sit at the full month price, so without this the
            // frozen bill is invisible on the ledger while it quietly diverges from the student's
            // current price. Same predicate as GetStudentsByStatusAsync - all three surfaces now agree.
            var anchorInfo = (await _unitOfWork.PaymentsRepo
                    .GetAnchorPeriodInfoByStudentIdsAsync(teacherId, storyStudentIds))
                .Where(a => a.IsProRated || a.IsProrationManual)
                .ToDictionary(a => a.StudentId);
            var joinDates = anchorInfo.Count > 0
                ? await _unitOfWork.PaymentsRepo
                    .GetEarliestAssignmentDatesForStudentsAsync(teacherId, anchorInfo.Keys.ToList())
                : new Dictionary<long, DateTime>();

            foreach (var r in flagged)
            {
                if (!long.TryParse(r.StudentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid)
                    || !anchorInfo.TryGetValue(sid, out var anc))
                    continue;
                // Teacher-LOCAL join day. AssignedAt is stamped DateTime.UtcNow, so a student assigned
                // at 01:30 Cairo carries 2026-09-07T22:30Z and rendered as "Joined 7 Sep" - a day before
                // the day the price was actually computed from, and a day off from the collect editor,
                // which is the one surface that already converts (ResolveJoinDateInAnchorMonth).
                r.ProrationJoinedAt = joinDates.TryGetValue(sid, out var jd)
                    ? _timeZoneService.ConvertUtcToLocal(jd).Date
                    : anc.PeriodStart;
                r.ProrationClassesTotal = anc.ClassesTotal;
                r.ProrationClassesBilled = anc.ClassesBilled;
                r.ProratedFirstMonthAmount = anc.AmountDue;
                // Display-only reconstruction of the full month price: the stored fraction was
                // round(amount ÷ base, 4), so base ≈ amount ÷ fraction rounded to whole currency.
                r.ProrationFullMonthAmount = anc.ProRatedFraction > 0m
                    ? Math.Round(anc.AmountDue / anc.ProRatedFraction, 0, MidpointRounding.AwayFromZero)
                    : null;
                r.IsProrationManual = anc.IsProrationManual;
            }
        }

        // ── (b) Manual-override audit (system-suggested vs set · by whom). ──
        var txIds = flagged
            .Where(r => long.TryParse(r.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(r => long.Parse(r.Id, CultureInfo.InvariantCulture))
            .Distinct()
            .ToList();
        if (txIds.Count == 0) return;

        var audit = await _unitOfWork.PaymentsRepo
            .GetProrationAuditByTransactionIdsAsync(teacherId, txIds);
        if (audit.Count == 0) return;

        var byTx = audit.ToDictionary(a => a.PaymentTransactionId);
        var setterIds = audit.Where(a => a.SetByUserId.HasValue).Select(a => a.SetByUserId!.Value).Distinct().ToList();
        var names = setterIds.Count > 0
            ? await _unitOfWork.Users.GetUserFullNamesByUserIdsAsync(setterIds)
            : new Dictionary<long, string>();

        foreach (var r in flagged)
        {
            if (!long.TryParse(r.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var txId)) continue;
            if (!byTx.TryGetValue(txId, out var a)) continue;
            r.SystemSuggestedProratedAmount = a.SuggestedAmount;
            if (a.SetByUserId is long uid && names.TryGetValue(uid, out var nm))
                r.ProrationSetByName = nm;
        }
    }

    /// <inheritdoc />
    public async Task<Result<CollectionsSummaryResponse>> GetCollectionsSummaryAsync(
        long teacherId, DateTime? from, DateTime? to, string? asOfMonth, long? sessionId = null,
        long? collectedByUserId = null, bool? exactRange = null,
        string? search = null, bool includeAdjustments = true)
    {
        var repo = _unitOfWork.PaymentsRepo;

        // ── Resolve the money/activity window (TRUE range). Both omitted → current local month. ──
        //
        // EXACT-INSTANT variant: mirrors the collections-ledger path (GetCollectionsByMonthAsync) so
        // the day-insight cards are computed over the SAME window as the rows they sit above. The
        // wallet's "in drawer now" scope is bounded by the exact last hand-over instant; widening it
        // to whole days made the card report the collector's entire day while the list below showed
        // only the post-hand-over slice — the "cards don't follow my filter" report.
        //
        // `exactRange` comes from the controller reading the RAW query text. NEVER re-infer it from
        // the parsed value's TimeOfDay: a midnight-to-midnight exact window parses identically to a
        // date-only day filter, and that ambiguity is exactly what leaked a whole extra day before.
        DateTime startDate, endExclusive;
        // Both bounds are required for an instant window: mirroring one bound onto the other would
        // make [from, to) empty rather than "that instant's day". A single-sided value always takes
        // the whole-day path below, which is also all the controller can ever produce.
        bool timed = (exactRange ?? false) && from.HasValue && to.HasValue;
        if (from.HasValue || to.HasValue)
        {
            if (timed)
            {
                // Precise instants [from, to) — the wallet's "in drawer now" window.
                var f = from!.Value;
                var t = to!.Value;
                if (t < f) (f, t) = (t, f);
                startDate = f;
                endExclusive = t;
            }
            else
            {
                var f = (from ?? to)!.Value.Date;
                var t = (to ?? from)!.Value.Date;
                if (t < f) (f, t) = (t, f);
                startDate = f;
                endExclusive = t.AddDays(1);
            }
        }
        else
        {
            timed = false;
            var today = _timeZoneService.GetTeacherLocalDate(teacherId);
            startDate = new DateTime(today.Year, today.Month, 1);
            endExclusive = startDate.AddMonths(1);
        }
        // Last moment of the window — drives the as-of month default below. On the day-based path this
        // is the final DAY (unchanged); on an exact range stepping back a whole day would misfile a
        // window that opens at a month boundary (e.g. Oct 1 00:00 → Oct 1 21:00 reading as September).
        DateTime toInclusive = timed ? endExclusive.AddTicks(-1) : endExclusive.AddDays(-1);
        DateTime endInclusiveTick = endExclusive.AddTicks(-1);

        // ── Resolve the as-of month for the status buckets (payment status is per-month). ──
        int asOfYear, asOfMonth_;
        if (!string.IsNullOrWhiteSpace(asOfMonth))
        {
            if (!TryParseYearMonth(asOfMonth, out asOfYear, out asOfMonth_))
                return Result<CollectionsSummaryResponse>.Failure(
                    _localizer, PaymentConstants.Messages.PaymentInvalidMonthFormat,
                    HttpStatusCode.UnprocessableEntity);
        }
        else
        {
            asOfYear = toInclusive.Year;
            asOfMonth_ = toInclusive.Month;
        }
        var asOfMonthStart = new DateTime(asOfYear, asOfMonth_, 1);
        var asOfMonthEnd = asOfMonthStart.AddMonths(1).AddDays(-1);

        // ── Money / activity — honour the true [startDate, endExclusive) range. ──
        decimal netCash, refundsTotal;
        int txCount, studentsPaid, departedTotal, departedRefundDue, departedAmountOwed;
        if (collectedByUserId is long collectorUid)
        {
            // COLLECTOR-SCOPED strip (the drill-in ledger's header): every figure the day strip
            // renders is attributed to THIS collector, computed from the SAME sources as the ledger
            // rows below it (!IsDeleted transactions + includeDeleted:false refund lines +
            // collector-narrowed departures) so the strip always reconciles with the visible list.
            // Previously the strip showed the ACCOUNT-WIDE day (e.g. "43 paid / 80 departed" on a
            // collector who took 23) — the "Omar ezz" customer report. The per-month status buckets
            // below stay account-wide: they are not collector-attributable and the collector screen
            // does not render them.
            // search + includeAdjustments follow the LIST (2026-09-09): the strip is rendered directly
            // above the rows, so it must answer for the same filtered set, not the whole day.
            // Three SQL aggregates, no rows. This used to fetch EVERY transaction in the window with
            // Session, Allocations, Allocations.PaymentPeriod and EditLogs eager-loaded, just to add
            // them up in memory — and the app's default wallet scope opens in 2020, so "the window"
            // was the collector's entire history.
            var (grossCollected, collectorTxCount, distinctPayingStudents) =
                await repo.GetTransactionRangeAggregatesAsync(
                    teacherId, startDate, endInclusiveTick, sessionId, collectorUid, search);
            // "Collections only" (includeAdjustments=false) hides refunds from the list, so the strip
            // must stop counting them too - otherwise it reports money out that the list denies.
            var collectorRefunds = includeAdjustments
                ? (await repo.GetCollectorRefundsInRangeAsync(
                        teacherId, collectorUid, startDate, endExclusive, includeDeleted: false))
                    .Where(r => r.RefundAmount > 0m)
                    .ToList()
                : new List<CollectorRefundRow>();
            var collectorDepartures = await repo.GetDepartureRefundsByDateRangeAsync(
                teacherId, startDate, endExclusive, collectorUid);

            refundsTotal = collectorRefunds.Sum(r => r.RefundAmount);
            // Clients render "collected" as net + refunds (gross) — emit net accordingly.
            netCash = grossCollected - refundsTotal;
            txCount = collectorTxCount;
            studentsPaid = distinctPayingStudents;
            // "Departed" on a collector strip = departures whose refund was charged to THIS
            // collector (they CONFIRMED the departure and handed the cash back — §7.4).
            // RefundDue mirrors the refund LINES charged to them, which is what clients render
            // as the refunds count.
            departedTotal = collectorDepartures.Count;
            departedRefundDue = collectorRefunds.Count;
            departedAmountOwed = 0;
        }
        else
        {
            decimal grossCash = await repo.GetCashCollectedInRangeAsync(teacherId, sessionId, startDate, endExclusive);
            var refunds = await repo.GetDepartureRefundsByDateRangeAsync(teacherId, startDate, endExclusive);
            refundsTotal = refunds.Sum(r => r.RefundAmount);
            // GetCashCollectedInRange is net of soft-deleted (reversed) transactions, but departure
            // refunds do NOT soft-delete the underlying collection — subtract them so the headline
            // reconciles with the collections ledger and per-collector cards (mirrors GetTrackingAsync).
            netCash = grossCash - refundsTotal;
            (_, txCount) = await repo.GetTransactionsByDateRangePagedAsync(
                teacherId, startDate, endInclusiveTick, sessionId, null, page: 1, pageSize: 1);
            studentsPaid = await repo.CountDistinctPayingStudentsInRangeAsync(
                teacherId, sessionId, startDate, endExclusive);

            // ── Departures — true range. ──
            (departedTotal, departedRefundDue, departedAmountOwed) =
                await repo.CountDeparturesInRangeAsync(teacherId, startDate, endExclusive);
        }

        // ── Student payment status — anchored to asOfMonth. ──
        var (paidInFull, prorated, unpaid) = await repo.GetStudentPaymentStatusCountsAsync(teacherId, asOfMonthEnd);
        int partial = await repo.CountPartiallyPaidStudentsInMonthAsync(teacherId, asOfMonthStart, asOfMonthEnd);

        // ── Per-collector — true range; enriched with name + role exactly like GetTrackingAsync. ──
        var collectors = await repo.GetDashboardPerCollectorAsync(teacherId, startDate, toInclusive);
        // OWN-SCOPE (2026-09-09). The controller forces collectedByUserId to the caller for an
        // ASSISTANT (AssistantScopeUserId), but GetDashboardPerCollectorAsync takes no collector
        // predicate, so this block still returned EVERY collector - each one's name, exact collected
        // amount and transaction count, including the tutor's own - to any assistant holding
        // Payment.ViewCollectorSummary. The app never rendered it, so it was invisible in use while
        // being plainly readable on the wire. 758aa6e scoped the rows and the money block and missed
        // this one. Teacher/SuperAdmin callers pass null here and are unaffected.
        if (collectedByUserId.HasValue)
            collectors = collectors.Where(c => c.UserId == collectedByUserId.Value).ToList();
        var collectorUserIds = collectors.Select(c => c.UserId).Distinct().ToList();
        var names = collectorUserIds.Count > 0
            ? await _unitOfWork.Users.GetUserFullNamesByUserIdsAsync(collectorUserIds)
            : new Dictionary<long, string>();
        // Role by IDENTITY, not an active-assistant allow-list: the ONLY "Teacher" collector is the
        // teacher account owner. Every other collector — assistant, REMOVED (soft-deleted) assistant,
        // or center assistant — is "Assistant". The old allow-list (GetUserIdsByTeacherAccountIdAsync,
        // active-only) excluded a removed assistant, so her collections defaulted to "Teacher" and the
        // app filed them under the teacher's own "collected by me" card (§7.4).
        var teacherOwnerUserId = await _unitOfWork.Users.GetTeacherUserIdByIdAsync(teacherId);
        var byCollector = collectors
            .OrderByDescending(c => c.Collected)
            .Select(c => new CollectionsSummaryCollectorDto
            {
                UserId = c.UserId.ToString(CultureInfo.InvariantCulture),
                Name = names.TryGetValue(c.UserId, out var nm) ? nm : c.UserName,
                Role = c.UserId == teacherOwnerUserId ? "Teacher" : "Assistant",
                CollectedAmount = c.Collected,
                TransactionCount = c.TransactionCount
            })
            .ToList();

        var response = new CollectionsSummaryResponse
        {
            From = startDate,
            To = toInclusive,
            AsOfMonth = $"{asOfYear:D4}-{asOfMonth_:D2}",
            AsOfMonthLabel = asOfMonthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            NetCashCollected = netCash,
            RefundsTotal = refundsTotal,
            TransactionCount = txCount,
            StudentsPaidCount = studentsPaid,
            PaidInFullCount = paidInFull,
            PartialCount = partial,
            ProratedCount = prorated,
            UnpaidCount = unpaid,
            DepartedCount = departedTotal,
            DepartedRefundDueCount = departedRefundDue,
            DepartedAmountOwedCount = departedAmountOwed,
            ByCollector = byCollector
        };

        return Result<CollectionsSummaryResponse>.Success(
            response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<AssistantWalletScreenResponse>> GetAssistantWalletScreenAsync(
        long teacherId, long assistantId, int page, int limit,
        long? restrictToAssistantUserId = null, string? search = null)
    {
        // Tenant-scoped lookup: a wallet belonging to another teacher's assistant returns null → 404.
        // TODO(assistant-dashboard): interim own-scoping. When an assistant calls, resolve THEIR OWN
        // wallet by user id and ignore the requested assistantId so they can never open a peer's
        // wallet. The dedicated assistant dashboard is to be built end-to-end by frontend + backend.
        var wallet = restrictToAssistantUserId is long ownUserId
            ? await _unitOfWork.PaymentsRepo.GetAssistantWalletByUserIdAsync(teacherId, ownUserId)
            : await _unitOfWork.PaymentsRepo.GetAssistantWalletAsync(teacherId, assistantId);
        if (wallet is null)
            return Result<AssistantWalletScreenResponse>.Failure(
                _localizer, PaymentConstants.Messages.WalletNotFound, HttpStatusCode.NotFound);

        (page, limit) = NormalizePaging(page, limit);

        DateTime nowUtc = DateTime.UtcNow;

        // WINDOWING (2026-08-06): the old rule scoped the list to "since the last WalletResetLog".
        // But a PARTIAL withdrawal ALSO writes a WalletResetLog, so any withdrawal reset the window
        // and emptied the list (and TotalCashCollected) even while a real balance was still held.
        // We now reconstruct the held balance from the collector's full signed event stream —
        // collections (+), refunds (−) and wallet hand-overs/withdrawals (−) — and anchor the window
        // at the LAST moment that running balance returned to exactly 0 (a full hand-over). Every
        // event after that point is precisely what still constitutes CurrentBalance, so the card and
        // the list stay consistent after a partial withdrawal, and the ledger reads:
        //   collections − refunds − withdrawals == held balance.
        // Bounded per collector in practice; a SQL-side windowed query is a documented follow-up if a
        // single collector's un-zeroed history ever grows past a few hundred rows.
        var allTxns = await _unitOfWork.PaymentsRepo
            .GetCollectorTransactionsInRangeAsync(teacherId, wallet.AssistantUserId, DateTime.MinValue, nowUtc);
        var allRefunds = await _unitOfWork.PaymentsRepo
            .GetCollectorRefundsInRangeAsync(teacherId, wallet.AssistantUserId, DateTime.MinValue, nowUtc);
        // Wallet hand-overs (full reset OR partial withdrawal) are money OUT of the held balance and
        // belong in the same ledger as negative lines, keyed on the wallet's AssistantId.
        // Reset/withdrawal ledger — keyed by AssistantId for a normal assistant, or CenterAssistantId
        // for a center-assistant wallet.
        var allResets = wallet.AssistantId.HasValue
            ? await _unitOfWork.PaymentsRepo.GetWalletResetLogsAsync(teacherId, wallet.AssistantId.Value)
            : (wallet.CenterAssistantId.HasValue
                ? await _unitOfWork.PaymentsRepo.GetWalletResetLogsForCenterAssistantAsync(teacherId, wallet.CenterAssistantId.Value)
                : (IReadOnlyList<Domain.Entities.WalletResetLog>)System.Array.Empty<Domain.Entities.WalletResetLog>());

        // NOTE: student-departure refunds are ALREADY in allRefunds — the departure-confirm path
        // reverses the period via ReverseDeparturePeriodAsync, which writes a PaymentEditLog(Reversed)
        // that GetCollectorRefundsInRangeAsync reads. Do NOT also read GetDepartureRefundsByDateRangeAsync
        // here or every departure refund is double-counted (breaks the collections − refunds − withdrawals
        // == CurrentBalance reconciliation).

        // One signed ledger. Amount carries the sign; Kind drives client rendering. CollectedAt is
        // the ordering instant. Both money reads stay on the UTC CollectedAt/EditedAt columns, and
        // ResetAt is UTC too, so the merged stream is instant-consistent (no local/UTC boundary).
        var ledger = new List<AssistantWalletCollectionItemDto>(
            allTxns.Count + allRefunds.Count + allResets.Count);
        ledger.AddRange(allTxns.Select(tx =>
        {
            var (appliedMonths, isEdited, originalAmount) = BuildCollectionLedgerMeta(tx);
            return new AssistantWalletCollectionItemDto
            {
                Id = tx.Id.ToString(CultureInfo.InvariantCulture),
                StudentId = tx.TeacherStudentId?.ToString(CultureInfo.InvariantCulture),
                StudentName = tx.StudentName,
                StudentCode = tx.StudentCode,
                // Live session name (eager-loaded); the transaction's own copy is a collection-time
                // snapshot that goes stale on rename, and is only the fallback for a deleted session.
                SessionName = ResolveSessionName(tx.Session?.SessionName, tx.SessionName),
                Amount = tx.AmountPaid,
                CollectedAt = tx.CollectedAt,
                Kind = "collection",
                // Collector's free-text note for a custom/partial collect (null for whole-month collects);
                // only collection lines carry a transaction — refund/withdrawal lines below leave it null.
                Note = tx.CollectionNote,
                // Which month(s) this collection settled (oldest-first) + any amount-edit trail.
                AppliedMonths = appliedMonths,
                IsEdited = isEdited,
                OriginalAmount = originalAmount
            };
        }));
        ledger.AddRange(allRefunds.Select(r => new AssistantWalletCollectionItemDto
        {
            Id = $"refund-{r.Id.ToString(CultureInfo.InvariantCulture)}",
            StudentId = r.StudentId?.ToString(CultureInfo.InvariantCulture),
            StudentName = r.StudentName,
            StudentCode = r.StudentCode,
            SessionName = string.IsNullOrEmpty(r.SessionName) ? null : r.SessionName,
            Amount = -r.RefundAmount, // negative → refund taken back from this collector
            CollectedAt = r.RefundedAt,
            Kind = "refund"
        }));
        ledger.AddRange(allResets.Select(w => new AssistantWalletCollectionItemDto
        {
            Id = $"withdrawal-{w.Id.ToString(CultureInfo.InvariantCulture)}",
            StudentId = null,
            StudentName = null,
            StudentCode = null,
            SessionName = null,
            Amount = -w.AmountReset, // negative → cash handed over to the tutor
            CollectedAt = w.ResetAt,
            Kind = "withdrawal"
        }));

        // Chronological order; on a same-instant tie, apply money-IN before money-OUT so a
        // collect-then-handover at the same timestamp nets to the right running balance.
        ledger.Sort((a, b) =>
        {
            int byTime = a.CollectedAt.CompareTo(b.CollectedAt);
            return byTime != 0 ? byTime : b.Amount.CompareTo(a.Amount);
        });

        // Anchor: the running held balance's zero-crossings (each a full hand-over). We track the last
        // TWO so we can fall back to the just-closed period when the current one is empty.
        decimal running = 0m;
        int lastZeroIdx = -1;
        int prevZeroIdx = -1;
        for (int i = 0; i < ledger.Count; i++)
        {
            running += ledger[i].Amount;
            if (running == 0m) { prevZeroIdx = lastZeroIdx; lastZeroIdx = i; }
        }

        // Normally the window is everything strictly AFTER the last full hand-over (the current holding
        // period). BUGFIX (2026-08-11): a FULL withdrawal ("take everything") drives the running balance
        // to exactly 0 at the withdrawal event, so it becomes lastZeroIdx and Skip(lastZeroIdx + 1)
        // returns nothing — the drill-in went blank right after a hand-over, hiding both the students
        // the collector collected from AND the withdrawal/refund negative lines. When the current
        // holding period is empty (the most recent event fully zeroed the balance), fall back to the
        // period that JUST CLOSED: from the previous full hand-over up to AND INCLUDING this one, so
        // those collections and the closing withdrawal stay visible. That period nets to 0
        // (collections − refunds − withdrawal), consistent with WalletBalance = CurrentBalance = 0.
        DateTime? sinceAt;
        List<AssistantWalletCollectionItemDto> windowed;
        bool currentPeriodEmpty = lastZeroIdx == ledger.Count - 1; // last event zeroed the balance
        if (currentPeriodEmpty && lastZeroIdx >= 0)
        {
            windowed = ledger.Skip(prevZeroIdx + 1).Take(lastZeroIdx - prevZeroIdx).ToList();
            sinceAt = prevZeroIdx >= 0 ? ledger[prevZeroIdx].CollectedAt : (DateTime?)null;
        }
        else
        {
            windowed = ledger.Skip(lastZeroIdx + 1).ToList();
            sinceAt = lastZeroIdx >= 0 ? ledger[lastZeroIdx].CollectedAt : (DateTime?)null;
        }

        // Gross collected / refunded within the window, reported separately (not netted) so the card
        // explains the list: collected X, refunded Y; withdrawals appear as their own negative lines.
        decimal periodCollected = windowed
            .Where(i => i.Kind == "collection").Sum(i => i.Amount);
        decimal periodRefunded = windowed
            .Where(i => i.Kind == "refund").Sum(i => -i.Amount);

        // Filter the LISTED rows by student name OR studentCode (case-insensitive). Applied AFTER the
        // window + totals are computed, so the card stays authoritative — only the list is narrowed.
        // A search naturally drops withdrawal lines (they carry no student).
        var merged = windowed;
        if (!string.IsNullOrWhiteSpace(search))
        {
            string searchLower = ArabicTextNormalizer.Normalize(search.Trim());
            merged = merged.Where(m =>
                (m.StudentName is not null && ArabicTextNormalizer.Normalize(m.StudentName).Contains(searchLower, StringComparison.Ordinal))
                || (m.StudentCode is not null && ArabicTextNormalizer.Normalize(m.StudentCode).Contains(searchLower, StringComparison.Ordinal)))
                .ToList();
        }

        int total = merged.Count;
        // Display order (mirrors the collector-scoped collections ledger): newest DAY first; within a
        // day, money-OUT (refunds/withdrawals, negative) before collections; newest time as the final
        // tiebreak. The running-balance pass above keeps its own chronological sort — untouched.
        var items = merged
            .OrderByDescending(i => i.CollectedAt.Date)
            .ThenBy(i => i.Amount < 0m ? 0 : 1)
            .ThenByDescending(i => i.CollectedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToList();

        // LIVE all-time collections count — the wallet's denormalized TransactionCount counter is
        // never decremented when a collection is later deleted, so it drifts above the live count
        // the tracking collectors card shows (the "45 outside / 53 inside" inconsistency report).
        int liveCollectionsCount = wallet.TransactionCount;
        var collectorUserId = wallet.Assistant?.UserId ?? wallet.CenterAssistant?.UserId;
        if (collectorUserId is long walletUserId)
        {
            var liveCounts = await _unitOfWork.PaymentsRepo
                .GetLiveCollectionCountsByCollectorUserAsync(teacherId);
            liveCollectionsCount = liveCounts.TryGetValue(walletUserId, out var n) ? n : 0;
        }

        var response = new AssistantWalletScreenResponse
        {
            Assistant = new AssistantWalletAssistantDto
            {
                Id = assistantId.ToString(CultureInfo.InvariantCulture),
                Name = wallet.Assistant?.User?.FullName ?? wallet.CenterAssistant?.User?.FullName,
                Role = "Assistant",
                AvatarUrl = null,
                TransactionCount = liveCollectionsCount
            },
            Wallet = new AssistantWalletInfoDto
            {
                TotalCashCollected = periodCollected,
                TotalRefunded = periodRefunded,
                WalletBalance = wallet.CurrentBalance,
                TotalCollectedAllTime = wallet.TotalCollected,
                CollectionsCount = liveCollectionsCount,
                LastActivityAt = wallet.LastCollectionAt
            },
            Collections = new AssistantWalletCollectionsDto
            {
                Total = total,
                Page = page,
                Limit = limit,
                SinceAt = sinceAt,
                // Strict last zero-crossing, independent of the just-closed-period display
                // fallback above -- the "in hand now" anchor (empty window after a full withdrawal).
                HeldSinceAt = lastZeroIdx >= 0 ? ledger[lastZeroIdx].CollectedAt : null,
                Items = items
            }
        };

        return Result<AssistantWalletScreenResponse>.Success(
            response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<CollectStudentsResponse>> GetCollectStudentsAsync(
        long teacherId, string? filter, string? search, int page, int limit, long? sessionId = null)
    {
        filter = string.IsNullOrWhiteSpace(filter) ? "all" : filter.Trim().ToLowerInvariant();
        if (filter != "all" && filter != "assigned" && filter != "unassigned")
            return Result<CollectStudentsResponse>.Failure(
                _localizer, PaymentConstants.Messages.PaymentInvalidCollectFilter,
                HttpStatusCode.UnprocessableEntity);

        (page, limit) = NormalizePaging(page, limit);

        // Session-scoped collect: resolve this session + its linked sessions (the SAME population the
        // take-attendance roster uses) so a session-launched collect shows only the relevant students.
        // A foreign/unknown sessionId yields an empty scope → no students (the repo still filters by
        // teacherId, so there is no cross-tenant leak). Null sessionId = teacher-wide, unchanged.
        IReadOnlyCollection<long>? sessionScopeIds = null;
        if (sessionId is long sid)
        {
            var linked = await _unitOfWork.SessionsRepo.GetLinkedSessionsAsync(sid);
            var scope = new List<long>(linked.Count + 1) { sid };
            scope.AddRange(linked.Select(s => s.Id));
            sessionScopeIds = scope;
        }

        var (rows, total, cAll, cAssigned, cUnassigned) = await _unitOfWork.PaymentsRepo
            .GetCollectStudentsPagedAsync(
                teacherId, filter, search, page, limit, CurrentMonthEnd(teacherId), sessionScopeIds);

        // Proration transparency: enrich ONLY the paged rows (no N+1) with the anchor month's prorated
        // amount/fraction + the anchor date (first-attendance vs assignment), so the collect card can show
        // the full amount beside a prorated first-month figure with a reason — instead of a bare 0.
        var proration = await BuildProrationEnrichmentAsync(
            teacherId, rows.Select(r => r.TeacherStudentId).Distinct().ToList());

        var students = new List<CollectStudentDto>(rows.Count);
        foreach (var r in rows)
        {
            proration.TryGetValue(r.TeacherStudentId, out var pe);
            students.Add(new CollectStudentDto
            {
                Id = r.TeacherStudentId.ToString(CultureInfo.InvariantCulture),
                Name = r.StudentName,
                StudentCode = r.StudentCode,
                AvatarUrl = null,
                Amount = r.Amount,
                MonthlyAmount = r.Amount,
                SessionName = r.SessionName,
                Assignment = r.IsAssigned ? "assigned" : "unassigned",
                Status = r.IsUnpaid ? "unpaid" : "paid",
                UnpaidMonths = r.UnpaidMonths,
                TotalOwed = r.TotalOwed,
                IsExempt = r.Amount == 0m,
                // Same wire shape as the collect lookup's breakdown, so the app's bulk
                // multi-select seeds the QR-scan review queue from these rows directly.
                UnpaidMonthsBreakdown = r.UnpaidMonthsList
                    .Select(m => new CollectLookupMonthDto
                    {
                        PeriodId = m.PeriodId.ToString(CultureInfo.InvariantCulture),
                        Month = m.PeriodStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                        MonthLabel = m.PeriodStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                        Amount = m.Remaining,
                        IsProrated = m.IsProRated,
                        ProRatedFraction = m.IsProRated ? m.ProRatedFraction : (decimal?)null
                    })
                    .ToList(),
                IsProrated = pe.IsProrated,
                ProRatedFraction = pe.Fraction,
                ProratedAmount = pe.ProratedAmount,
                JoinedAt = pe.JoinedAt,
                JoinedAtIsFirstAttendance = pe.JoinedAtIsFirstAttendance,
                IsProrationManual = pe.IsProrationManual,
                ProrationClassesTotal = pe.ClassesTotal,
                ProrationClassesBilled = pe.ClassesBilled
            });
        }

        var response = new CollectStudentsResponse
        {
            Counts = new CollectStudentsCountsDto { All = cAll, Assigned = cAssigned, Unassigned = cUnassigned },
            Page = page,
            Limit = limit,
            TotalItems = total,
            TotalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)limit),
            Students = students
        };

        return Result<CollectStudentsResponse>.Success(
            response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <summary>Per-student proration transparency computed by <see cref="BuildProrationEnrichmentAsync"/>.
    /// <c>JoinedAtIsFirstAttendance</c> is a retained wire flag, always false since rev 2 (2026-09-03) —
    /// the anchor date is ALWAYS the enrollment (earliest assignment) date; attendance never prices the
    /// joining month.</summary>
    private readonly record struct ProrationEnrichment(
        bool IsProrated, decimal? Fraction, decimal? ProratedAmount,
        DateTime? JoinedAt, bool JoinedAtIsFirstAttendance, bool IsProrationManual,
        int? ClassesTotal, int? ClassesBilled);

    /// <summary>
    /// Batch-builds proration transparency for a set of students: the anchor month's prorated amount +
    /// fraction and the ENROLLMENT date ("joined {date}" — rev 2). Only students with a PRORATED anchor
    /// appear in the result. This mirrors the inline enrichment in <see cref="GetStudentsByStatusAsync"/>
    /// (kept intact for live-safety) so the collect list and the status list agree on the same figures.
    /// A HAND-SET anchor is included even when it is not prorated (2026-09-09).
    /// One round of batch queries, no N+1.
    /// </summary>
    private async Task<Dictionary<long, ProrationEnrichment>> BuildProrationEnrichmentAsync(
        long teacherId, IReadOnlyList<long> studentIds)
    {
        var result = new Dictionary<long, ProrationEnrichment>();
        if (studentIds.Count == 0) return result;

        // See EnrichProrationTransparencyAsync: a hand-set anchor counts even when not prorated.
        var anchorInfo = (await _unitOfWork.PaymentsRepo
                .GetAnchorPeriodInfoByStudentIdsAsync(teacherId, studentIds))
            .Where(a => a.IsProRated || a.IsProrationManual)
            .ToDictionary(a => a.StudentId);
        if (anchorInfo.Count == 0) return result;

        // Enrollment day per student = earliest assignment across ALL sessions (the original join).
        var joinDates = await _unitOfWork.PaymentsRepo
            .GetEarliestAssignmentDatesForStudentsAsync(teacherId, anchorInfo.Keys.ToList());

        foreach (var (studentId, anc) in anchorInfo)
        {
            if (anc.SessionId is null) continue;
            // Teacher-LOCAL join day - see EnrichProrationTransparencyAsync.
            var joinedAt = joinDates.TryGetValue(studentId, out var ad)
                ? _timeZoneService.ConvertUtcToLocal(ad).Date
                : anc.PeriodStart;
            // IsProrated comes from the anchor row, not a hardcoded true: a hand-set anchor priced at
            // the FULL month now reaches this point and must not claim to be prorated.
            result[studentId] = new ProrationEnrichment(
                anc.IsProRated, anc.ProRatedFraction, anc.AmountDue, joinedAt, false,
                anc.IsProrationManual, anc.ClassesTotal, anc.ClassesBilled);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<Result<StudentsByStatusResponse>> GetStudentsByStatusAsync(
        long teacherId, string? month, string? status, int page, int limit,
        long? sessionId = null, string? search = null)
    {
        // PAY-2: default to the teacher's current local month when omitted (only 422 on malformed).
        if (!TryResolveMonth(teacherId, month, out int year, out int mon))
            return Result<StudentsByStatusResponse>.Failure(
                _localizer, PaymentConstants.Messages.PaymentInvalidMonthFormat,
                HttpStatusCode.UnprocessableEntity);

        // B1: status is now OPTIONAL. Null → the whole (session-scoped) roster, each student
        // carrying its own status. A SUPPLIED status still behaves exactly as before (422 on a
        // value that is neither paid|partial|prorated|unpaid).
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (status is not null
            && status != "paid" && status != "partial" && status != "prorated" && status != "unpaid")
            return Result<StudentsByStatusResponse>.Failure(
                _localizer, PaymentConstants.Messages.PaymentInvalidStatusFilter,
                HttpStatusCode.UnprocessableEntity);
        (page, limit) = NormalizePaging(page, limit);
        var monthStart = new DateTime(year, mon, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var (rows, total, groupCollected, groupExpected, groupUnpaid, groupExpectedRate) = await _unitOfWork.PaymentsRepo
            .GetStudentsByPaymentStatusPagedAsync(
                teacherId, status, monthStart, monthEnd, page, limit, sessionId, search);

        // Resolve collector identity for the paid card: batch-fetch the distinct collector user ids'
        // display names and classify each as Teacher vs Assistant (same mechanism as GetTrackingAsync),
        // so a paid row can show "collected by X" without an N+1.
        var collectorUserIds = rows
            .Where(r => r.CollectedByUserId.HasValue)
            .Select(r => r.CollectedByUserId!.Value)
            .Distinct()
            .ToList();
        var collectorNames = collectorUserIds.Count > 0
            ? await _unitOfWork.Users.GetUserFullNamesByUserIdsAsync(collectorUserIds)
            : new Dictionary<long, string>();
        // Role by identity (see GetCollectionsSummaryAsync): "Teacher" only for the account owner's
        // own collections; every other collector — including a removed assistant — is "Assistant".
        var teacherOwnerUserId = collectorUserIds.Count > 0
            ? await _unitOfWork.Users.GetTeacherUserIdByIdAsync(teacherId)
            : (long?)null;

        // Proration transparency: batch the anchor-period info (prorated amount + fraction) and the
        // ENROLLMENT date (earliest assignment — rev 2: attendance never prices the joining month) so a
        // prorated row can justify its reduced amount without an N+1. Enriched for a PRORATED anchor
        // OR a hand-set one: a joining month fixed by hand is STICKY (every automatic re-price skips
        // it) even when it is priced at the full month and even when proration is switched OFF, so the
        // row must be able to say "set by hand" — otherwise the frozen bill is invisible while it
        // quietly diverges from the student's current price (prod 2026-09-08, teacher 171).
        var anchorInfo = (await _unitOfWork.PaymentsRepo
                .GetAnchorPeriodInfoByStudentIdsAsync(
                    teacherId, rows.Select(r => r.TeacherStudentId).Distinct().ToList()))
            .Where(a => a.IsProRated || a.IsProrationManual)
            .ToDictionary(a => a.StudentId);
        var anchorJoinDates = anchorInfo.Count > 0
            ? await _unitOfWork.PaymentsRepo
                .GetEarliestAssignmentDatesForStudentsAsync(teacherId, anchorInfo.Keys.ToList())
            : new Dictionary<long, DateTime>();

        var students = new List<StudentByStatusDto>(rows.Count);
        foreach (var r in rows)
        {
            string? collectedByName = null;
            string? collectedByRole = null;
            if (r.CollectedByUserId is long cby)
            {
                collectedByName = collectorNames.TryGetValue(cby, out var cn) ? cn : null;
                collectedByRole = cby == teacherOwnerUserId ? "Teacher" : "Assistant";
            }

            bool isProrated = false;
            bool isProrationManual = false;
            decimal? proratedFraction = null, proratedAmount = null;
            int? prorationClassesTotal = null, prorationClassesBilled = null;
            DateTime? joinedAt = null;
            // Retained wire flag, always false since rev 2 — the date shown is always the enrollment day.
            bool joinedAtIsFirstAttendance = false;
            if (anchorInfo.TryGetValue(r.TeacherStudentId, out var anc) && anc.SessionId is not null)
            {
                // Taken from the anchor row, never assumed: the set now also carries hand-set anchors
                // that are priced at the FULL month (IsProRated false).
                isProrated = anc.IsProRated;
                isProrationManual = anc.IsProrationManual;
                proratedFraction = anc.ProRatedFraction;
                proratedAmount = anc.AmountDue;
                prorationClassesTotal = anc.ClassesTotal;
                prorationClassesBilled = anc.ClassesBilled;
                // Teacher-LOCAL join day - see EnrichProrationTransparencyAsync.
                joinedAt = anchorJoinDates.TryGetValue(r.TeacherStudentId, out var ad)
                    ? _timeZoneService.ConvertUtcToLocal(ad).Date
                    : anc.PeriodStart;
            }

            students.Add(new StudentByStatusDto
            {
                Id = r.TeacherStudentId.ToString(CultureInfo.InvariantCulture),
                Name = r.StudentName,
                AvatarUrl = null,
                // With a filter every row matches the requested status; without one each row
                // carries its own repo-computed status (falls back to "unpaid" defensively).
                Status = status ?? r.Status ?? "unpaid",
                AmountPerMonth = r.AmountPerMonth,
                AmountPaid = r.AmountPaid,
                AmountDue = r.AmountDue,
                UnpaidAmount = r.UnpaidAmount,
                UnpaidMonths = r.UnpaidMonths,
                StudentCode = r.StudentCode,
                IsExempt = r.AmountPerMonth == 0m,
                SessionId = r.SessionId,
                SessionName = r.SessionName,
                PaidOn = r.PaidOn,
                CollectedByName = collectedByName,
                CollectedByRole = collectedByRole,
                IsProrated = isProrated,
                ProRatedFraction = proratedFraction,
                ProratedAmount = proratedAmount,
                JoinedAt = joinedAt,
                JoinedAtIsFirstAttendance = joinedAtIsFirstAttendance,
                IsProrationManual = isProrationManual,
                ProrationClassesTotal = prorationClassesTotal,
                ProrationClassesBilled = prorationClassesBilled
            });
        }

        var response = new StudentsByStatusResponse
        {
            Month = $"{year:D4}-{mon:D2}",
            MonthLabel = monthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            // Top-level status echoes the filter; "all" when the roster is mixed (no filter).
            Status = status ?? "all",
            TotalCollected = groupCollected,
            MonthAmount = groupExpected,
            // Ambiguous top-level "default fee": no single value exists (per-session/per-student).
            // The per-student amountPerMonth is authoritative; confirm intended meaning with FE.
            AmountPerMonth = 0m,
            // Full-set expected revenue for this scope (Σ per-student custom-or-session rate). The
            // session-detail screen renders this directly instead of session-default × student-count,
            // so a per-student custom amount is reflected in the expected total.
            ExpectedAmount = groupExpectedRate,
            TotalUnpaidAmount = status == "paid" ? 0m : groupUnpaid,
            Total = total,
            Page = page,
            Limit = limit,
            Students = students
        };

        return Result<StudentsByStatusResponse>.Success(
            response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<YearlyCollectionsResponse>> GetYearlyCollectionsAsync(
        long teacherId, string? month, int? year, int page, int limit)
    {
        // DASH-1: a yearly view only needs the YEAR — derive it from the unified "YYYY-MM" selector
        // (take its year component) OR the legacy ?year=<int>, defaulting to the teacher's current
        // local year when omitted. The month component (if any) is genuinely irrelevant here and is
        // neither used nor validated — a malformed month must not 422 a year-only screen.
        var errorKey = ResolveCollectionsYear(teacherId, month, year, out int resolvedYear);
        if (errorKey is not null)
            return Result<YearlyCollectionsResponse>.Failure(
                _localizer, errorKey, HttpStatusCode.UnprocessableEntity);

        (page, limit) = NormalizePaging(page, limit);
        var yearStart = new DateTime(resolvedYear, 1, 1);
        var yearEnd = new DateTime(resolvedYear, 12, 31);

        var (rows, total) = await _unitOfWork.PaymentsRepo
            .GetYearlyCollectionsPagedAsync(teacherId, yearStart, yearEnd, page, limit);

        int baseIndex = (page - 1) * limit;
        var items = new List<YearlyStudentDto>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var months = r.Months.Select(m => new YearlyMonthDto
            {
                Month = m.Month,
                Status = m.IsPaid ? "paid" : (m.IsProRated ? "prorated" : "unpaid"),
                Amount = m.AmountPaid
            }).ToList();

            int paidMonths = months.Count(m => m.Status == "paid");
            int unpaidMonths = months.Count(m => m.Status == "unpaid");
            decimal totalCollected = months.Sum(m => m.Amount);

            items.Add(new YearlyStudentDto
            {
                Id = r.TeacherStudentId.ToString(CultureInfo.InvariantCulture),
                Index = baseIndex + i + 1,
                StudentName = r.StudentName,
                Summary = new YearlyStudentSummaryDto
                {
                    PaidMonths = paidMonths,
                    UnpaidMonths = unpaidMonths,
                    TotalCollected = totalCollected,
                    Label = $"Paid {paidMonths} months, unpaid {unpaidMonths} months"
                },
                Months = months
            });
        }

        var response = new YearlyCollectionsResponse
        {
            Year = resolvedYear,
            Page = page,
            Limit = limit,
            TotalItems = total,
            TotalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)limit),
            Items = items
        };

        return Result<YearlyCollectionsResponse>.Success(
            response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<CollectLookupResponse>> ResolveLookupAsync(
        long teacherId, string? qr, string? code, string? name, string? month = null)
    {
        if (string.IsNullOrWhiteSpace(qr) && string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name))
            return Result<CollectLookupResponse>.Failure(
                _localizer, PaymentConstants.Messages.PaymentLookupCriteriaRequired,
                HttpStatusCode.UnprocessableEntity);

        // Month-scoped screens pass their opened month so amountDue/monthsOwed/breakdown are the
        // arrears THROUGH that month — the same figure their card displayed and the same boundary
        // the month-scoped mark-paid charges. No month → through the current month, as before.
        if (!TryResolveThroughMonthEnd(teacherId, month, out var throughMonthEnd))
            return Result<CollectLookupResponse>.Failure(
                _localizer, PaymentConstants.Messages.PaymentInvalidMonthFormat,
                HttpStatusCode.UnprocessableEntity);

        // One-month-in-advance option (only for the current-month collect flow, which is what QR
        // scan + tap-to-collect use): when the student is caught up through this month, the lookup
        // also offers next month (current + 1 — the same cap CollectPaymentAsync accepts). Anchored
        // to the teacher's LOCAL current month, and only when the lookup covers it, so a past-month
        // view never offers an "advance". Paying one month ahead is optional, never forced.
        var localDate = _timeZoneService.GetTeacherLocalDate(teacherId);
        var currentMonthStart = new DateTime(localDate.Year, localDate.Month, 1);
        var currentMonthEnd = currentMonthStart.AddMonths(1).AddDays(-1);
        DateTime? advanceCapEnd = throughMonthEnd >= currentMonthEnd
            ? currentMonthStart.AddMonths(2).AddDays(-1)
            : (DateTime?)null;

        var row = await _unitOfWork.PaymentsRepo.ResolveCollectLookupAsync(
            teacherId, qr, code, name, throughMonthEnd, advanceCapEnd);
        if (row is null)
            return Result<CollectLookupResponse>.Failure(
                _localizer, PaymentConstants.Messages.PaymentLookupStudentNotFound, HttpStatusCode.NotFound);

        var response = new CollectLookupResponse
        {
            Student = new CollectLookupStudentDto
            {
                Id = row.TeacherStudentId.ToString(CultureInfo.InvariantCulture),
                Name = row.StudentName,
                Code = row.StudentCode,
                Group = row.Group,
                AvatarUrl = null
            },
            AmountDue = row.AmountDue,
            PaymentStatus = row.IsUnpaid ? "unpaid" : "paid",
            // Feature C (additive): per-month rate + how many months / how much is owed.
            MonthlyAmount = row.MonthlyAmount,
            MonthsOwed = row.MonthsOwed,
            TotalOwed = row.AmountDue,
            // req 6: oldest-first per-month breakdown so the collect UI can default to the full owed
            // amount, offer a 1..N month counter, and label exactly which month(s) are being paid.
            UnpaidMonthsBreakdown = row.UnpaidMonths
                .Select(m => new CollectLookupMonthDto
                {
                    PeriodId = m.PeriodId.ToString(CultureInfo.InvariantCulture),
                    Month = m.PeriodStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    MonthLabel = m.PeriodStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                    Amount = m.Remaining,
                    IsProrated = m.IsProRated,
                    ProRatedFraction = m.IsProRated ? m.ProRatedFraction : (decimal?)null
                })
                .ToList(),
            // Separate advance offer — the student stays "paid"; only the collect pop-up uses this.
            AdvanceMonth = row.AdvanceMonth is null ? null : new CollectLookupMonthDto
            {
                PeriodId = row.AdvanceMonth.PeriodId.ToString(CultureInfo.InvariantCulture),
                Month = row.AdvanceMonth.PeriodStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                MonthLabel = row.AdvanceMonth.PeriodStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                Amount = row.AdvanceMonth.Remaining,
                IsProrated = row.AdvanceMonth.IsProRated,
                ProRatedFraction = row.AdvanceMonth.IsProRated ? row.AdvanceMonth.ProRatedFraction : (decimal?)null
            }
        };

        // Joining-month proration enrichment (REQ-PAY-021/022): if the student has a still-unpaid anchor
        // month, decorate its breakdown entry with the system suggestion + class counts so the collect
        // popup renders the "prorated joining month" box in ONE call. Single student → no N+1.
        var studentForSession = await _unitOfWork.Students
            .GetActiveByIdAndTeacherAsync(row.TeacherStudentId, teacherId);
        if (studentForSession?.SessionId is long lookupSessionId)
        {
            var suggestion = await _paymentService.ComputeProrationSuggestionAsync(
                teacherId, row.TeacherStudentId, lookupSessionId);
            if (suggestion.Applicable && suggestion.AnchorPeriodId is long anchorId)
            {
                var anchorIdStr = anchorId.ToString(CultureInfo.InvariantCulture);
                var monthDto = response.UnpaidMonthsBreakdown
                    .FirstOrDefault(m => m.PeriodId == anchorIdStr);
                if (monthDto is not null)
                {
                    monthDto.IsJoiningMonthProrated = true;
                    monthDto.SuggestedProratedAmount = suggestion.SuggestedAmount;
                    monthDto.ClassesAttendedThisMonth = suggestion.ClassesAttendedThisMonth;
                    monthDto.ClassesTotalThisMonth = suggestion.ClassesTotalThisMonth;
                    monthDto.ClassesBilledThisMonth = suggestion.ClassesBilledThisMonth;
                    monthDto.FirstClassDate = suggestion.FirstClassDate;
                    monthDto.JoinDate = suggestion.JoinDate;
                    monthDto.ProratedReason = suggestion.Reason;
                    monthDto.IsProrationManual = suggestion.IsManualOverride;
                    monthDto.ProrationSetByName = suggestion.SetByName;
                    monthDto.ProrationSetAt = suggestion.SetAt;

                    // Mirror onto the response root so the mobile collect popup reads it without
                    // walking the breakdown (the item above stays populated for other clients).
                    response.IsJoiningMonthProrated = true;
                    response.SuggestedProratedAmount = suggestion.SuggestedAmount;
                    response.ClassesAttendedThisMonth = suggestion.ClassesAttendedThisMonth;
                    response.ClassesTotalThisMonth = suggestion.ClassesTotalThisMonth;
                    response.ClassesBilledThisMonth = suggestion.ClassesBilledThisMonth;
                    response.FirstClassDate = suggestion.FirstClassDate;
                    response.JoinDate = suggestion.JoinDate;
                    response.ProratedReason = suggestion.Reason;
                    response.IsProrationManual = suggestion.IsManualOverride;
                    response.ProrationSetByName = suggestion.SetByName;
                    response.ProrationSetAt = suggestion.SetAt;
                }
            }

            // HAND-SET, PRORATION OFF. ComputeProrationSuggestionAsync returns Applicable=false the
            // moment proration is disabled, so the block above never runs and a joining month someone
            // fixed by hand became invisible — while still STICKY: every later automatic re-price
            // skips it, so the bill silently diverges from the student's current price (prod
            // 2026-09-08, teacher 171 — two students hand-set at 80, later priced 50, September stuck
            // at 80 with nothing on any screen to say so). The flags ride along on the unpaid-months
            // projection (no extra query); the actor/date audit is read ONLY when one is actually
            // hand-set.
            if (!response.IsProrationManual
                && row.UnpaidMonths.FirstOrDefault(m => m.IsProrationAnchorMonth && m.IsProrationManual)
                    is { } handSet)
            {
                var handSetIdStr = handSet.PeriodId.ToString(CultureInfo.InvariantCulture);
                var handSetDto = response.UnpaidMonthsBreakdown
                    .FirstOrDefault(m => m.PeriodId == handSetIdStr);
                var audit = await _unitOfWork.PaymentsRepo
                    .GetLatestProrationEditForPeriodAsync(teacherId, handSet.PeriodId);
                string? setByName = audit?.SetByUserId is long auditUid
                    ? await _unitOfWork.Users.GetUserFullNameByUserIdAsync(auditUid)
                    : null;

                response.IsProrationManual = true;
                response.ProrationSetByName = setByName;
                response.ProrationSetAt = audit?.SetAt;
                if (handSetDto is not null)
                {
                    handSetDto.IsProrationManual = true;
                    handSetDto.ProrationSetByName = setByName;
                    handSetDto.ProrationSetAt = audit?.SetAt;
                }
            }
        }

        // Same-day cross-collector soft-confirm (Issue 1): if someone already collected from this student
        // today, surface WHO + how much + which month so the collect popup can warn (never block) before
        // recording. One indexed query; a name/period read only when a same-day payment actually exists.
        var sameDayToday = await _unitOfWork.PaymentsRepo
            .GetSameDayTransactionsAsync(teacherId, row.TeacherStudentId, localDate);
        if (sameDayToday.Count > 0)
        {
            var mostRecentToday = sameDayToday.OrderByDescending(t => t.CollectedAt).First();
            response.HasSameDayPayment = true;
            response.TodayPaidAmount = sameDayToday.Sum(t => t.AmountPaid);
            response.TodayPaidByName = mostRecentToday.CollectedByUserId is long todayCid
                ? await _unitOfWork.Users.GetUserFullNameByUserIdAsync(todayCid)
                : null;
            if (mostRecentToday.PaymentPeriodId is long todayPid)
            {
                var settled = await _unitOfWork.PaymentsRepo.GetPaymentPeriodByIdAsync(todayPid);
                if (settled is not null)
                    response.TodayPaidMonthLabel =
                        settled.PeriodStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
            }
        }

        return Result<CollectLookupResponse>.Success(
            response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<TrackingResponse>> GetTrackingAsync(long teacherId, string? month)
    {
        // PAY-2: the main tracking-screen load carries no month by default → use the teacher's
        // current local month (only 422 when a month is provided but malformed).
        if (!TryResolveMonth(teacherId, month, out int year, out int mon))
            return Result<TrackingResponse>.Failure(
                _localizer, PaymentConstants.Messages.PaymentInvalidMonthFormat,
                HttpStatusCode.UnprocessableEntity);

        var monthStart = new DateTime(year, mon, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var repo = _unitOfWork.PaymentsRepo;

        // Period-based, assigned-gated obligation aggregates powering the card's two perspectives
        // (This month | Total-through-this-month). Expected/Collected/Remaining here are attributed
        // to the month each installment BELONGS to (never the cash date) and gated to currently-assigned
        // students, so each perspective reconciles and matches the paid/prorated/unpaid headcounts.
        // CollectedAmount (below) stays the date-based physical "cash collected this calendar month".
        // ── OBLIGATION view (Expected / Remaining) — "Current + Overdue", net of forgiven ──
        // (teacher-requested 2026-08-19, mohamedatef). The Total column is what is STILL due or overdue
        // right now: this month's net bill + every earlier month's unpaid balance. Fully-collected closed
        // months drop off (their cash is done; it lives on the collections/ledger screen), so Expected never
        // counts money already collected for a past month. Net-of-forgiven so forgiving lowers Expected in
        // both columns and each reconciles as Expected = Collected(period) + Remaining. The repo's gross
        // all-time Expected/Collected totals (tuple slots 4/5) are intentionally discarded here.
        var (_, collectedThisMonthPeriod, remainingThisMonth, _, _, outstandingTotal, _) =
            await repo.GetAssignedObligationAggregatesAsync(teacherId, monthStart, monthEnd);

        decimal expectedThisMonth = collectedThisMonthPeriod + remainingThisMonth;   // Σ(AmountDue − Forgiven) this month
        decimal earlierRemaining = outstandingTotal - remainingThisMonth;            // unpaid balance of earlier months
        if (earlierRemaining < 0m) earlierRemaining = 0m;
        decimal expectedTotal = expectedThisMonth + earlierRemaining;               // this month's bill + arrears only

        // ── CASH view (Collected) — real money physically collected THIS calendar month, decomposed by the
        // installment month each payment settled (a July arrear paid in August lands in August's "earlier"
        // slice, never on July's card). A SEPARATE lens from the obligation table above — the two
        // intentionally do NOT reconcile with each other. collectedTotal ties to the collectors' cash total
        // (CollectedByAssistant.TotalCollected) to the cent, and = collectedThisMonth + previous + advance.
        var (collectedTotal, collectedThisMonth, collectedPreviousMonths, collectedAdvance) =
            await repo.GetCashCollectedBreakdownAsync(teacherId, monthStart, monthEnd);

        var (paidCount, proratedCount, unpaidCount) = await repo.GetStudentPaymentStatusCountsAsync(teacherId, monthEnd);
        var perSession = await repo.GetDashboardPerSessionAsync(teacherId, null, null, monthStart, monthEnd);
        var activeMeta = await repo.GetActiveSessionsCollectionSummaryAsync(
            teacherId, _timeZoneService.GetTeacherLocalDate(teacherId));
        var collectors = await repo.GetDashboardPerCollectorAsync(teacherId, monthStart, monthEnd);
        // TotalStudents = students currently assigned to a session (the ones a teacher expects
        // to collect from), not every student on the account.
        int totalStudents = await repo.CountAssignedStudentsAsync(teacherId);

        var metaBySession = activeMeta.ToDictionary(m => m.SessionId);
        // Month-scoped per-session cash + distinct paying students, and current assigned totals.
        var monthColl = (await repo.GetSessionMonthCollectionAsync(teacherId, monthStart, monthStart.AddMonths(1)))
            .ToDictionary(x => x.SessionId);
        var assignedBySession = (await repo.GetAssignedStudentCountsPerSessionAsync(teacherId))
            .ToDictionary(x => x.SessionId, x => x.TotalStudents);

        // Enrich collectors with real names (repo returns null) + role (assistant vs teacher).
        var collectorUserIds = collectors.Select(c => c.UserId).Distinct().ToList();
        var names = collectorUserIds.Count > 0
            ? await _unitOfWork.Users.GetUserFullNamesByUserIdsAsync(collectorUserIds)
            : new Dictionary<long, string>();
        // Role by identity (see GetCollectionsSummaryAsync): the teacher row is ONLY the account
        // owner's own collections. A removed/center assistant who collected is NOT a teacher row —
        // she surfaces from her wallet in assistantRows below, correctly labeled "Assistant".
        var teacherOwnerUserId = await _unitOfWork.Users.GetTeacherUserIdByIdAsync(teacherId);

        // All assistant wallets (every assistant on the account, even those who collected nothing
        // this month), keyed by user id — the source for "show ALL assistants" + each one's held
        // balance, and the AssistantId the wallet drill-in route actually matches on (the collector
        // row's user id is NOT a valid wallet id, which is why tapping an assistant used to 404).
        var wallets = await _unitOfWork.PaymentsRepo.GetAllAssistantWalletsAsync(teacherId);
        var collectorByUser = collectors
            .GroupBy(c => c.UserId)
            .ToDictionary(g => g.Key, g => g.First());

        // Teacher row: the account owner's own collections (at most one). The teacher has no wallet,
        // but their card is still shown (and the app links it to the teacher's own collections list).
        var teacherRows = collectors
            .Where(c => c.UserId == teacherOwnerUserId)
            .Select(c => new TrackingAssistantDto
            {
                Id = c.UserId.ToString(CultureInfo.InvariantCulture),
                Name = names.TryGetValue(c.UserId, out var nm) ? nm : c.UserName,
                AvatarUrl = null,
                Role = "Teacher",
                TransactionCount = c.TransactionCount,
                CollectedAmount = c.Collected,
                AssistantId = null,
                WalletBalance = 0m
            });

        // REMOVED collectors are HISTORY, not roster (2026-09-05). Soft-deleting an assistant is
        // terminal (AssistantCleanupJob is a no-op) and their wallet row lives forever, so the
        // roster-driven list above used to show a removed person on EVERY future month as a silent
        // "0 EGP / 0 payments" card. A removed collector now survives only through their removal
        // MONTH — deleted in July ⇒ visible in July and every earlier month, gone from August on —
        // so the months they actually worked keep their history and later months read true.
        // Safe by construction: delete is blocked while the wallet holds cash and login is killed on
        // delete, so a removed collector can neither collect nor confirm a departure afterwards and
        // their post-removal months are always empty. The activity escape hatch below is a belt for
        // any path that could still attribute money to them — a row with money is NEVER hidden, so
        // the visible cards always reconcile with CollectedByAssistant.TotalCollected.
        bool IsVisibleThisMonth(Domain.Entities.AssistantWallet w)
        {
            // RemovedAt, NOT DeletedAt: DeletedAt is also stamped by a temporary SUSPEND
            // (AssistantService.ToggleStatus), so reading it here labelled a suspended assistant
            // "Removed" and dropped her from the next month's card — while suspension exists
            // precisely to stop someone collecting for a while and then bring them back.
            var removedAtUtc = w.Assistant?.RemovedAt ?? w.CenterAssistant?.RemovedAt;
            if (removedAtUtc is null)
                return true;
            if (collectorByUser.TryGetValue(w.AssistantUserId, out var activity)
                && (activity.TransactionCount != 0 || activity.Collected != 0m))
                return true;
            // Held cash is never hidden, in any month. Delete is blocked on a non-zero balance, but a
            // post-removal correction (a deleted/edited collection charged back to the original
            // collector) can drive the balance non-zero afterwards — in a month with no activity of
            // its own, which the escape hatch above would not catch. Hiding that row would hide
            // money the account still owes or is still owed.
            if (w.CurrentBalance != 0m)
                return true;
            // Teacher-local removal day vs the teacher-local month being viewed (monthStart is a
            // naive local date), so a late-night removal is judged on the teacher's calendar.
            return _timeZoneService.ConvertUtcToLocal(removedAtUtc.Value).Date >= monthStart;
        }

        // One row per assistant (from wallets = the full roster, minus collectors removed before this
        // month), overlaying this month's collection totals where present. AssistantId is the
        // wallet-navigation id; WalletBalance is cash held.
        var assistantRows = wallets
            .Where(IsVisibleThisMonth)
            .Select(w =>
            {
                // Value-tuple: a missing key yields a default (all-zero) tuple, so month totals
                // fall back to 0 for an assistant who didn't collect this month.
                collectorByUser.TryGetValue(w.AssistantUserId, out var c);
                return new TrackingAssistantDto
                {
                    Id = w.AssistantUserId.ToString(CultureInfo.InvariantCulture),
                    Name = w.Assistant?.User?.FullName
                        ?? (names.TryGetValue(w.AssistantUserId, out var an) ? an : null),
                    AvatarUrl = null,
                    Role = "Assistant",
                    TransactionCount = c.TransactionCount,
                    CollectedAmount = c.Collected,
                    AssistantId = (w.AssistantId ?? w.CenterAssistantId ?? 0).ToString(CultureInfo.InvariantCulture),
                    WalletBalance = w.CurrentBalance,
                    // Marks the surviving history rows so the app can label them instead of showing a
                    // normal card for someone no longer on the account.
                    IsRemoved = (w.Assistant?.RemovedAt ?? w.CenterAssistant?.RemovedAt) is not null
                };
            })
            // Most relevant first: collected this month, then those still holding cash, then name.
            .OrderByDescending(a => a.CollectedAmount)
            .ThenByDescending(a => a.WalletBalance)
            .ThenBy(a => a.Name);

        var assistants = teacherRows.Concat(assistantRows).ToList();

        // Sessions: everything month-scoped. CollectedAmount = actual cash into the session this
        // month; StudentsCollected = distinct students who paid into it this month; StudentsTotal =
        // students currently assigned to it. Schedule (day/time) still comes from the session meta.
        var sessions = perSession.Select(s =>
        {
            metaBySession.TryGetValue(s.SessionId, out var meta);
            monthColl.TryGetValue(s.SessionId, out var mc);
            decimal cash = mc.CashCollected;
            int paidStudents = mc.PaidStudents;
            int totalStudents = assignedBySession.TryGetValue(s.SessionId, out var tot) ? tot : 0;
            decimal pct = s.Expected > 0 ? Math.Round(cash / s.Expected * 100m, 2) : 0m;
            return new TrackingSessionDto
            {
                Id = s.SessionId.ToString(CultureInfo.InvariantCulture),
                Title = s.SessionName,
                Day = meta?.SelectedDays,
                Time = meta != null ? FormatTime(meta.StartTime) : null,
                Grade = null,
                CollectedAmount = cash,
                StudentsCollected = paidStudents,
                StudentsTotal = totalStudents,
                ProgressPercent = pct,
                GroupId = s.SessionGroupId?.ToString(CultureInfo.InvariantCulture),
                GroupName = s.SessionGroupName
            };
        }).ToList();

        int sessionsTotal = sessions.Count;
        int sessionsCollected = sessions.Count(s => s.CollectedAmount > 0m);
        // Progress is now STUDENT-based (caught-up assigned students ÷ all assigned students) so the bar
        // fill matches the "X of Y students paid" label beside it. paidCount / totalStudents both come
        // from the same assigned-student population (§7.4), so they reconcile.
        decimal progressPercent = totalStudents > 0
            ? Math.Round((decimal)paidCount / totalStudents * 100m, 2)
            : 0m;

        var response = new TrackingResponse
        {
            Month = $"{year:D4}-{mon:D2}",
            MonthLabel = monthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            Summary = new TrackingSummaryDto
            {
                TotalStudents = totalStudents,
                TotalSessions = sessionsTotal,
                SessionsCollected = sessionsCollected,
                SessionsTotal = sessionsTotal,
                ProgressPercent = progressPercent,
                // OBLIGATION view (net of forgiven). This month = current bill; Total = current bill + arrears.
                ExpectedRevenue = expectedThisMonth,
                RemainingAmount = remainingThisMonth,
                ExpectedTotal = expectedTotal,
                RemainingTotal = outstandingTotal,
                // CASH view: real cash collected THIS calendar month, split by the installment it settled.
                // Total = ThisMonth + PreviousMonths + Advance (a separate lens from Expected/Remaining above).
                CollectedTotal = collectedTotal,
                CollectedThisMonth = collectedThisMonth,
                CollectedPreviousMonths = collectedPreviousMonths,
                CollectedInAdvance = collectedAdvance
            },
            StatusBreakdown = new TrackingStatusBreakdownDto
            {
                Paid = paidCount,
                Prorated = proratedCount,
                Unpaid = unpaidCount
            },
            CollectedByAssistant = new TrackingByAssistantDto
            {
                TotalCollected = collectors.Sum(c => c.Collected),
                Assistants = assistants
            },
            CollectedBySessions = new TrackingBySessionsDto { Sessions = sessions }
        };

        return Result<TrackingResponse>.Success(response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<MarkPaidResponse>> MarkPaidAsync(
        long teacherId, long actingUserId, List<long> studentIds, string? idempotencyKey,
        string? month = null, bool duplicateConfirmed = false)
    {
        if (studentIds is null || studentIds.Count == 0)
            return Result<MarkPaidResponse>.Failure(
                _localizer, PaymentConstants.Messages.PaymentNoStudentsSelected,
                HttpStatusCode.UnprocessableEntity);

        // Month-scoped screens (a July unpaid card) pass their opened month: the charge is capped
        // at that month's end so the student pays exactly what the card displayed — arrears
        // THROUGH the opened month — never later months' pre-generated periods. No month → the
        // current local month, the pre-existing behavior for every other entry point.
        if (!TryResolveThroughMonthEnd(teacherId, month, out var throughMonthEnd))
            return Result<MarkPaidResponse>.Failure(
                _localizer, PaymentConstants.Messages.PaymentInvalidMonthFormat,
                HttpStatusCode.UnprocessableEntity);

        // Idempotent replay: return the stored result instead of re-collecting.
        var stored = await _idempotency.GetStoredResultAsync("mark-paid", teacherId, idempotencyKey);
        if (stored is not null)
            return Result<MarkPaidResponse>.Success(
                JsonSerializer.Deserialize<MarkPaidResponse>(stored, IdempotencyJson)!,
                _localizer, PaymentConstants.Messages.Success);

        var results = new List<MarkPaidResultDto>();
        int markedPaid = 0;

        foreach (var studentId in studentIds.Distinct())
        {
            string idStr = studentId.ToString(CultureInfo.InvariantCulture);

            var student = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(studentId, teacherId);
            if (student is null)
            {
                results.Add(new MarkPaidResultDto { StudentId = idStr, Status = "failed", Reason = "Student not found." });
                continue;
            }
            if (student.SessionId is null)
            {
                results.Add(new MarkPaidResultDto { StudentId = idStr, Status = "failed", Reason = "Student is not assigned to a session." });
                continue;
            }

            // Amount = the student's TOTAL arrears through the resolved month boundary (all overdue
            // months up to it, not just the earliest). The collection engine then cascades it across
            // those months, oldest first, clearing each. Already paid → no-op success.
            decimal amount = await _unitOfWork.PaymentsRepo
                .GetOverdueTotalThroughAsync(teacherId, studentId, student.SessionId, throughMonthEnd);
            if (amount <= 0m)
            {
                results.Add(new MarkPaidResultDto { StudentId = idStr, Status = "paid", Reason = "Already paid." });
                markedPaid++;
                continue;
            }

            // Route through the single proven collection path (its own transaction per student).
            var collect = await _paymentService.CollectPaymentAsync(new CollectPaymentDto
            {
                TeacherId = teacherId,
                TeacherStudentId = studentId,
                SessionId = student.SessionId.Value,
                Amount = amount,
                PaymentMethod = PaymentCollectionMethod.ManualCode,
                CollectedByUserId = actingUserId,
                // Issue 1 (2026-09-02): honour the confirm flag so a re-submit proceeds past a same-day
                // warning instead of dead-ending on "Already collected today".
                DuplicateConfirmed = duplicateConfirmed,
                AlreadyPaidConfirmed = false
            });

            if (collect.IsSuccess && collect.Data?.Transaction is not null)
            {
                results.Add(new MarkPaidResultDto { StudentId = idStr, Status = "paid", Reason = null });
                markedPaid++;
            }
            else if (collect.Data?.IsAlreadyPaid == true)
            {
                results.Add(new MarkPaidResultDto { StudentId = idStr, Status = "paid", Reason = "Already paid." });
                markedPaid++;
            }
            else if (collect.Data?.IsSameDayDuplicate == true)
            {
                // Confirmable warning (NOT a dead fail): carry the attribution so the client can render
                // "Mohamed collected 200 for September today · Collect anyway" and re-submit confirmed.
                results.Add(new MarkPaidResultDto
                {
                    StudentId = idStr,
                    Status = "needs_confirmation",
                    Reason = collect.Message,
                    NeedsConfirmation = true,
                    TodayPaidByName = collect.Data.TodayPaidByName,
                    TodayPaidAmount = collect.Data.TodayPaidAmount,
                    TodayPaidMonthLabel = collect.Data.TodayPaidMonthLabel
                });
            }
            else
            {
                results.Add(new MarkPaidResultDto { StudentId = idStr, Status = "failed", Reason = collect.Message });
            }
        }

        var response = new MarkPaidResponse { MarkedPaidCount = markedPaid, Results = results };
        // Only cache the idempotent result when nothing is pending a confirm — otherwise a re-submit with
        // the SAME key + duplicateConfirmed=true would replay the (stale) needs_confirmation response and
        // never collect. No money moved on a needs_confirmation pass, so re-running it is safe.
        if (!results.Any(r => r.NeedsConfirmation))
            await _idempotency.StoreResultAsync("mark-paid", teacherId, idempotencyKey,
                JsonSerializer.Serialize(response, IdempotencyJson));
        return Result<MarkPaidResponse>.Success(response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<SubmitCollectionResponse>> SubmitCollectionAsync(
        long teacherId, long actingUserId, string? month, long? classSessionId,
        List<SubmitCollectionItem> students, string? note, string? idempotencyKey)
    {
        if (students is null || students.Count == 0)
            return Result<SubmitCollectionResponse>.Failure(
                _localizer, PaymentConstants.Messages.PaymentSubmitBatchEmpty, HttpStatusCode.Conflict);

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        // Feature C: a note is REQUIRED for any resolvable item whose amount is a partial/custom
        // value — i.e. NOT a whole-month multiple of that student's monthly rate. A whole-month
        // multiple (N × rate) is a plain "pay N months" and needs none. The note may be PER-ITEM
        // (item.Note — how the mobile app sends it) or batch-level; either satisfies the rule.
        // Unresolved students / invalid amounts are left to the per-item loop below (they fail
        // there, not as a note error).
        //
        // SECOND, WIDER acceptance (added with the offline-collect fix): an amount that exactly
        // settles a whole number of the student's OWED months is equally unremarkable, even when
        // those months are not all priced at the monthly rate. The multiple rule silently assumed
        // they were — so a correctly prorated joining month (100 against a 300 rate), a
        // carried-forward transfer balance, or a partly-paid month were all rejected as "custom"
        // and 400'd. Strictly widening: the cheap multiple check still runs first and anything it
        // accepted is untouched; the extra query only happens on amounts that used to be rejected.
        // Hoisted out of the loop: the window is the same for every student in the batch, so resolving
        // the teacher's local date per item meant a timezone lookup per row for no reason.
        var noteCheckLocalDate = _timeZoneService.GetTeacherLocalDate(teacherId);
        var noteCheckAdvanceCapEnd =
            new DateTime(noteCheckLocalDate.Year, noteCheckLocalDate.Month, 1).AddMonths(2).AddDays(-1);

        // Existence + rate for the WHOLE batch in two keyed reads, before the loop. Per item these
        // were two round trips each (a 40-student batch spent up to 80 before any money moved) and
        // both answers are pure lookups that cannot change mid-validation. The cheap
        // IsWholeMonthMultiple short-circuit still runs first inside the loop, so the expensive
        // per-student period read below still only happens for amounts that fail it.
        var noteCheckCandidateIds = students
            .Where(i => EffectiveItemNote(i, note) is null && i.Amount > 0m)
            .Select(i => i.StudentId)
            .Distinct()
            .ToList();
        var noteCheckActiveIds = (await _unitOfWork.PaymentsRepo
            .GetActiveStudentIdsForTeacherAsync(teacherId, noteCheckCandidateIds)).ToHashSet();
        var noteCheckRates = await _unitOfWork.PaymentsRepo
            .GetStudentMonthlyRatesAsync(teacherId, noteCheckActiveIds.ToList());

        foreach (var item in students)
        {
            if (EffectiveItemNote(item, note) is not null) continue;
            if (item.Amount <= 0m) continue;
            if (!noteCheckActiveIds.Contains(item.StudentId)) continue;
            // Absent only for a student the rate query could not resolve at all; 0 is what the
            // single-student form returns in that case, and IsWholeMonthMultiple treats it the same.
            decimal rate = noteCheckRates.TryGetValue(item.StudentId, out var r) ? r : 0m;
            if (Extensions.PaymentAmountRules.IsWholeMonthMultiple(item.Amount, rate)) continue;

            // Same window and ordering the collect engine itself fills: arrears through the current
            // local month plus at most one month in advance, oldest first. Reading it here keeps the
            // "does this need a note?" answer aligned with what the engine will actually do.
            var owedPeriods = await _unitOfWork.PaymentsRepo
                .GetUnpaidPeriodsThroughAsync(teacherId, item.StudentId, null, noteCheckAdvanceCapEnd);
            var remainings = owedPeriods
                .Select(p => p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m))
                .ToList();

            if (!Extensions.PaymentAmountRules.SettlesWholeOwedMonths(item.Amount, remainings))
                return Result<SubmitCollectionResponse>.Failure(
                    _localizer, PaymentConstants.Messages.CollectNoteRequired, HttpStatusCode.BadRequest);
        }

        var stored = await _idempotency.GetStoredResultAsync("submit", teacherId, idempotencyKey);
        if (stored is not null)
            return Result<SubmitCollectionResponse>.Success(
                JsonSerializer.Deserialize<SubmitCollectionResponse>(stored, IdempotencyJson)!,
                _localizer, PaymentConstants.Messages.Success);

        var results = new List<SubmitCollectionResultDto>(students.Count);
        int submitted = 0;
        decimal totalCollected = 0m;
        // PAY-6: a studentId repeated in the batch must NOT be collected twice — mark repeats failed
        // (mark-paid dedupes with .Distinct(); submit carries per-item amounts, so report per item).
        var seen = new HashSet<long>();

        foreach (var item in students)
        {
            string idStr = item.StudentId.ToString(CultureInfo.InvariantCulture);

            if (!seen.Add(item.StudentId))
            {
                results.Add(new SubmitCollectionResultDto { StudentId = idStr, Status = "failed", Reason = "Duplicate student in this batch." });
                continue;
            }

            if (item.Amount <= 0m)
            {
                results.Add(new SubmitCollectionResultDto { StudentId = idStr, Status = "failed", Reason = "Invalid amount." });
                continue;
            }

            var student = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(item.StudentId, teacherId);
            if (student is null)
            {
                results.Add(new SubmitCollectionResultDto { StudentId = idStr, Status = "failed", Reason = "Student not found." });
                continue;
            }

            long? sessionId = classSessionId ?? student.SessionId;
            if (sessionId is null)
            {
                results.Add(new SubmitCollectionResultDto { StudentId = idStr, Status = "failed", Reason = "No session for this student." });
                continue;
            }

            // Explicit submit after client-side review → confirm duplicates/already-paid (like batch-collect).
            var collect = await _paymentService.CollectPaymentAsync(new CollectPaymentDto
            {
                TeacherId = teacherId,
                TeacherStudentId = item.StudentId,
                SessionId = sessionId.Value,
                Amount = item.Amount,
                PaymentMethod = PaymentCollectionMethod.BarcodeScan,
                CollectedByUserId = actingUserId,
                CollectionNote = EffectiveItemNote(item, note),
                DuplicateConfirmed = true,
                AlreadyPaidConfirmed = true
            });

            if (collect.IsSuccess && collect.Data?.Transaction is not null)
            {
                results.Add(new SubmitCollectionResultDto { StudentId = idStr, Status = "committed", Reason = null });
                submitted++;
                totalCollected += collect.Data.Transaction.AmountPaid;
            }
            else
            {
                results.Add(new SubmitCollectionResultDto { StudentId = idStr, Status = "failed", Reason = collect.Message });
            }
        }

        var response = new SubmitCollectionResponse
        {
            SubmittedCount = submitted,
            TotalCollected = totalCollected,
            Results = results
        };
        await _idempotency.StoreResultAsync("submit", teacherId, idempotencyKey,
            JsonSerializer.Serialize(response, IdempotencyJson));
        return Result<SubmitCollectionResponse>.Success(response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <summary>
    /// Feature C: the note that applies to one submit item — its own trimmed <c>Note</c> when
    /// present, else the batch-level note (already trimmed by the caller). Null when neither is set.
    /// </summary>
    private static string? EffectiveItemNote(SubmitCollectionItem item, string? batchNote)
        => string.IsNullOrWhiteSpace(item.Note) ? batchNote : item.Note.Trim();

    /// <inheritdoc />
    public async Task<Result<WalletWithdrawResponse>> WithdrawAsync(
        long teacherId, long assistantId, decimal? amount, long actingUserId, string? idempotencyKey)
    {
        var stored = await _idempotency.GetStoredResultAsync("withdraw", teacherId, idempotencyKey);
        if (stored is not null)
            return Result<WalletWithdrawResponse>.Success(
                JsonSerializer.Deserialize<WalletWithdrawResponse>(stored, IdempotencyJson)!,
                _localizer, PaymentConstants.Messages.Success);

        var result = await _paymentService.WithdrawFromWalletAsync(teacherId, assistantId, amount, actingUserId);
        if (!result.IsSuccess || result.Data is null)
            // PAY-5: propagate the underlying localized message + stable code (now set by
            // WithdrawFromWalletAsync) instead of dropping them behind a hardcoded English string.
            return Result<WalletWithdrawResponse>.Failure(result);

        var r = result.Data;
        var response = new WalletWithdrawResponse
        {
            WithdrawalId = r.WithdrawalId.ToString(CultureInfo.InvariantCulture),
            Status = "completed",
            Amount = r.Amount,
            WalletBalanceAfter = r.WalletBalanceAfter,
            RequestedAt = r.RequestedAt
        };
        // Only successful withdrawals are cached (a failed attempt can be retried freely).
        await _idempotency.StoreResultAsync("withdraw", teacherId, idempotencyKey,
            JsonSerializer.Serialize(response, IdempotencyJson));
        return Result<WalletWithdrawResponse>.Success(response, _localizer, PaymentConstants.Messages.Success);
    }

    /// <inheritdoc />
    public async Task<Result<ForgiveBalanceResponse>> ForgiveBalanceAsync(
        long teacherId, long actingUserId, long teacherStudentId, decimal amount, string? note)
    {
        // 1. Validate amount (> 0) and student.
        if (amount <= 0m)
            return Result<ForgiveBalanceResponse>.Failure(
                _localizer, PaymentConstants.Messages.ForgiveAmountInvalid, HttpStatusCode.UnprocessableEntity);

        var student = await _unitOfWork.Students.GetActiveByIdAndTeacherAsync(teacherStudentId, teacherId);
        if (student is null)
            return Result<ForgiveBalanceResponse>.Failure(
                _localizer, PaymentConstants.Messages.StudentNotFound, HttpStatusCode.NotFound);

        // 2. Outstanding is judged THROUGH the current teacher-local month (§7.4) — never future
        //    pre-generated months. Forgive at most that.
        var monthEnd = CurrentMonthEnd(teacherId);
        decimal outstanding = await _unitOfWork.PaymentsRepo
            .GetOverdueTotalThroughAsync(teacherId, teacherStudentId, null, monthEnd);
        if (amount > outstanding)
            return Result<ForgiveBalanceResponse>.Failure(
                _localizer, PaymentConstants.Messages.ForgiveAmountExceedsOutstanding, HttpStatusCode.UnprocessableEntity);

        string? sessionName = await ResolveStudentSessionNameAsync(teacherId, student.SessionId);

        // 3. Oldest-unpaid-first cascade through the current month (tracked periods).
        var periods = await _unitOfWork.PaymentsRepo
            .GetUnpaidPeriodsThroughAsync(teacherId, teacherStudentId, null, monthEnd);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction) await _unitOfWork.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            var forgiveness = new PaymentForgiveness
            {
                TeacherId = teacherId,
                TeacherStudentId = teacherStudentId,
                SessionId = student.SessionId,
                Amount = amount,
                Note = Truncate(note, PaymentConstants.EditReasonMaxLength),
                Status = ForgivenessStatus.Active,
                ForgivenByUserId = actingUserId,
                ForgivenAt = now,
                StudentName = student.StudentName,
                StudentCode = student.StudentCode,
                SessionName = sessionName,
                CreateAt = now
            };
            await _unitOfWork.PaymentsRepo.AddPaymentForgivenessAsync(forgiveness);

            decimal left = amount;
            int periodsNewlySettled = 0;
            var allocations = new List<PaymentForgivenessAllocation>();
            foreach (var p in periods)
            {
                if (left <= 0m) break;
                decimal remaining = p.AmountDue - p.AmountPaid - (p.ForgivenAmount ?? 0m);
                if (remaining <= 0m) continue;

                decimal apply = Math.Min(left, remaining);
                p.ForgivenAmount = (p.ForgivenAmount ?? 0m) + apply;
                left -= apply;

                bool nowSettled = p.AmountPaid + (p.ForgivenAmount ?? 0m) >= p.AmountDue;
                if (nowSettled && p.PaymentStatus != PaymentStatus.Paid) periodsNewlySettled++;
                p.PaymentStatus = nowSettled
                    ? PaymentStatus.Paid
                    : (p.AmountPaid > 0m ? PaymentStatus.PartiallyPaid : PaymentStatus.Unpaid);
                await _unitOfWork.PaymentsRepo.UpdatePaymentPeriodAsync(p);

                allocations.Add(new PaymentForgivenessAllocation
                {
                    PaymentForgiveness = forgiveness,
                    PaymentPeriodId = p.Id,
                    TeacherId = teacherId,
                    AmountForgiven = apply,
                    CreateAt = now
                });
            }

            // Defensive: `amount` was validated <= outstanding, so `left` should be 0; record only
            // what actually landed on periods.
            decimal appliedTotal = amount - left;
            forgiveness.Amount = appliedTotal;

            if (allocations.Count > 0)
                await _unitOfWork.PaymentsRepo.AddPaymentForgivenessAllocationsRangeAsync(allocations);

            // Counter stays in sync: outstanding drops by the waived cash-equivalent; settled months
            // leave the unpaid tail (NOT counted as PAID — no cash was collected). Recompute streak.
            await AdjustCounterForForgivenessAsync(
                teacherId, teacherStudentId, -appliedTotal, -periodsNewlySettled);

            await _unitOfWork.SaveChangesAsync();
            if (ownsTransaction) await _unitOfWork.CommitAsync();

            var summary = await BuildForgiveStudentSummaryAsync(
                teacherId, teacherStudentId, student.StudentName, student.StudentCode, sessionName, monthEnd);
            var byName = await ResolveUserNameAsync(actingUserId);

            var response = new ForgiveBalanceResponse
            {
                Forgiveness = new ForgivenessDto
                {
                    Id = forgiveness.Id.ToString(CultureInfo.InvariantCulture),
                    Amount = appliedTotal,
                    Note = forgiveness.Note,
                    ByName = byName,
                    ByUserId = actingUserId.ToString(CultureInfo.InvariantCulture),
                    Date = forgiveness.ForgivenAt,
                    Status = "active"
                },
                Student = summary
            };
            return Result<ForgiveBalanceResponse>.Success(
                response, _localizer, PaymentConstants.Messages.ForgiveSuccess);
        }
        catch
        {
            if (ownsTransaction) await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<ForgiveBalanceResponse>> ReverseForgivenessAsync(
        long teacherId, long actingUserId, long forgivenessId, string? note)
    {
        var forgiveness = await _unitOfWork.PaymentsRepo
            .GetForgivenessByIdAndTeacherAsync(teacherId, forgivenessId);
        if (forgiveness is null)
            return Result<ForgiveBalanceResponse>.Failure(
                _localizer, PaymentConstants.Messages.ForgivenessNotFound, HttpStatusCode.NotFound);
        if (forgiveness.Status == ForgivenessStatus.Reversed)
            return Result<ForgiveBalanceResponse>.Failure(
                _localizer, PaymentConstants.Messages.ForgivenessAlreadyReversed, HttpStatusCode.Conflict);

        bool ownsTransaction = !_unitOfWork.HasActiveTransaction;
        if (ownsTransaction) await _unitOfWork.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            decimal restored = 0m;
            int periodsReopened = 0;

            foreach (var alloc in forgiveness.Allocations)
            {
                var p = alloc.PaymentPeriod;
                if (p is null) continue; // period purged with the student — nothing to restore

                decimal current = p.ForgivenAmount ?? 0m;
                decimal take = Math.Min(current, alloc.AmountForgiven);
                if (take <= 0m) continue;

                bool wasSettled = p.AmountPaid + current >= p.AmountDue;
                p.ForgivenAmount = current - take;
                restored += take;

                bool nowSettled = p.AmountPaid + (p.ForgivenAmount ?? 0m) >= p.AmountDue;
                if (wasSettled && !nowSettled) periodsReopened++;
                p.PaymentStatus = nowSettled
                    ? PaymentStatus.Paid
                    : (p.AmountPaid > 0m ? PaymentStatus.PartiallyPaid : PaymentStatus.Unpaid);
                await _unitOfWork.PaymentsRepo.UpdatePaymentPeriodAsync(p);
            }

            forgiveness.Status = ForgivenessStatus.Reversed;
            forgiveness.ReversedByUserId = actingUserId;
            forgiveness.ReversedAt = now;
            forgiveness.ReversalNote = Truncate(note, PaymentConstants.EditReasonMaxLength);

            if (forgiveness.TeacherStudentId.HasValue)
                await AdjustCounterForForgivenessAsync(
                    teacherId, forgiveness.TeacherStudentId.Value, restored, periodsReopened);

            await _unitOfWork.SaveChangesAsync();
            if (ownsTransaction) await _unitOfWork.CommitAsync();

            var monthEnd = CurrentMonthEnd(teacherId);
            ForgiveStudentSummaryDto summary;
            if (forgiveness.TeacherStudentId.HasValue)
            {
                var student = await _unitOfWork.Students
                    .GetActiveByIdAndTeacherAsync(forgiveness.TeacherStudentId.Value, teacherId);
                var sessionName = student is not null
                    ? await ResolveStudentSessionNameAsync(teacherId, student.SessionId)
                    : forgiveness.SessionName;
                summary = await BuildForgiveStudentSummaryAsync(
                    teacherId, forgiveness.TeacherStudentId.Value,
                    student?.StudentName ?? forgiveness.StudentName,
                    student?.StudentCode ?? forgiveness.StudentCode,
                    sessionName, monthEnd);
            }
            else
            {
                // Student was purged — no live outstanding to report.
                summary = new ForgiveStudentSummaryDto
                {
                    Id = string.Empty,
                    Name = forgiveness.StudentName,
                    StudentCode = forgiveness.StudentCode,
                    SessionName = forgiveness.SessionName,
                    Outstanding = 0m,
                    MonthsOwed = 0,
                    Status = "paid"
                };
            }

            var byName = await ResolveUserNameAsync(actingUserId);
            var response = new ForgiveBalanceResponse
            {
                Forgiveness = new ForgivenessDto
                {
                    Id = forgiveness.Id.ToString(CultureInfo.InvariantCulture),
                    Amount = forgiveness.Amount,
                    Note = forgiveness.Note,
                    ByName = byName,
                    ByUserId = forgiveness.ForgivenByUserId.ToString(CultureInfo.InvariantCulture),
                    Date = forgiveness.ForgivenAt,
                    Status = "reversed"
                },
                Student = summary
            };
            return Result<ForgiveBalanceResponse>.Success(
                response, _localizer, PaymentConstants.Messages.ForgiveReversedSuccess);
        }
        catch
        {
            if (ownsTransaction) await _unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Keeps the all-time <see cref="StudentPaymentCounter"/> consistent after a forgive/reverse.
    /// <paramref name="outstandingDelta"/> is added to <c>TotalOutstanding</c> (negative on forgive,
    /// positive on reverse); <paramref name="unpaidPeriodsDelta"/> adjusts <c>TotalUnpaidPeriods</c>
    /// (negative when months are settled by a waiver, positive when a reversal re-opens them). A waived
    /// month is NOT counted as PAID (no cash), so <c>TotalPaidPeriods</c> is untouched. The consecutive
    /// streak is recomputed from live period state.
    /// </summary>
    private async Task AdjustCounterForForgivenessAsync(
        long teacherId, long teacherStudentId, decimal outstandingDelta, int unpaidPeriodsDelta)
    {
        var counter = await _unitOfWork.PaymentsRepo.GetPaymentCounterAsync(teacherId, teacherStudentId);
        if (counter is null) return;

        counter.TotalOutstanding += outstandingDelta;
        if (counter.TotalOutstanding < 0m) counter.TotalOutstanding = 0m;

        counter.TotalUnpaidPeriods = Math.Max(0, counter.TotalUnpaidPeriods + unpaidPeriodsDelta);
        counter.ConsecutiveUnpaid = await _unitOfWork.PaymentsRepo
            .RecalculateConsecutiveUnpaidAsync(teacherId, teacherStudentId);

        await _unitOfWork.PaymentsRepo.UpdatePaymentCounterAsync(counter);
    }

    /// <summary>Builds the student payment summary row returned by forgive/reverse — arrears + unpaid
    /// month count through the current teacher-local month (post-change).</summary>
    private async Task<ForgiveStudentSummaryDto> BuildForgiveStudentSummaryAsync(
        long teacherId, long teacherStudentId, string? name, string? code, string? sessionName, DateTime monthEnd)
    {
        decimal outstanding = await _unitOfWork.PaymentsRepo
            .GetOverdueTotalThroughAsync(teacherId, teacherStudentId, null, monthEnd);
        var unpaid = await _unitOfWork.PaymentsRepo
            .GetUnpaidPeriodsThroughAsync(teacherId, teacherStudentId, null, monthEnd);

        return new ForgiveStudentSummaryDto
        {
            Id = teacherStudentId.ToString(CultureInfo.InvariantCulture),
            Name = name,
            StudentCode = code,
            SessionName = sessionName,
            Outstanding = outstanding,
            MonthsOwed = unpaid.Count,
            Status = outstanding > 0m ? "unpaid" : "paid"
        };
    }

    /// <summary>Resolves the display name of the student's current session (null when unassigned/unknown).</summary>
    private async Task<string?> ResolveStudentSessionNameAsync(long teacherId, long? sessionId)
    {
        if (sessionId is null) return null;
        var session = await _unitOfWork.SessionsRepo.GetByIdAndTeacherAsync(sessionId.Value, teacherId);
        return session?.SessionName;
    }

    /// <summary>Resolves one user id → full display name (null when unknown).</summary>
    private async Task<string?> ResolveUserNameAsync(long userId)
    {
        var names = await _unitOfWork.Users.GetUserFullNamesByUserIdsAsync(new List<long> { userId });
        return names.TryGetValue(userId, out var name) ? name : null;
    }

    /// <summary>Trims a note to the column limit (null/blank → null).</summary>
    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }

    /// <summary>Formats a session start time as e.g. "5:00 PM".</summary>
    private static string FormatTime(TimeSpan t) =>
        new DateTime(1, 1, 1).Add(t).ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>
    /// The session name to DISPLAY: the live <c>Session.SessionName</c> when the session still
    /// exists, else the denormalized snapshot stored on the payment row. Payment rows copy the name
    /// at write time, so a session renamed afterwards kept showing its OLD name on the payment
    /// screens (and rows written either side of a rename listed one session under two names). The
    /// snapshot is retained purely so a DELETED session still reads sensibly in the money history.
    /// </summary>
    private static string? ResolveSessionName(string? liveName, string? snapshotName)
    {
        if (!string.IsNullOrEmpty(liveName))
            return liveName;
        return string.IsNullOrEmpty(snapshotName) ? null : snapshotName;
    }

    /// <summary>
    /// Builds the ledger enrichment shared by the collections list and the assistant-wallet screen for
    /// a <see cref="PaymentTransaction"/>: the oldest-first per-month settlement slices (from the
    /// allocation ledger — requires <c>Allocations.PaymentPeriod</c> eager-loaded) so the row can name
    /// the installment(s) it settled, and the amount-edit trail (requires <c>EditLogs</c> eager-loaded)
    /// so an edited collection can render "from X to Y". Returns empties/false when the navigations are
    /// absent (legacy rows / never-edited transactions).
    /// </summary>
    private static (List<CollectionMonthSlice> AppliedMonths, bool IsEdited, decimal? OriginalAmount)
        BuildCollectionLedgerMeta(PaymentTransaction tx)
    {
        var appliedMonths = (tx.Allocations ?? (ICollection<PaymentTransactionAllocation>)System.Array.Empty<PaymentTransactionAllocation>())
            .Where(a => a.PaymentPeriod != null)
            .OrderBy(a => a.PaymentPeriod!.PeriodStart)
            .Select(a => new CollectionMonthSlice
            {
                Month = a.PaymentPeriod!.PeriodStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                MonthLabel = a.PaymentPeriod!.PeriodStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                Amount = a.AmountApplied,
                // Proration is history — surface it per settled month so the collections/wallet ledger
                // shows a prorated month as prorated even after the cash was collected. The PaymentPeriod
                // navigation is already loaded (its PeriodStart is read above), so no extra query.
                IsProRated = a.PaymentPeriod!.IsProRated,
                ProRatedFraction = a.PaymentPeriod!.IsProRated ? a.PaymentPeriod!.ProRatedFraction : (decimal?)null
            })
            .ToList();

        // The originally-collected amount = the PreviousAmount of the FIRST amount-edit (the current
        // AmountPaid already reflects every edit). Only genuine amount changes count — a Reversed/Deleted
        // (full refund) is surfaced as its own refund line, not an "edit".
        var firstAmountEdit = (tx.EditLogs ?? (ICollection<PaymentEditLog>)System.Array.Empty<PaymentEditLog>())
            .Where(l => l.EditAction == PaymentEditAction.AmountChanged)
            .OrderBy(l => l.EditedAt).ThenBy(l => l.Id)
            .FirstOrDefault();

        return (appliedMonths, firstAmountEdit != null, firstAmountEdit?.PreviousAmount);
    }

    /// <summary>
    /// Resolves the "YYYY-MM" month selector, DEFAULTING to the teacher's current local
    /// (Africa/Cairo) month when it is omitted (PAY-2) — matching the documented "screens with no
    /// explicit month use the teacher's current local month" contract (§7.4). Returns false ONLY
    /// when a value is provided but malformed/out of range.
    /// </summary>
    private bool TryResolveMonth(long teacherId, string? month, out int year, out int mon)
    {
        if (string.IsNullOrWhiteSpace(month))
        {
            var today = _timeZoneService.GetTeacherLocalDate(teacherId);
            year = today.Year;
            mon = today.Month;
            return true;
        }
        return TryParseYearMonth(month, out year, out mon);
    }

    /// <summary>
    /// DASH-1: unifies the month selector for the collections screens (<c>collections</c> and
    /// <c>collections/yearly</c>) so the SAME value that drives <c>tracking</c>/<c>students</c> works
    /// here too. Resolution order:
    /// <list type="number">
    ///   <item>A unified <c>"YYYY-MM"</c> <paramref name="month"/> (reuses <see cref="TryParseYearMonth"/>)
    ///         is self-contained — it supplies BOTH year and month and takes precedence over a
    ///         separate <paramref name="year"/>; malformed → <c>PaymentInvalidMonthFormat</c>.</item>
    ///   <item>Otherwise the LEGACY form: a separate <paramref name="year"/> (validated when supplied
    ///         → <c>PaymentInvalidYear</c>) and an integer <paramref name="month"/> string 1-12
    ///         (validated when supplied → <c>PaymentInvalidMonthInteger</c>).</item>
    ///   <item>Anything omitted defaults to the teacher's current local (Africa/Cairo) month/year.</item>
    /// </list>
    /// Returns <c>null</c> on success (with <paramref name="resolvedYear"/>/<paramref name="resolvedMonth"/>
    /// set); otherwise the stable localization key to fail the request with (HTTP 422).
    /// </summary>
    private string? ResolveCollectionsMonthYear(
        long teacherId, string? month, int? year, out int resolvedYear, out int resolvedMonth)
    {
        var today = _timeZoneService.GetTeacherLocalDate(teacherId);
        resolvedYear = today.Year;
        resolvedMonth = today.Month;

        // (1) Unified "YYYY-MM" selector — self-contained, wins over a separate ?year=.
        if (!string.IsNullOrWhiteSpace(month) && month.Contains('-'))
        {
            if (!TryParseYearMonth(month, out resolvedYear, out resolvedMonth))
                return PaymentConstants.Messages.PaymentInvalidMonthFormat;
            return null;
        }

        // (2a) Legacy ?year=<int> — validate only when supplied.
        if (year.HasValue)
        {
            if (year.Value < 2000 || year.Value > 2100)
                return PaymentConstants.Messages.PaymentInvalidYear;
            resolvedYear = year.Value;
        }

        // (2b) Legacy ?month=<int 1-12> — validate only when supplied.
        if (!string.IsNullOrWhiteSpace(month))
        {
            if (!int.TryParse(month.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out resolvedMonth)
                || resolvedMonth < 1 || resolvedMonth > 12)
                return PaymentConstants.Messages.PaymentInvalidMonthInteger;
        }

        return null;
    }

    /// <summary>
    /// DASH-1: resolves ONLY the year for the yearly-collections view. A unified <c>"YYYY-MM"</c>
    /// <paramref name="month"/> contributes just its YEAR component — its month is NOT validated
    /// (a year-only screen must never 422 on a malformed month); otherwise the legacy
    /// <c>?year=&lt;int&gt;</c> is used (a legacy integer <c>?month=</c> is ignored); otherwise the
    /// teacher's current local (Africa/Cairo) year. Returns <c>null</c> on success (with
    /// <paramref name="resolvedYear"/> set), else the stable localization key to fail with (HTTP 422).
    /// </summary>
    private string? ResolveCollectionsYear(long teacherId, string? month, int? year, out int resolvedYear)
    {
        resolvedYear = _timeZoneService.GetTeacherLocalDate(teacherId).Year;

        // (1) Unified "YYYY-MM" — take only its YEAR; the month component is ignored (not validated).
        if (!string.IsNullOrWhiteSpace(month) && month.Contains('-'))
        {
            var yearPart = month.Trim().Split('-')[0];
            if (!int.TryParse(yearPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out resolvedYear)
                || resolvedYear < 2000 || resolvedYear > 2100)
                return PaymentConstants.Messages.PaymentInvalidMonthFormat;
            return null;
        }

        // (2) Legacy ?year=<int> — validated only when supplied. A legacy integer ?month= is
        // irrelevant to a yearly view and is intentionally ignored.
        if (year.HasValue)
        {
            if (year.Value < 2000 || year.Value > 2100)
                return PaymentConstants.Messages.PaymentInvalidYear;
            resolvedYear = year.Value;
        }

        return null;
    }

    /// <inheritdoc />
    public Task<Result<ProrationUpdateResultDto>> SetProrationAmountAsync(
        long teacherId, long actingUserId, long teacherStudentId, decimal? amount) =>
        // Pure delegation — all money/reprice/audit logic lives in the single PaymentService owner.
        _paymentService.SetStudentProrationAmountAsync(teacherId, actingUserId, teacherStudentId, amount);

    /// <summary>Parses a "YYYY-MM" month selector; false when malformed or out of range.</summary>
    private static bool TryParseYearMonth(string? value, out int year, out int month)
    {
        year = 0; month = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split('-');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out year)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out month)) return false;
        return year >= 2000 && year <= 2100 && month >= 1 && month <= 12;
    }

    /// <summary>Clamps paging to sane bounds (page ≥ 1; 1 ≤ limit ≤ 100, default 20).</summary>
    private static (int page, int limit) NormalizePaging(int page, int limit)
    {
        if (page < 1) page = 1;
        if (limit < 1) limit = 20;
        else if (limit > 100) limit = 100;
        return (page, limit);
    }
}
