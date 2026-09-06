using System.Text.Json.Serialization;
using Edvanz.Application.Json;
using Edvanz.Domain.Enums;

namespace Edvanz.Application.Dtos.Payment;

// ══════════════════════════════════════════════════════════════════════════
// STUDENT / PARENT PAYMENT TRACKING SCREEN DTOs
//
// One screen = one call. GetStudentPaymentTrackingAsync returns the whole screen:
// a header, the single Upcoming month, the Paid section, and the Overdue section.
// Gated by TeacherConfiguration.StudentVisibilityPayment / ParentVisibilityPayment.
//
// FRONTEND MAPPING CONTRACT:
// - Every money value is named EXPLICITLY (AmountDue / AmountPaid / OutstandingAmount)
//   so a field never has to be interpreted by which section it sits in:
//     • Paid card     → render AmountPaid   (prefix "+")
//     • Overdue card  → render OutstandingAmount (prefix "-")
//     • Upcoming card → render AmountDue     (prefix "+")
// - All three sections use the SAME row shape (StudentPaymentPeriodDto) so the client
//   can bind one "period card" component everywhere.
// - Amounts are POSITIVE magnitudes; the sign is a presentation concern of the section.
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Full payload for the student/parent "Payment" tracking screen.
/// </summary>
public class StudentPaymentTrackingDto
{
    /// <summary>The teacher whose payments these are (echoes the route teacherId).</summary>
    public long TeacherId { get; set; }

    /// <summary>Header session — the student's most-recent session-linked period; null if none.</summary>
    public long? CurrentSessionId { get; set; }

    /// <summary>Display name of <see cref="CurrentSessionId"/>; null if none.</summary>
    public string? CurrentSessionName { get; set; }

    /// <summary>
    /// Paid ÷ (Paid + Overdue), rounded to 4 dp, range 0..1. Drives the green (paid) and
    /// red (overdue) progress bars — the red bar can render as (1 − PaidProgressRatio).
    /// Equals 1 when nothing is owed.
    /// </summary>
    public decimal PaidProgressRatio { get; set; }

    /// <summary>
    /// The single nearest FUTURE unpaid month (the "Upcoming" row). Null when no future
    /// obligation is scheduled. Read <see cref="StudentPaymentPeriodDto.AmountDue"/> for the
    /// expected amount.
    /// </summary>
    public StudentPaymentPeriodDto? UpcomingPayment { get; set; }

    /// <summary>Fully-settled periods, newest first.</summary>
    public PaymentSectionDto PaidSection { get; set; } = new();

    /// <summary>Periods due through the current month that are still owed, oldest first.</summary>
    public PaymentSectionDto OverdueSection { get; set; } = new();
}

/// <summary>
/// One collapsible section on the screen (Paid or Overdue): its header total, the number of
/// period rows, and the rows themselves.
/// </summary>
public class PaymentSectionDto
{
    /// <summary>
    /// Sum of the section's amounts. Paid → sum of <see cref="StudentPaymentPeriodDto.AmountPaid"/>;
    /// Overdue → sum of <see cref="StudentPaymentPeriodDto.OutstandingAmount"/>. Always positive.
    /// </summary>
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Number of period rows in the section. For monthly billing this reads as "N months";
    /// for per-session billing it reads as "N lessons".
    /// </summary>
    public int PeriodCount { get; set; }

    /// <summary>The period rows (see ordering on the parent section's XML doc).</summary>
    public List<StudentPaymentPeriodDto> Periods { get; set; } = new();
}

/// <summary>
/// One period "card": a month for monthly billing, or an occurrence for per-session billing.
/// Used uniformly by the Upcoming, Paid, and Overdue sections.
/// </summary>
public class StudentPaymentPeriodDto
{
    /// <summary>Identifier of the underlying PaymentPeriod.</summary>
    public long PeriodId { get; set; }

    /// <summary>
    /// First day of the period. The frontend formats it: "MMM yyyy" for monthly billing,
    /// "dd MMM yyyy" for per-session.
    /// </summary>
    public DateTime PeriodStartDate { get; set; }

    /// <summary>Denormalized session name for the period.</summary>
    public string SessionName { get; set; } = null!;

    /// <summary>Monthly or PerSession — tells the frontend how to format the date/label.</summary>
    public PeriodType PeriodType { get; set; }

    /// <summary>Per-period status: Unpaid / PartiallyPaid / Paid / Overpaid.</summary>
    public PaymentStatus Status { get; set; }

    /// <summary>Full amount billed for the period. Render this on the Upcoming card.</summary>
    public decimal AmountDue { get; set; }

    /// <summary>Amount collected so far for the period. Render this on a Paid card.</summary>
    public decimal AmountPaid { get; set; }

    /// <summary>AmountDue − AmountPaid. Render this on an Overdue card (as a "-" amount).</summary>
    public decimal OutstandingAmount { get; set; }

    /// <summary>
    /// Settlement date (latest non-deleted transaction, teacher-local) for a Paid card;
    /// null on Upcoming/Overdue cards. Backs "Paid on: &lt;date&gt;".
    /// </summary>
    [JsonConverter(typeof(NullableLocalWallClockDateTimeJsonConverter))]
    public DateTime? PaidOnDate { get; set; }

    /// <summary>
    /// Whole months this period is past due (0 = current month). Set on Overdue cards; null
    /// otherwise. Backs "Overdue: N month".
    /// </summary>
    public int? MonthsOverdue { get; set; }

    /// <summary>True when this is the prorated first month.</summary>
    public bool IsProRated { get; set; }

    /// <summary>True when this period's balance was carried in from another session (transfer).</summary>
    public bool IsCarriedForward { get; set; }

    /// <summary>
    /// Id of the session this obligation was moved FROM on a student reassignment (A → B);
    /// null for a normal (non-moved) period. Pairs with <see cref="MovedFromSessionName"/>.
    /// </summary>
    public long? MovedFromSessionId { get; set; }

    /// <summary>Display snapshot of the session this obligation was moved FROM; null if not moved.</summary>
    public string? MovedFromSessionName { get; set; }
}
