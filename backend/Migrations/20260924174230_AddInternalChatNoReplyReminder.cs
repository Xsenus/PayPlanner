using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInternalChatNoReplyReminder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextNoReplyReminderAt",
                table: "InternalChatMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMessages_NextNoReplyReminderAt",
                table: "InternalChatMessages",
                column: "NextNoReplyReminderAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InternalChatMessages_NextNoReplyReminderAt",
                table: "InternalChatMessages");

            migrationBuilder.DropColumn(
                name: "NextNoReplyReminderAt",
                table: "InternalChatMessages");
        }
    }
}
