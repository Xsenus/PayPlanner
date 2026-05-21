using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    [Migration("20260521093000_AddClientCaseProceduralDeadlineEntities")]
    [DbContext(typeof(PaymentContext))]
    public partial class AddClientCaseProceduralDeadlineEntities : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseProceduralDeadlines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeadlineType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    DueAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WarningDaysBefore = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskJournalItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientCaseDocumentId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClosedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CloseReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    AuditCreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AuditCreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AuditCreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AuditUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AuditUpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AuditUpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AuditIsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    AuditDeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AuditDeletedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AuditDeletedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseProceduralDeadlines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseProceduralDeadlines_ClientCaseDocuments_ClientCaseDocumentId",
                        column: x => x.ClientCaseDocumentId,
                        principalTable: "ClientCaseDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientCaseProceduralDeadlines_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientCaseProceduralDeadlines_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseProceduralDeadlines_Case_Status_DueAt",
                table: "ClientCaseProceduralDeadlines",
                columns: new[] { "ClientCaseId", "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseProceduralDeadlines_ClientCaseId",
                table: "ClientCaseProceduralDeadlines",
                column: "ClientCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseProceduralDeadlines_DeadlineType",
                table: "ClientCaseProceduralDeadlines",
                column: "DeadlineType");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseProceduralDeadlines_DocumentId",
                table: "ClientCaseProceduralDeadlines",
                column: "ClientCaseDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseProceduralDeadlines_DueAt",
                table: "ClientCaseProceduralDeadlines",
                column: "DueAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseProceduralDeadlines_Status",
                table: "ClientCaseProceduralDeadlines",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseProceduralDeadlines_TaskJournalItemId",
                table: "ClientCaseProceduralDeadlines",
                column: "TaskJournalItemId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ClientCaseProceduralDeadlines");
        }
    }
}
