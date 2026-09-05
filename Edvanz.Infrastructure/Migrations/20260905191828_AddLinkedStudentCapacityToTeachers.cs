using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Edvanz.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkedStudentCapacityToTeachers : Migration
    {
        /// <summary>
        /// Adds the per-teacher STUDENT-APP-ACCOUNT limit (Teacher.LinkedStudentCapacity — the
        /// limit the subscription price is now computed from), the matching number on
        /// SubscriptionRequests, and CapacityIncreaseRequests.CapacityKind so the one
        /// capacity-request queue can serve BOTH limits.
        ///
        /// PRICE-NEUTRAL BY CONSTRUCTION: every existing teacher is backfilled to
        /// LinkedStudentCapacity = StudentCapacity, so no bill changes on deploy day.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CapacityIncreaseRequests_Teacher_Pending",
                table: "CapacityIncreaseRequests");

            migrationBuilder.AddColumn<int>(
                name: "LinkedStudentCapacity",
                table: "Teachers",
                type: "int",
                nullable: false,
                defaultValue: 500);

            // Backfill: existing teachers keep their exact current pricing basis.
            // BUG-10 — a migration's operations are emitted as ONE batch, so a BARE
            // Sql("UPDATE [Teachers] SET [LinkedStudentCapacity] = ...") would be name-resolved at
            // batch-compile time, BEFORE the column above exists, and fail with error 207. Wrapping
            // it in EXEC(N'...') defers name resolution to run time, after the ALTER has applied.
            migrationBuilder.Sql(
                "EXEC(N'UPDATE [Teachers] SET [LinkedStudentCapacity] = [StudentCapacity]')");

            migrationBuilder.AddColumn<int>(
                name: "RequestedLinkedStudents",
                table: "SubscriptionRequests",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // 1 = CapacityKind.AccountStudents — every row written before this column existed
            // targeted Teacher.StudentCapacity, so the default preserves their meaning. Applied by
            // the ALTER itself (not a follow-up UPDATE), so the rows are already correct when the
            // filtered unique index below is created.
            migrationBuilder.AddColumn<int>(
                name: "CapacityKind",
                table: "CapacityIncreaseRequests",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "UX_CapacityIncreaseRequests_Teacher_Kind_Pending",
                table: "CapacityIncreaseRequests",
                columns: new[] { "TeacherId", "CapacityKind" },
                unique: true,
                filter: "[Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CapacityIncreaseRequests_Teacher_Kind_Pending",
                table: "CapacityIncreaseRequests");

            migrationBuilder.DropColumn(
                name: "LinkedStudentCapacity",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "RequestedLinkedStudents",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "CapacityKind",
                table: "CapacityIncreaseRequests");

            migrationBuilder.CreateIndex(
                name: "UX_CapacityIncreaseRequests_Teacher_Pending",
                table: "CapacityIncreaseRequests",
                column: "TeacherId",
                unique: true,
                filter: "[Status] = 1");
        }
    }
}
