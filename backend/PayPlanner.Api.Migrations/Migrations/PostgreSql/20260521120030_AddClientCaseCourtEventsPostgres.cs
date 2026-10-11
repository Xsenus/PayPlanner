using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    [Migration("20260521120030_AddClientCaseCourtEventsPostgres")]
    [DbContext(typeof(PostgreSqlPaymentContext))]
    public partial class AddClientCaseCourtEventsPostgres : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseCourtEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AllDay = table.Column<bool>(type: "boolean", nullable: false),
                    CourtInstance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CourtName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CourtCaseNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Courtroom = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    JudgeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SecretaryName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MovementStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResultSummary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClientCaseDocumentId = table.Column<int>(type: "integer", nullable: true),
                    ProceduralDeadlineId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AuditCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AuditCreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    AuditCreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AuditUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AuditUpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    AuditUpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AuditIsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AuditDeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AuditDeletedByUserId = table.Column<int>(type: "integer", nullable: true),
                    AuditDeletedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
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

            migrationBuilder.CreateIndex("IX_ClientCaseCourtEvents_Case_Status_ScheduledAt", "ClientCaseCourtEvents", new[] { "ClientCaseId", "MovementStatus", "ScheduledAt" });
            migrationBuilder.CreateIndex("IX_ClientCaseCourtEvents_ClientCaseId", "ClientCaseCourtEvents", "ClientCaseId");
            migrationBuilder.CreateIndex("IX_ClientCaseCourtEvents_DeadlineId", "ClientCaseCourtEvents", "ProceduralDeadlineId");
            migrationBuilder.CreateIndex("IX_ClientCaseCourtEvents_DocumentId", "ClientCaseCourtEvents", "ClientCaseDocumentId");
            migrationBuilder.CreateIndex("IX_ClientCaseCourtEvents_EventType", "ClientCaseCourtEvents", "EventType");
            migrationBuilder.CreateIndex("IX_ClientCaseCourtEvents_MovementStatus", "ClientCaseCourtEvents", "MovementStatus");
            migrationBuilder.CreateIndex("IX_ClientCaseCourtEvents_ScheduledAt", "ClientCaseCourtEvents", "ScheduledAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ClientCaseCourtEvents");
        }
    }
}
