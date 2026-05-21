using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class AddClientCaseCourtEvents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseCourtEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    CourtInstance = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CourtName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CourtCaseNumber = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Courtroom = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    JudgeName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SecretaryName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    MovementStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ResultSummary = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientCaseDocumentId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProceduralDeadlineId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
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
                    table.PrimaryKey("PK_ClientCaseCourtEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtEvents_ClientCaseDocuments_ClientCaseDocumentId",
                        column: x => x.ClientCaseDocumentId,
                        principalTable: "ClientCaseDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtEvents_ClientCaseProceduralDeadlines_ProceduralDeadlineId",
                        column: x => x.ProceduralDeadlineId,
                        principalTable: "ClientCaseProceduralDeadlines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtEvents_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseCourtEvents_Case_Status_ScheduledAt",
                table: "ClientCaseCourtEvents",
                columns: new[] { "ClientCaseId", "MovementStatus", "ScheduledAt" });
            migrationBuilder.CreateIndex(name: "IX_ClientCaseCourtEvents_ClientCaseId", table: "ClientCaseCourtEvents", column: "ClientCaseId");
            migrationBuilder.CreateIndex(name: "IX_ClientCaseCourtEvents_DeadlineId", table: "ClientCaseCourtEvents", column: "ProceduralDeadlineId");
            migrationBuilder.CreateIndex(name: "IX_ClientCaseCourtEvents_DocumentId", table: "ClientCaseCourtEvents", column: "ClientCaseDocumentId");
            migrationBuilder.CreateIndex(name: "IX_ClientCaseCourtEvents_EventType", table: "ClientCaseCourtEvents", column: "EventType");
            migrationBuilder.CreateIndex(name: "IX_ClientCaseCourtEvents_MovementStatus", table: "ClientCaseCourtEvents", column: "MovementStatus");
            migrationBuilder.CreateIndex(name: "IX_ClientCaseCourtEvents_ScheduledAt", table: "ClientCaseCourtEvents", column: "ScheduledAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ClientCaseCourtEvents");
        }
    }
}
