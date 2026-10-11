using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInternalChatNotificationRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastNotificationDispatchAttemptAt",
                table: "InternalChatMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotificationDispatchPending",
                table: "InternalChatMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMessages_NotificationPending_Attempt_Id",
                table: "InternalChatMessages",
                columns: new[] { "NotificationDispatchPending", "LastNotificationDispatchAttemptAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InternalChatMessages_NotificationPending_Attempt_Id",
                table: "InternalChatMessages");

            migrationBuilder.DropColumn(
                name: "LastNotificationDispatchAttemptAt",
                table: "InternalChatMessages");

            migrationBuilder.DropColumn(
                name: "NotificationDispatchPending",
                table: "InternalChatMessages");
        }
    }
}
