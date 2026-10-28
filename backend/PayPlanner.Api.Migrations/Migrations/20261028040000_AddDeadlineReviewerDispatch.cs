using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class AddDeadlineReviewerDispatch : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "ReviewerDispatchAttempts", table: "TaskJournalDeadlineChangeRequests", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>(name: "ReviewerDispatchLeaseToken", table: "TaskJournalDeadlineChangeRequests", type: "TEXT", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "ReviewerDispatchLeaseUntil", table: "TaskJournalDeadlineChangeRequests", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "ReviewerDispatchPending", table: "TaskJournalDeadlineChangeRequests", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.CreateIndex(name: "IX_TaskDeadlineChangeRequests_ReviewerDispatch", table: "TaskJournalDeadlineChangeRequests", columns: new[] { "ReviewerDispatchPending", "ReviewerDispatchLeaseUntil", "Id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_TaskDeadlineChangeRequests_ReviewerDispatch", table: "TaskJournalDeadlineChangeRequests");
            migrationBuilder.DropColumn(name: "ReviewerDispatchAttempts", table: "TaskJournalDeadlineChangeRequests");
            migrationBuilder.DropColumn(name: "ReviewerDispatchLeaseToken", table: "TaskJournalDeadlineChangeRequests");
            migrationBuilder.DropColumn(name: "ReviewerDispatchLeaseUntil", table: "TaskJournalDeadlineChangeRequests");
            migrationBuilder.DropColumn(name: "ReviewerDispatchPending", table: "TaskJournalDeadlineChangeRequests");
        }
    }
}
