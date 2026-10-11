using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddInternalChatNoReplyReminderPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextNoReplyReminderAt",
                table: "InternalChatMessages",
                type: "timestamp without time zone",
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
