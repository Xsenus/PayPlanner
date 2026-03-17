using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceLineDictionaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvoiceItemNames",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DefaultPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceItemNames", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceUnits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceUnits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItemNameDictionaryItems_IsActive",
                table: "InvoiceItemNames",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItemNameDictionaryItems_IsActive_Name",
                table: "InvoiceItemNames",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItemNameDictionaryItems_Name",
                table: "InvoiceItemNames",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceUnitDictionaryItems_IsActive",
                table: "InvoiceUnits",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceUnitDictionaryItems_IsActive_Name",
                table: "InvoiceUnits",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceUnitDictionaryItems_Name",
                table: "InvoiceUnits",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceItemNames");

            migrationBuilder.DropTable(
                name: "InvoiceUnits");
        }
    }
}
