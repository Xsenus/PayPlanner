using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseHearingPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseHearingPreparations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    HearingAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContractCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ContractCheckedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ContractCheckedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AuthorityCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AuthorityCheckedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AuthorityCheckedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MaterialsCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MaterialsCheckedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    MaterialsCheckedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseHearingPreparations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseHearingPreparations_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientCaseHearingPreparationDocuments",
                columns: table => new
                {
                    ClientCaseHearingPreparationId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientCaseDocumentId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseHearingPreparationDocuments", x => new { x.ClientCaseHearingPreparationId, x.ClientCaseDocumentId });
                    table.ForeignKey(
                        name: "FK_ClientCaseHearingPreparationDocuments_ClientCaseDocuments_ClientCaseDocumentId",
                        column: x => x.ClientCaseDocumentId,
                        principalTable: "ClientCaseDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientCaseHearingPreparationDocuments_ClientCaseHearingPreparations_ClientCaseHearingPreparationId",
                        column: x => x.ClientCaseHearingPreparationId,
                        principalTable: "ClientCaseHearingPreparations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseHearingPreparationDocuments_ClientCaseDocumentId",
                table: "ClientCaseHearingPreparationDocuments",
                column: "ClientCaseDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseHearingPreparations_ClientCaseId_HearingAt",
                table: "ClientCaseHearingPreparations",
                columns: new[] { "ClientCaseId", "HearingAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientCaseHearingPreparationDocuments");

            migrationBuilder.DropTable(
                name: "ClientCaseHearingPreparations");
        }
    }
}
