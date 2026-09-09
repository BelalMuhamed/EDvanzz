using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Edvanz.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExamStatusAuditAndAssistantRemovedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "PreviousStatus",
                table: "StudentOnlineExamReports",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAt",
                table: "StudentOnlineExamReports",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StatusChangedByUserId",
                table: "StudentOnlineExamReports",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RemovedAt",
                table: "CenterAssistants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RemovedAt",
                table: "Assistants",
                type: "datetime2",
                nullable: true);

            // ── Backfill RemovedAt from DeletedAt ────────────────────────────────────────────────
            // EXEC(N'...') is REQUIRED: RemovedAt is added by THIS migration, and EF emits a
            // migration's operations as one GO-less batch, so a bare Sql() UPDATE would be bound at
            // batch-compile time - before the column exists - and fail with "Invalid column name"
            // (the documented BUG-10 outage). EXEC defers name resolution to run time.
            //
            // WHY COPY RATHER THAN DISCRIMINATE: suspend and delete BOTH wrote DeletedAt, and nothing
            // stored on the row separates them (suspend wrote UtcNow+2min, delete wrote UtcNow, and
            // neither touched UpdatedAt). Copying across makes EXISTING rows behave exactly as they do
            // today - no genuine removal is ever lost - and only NEW suspensions get the corrected
            // behaviour. An already-suspended assistant who is currently mislabelled is fixed
            // operationally by reactivating and re-suspending; reactivation clears RemovedAt.
            migrationBuilder.Sql(@"
EXEC(N'
UPDATE [Assistants]       SET [RemovedAt] = [DeletedAt] WHERE [DeletedAt] IS NOT NULL AND [RemovedAt] IS NULL;
UPDATE [CenterAssistants] SET [RemovedAt] = [DeletedAt] WHERE [DeletedAt] IS NOT NULL AND [RemovedAt] IS NULL;
');");

            // ── Item 8: clear the fabricated ""Paid X for {month}"" on pre-2026-08-02 departures ──
            // Migration 20260905231420 backfilled PaidAmountAtDeparture as
            // OriginalCalculatedAmount + ProRatedAmount for every RefundDue row, justified as
            // "a RefundDue row's refund is by definition paid - prorated". That identity only became
            // true in commit 42b2106 (2026-08-02 16:12:42 UTC). Before it, the proration-ENABLED
            // branch computed full - prorated, so the backfill stored the session's PRICE LIST amount
            // and the departure card now states it as money the student paid, on a permanent record a
            // parent can be shown. NULL renders as "not recorded", which is honest.
            //
            // ProRatedAmount > 0 is the only reliable discriminator: the proration-DISABLED branch
            // forced it to 0 and its backfilled value IS the real cash, so those rows are left alone.
            // Pre-cutoff rows with ProRatedAmount = 0 are a known, accepted residue - nothing stored
            // distinguishes "correct" from "wrong" there, and clearing them would destroy good data.
            //
            // No EXEC needed: every column here already exists in production (added by 20260905231420).
            // Idempotent - cleared rows are NULL and can never match again. The equality guard means a
            // figure a human has since corrected is never clobbered.
            migrationBuilder.Sql(@"
UPDATE [StudentDepartures]
   SET [PaidAmountAtDeparture] = NULL
 WHERE [DepartureOutcome] = 1
   AND [DepartedAt] < '2026-08-02T16:12:42'
   AND [ProRatedAmount] > 0
   AND [PaidAmountAtDeparture] IS NOT NULL
   AND [PaidAmountAtDeparture] = [OriginalCalculatedAmount] + [ProRatedAmount];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousStatus",
                table: "StudentOnlineExamReports");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "StudentOnlineExamReports");

            migrationBuilder.DropColumn(
                name: "StatusChangedByUserId",
                table: "StudentOnlineExamReports");

            migrationBuilder.DropColumn(
                name: "RemovedAt",
                table: "CenterAssistants");

            migrationBuilder.DropColumn(
                name: "RemovedAt",
                table: "Assistants");

            // The two data corrections above are NOT reversed: the cleared PaidAmountAtDeparture
            // values were fabricated by a bad backfill and restoring them would re-introduce the very
            // figures this migration exists to remove. RemovedAt is dropped with its column.
        }
    }
}
