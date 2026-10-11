using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class ProtectStorageCapacityReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_StorageCapacityReservations_Bytes",
                table: "StorageCapacityReservations",
                sql: "\"Bytes\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StorageCapacityReservations_Status",
                table: "StorageCapacityReservations",
                sql: "\"Status\" IN ('pending', 'released')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StorageCapacityEpochs_Singleton",
                table: "StorageCapacityEpochs",
                sql: "\"Id\" = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StorageCapacityEpochs_Version",
                table: "StorageCapacityEpochs",
                sql: "\"Version\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StorageCapacityReservations_Bytes",
                table: "StorageCapacityReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StorageCapacityReservations_Status",
                table: "StorageCapacityReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StorageCapacityEpochs_Singleton",
                table: "StorageCapacityEpochs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StorageCapacityEpochs_Version",
                table: "StorageCapacityEpochs");
        }
    }
}
