using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddTaskUploadDiscoveryPrefixIndexPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StorageOperations_CommitIdempotencyPrefix",
                table: "StorageOperations",
                column: "IdempotencyKey",
                filter: "\"OperationType\" = 'commit'")
                .Annotation("Npgsql:IndexOperators", new[] { "varchar_pattern_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StorageOperations_CommitIdempotencyPrefix",
                table: "StorageOperations");
        }
    }
}
