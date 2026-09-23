using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCaseAppealDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseAppealDecisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    CourtDecisionDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppealDeadlineDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Choice = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    TaskJournalItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    DecidedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DecidedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseAppealDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseAppealDecisions_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientCaseAppealDecisions_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseAppealDecisions_ClientCaseId_CourtDecisionDate",
                table: "ClientCaseAppealDecisions",
                columns: new[] { "ClientCaseId", "CourtDecisionDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseAppealDecisions_TaskJournalItemId",
                table: "ClientCaseAppealDecisions",
                column: "TaskJournalItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientCaseAppealDecisions");
        }
    }
}
