using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCaseHearingPreparationPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseHearingPreparations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    HearingAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ContractCheckedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ContractCheckedByUserId = table.Column<int>(type: "integer", nullable: true),
                    ContractCheckedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AuthorityCheckedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AuthorityCheckedByUserId = table.Column<int>(type: "integer", nullable: true),
                    AuthorityCheckedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MaterialsCheckedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    MaterialsCheckedByUserId = table.Column<int>(type: "integer", nullable: true),
                    MaterialsCheckedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
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
                    ClientCaseHearingPreparationId = table.Column<int>(type: "integer", nullable: false),
                    ClientCaseDocumentId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseHearingPreparationDocuments", x => new { x.ClientCaseHearingPreparationId, x.ClientCaseDocumentId });
                    table.ForeignKey(
                        name: "FK_ClientCaseHearingPreparationDocuments_ClientCaseDocuments_C~",
                        column: x => x.ClientCaseDocumentId,
                        principalTable: "ClientCaseDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientCaseHearingPreparationDocuments_ClientCaseHearingPrep~",
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
