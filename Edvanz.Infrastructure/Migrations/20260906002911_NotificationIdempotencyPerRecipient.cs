using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Edvanz.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NotificationIdempotencyPerRecipient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_UserNotifications_SourceType_SourceEntityId",
                table: "UserNotifications");

            migrationBuilder.CreateIndex(
                name: "UX_UserNotifications_User_SourceType_SourceEntityId",
                table: "UserNotifications",
                columns: new[] { "UserId", "SourceType", "SourceEntityId" },
                unique: true,
                filter: "[SourceEntityId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_UserNotifications_User_SourceType_SourceEntityId",
                table: "UserNotifications");

            migrationBuilder.CreateIndex(
                name: "UX_UserNotifications_SourceType_SourceEntityId",
                table: "UserNotifications",
                columns: new[] { "SourceType", "SourceEntityId" },
                unique: true,
                filter: "[SourceEntityId] IS NOT NULL");
        }
    }
}
