using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCasePublicationQueuePagingIndexesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StorageOperations_OperationType_CreatedAt_Id",
                table: "StorageOperations",
                columns: new[] { "OperationType", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_StorageOperations_OperationType_Status_CreatedAt_Id",
                table: "StorageOperations",
                columns: new[] { "OperationType", "Status", "CreatedAt", "Id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StorageOperations_OperationType_CreatedAt_Id",
                table: "StorageOperations");

            migrationBuilder.DropIndex(
                name: "IX_StorageOperations_OperationType_Status_CreatedAt_Id",
                table: "StorageOperations");
        }
    }
}
