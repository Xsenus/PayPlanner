using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class LinkMoneyOperationsToIncomeTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IncomeTypeId",
                table: "MoneyOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_IncomeTypeId",
                table: "MoneyOperations",
                column: "IncomeTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_MoneyOperations_IncomeTypes_IncomeTypeId",
                table: "MoneyOperations",
                column: "IncomeTypeId",
                principalTable: "IncomeTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MoneyOperations_IncomeTypes_IncomeTypeId",
                table: "MoneyOperations");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOperations_IncomeTypeId",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "IncomeTypeId",
                table: "MoneyOperations");
        }
    }
}
