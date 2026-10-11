using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddGeneratedCaseReservationRecoveryPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GeneratedRecoveryAttemptCount",
                table: "StorageCapacityReservations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "GeneratedRecoveryLastStatus",
                table: "StorageCapacityReservations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GeneratedRecoveryLeaseExpiresAt",
                table: "StorageCapacityReservations",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GeneratedRecoveryLeaseToken",
                table: "StorageCapacityReservations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GeneratedRecoveryNextAttemptAt",
                table: "StorageCapacityReservations",
                type: "timestamp without time zone",
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
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "StorageCapacityReservations"
                        WHERE "GeneratedRecoveryLeaseToken" IS NOT NULL
                        AND ("GeneratedRecoveryLeaseExpiresAt" IS NULL
                            OR "GeneratedRecoveryLeaseExpiresAt" > (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))) THEN
                        RAISE EXCEPTION 'Cannot downgrade while generated reservation recovery leases are active';
                    END IF;
                END $$;
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
