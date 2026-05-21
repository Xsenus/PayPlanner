using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    [Migration("20260521173000_AddClientCaseCalendarAuditColumns")]
    [DbContext(typeof(PaymentContext))]
    public partial class AddClientCaseCalendarAuditColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            AddAuditColumns(migrationBuilder, "ClientCaseProceduralDeadlines", "ClientCaseProceduralDeadline");
            AddAuditColumns(migrationBuilder, "ClientCaseCourtEvents", "ClientCaseCourtEvent");
            AddAuditColumns(migrationBuilder, "ClientCaseCourtParties", "ClientCaseCourtParty");
            AddAuditColumns(migrationBuilder, "ClientCaseCourts", "ClientCaseCourt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropAuditColumns(migrationBuilder, "ClientCaseProceduralDeadlines", "ClientCaseProceduralDeadline");
            DropAuditColumns(migrationBuilder, "ClientCaseCourtEvents", "ClientCaseCourtEvent");
            DropAuditColumns(migrationBuilder, "ClientCaseCourtParties", "ClientCaseCourtParty");
            DropAuditColumns(migrationBuilder, "ClientCaseCourts", "ClientCaseCourt");
        }

        private static void AddAuditColumns(MigrationBuilder migrationBuilder, string tableName, string entityName)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: tableName,
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: tableName,
                type: "TEXT",
                nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: tableName,
                type: "INTEGER",
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: tableName,
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex($"IX_{entityName}_IsDeleted", tableName, "IsDeleted");
            migrationBuilder.CreateIndex($"IX_{entityName}_DeletedAt_Audit", tableName, "DeletedAt");
            migrationBuilder.CreateIndex($"IX_{entityName}_UpdatedAt_Audit", tableName, "UpdatedAt");
        }

        private static void DropAuditColumns(MigrationBuilder migrationBuilder, string tableName, string entityName)
        {
            migrationBuilder.DropIndex($"IX_{entityName}_UpdatedAt_Audit", tableName);
            migrationBuilder.DropIndex($"IX_{entityName}_DeletedAt_Audit", tableName);
            migrationBuilder.DropIndex($"IX_{entityName}_IsDeleted", tableName);
            migrationBuilder.DropColumn("DeletedByUserName", tableName);
            migrationBuilder.DropColumn("DeletedByUserId", tableName);
            migrationBuilder.DropColumn("DeletedAt", tableName);
            migrationBuilder.DropColumn("IsDeleted", tableName);
        }
    }
}
