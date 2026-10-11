using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMoneyAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MoneyAccountId",
                table: "MoneyOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MoneyAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "RUB"),
                    BankName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BankAccountNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoneyAccounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_MoneyAccountId",
                table: "MoneyOperations",
                column: "MoneyAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyAccounts_IsActive",
                table: "MoneyAccounts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyAccounts_Name",
                table: "MoneyAccounts",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyAccounts_Source",
                table: "MoneyAccounts",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyAccounts_Source_Name",
                table: "MoneyAccounts",
                columns: new[] { "Source", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MoneyOperations_MoneyAccounts_MoneyAccountId",
                table: "MoneyOperations",
                column: "MoneyAccountId",
                principalTable: "MoneyAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MoneyOperations_MoneyAccounts_MoneyAccountId",
                table: "MoneyOperations");

            migrationBuilder.DropTable(
                name: "MoneyAccounts");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOperations_MoneyAccountId",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "MoneyAccountId",
                table: "MoneyOperations");
        }
    }
}
