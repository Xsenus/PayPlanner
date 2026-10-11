using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCaseRelatedParties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseRelatedParties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalContactId = table.Column<int>(type: "INTEGER", nullable: true),
                    LegalEntityId = table.Column<int>(type: "INTEGER", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    OrganizationName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseRelatedParties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseRelatedParties_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientCaseRelatedParties_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientCaseRelatedParties_ExternalContacts_ExternalContactId",
                        column: x => x.ExternalContactId,
                        principalTable: "ExternalContacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientCaseRelatedParties_LegalEntities_LegalEntityId",
                        column: x => x.LegalEntityId,
                        principalTable: "LegalEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseRelatedParties_ClientCaseId",
                table: "ClientCaseRelatedParties",
                column: "ClientCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseRelatedParties_ClientId",
                table: "ClientCaseRelatedParties",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseRelatedParties_ExternalContactId",
                table: "ClientCaseRelatedParties",
                column: "ExternalContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseRelatedParties_LegalEntityId",
                table: "ClientCaseRelatedParties",
                column: "LegalEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseRelatedParties_SourceType",
                table: "ClientCaseRelatedParties",
                column: "SourceType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientCaseRelatedParties");
        }
    }
}
