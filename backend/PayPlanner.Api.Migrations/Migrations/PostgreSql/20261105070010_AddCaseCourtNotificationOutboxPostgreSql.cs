using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    public partial class AddCaseCourtNotificationOutboxPostgreSql : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaseCourtNotificationOutboxes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    BlockedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EventKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    JournalChanged = table.Column<bool>(type: "boolean", nullable: false),
                    JournalCompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LeaseToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    NotificationCompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    SourceId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseCourtNotificationOutboxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseCourtNotificationOutboxes_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(
                name: "IX_CaseCourtNotifications_Source",
                table: "CaseCourtNotificationOutboxes",
                columns: new[] { "ClientCaseId", "Kind", "SourceId", "Id" });
            migrationBuilder.CreateIndex(
                name: "IX_CaseCourtNotificationOutboxes_EventKey",
                table: "CaseCourtNotificationOutboxes",
                column: "EventKey",
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_CaseCourtNotifications_Due",
                table: "CaseCourtNotificationOutboxes",
                columns: new[] { "CompletedAt", "BlockedAt", "NextAttemptAt", "LeaseUntil", "Id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "CaseCourtNotificationOutboxes") THEN
                        RAISE EXCEPTION 'Accepted court notification events require their recovery schema' USING ERRCODE = '23514';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(name: "CaseCourtNotificationOutboxes");
        }
    }
}
