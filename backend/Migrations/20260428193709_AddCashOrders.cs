using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCashOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CashOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 12, nullable: true),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    PaymentId = table.Column<int>(type: "INTEGER", nullable: true),
                    MoneyOperationId = table.Column<int>(type: "INTEGER", nullable: true),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Account = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    PayerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Basis = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeletedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashOrders_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CashOrders_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CashOrders_MoneyOperations_MoneyOperationId",
                        column: x => x.MoneyOperationId,
                        principalTable: "MoneyOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CashOrders_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashOrder_DeletedAt_Audit",
                table: "CashOrders",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrder_IsDeleted",
                table: "CashOrders",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrder_UpdatedAt_Audit",
                table: "CashOrders",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_ClientId",
                table: "CashOrders",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Date",
                table: "CashOrders",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_InvoiceId",
                table: "CashOrders",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_MoneyOperationId",
                table: "CashOrders",
                column: "MoneyOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Number",
                table: "CashOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_PaymentId",
                table: "CashOrders",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Status",
                table: "CashOrders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Type",
                table: "CashOrders",
                column: "Type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashOrders");
        }
    }
}
