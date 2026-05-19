using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientCaseRelatedPartiesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseRelatedParties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    ExternalContactId = table.Column<int>(type: "integer", nullable: true),
                    LegalEntityId = table.Column<int>(type: "integer", nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Role = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Phone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
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
