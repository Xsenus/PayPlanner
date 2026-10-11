using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
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
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MoneyAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "RUB"),
                    BankName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BankAccountNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
