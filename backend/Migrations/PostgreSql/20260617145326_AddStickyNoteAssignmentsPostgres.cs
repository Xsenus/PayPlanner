using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddStickyNoteAssignmentsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssignedUserId",
                table: "StickyNotes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedUserName",
                table: "StickyNotes",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderAt",
                table: "StickyNotes",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderSentAt",
                table: "StickyNotes",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StickyNotes_AssignedUserId",
                table: "StickyNotes",
                column: "AssignedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNotes_ReminderAt_ReminderSentAt",
                table: "StickyNotes",
                columns: new[] { "ReminderAt", "ReminderSentAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_StickyNotes_Users_AssignedUserId",
                table: "StickyNotes",
                column: "AssignedUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StickyNotes_Users_AssignedUserId",
                table: "StickyNotes");

            migrationBuilder.DropIndex(
                name: "IX_StickyNotes_AssignedUserId",
                table: "StickyNotes");

            migrationBuilder.DropIndex(
                name: "IX_StickyNotes_ReminderAt_ReminderSentAt",
                table: "StickyNotes");

            migrationBuilder.DropColumn(
                name: "AssignedUserId",
                table: "StickyNotes");

            migrationBuilder.DropColumn(
                name: "AssignedUserName",
                table: "StickyNotes");

            migrationBuilder.DropColumn(
                name: "ReminderAt",
                table: "StickyNotes");

            migrationBuilder.DropColumn(
                name: "ReminderSentAt",
                table: "StickyNotes");
        }
    }
}
