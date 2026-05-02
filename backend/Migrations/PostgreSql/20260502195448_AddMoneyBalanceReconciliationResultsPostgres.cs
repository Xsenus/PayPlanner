using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddMoneyBalanceReconciliationResultsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MoneyBalanceReconciliationResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Income = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Expense = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ClosingBalance = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    IncomeCount = table.Column<int>(type: "integer", nullable: false),
                    ExpenseCount = table.Column<int>(type: "integer", nullable: false),
                    IsBalanced = table.Column<bool>(type: "boolean", nullable: false),
                    IssueCount = table.Column<int>(type: "integer", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CheckedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoneyBalanceReconciliationResults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MoneyBalanceReconciliationResults_CheckedAt",
                table: "MoneyBalanceReconciliationResults",
                column: "CheckedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyBalanceReconciliationResults_Date",
                table: "MoneyBalanceReconciliationResults",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyBalanceReconciliationResults_Source_Account_Date",
                table: "MoneyBalanceReconciliationResults",
                columns: new[] { "Source", "AccountName", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoneyBalanceReconciliationResults_Source_Date",
                table: "MoneyBalanceReconciliationResults",
                columns: new[] { "Source", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MoneyBalanceReconciliationResults");
        }
    }
}
