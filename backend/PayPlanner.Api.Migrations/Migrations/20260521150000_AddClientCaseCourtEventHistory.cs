using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    [Migration("20260521150000_AddClientCaseCourtEventHistory")]
    [DbContext(typeof(PaymentContext))]
    public partial class AddClientCaseCourtEventHistory : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseCourtEventHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientCaseCourtEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseCourtEventHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtEventHistory_ClientCaseCourtEvents_ClientCaseCourtEventId",
                        column: x => x.ClientCaseCourtEventId,
                        principalTable: "ClientCaseCourtEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtEventHistory_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("IX_ClientCaseCourtEventHistory_Case_OccurredAt", "ClientCaseCourtEventHistory", new[] { "ClientCaseId", "OccurredAt" });
            migrationBuilder.CreateIndex("IX_ClientCaseCourtEventHistory_ClientCaseId", "ClientCaseCourtEventHistory", "ClientCaseId");
            migrationBuilder.CreateIndex("IX_ClientCaseCourtEventHistory_CourtEventId", "ClientCaseCourtEventHistory", "ClientCaseCourtEventId");
            migrationBuilder.CreateIndex("IX_ClientCaseCourtEventHistory_OccurredAt", "ClientCaseCourtEventHistory", "OccurredAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ClientCaseCourtEventHistory");
        }
    }
}
