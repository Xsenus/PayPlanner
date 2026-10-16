using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMoneyBalanceScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MoneyBalanceReconciliationResults_Source_Account_Date",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "MoneyBalanceReconciliationResults",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "MoneyBalanceReconciliationResults",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MoneyAccountId",
                table: "MoneyBalanceReconciliationResults",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScopeKey",
                table: "MoneyBalanceReconciliationResults",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoneyBalanceReconciliationResults_Company_Account_Date",
                table: "MoneyBalanceReconciliationResults",
                columns: new[] { "CompanyId", "MoneyAccountId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_MoneyBalanceReconciliationResults_MoneyAccountId",
                table: "MoneyBalanceReconciliationResults",
                column: "MoneyAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyBalanceReconciliationResults_ScopeKey_Date",
                table: "MoneyBalanceReconciliationResults",
                columns: new[] { "ScopeKey", "Date" },
                unique: true,
                filter: "\"ScopeKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyBalanceReconciliationResults_Source_Account_Date",
                table: "MoneyBalanceReconciliationResults",
                columns: new[] { "Source", "AccountName", "Date" },
                unique: true,
                filter: "\"ScopeKey\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_MoneyBalanceReconciliationResults_LegalEntities_CompanyId",
                table: "MoneyBalanceReconciliationResults",
                column: "CompanyId",
                principalTable: "LegalEntities",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MoneyBalanceReconciliationResults_MoneyAccounts_MoneyAccountId",
                table: "MoneyBalanceReconciliationResults",
                column: "MoneyAccountId",
                principalTable: "MoneyAccounts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MoneyBalanceReconciliationResults_LegalEntities_CompanyId",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.DropForeignKey(
                name: "FK_MoneyBalanceReconciliationResults_MoneyAccounts_MoneyAccountId",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.DropIndex(
                name: "IX_MoneyBalanceReconciliationResults_Company_Account_Date",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.DropIndex(
                name: "IX_MoneyBalanceReconciliationResults_MoneyAccountId",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.DropIndex(
                name: "IX_MoneyBalanceReconciliationResults_ScopeKey_Date",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.DropIndex(
                name: "IX_MoneyBalanceReconciliationResults_Source_Account_Date",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.DropColumn(
                name: "MoneyAccountId",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.DropColumn(
                name: "ScopeKey",
                table: "MoneyBalanceReconciliationResults");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyBalanceReconciliationResults_Source_Account_Date",
                table: "MoneyBalanceReconciliationResults",
                columns: new[] { "Source", "AccountName", "Date" },
                unique: true);
        }
    }
}
