using System;
using System.Collections.Generic;

namespace Edvanz.Application.Extensions;

/// <summary>
/// Shared money-amount rules for the payment module. Single-sourced so the "does this amount need a
/// note?" decision is IDENTICAL across the collect (Feature C) and edit (Feature B) flows.
/// </summary>
public static class PaymentAmountRules
{
    /// <summary>
    /// True when <paramref name="amount"/> is a WHOLE-MONTH multiple of <paramref name="monthlyRate"/>
    /// — i.e. <c>amount == N × monthlyRate</c> for some integer N ≥ 1. Such an amount is a plain
    /// "pay N months" and needs no explanatory note. Anything else — a partial/custom amount, 0, or an
    /// unknown/zero monthly rate — returns false (a note is required). Uses a small epsilon so decimal
    /// rounding (e.g. 300.00 / 100.00) does not misclassify an exact multiple.
    /// </summary>
    public static bool IsWholeMonthMultiple(decimal amount, decimal monthlyRate)
    {
        if (monthlyRate <= 0m || amount <= 0m) return false;
        decimal ratio = amount / monthlyRate;
        decimal rounded = Math.Round(ratio);
        return rounded >= 1m && Math.Abs(ratio - rounded) < 0.0001m;
    }

    /// <summary>
    /// True when <paramref name="amount"/> exactly settles a WHOLE NUMBER of the owed months, i.e. it
    /// equals the cumulative remaining of the first N unpaid periods (oldest first) for some N ≥ 1.
    /// Such an amount clears those months exactly and leaves no stray remainder, so — like
    /// <see cref="IsWholeMonthMultiple"/> — it needs no explanatory note.
    /// <para>
    /// This exists because <see cref="IsWholeMonthMultiple"/> alone assumes every owed month costs the
    /// same. It does not: an enrollment-anchored PRORATED joining month is deliberately cheaper than
    /// the monthly rate (§7.4), so collecting exactly what a prorated student owes — 100 against a 300
    /// rate — was rejected as a "partial/custom" amount and 400'd with <c>CollectNoteRequired</c>.
    /// A carried-forward transfer balance and a partly-paid month behave the same way. The rule is
    /// strictly WIDENING: every amount that satisfied the multiple rule still passes it first.
    /// </para>
    /// <param name="orderedRemainings">Per-period remaining due (<c>AmountDue − AmountPaid − Forgiven</c>),
    /// oldest first, matching the order the collect engine fills them in. Non-positive entries are
    /// skipped, exactly as the engine skips them.</param>
    /// </summary>
    public static bool SettlesWholeOwedMonths(decimal amount, IReadOnlyList<decimal>? orderedRemainings)
    {
        if (amount <= 0m || orderedRemainings is null || orderedRemainings.Count == 0) return false;

        decimal cumulative = 0m;
        foreach (var remaining in orderedRemainings)
        {
            if (remaining <= 0m) continue;          // already settled — the engine skips it too
            cumulative += remaining;
            // Same epsilon as IsWholeMonthMultiple so decimal scale (100 vs 100.00) never misclassifies.
            if (Math.Abs(amount - cumulative) < 0.0001m) return true;
            if (cumulative > amount) return false;  // overshot — no longer prefix; ordering is ascending
        }
        return false;
    }
}
