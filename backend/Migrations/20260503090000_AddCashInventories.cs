using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCashInventories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CashInventories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    AccountName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    AccountingBalance = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ActualBalance = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Difference = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CorrectionCashOrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeletedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashInventories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashInventories_CashOrders_CorrectionCashOrderId",
                        column: x => x.CorrectionCashOrderId,
                        principalTable: "CashOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashInventories_AccountName",
                table: "CashInventories",
                column: "AccountName");

            migrationBuilder.CreateIndex(
                name: "IX_CashInventories_CorrectionCashOrderId",
                table: "CashInventories",
                column: "CorrectionCashOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_CashInventories_Date",
                table: "CashInventories",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_CashInventory_DeletedAt_Audit",
                table: "CashInventories",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CashInventory_IsDeleted",
                table: "CashInventories",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_CashInventories_Number",
                table: "CashInventories",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashInventory_UpdatedAt_Audit",
                table: "CashInventories",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashInventories");
        }
    }
}
