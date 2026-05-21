using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    public partial class AddClientCaseCourtPartiesPostgres : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseCourtParties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    RelatedPartyId = table.Column<int>(type: "integer", nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Side = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProcessRole = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RepresentativeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AuditCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AuditCreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    AuditCreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AuditUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AuditUpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    AuditUpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AuditIsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AuditDeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AuditDeletedByUserId = table.Column<int>(type: "integer", nullable: true),
                    AuditDeletedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
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
