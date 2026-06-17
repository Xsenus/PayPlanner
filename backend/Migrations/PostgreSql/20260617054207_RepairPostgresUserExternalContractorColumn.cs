using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class RepairPostgresUserExternalContractorColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Users"
                ADD COLUMN IF NOT EXISTS "IsExternalContractor" boolean NOT NULL DEFAULT false;
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "idx_users_isexternalcontractor"
                ON "Users" ("IsExternalContractor");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Repair migration: keep rollback non-destructive for databases where the column existed before this migration.
        }
    }
}
