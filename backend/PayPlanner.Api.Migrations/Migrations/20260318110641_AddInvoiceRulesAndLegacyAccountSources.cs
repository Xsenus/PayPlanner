using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceRulesAndLegacyAccountSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LegacyAccountDate",
                table: "Payments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegacyAccountNumber",
                table: "Payments",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LegacyAccount_LegacyAccountDate",
                table: "Payments",
                columns: new[] { "LegacyAccountNumber", "LegacyAccountDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LegacyAccountDate",
                table: "Payments",
                column: "LegacyAccountDate");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LegacyAccountNumber",
                table: "Payments",
                column: "LegacyAccountNumber");

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_Key",
                table: "SystemSettings",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropIndex(
                name: "IX_Payments_LegacyAccount_LegacyAccountDate",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_LegacyAccountDate",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_LegacyAccountNumber",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "LegacyAccountDate",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "LegacyAccountNumber",
                table: "Payments");
        }
    }
}
