using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SalesRequestId",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalesRequestEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SalesRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    FromStatusId = table.Column<int>(type: "INTEGER", nullable: true),
                    ToStatusId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesRequestEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ClientPhone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: true),
                    LinkedCaseWasClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    StatusId = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusEnteredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResponsibleUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    FinalKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "None"),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    PaidPaymentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesRequestStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    FinalKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "None"),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesRequestStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesRequestStatusTaskRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StatusId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    AssigneeUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DueInMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesRequestStatusTaskRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_SalesRequestId",
                table: "TaskJournalItems",
                column: "SalesRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesRequestEvents_Request_CreatedAt",
                table: "SalesRequestEvents",
                columns: new[] { "SalesRequestId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesRequests_Client_CreatedAt",
                table: "SalesRequests",
                columns: new[] { "ClientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesRequests_CreatedAt",
                table: "SalesRequests",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SalesRequests_Status_Responsible",
                table: "SalesRequests",
                columns: new[] { "StatusId", "ResponsibleUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesRequestStatuses_Key",
                table: "SalesRequestStatuses",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesRequestStatusTaskRules_Status",
                table: "SalesRequestStatusTaskRules",
                column: "StatusId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalesRequestEvents");

            migrationBuilder.DropTable(
                name: "SalesRequests");

            migrationBuilder.DropTable(
                name: "SalesRequestStatuses");

            migrationBuilder.DropTable(
                name: "SalesRequestStatusTaskRules");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_SalesRequestId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "SalesRequestId",
                table: "TaskJournalItems");
        }
    }
}
