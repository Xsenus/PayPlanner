using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMoneyOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MoneyOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    AccountingType = table.Column<string>(type: "TEXT", nullable: false),
                    AccountName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CounterpartyName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CategoryName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 12, nullable: true),
                    PaymentOrderNumber = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    RecognitionDate = table.Column<DateTime>(type: "date", nullable: true),
                    IsAdvancePayment = table.Column<bool>(type: "INTEGER", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    PaidInvoiceNumber = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    PaidAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    ProjectName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DirectionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SalesPointName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PaymentPriority = table.Column<int>(type: "INTEGER", nullable: true),
                    PaidAccountName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoneyOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_Date",
                table: "MoneyOperations",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_Direction",
                table: "MoneyOperations",
                column: "Direction");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_Source",
                table: "MoneyOperations",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_Source_Date",
                table: "MoneyOperations",
                columns: new[] { "Source", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MoneyOperations");
        }
    }
}
