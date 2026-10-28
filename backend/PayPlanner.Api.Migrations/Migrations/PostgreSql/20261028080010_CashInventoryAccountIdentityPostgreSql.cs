using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class CashInventoryAccountIdentityPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "CashOrders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MoneyAccountId",
                table: "CashOrders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "CashInventories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MoneyAccountId",
                table: "CashInventories",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_CompanyId",
                table: "CashOrders",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_MoneyAccount_Date",
                table: "CashOrders",
                columns: new[] { "MoneyAccountId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_CashInventories_Company_Account_Date",
                table: "CashInventories",
                columns: new[] { "CompanyId", "MoneyAccountId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_CashInventories_MoneyAccountId",
                table: "CashInventories",
                column: "MoneyAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_CashInventories_LegalEntities_CompanyId",
                table: "CashInventories",
                column: "CompanyId",
                principalTable: "LegalEntities",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CashInventories_MoneyAccounts_MoneyAccountId",
                table: "CashInventories",
                column: "MoneyAccountId",
                principalTable: "MoneyAccounts",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CashOrders_LegalEntities_CompanyId",
                table: "CashOrders",
                column: "CompanyId",
                principalTable: "LegalEntities",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CashOrders_MoneyAccounts_MoneyAccountId",
                table: "CashOrders",
                column: "MoneyAccountId",
                principalTable: "MoneyAccounts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashInventories_LegalEntities_CompanyId",
                table: "CashInventories");

            migrationBuilder.DropForeignKey(
                name: "FK_CashInventories_MoneyAccounts_MoneyAccountId",
                table: "CashInventories");

            migrationBuilder.DropForeignKey(
                name: "FK_CashOrders_LegalEntities_CompanyId",
                table: "CashOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_CashOrders_MoneyAccounts_MoneyAccountId",
                table: "CashOrders");

            migrationBuilder.DropIndex(
                name: "IX_CashOrders_CompanyId",
                table: "CashOrders");

            migrationBuilder.DropIndex(
                name: "IX_CashOrders_MoneyAccount_Date",
                table: "CashOrders");

            migrationBuilder.DropIndex(
                name: "IX_CashInventories_Company_Account_Date",
                table: "CashInventories");

            migrationBuilder.DropIndex(
                name: "IX_CashInventories_MoneyAccountId",
                table: "CashInventories");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "CashOrders");

            migrationBuilder.DropColumn(
                name: "MoneyAccountId",
                table: "CashOrders");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "CashInventories");

            migrationBuilder.DropColumn(
                name: "MoneyAccountId",
                table: "CashInventories");
        }
    }
}
