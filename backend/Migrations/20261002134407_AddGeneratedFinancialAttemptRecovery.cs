using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedFinancialAttemptRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecoveryAttemptCount",
                table: "GeneratedFinancialDocumentAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryLastStatus",
                table: "GeneratedFinancialDocumentAttempts",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveryLeaseExpiresAt",
                table: "GeneratedFinancialDocumentAttempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecoveryLeaseToken",
                table: "GeneratedFinancialDocumentAttempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveryNextAttemptAt",
                table: "GeneratedFinancialDocumentAttempts",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedFinancialAttempts_Recovery",
                table: "GeneratedFinancialDocumentAttempts",
                columns: new[] { "PublishedDocumentId", "RecoveryNextAttemptAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "FinancialRecoveryDownGuard" (
                    "ActiveLeases" INTEGER NOT NULL CHECK ("ActiveLeases" = 0));
                INSERT INTO "FinancialRecoveryDownGuard" ("ActiveLeases")
                SELECT COUNT(*) FROM "GeneratedFinancialDocumentAttempts"
                WHERE "RecoveryLeaseToken" IS NOT NULL AND "RecoveryLeaseExpiresAt" > strftime('%Y-%m-%d %H:%M:%f', 'now');
                DROP TABLE "FinancialRecoveryDownGuard";
                """);
            migrationBuilder.DropIndex(
                name: "IX_GeneratedFinancialAttempts_Recovery",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.DropColumn(
                name: "RecoveryAttemptCount",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.DropColumn(
                name: "RecoveryLastStatus",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.DropColumn(
                name: "RecoveryLeaseExpiresAt",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.DropColumn(
                name: "RecoveryLeaseToken",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.DropColumn(
                name: "RecoveryNextAttemptAt",
                table: "GeneratedFinancialDocumentAttempts");
        }
    }
}
