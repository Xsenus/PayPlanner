using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class AddActAnnualNumbering : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActAnnualNumberCounters",
                columns: table => new
                {
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    LastNumber = table.Column<long>(type: "INTEGER", nullable: false)
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
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActId = table.Column<int>(type: "INTEGER", nullable: false),
                    CaseId = table.Column<int>(type: "INTEGER", nullable: true),
                    CommandSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PublishedDocumentId = table.Column<int>(type: "INTEGER", nullable: true),
                    WorkOrderId = table.Column<int>(type: "INTEGER", nullable: true)
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