using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddMoneyOperationVatFieldsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "MoneyOperations"
                    ADD COLUMN IF NOT EXISTS "VatAmount" numeric(18,2),
                    ADD COLUMN IF NOT EXISTS "VatRate" numeric(5,2);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VatAmount",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "VatRate",
                table: "MoneyOperations");
        }
    }
}
