using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddGeneratedFinancialAttemptRecoveryPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecoveryAttemptCount",
                table: "GeneratedFinancialDocumentAttempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryLastStatus",
                table: "GeneratedFinancialDocumentAttempts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveryLeaseExpiresAt",
                table: "GeneratedFinancialDocumentAttempts",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecoveryLeaseToken",
                table: "GeneratedFinancialDocumentAttempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveryNextAttemptAt",
                table: "GeneratedFinancialDocumentAttempts",
                type: "timestamp without time zone",
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
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "GeneratedFinancialDocumentAttempts"
                        WHERE "RecoveryLeaseToken" IS NOT NULL
                        AND "RecoveryLeaseExpiresAt" > (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')) THEN
                        RAISE EXCEPTION 'Cannot downgrade while financial recovery leases are active';
                    END IF;
                END $$;
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
