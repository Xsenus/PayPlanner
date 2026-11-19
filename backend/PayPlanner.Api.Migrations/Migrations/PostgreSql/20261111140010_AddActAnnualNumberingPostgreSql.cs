using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    public partial class AddActAnnualNumberingPostgreSql : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActAnnualNumberCounters",
                columns: table => new
                {
                    Year = table.Column<int>(type: "integer", nullable: false),
                    LastNumber = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActAnnualNumberCounters", row => row.Year);
                    table.CheckConstraint("CK_ActAnnualNumberCounters_Value", "\"Year\" BETWEEN 1 AND 9999 AND \"LastNumber\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "ActCreateReceipts",
                columns: table => new
                {
                    ActorUserId = table.Column<int>(type: "integer", nullable: false),
                    UploadKey = table.Column<Guid>(type: "uuid", nullable: false),
                    ActId = table.Column<int>(type: "integer", nullable: false),
                    CaseId = table.Column<int>(type: "integer", nullable: true),
                    CommandSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PublishedDocumentId = table.Column<int>(type: "integer", nullable: true),
                    WorkOrderId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActCreateReceipts", row => new { row.ActorUserId, row.UploadKey });
                    table.CheckConstraint("CK_ActCreateReceipts_Identity", "\"ActorUserId\" > 0 AND \"ActId\" > 0 AND length(\"CommandSha256\") = 64 AND (\"PublishedDocumentId\" IS NULL OR \"PublishedDocumentId\" > 0) AND ((\"Operation\" = 'ordinary' AND \"CaseId\" IS NULL AND \"WorkOrderId\" IS NULL) OR (\"Operation\" = 'work-order' AND \"CaseId\" IS NOT NULL AND \"WorkOrderId\" IS NOT NULL AND \"CaseId\" > 0 AND \"WorkOrderId\" > 0))");
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ActCreateReceipts");
            migrationBuilder.DropTable(name: "ActAnnualNumberCounters");
        }
    }
}