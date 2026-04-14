using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddActLineItemsAndCreationModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreationMode",
                table: "Acts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateTable(
                name: "ActLineItem",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActId = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    InvoiceLineItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SourceSortOrder = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActLineItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActLineItem_Acts_ActId",
                        column: x => x.ActId,
                        principalTable: "Acts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActLineItems_ActId",
                table: "ActLineItem",
                column: "ActId");

            migrationBuilder.CreateIndex(
                name: "IX_ActLineItems_ActId_SortOrder",
                table: "ActLineItem",
                columns: new[] { "ActId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ActLineItems_ActId_SourceKey",
                table: "ActLineItem",
                columns: new[] { "ActId", "SourceKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActLineItem");

            migrationBuilder.DropColumn(
                name: "CreationMode",
                table: "Acts");
        }
    }
}
