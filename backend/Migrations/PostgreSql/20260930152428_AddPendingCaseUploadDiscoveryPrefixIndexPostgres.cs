using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddPendingCaseUploadDiscoveryPrefixIndexPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StorageCapacityReservations_PendingIntentPrefix",
                table: "StorageCapacityReservations",
                column: "IdempotencyKey",
                filter: "\"Status\" = 'pending' AND \"ProductContextJson\" IS NOT NULL")
                .Annotation("Npgsql:IndexOperators", new[] { "varchar_pattern_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StorageCapacityReservations_PendingIntentPrefix",
                table: "StorageCapacityReservations");
        }
    }
}
