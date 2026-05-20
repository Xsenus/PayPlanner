using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskApprovalDueAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalDueAt",
                table: "TaskJournalItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_ApprovalDueAt",
                table: "TaskJournalItems",
                column: "ApprovalDueAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_ApprovalDueAt",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ApprovalDueAt",
                table: "TaskJournalItems");
        }
    }
}
