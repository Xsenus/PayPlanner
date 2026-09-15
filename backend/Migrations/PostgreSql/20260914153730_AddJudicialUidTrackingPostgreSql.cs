using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddJudicialUidTrackingPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JudicialUid",
                table: "ClientCases",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "JudicialUid",
                table: "ClientCaseCourtInstances",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE "ClientCaseCourtInstances"
                SET "JudicialUid" = trim(substring("Notes" from 'Внешний идентификатор:\\s*([^\\r\\n]+)'))
                WHERE "Notes" ~ 'Внешний идентификатор:';

                UPDATE "ClientCases" AS c
                SET "JudicialUid" = source."JudicialUid"
                FROM (
                    SELECT DISTINCT ON ("ClientCaseId") "ClientCaseId", "JudicialUid"
                    FROM "ClientCaseCourtInstances"
                    WHERE trim("JudicialUid") <> ''
                    ORDER BY "ClientCaseId", "Id"
                ) AS source
                WHERE source."ClientCaseId" = c."Id" AND trim(c."JudicialUid") = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_JudicialUid",
                table: "ClientCases",
                column: "JudicialUid",
                filter: "\"JudicialUid\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseCourtInstances_JudicialUid",
                table: "ClientCaseCourtInstances",
                column: "JudicialUid",
                filter: "\"JudicialUid\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientCases_JudicialUid",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCaseCourtInstances_JudicialUid",
                table: "ClientCaseCourtInstances");

            migrationBuilder.DropColumn(
                name: "JudicialUid",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "JudicialUid",
                table: "ClientCaseCourtInstances");
        }
    }
}
