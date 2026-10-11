using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskInactivityControlCycleState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InactivityControlCycleNumber",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InactivityControlNextCheckAt",
                table: "TaskJournalItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_InactivityControlNextCheckAt",
                table: "TaskJournalItems",
                column: "InactivityControlNextCheckAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_InactivityControlNextCheckAt",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "InactivityControlCycleNumber",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "InactivityControlNextCheckAt",
                table: "TaskJournalItems");
        }
    }
}
