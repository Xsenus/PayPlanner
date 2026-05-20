using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientCaseWorkOrdersPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseWorkOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientCaseWorkOrderId = table.Column<int>(type: "integer", nullable: false),
                    ServiceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ExecutorUserId = table.Column<int>(type: "integer", nullable: true),
                    ExecutorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseWorkOrderItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseWorkOrderItems_ClientCaseWorkOrders_ClientCaseWor~",
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
