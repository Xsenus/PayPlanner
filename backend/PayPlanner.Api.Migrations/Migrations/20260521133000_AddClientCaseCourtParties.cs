using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    [Migration("20260521133000_AddClientCaseCourtParties")]
    [DbContext(typeof(PaymentContext))]
    public partial class AddClientCaseCourtParties : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseCourtParties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelatedPartyId = table.Column<int>(type: "INTEGER", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Side = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProcessRole = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    RepresentativeName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AuditCreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AuditCreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AuditCreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AuditUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AuditUpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AuditUpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AuditIsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    AuditDeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AuditDeletedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AuditDeletedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseCourtParties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtParties_ClientCaseRelatedParties_RelatedPartyId",
                        column: x => x.RelatedPartyId,
                        principalTable: "ClientCaseRelatedParties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtParties_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("IX_ClientCaseCourtParties_Case_Side_Archived", "ClientCaseCourtParties", new[] { "ClientCaseId", "Side", "IsArchived" });
            migrationBuilder.CreateIndex("IX_ClientCaseCourtParties_ClientCaseId", "ClientCaseCourtParties", "ClientCaseId");
            migrationBuilder.CreateIndex("IX_ClientCaseCourtParties_RelatedPartyId", "ClientCaseCourtParties", "RelatedPartyId");
            migrationBuilder.CreateIndex("IX_ClientCaseCourtParties_Side", "ClientCaseCourtParties", "Side");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ClientCaseCourtParties");
        }
    }
}
