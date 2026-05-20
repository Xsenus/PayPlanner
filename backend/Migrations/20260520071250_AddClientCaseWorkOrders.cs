using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCaseWorkOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseWorkOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseWorkOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseWorkOrders_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientCaseWorkOrderItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientCaseWorkOrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ExecutorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExecutorName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DueDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseWorkOrderItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseWorkOrderItems_ClientCaseWorkOrders_ClientCaseWorkOrderId",
                        column: x => x.ClientCaseWorkOrderId,
                        principalTable: "ClientCaseWorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientCaseWorkOrderItems_Users_ExecutorUserId",
                        column: x => x.ExecutorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseWorkOrderItems_DueDate",
                table: "ClientCaseWorkOrderItems",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseWorkOrderItems_ExecutorUserId",
                table: "ClientCaseWorkOrderItems",
                column: "ExecutorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseWorkOrderItems_WorkOrderId",
                table: "ClientCaseWorkOrderItems",
                column: "ClientCaseWorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseWorkOrders_ClientCaseId",
                table: "ClientCaseWorkOrders",
                column: "ClientCaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseWorkOrders_CreatedAt",
                table: "ClientCaseWorkOrders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseWorkOrders_Status",
                table: "ClientCaseWorkOrders",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientCaseWorkOrderItems");

            migrationBuilder.DropTable(
                name: "ClientCaseWorkOrders");
        }
    }
}
