using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class AddCaseCourtNotificationOutbox : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaseCourtNotificationOutboxes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    JournalChanged = table.Column<bool>(type: "INTEGER", nullable: false),
                    JournalCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LeaseToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NotificationCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false)
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
                CREATE TEMP TABLE "__CourtNotificationOutboxDownGuard" ("Blocked" INTEGER NOT NULL CHECK ("Blocked" = 0));
                INSERT INTO "__CourtNotificationOutboxDownGuard" ("Blocked")
                SELECT 1 WHERE EXISTS (SELECT 1 FROM "CaseCourtNotificationOutboxes");
                DROP TABLE "__CourtNotificationOutboxDownGuard";
                """);
            migrationBuilder.DropTable(name: "CaseCourtNotificationOutboxes");
        }
    }
}
