using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddTaskInactivityControlCycleStatePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InactivityControlCycleNumber",
                table: "TaskJournalItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InactivityControlNextCheckAt",
                table: "TaskJournalItems",
                type: "timestamp without time zone",
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
