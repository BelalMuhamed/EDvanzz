using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Edvanz.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDepartureStoryAndParentPortalName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnchorPeriodStart",
                table: "StudentDepartures",
                type: "datetime2(0)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PaidAmountAtDeparture",
                table: "StudentDepartures",
                type: "decimal(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentName",
                table: "ParentPortalAccesses",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            // ── Backfill the departure story onto rows recorded before these columns existed. ──
            //
            // Wrapped in EXEC so name resolution is DEFERRED to run time. EF emits a migration's
            // operations as one GO-less batch, so a bare Sql() UPDATE naming the columns added just
            // above is bound at batch-compile time, when they do not yet exist — that is exactly the
            // "Invalid column name" failure that shipped a broken migration once already (BUG-10).
            //
            // Both figures are reconstructed from frozen, non-nullable columns, never recomputed
            // from live data:
            //   • AnchorPeriodStart — RefundPeriodStart already holds the anchored month, but only
            //     on refunds that actually paid out. Nothing else can be recovered, so other rows
            //     stay NULL and the client simply omits the month line.
            //   • PaidAmountAtDeparture — for a RefundDue row the calculated refund is by definition
            //     paid − prorated (that branch never clamps; a clamp to zero is recorded as
            //     NoObligation instead), so paid = OriginalCalculatedAmount + ProRatedAmount, and
            //     that holds whether or not a tutor later overrode the settled figure. An AmountOwed
            //     row is only reachable when nothing was paid, so it is exactly 0. NoObligation is
            //     genuinely ambiguous — nothing-paid-nothing-attended and paid-but-fully-consumed
            //     both land there — so it is left NULL rather than guessed.
            migrationBuilder.Sql(@"
EXEC(N'
UPDATE [StudentDepartures]
   SET [AnchorPeriodStart] = [RefundPeriodStart]
 WHERE [AnchorPeriodStart] IS NULL
   AND [RefundPeriodStart] IS NOT NULL;

UPDATE [StudentDepartures]
   SET [PaidAmountAtDeparture] = [OriginalCalculatedAmount] + [ProRatedAmount]
 WHERE [PaidAmountAtDeparture] IS NULL
   AND [DepartureOutcome] = 1;

UPDATE [StudentDepartures]
   SET [PaidAmountAtDeparture] = 0
 WHERE [PaidAmountAtDeparture] IS NULL
   AND [DepartureOutcome] = 2;
');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnchorPeriodStart",
                table: "StudentDepartures");

            migrationBuilder.DropColumn(
                name: "PaidAmountAtDeparture",
                table: "StudentDepartures");

            // The backfill needs no inverse — Down drops the very columns it wrote.
            migrationBuilder.DropColumn(
                name: "ParentName",
                table: "ParentPortalAccesses");
        }
    }
}
