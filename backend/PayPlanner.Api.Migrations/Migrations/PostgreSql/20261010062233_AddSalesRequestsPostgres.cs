using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddSalesRequestsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SalesRequestId",
                table: "TaskJournalItems",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalesRequestEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SalesRequestId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    FromStatusId = table.Column<int>(type: "integer", nullable: true),
                    ToStatusId = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesRequestEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    ClientName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ClientPhone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: true),
                    LinkedCaseWasClosed = table.Column<bool>(type: "boolean", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    StatusId = table.Column<int>(type: "integer", nullable: false),
                    StatusEnteredAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ResponsibleUserId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    FinalKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "None"),
                    ClosedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ClosedByUserId = table.Column<int>(type: "integer", nullable: true),
                    PaidPaymentId = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesRequestStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    FinalKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "None"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesRequestStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesRequestStatusTaskRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StatusId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AssigneeUserId = table.Column<int>(type: "integer", nullable: true),
                    DueInMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
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
