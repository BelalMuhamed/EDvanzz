using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Edvanz.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcquisitionSource",
                table: "Teachers",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SalesRepId",
                table: "Teachers",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AdminAccessLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AdminUserId = table.Column<long>(type: "bigint", nullable: false),
                    AdminName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TeacherId = table.Column<long>(type: "bigint", nullable: false),
                    Route = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CreateAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAccessLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminNotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TeacherId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorUserId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    FollowUpDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreateAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminNotes_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SalesReps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreateAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeacherUsageDays",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TeacherId = table.Column<long>(type: "bigint", nullable: false),
                    ActivityDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StudentWrites = table.Column<int>(type: "int", nullable: false),
                    SessionWrites = table.Column<int>(type: "int", nullable: false),
                    AttendanceWrites = table.Column<int>(type: "int", nullable: false),
                    PaymentWrites = table.Column<int>(type: "int", nullable: false),
                    VideoWrites = table.Column<int>(type: "int", nullable: false),
                    OnlineExamWrites = table.Column<int>(type: "int", nullable: false),
                    ExamHomeworkWrites = table.Column<int>(type: "int", nullable: false),
                    MessagingWrites = table.Column<int>(type: "int", nullable: false),
                    ParentPortalWrites = table.Column<int>(type: "int", nullable: false),
                    ModulesMask = table.Column<int>(type: "int", nullable: false),
                    TotalWrites = table.Column<int>(type: "int", nullable: false),
                    TeacherWrites = table.Column<int>(type: "int", nullable: false),
                    AssistantWrites = table.Column<int>(type: "int", nullable: false),
                    UnattributedWrites = table.Column<int>(type: "int", nullable: false),
                    CreateAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeacherUsageDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeacherUsageDays_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TeacherUsageSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TeacherId = table.Column<long>(type: "bigint", nullable: false),
                    ActiveDays7 = table.Column<int>(type: "int", nullable: false),
                    ActiveDays30 = table.Column<int>(type: "int", nullable: false),
                    ActiveDays90 = table.Column<int>(type: "int", nullable: false),
                    TotalWrites30 = table.Column<int>(type: "int", nullable: false),
                    Cadence = table.Column<byte>(type: "tinyint", nullable: false),
                    ModulesUsedMask = table.Column<int>(type: "int", nullable: false),
                    ModulesUsedAllTimeMask = table.Column<int>(type: "int", nullable: false),
                    Depth = table.Column<byte>(type: "tinyint", nullable: false),
                    Operators = table.Column<byte>(type: "tinyint", nullable: false),
                    LastTeacherActivityAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastAssistantActivityAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActiveAssistantCount = table.Column<int>(type: "int", nullable: false),
                    FirstActivityAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StudentCount = table.Column<int>(type: "int", nullable: false),
                    StudentsAssignedToSession = table.Column<int>(type: "int", nullable: false),
                    SessionCount = table.Column<int>(type: "int", nullable: false),
                    SessionsWithOccurrences = table.Column<int>(type: "int", nullable: false),
                    LinkedAccountCount = table.Column<int>(type: "int", nullable: false),
                    BoundAccountCount = table.Column<int>(type: "int", nullable: false),
                    HasEverMarkedAttendance = table.Column<bool>(type: "bit", nullable: false),
                    HasEverCollectedPayment = table.Column<bool>(type: "bit", nullable: false),
                    HasRealData = table.Column<bool>(type: "bit", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreateAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeacherUsageSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeacherUsageSnapshots_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Teachers_SalesRepId",
                table: "Teachers",
                column: "SalesRepId",
                filter: "[SalesRepId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccessLogs_AdminUserId_CreateAt",
                table: "AdminAccessLogs",
                columns: new[] { "AdminUserId", "CreateAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccessLogs_TeacherId_CreateAt",
                table: "AdminAccessLogs",
                columns: new[] { "TeacherId", "CreateAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminNotes_FollowUpDate",
                table: "AdminNotes",
                column: "FollowUpDate",
                filter: "[FollowUpDate] IS NOT NULL AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_AdminNotes_TeacherId_IsPinned_CreateAt",
                table: "AdminNotes",
                columns: new[] { "TeacherId", "IsPinned", "CreateAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReps_IsActive_Name",
                table: "SalesReps",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_TeacherUsageDays_ActivityDate",
                table: "TeacherUsageDays",
                column: "ActivityDate");

            migrationBuilder.CreateIndex(
                name: "UX_TeacherUsageDays_TeacherId_ActivityDate",
                table: "TeacherUsageDays",
                columns: new[] { "TeacherId", "ActivityDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeacherUsageSnapshots_Cadence_LastActivityAt",
                table: "TeacherUsageSnapshots",
                columns: new[] { "Cadence", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TeacherUsageSnapshots_HasRealData_Cadence",
                table: "TeacherUsageSnapshots",
                columns: new[] { "HasRealData", "Cadence" });

            migrationBuilder.CreateIndex(
                name: "IX_TeacherUsageSnapshots_Operators_LastActivityAt",
                table: "TeacherUsageSnapshots",
                columns: new[] { "Operators", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "UX_TeacherUsageSnapshots_TeacherId",
                table: "TeacherUsageSnapshots",
                column: "TeacherId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Teachers_SalesReps_SalesRepId",
                table: "Teachers",
                column: "SalesRepId",
                principalTable: "SalesReps",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Teachers_SalesReps_SalesRepId",
                table: "Teachers");

            migrationBuilder.DropTable(
                name: "AdminAccessLogs");

            migrationBuilder.DropTable(
                name: "AdminNotes");

            migrationBuilder.DropTable(
                name: "SalesReps");

            migrationBuilder.DropTable(
                name: "TeacherUsageDays");

            migrationBuilder.DropTable(
                name: "TeacherUsageSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Teachers_SalesRepId",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "AcquisitionSource",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "SalesRepId",
                table: "Teachers");
        }
    }
}
