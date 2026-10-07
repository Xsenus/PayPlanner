using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedCaseReservationRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GeneratedRecoveryAttemptCount",
                table: "StorageCapacityReservations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "GeneratedRecoveryLastStatus",
                table: "StorageCapacityReservations",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GeneratedRecoveryLeaseExpiresAt",
                table: "StorageCapacityReservations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GeneratedRecoveryLeaseToken",
                table: "StorageCapacityReservations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GeneratedRecoveryNextAttemptAt",
                table: "StorageCapacityReservations",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_StorageCapacityReservations_GeneratedRecoveryWindow",
                table: "StorageCapacityReservations",
                columns: new[] { "ResourceKey", "Status", "GeneratedRecoveryNextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "GeneratedReservationRecoveryDownGuard" (
                    "ActiveLeases" INTEGER NOT NULL CHECK ("ActiveLeases" = 0));
                INSERT INTO "GeneratedReservationRecoveryDownGuard" ("ActiveLeases")
                SELECT COUNT(*) FROM "StorageCapacityReservations"
                WHERE "GeneratedRecoveryLeaseToken" IS NOT NULL
                    AND ("GeneratedRecoveryLeaseExpiresAt" IS NULL
                        OR "GeneratedRecoveryLeaseExpiresAt" > strftime('%Y-%m-%d %H:%M:%f', 'now'));
                DROP TABLE "GeneratedReservationRecoveryDownGuard";
                """);
            migrationBuilder.DropIndex(
                name: "IX_StorageCapacityReservations_GeneratedRecoveryWindow",
                table: "StorageCapacityReservations");

            migrationBuilder.DropColumn(
                name: "GeneratedRecoveryAttemptCount",
                table: "StorageCapacityReservations");

            migrationBuilder.DropColumn(
                name: "GeneratedRecoveryLastStatus",
                table: "StorageCapacityReservations");

            migrationBuilder.DropColumn(
                name: "GeneratedRecoveryLeaseExpiresAt",
                table: "StorageCapacityReservations");

            migrationBuilder.DropColumn(
                name: "GeneratedRecoveryLeaseToken",
                table: "StorageCapacityReservations");

            migrationBuilder.DropColumn(
                name: "GeneratedRecoveryNextAttemptAt",
                table: "StorageCapacityReservations");
        }
    }
}
