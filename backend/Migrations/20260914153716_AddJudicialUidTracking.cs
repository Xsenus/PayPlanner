using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddJudicialUidTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JudicialUid",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "JudicialUid",
                table: "ClientCaseCourtInstances",
                type: "TEXT",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE "ClientCaseCourtInstances"
                SET "JudicialUid" = trim(
                    CASE
                        WHEN instr(substr("Notes", instr("Notes", 'Внешний идентификатор:') + length('Внешний идентификатор:')), char(10)) > 0
                        THEN substr(
                            substr("Notes", instr("Notes", 'Внешний идентификатор:') + length('Внешний идентификатор:')),
                            1,
                            instr(substr("Notes", instr("Notes", 'Внешний идентификатор:') + length('Внешний идентификатор:')), char(10)) - 1)
                        ELSE substr("Notes", instr("Notes", 'Внешний идентификатор:') + length('Внешний идентификатор:'))
                    END)
                WHERE instr("Notes", 'Внешний идентификатор:') > 0;

                UPDATE "ClientCases"
                SET "JudicialUid" = COALESCE((
                    SELECT NULLIF(trim(i."JudicialUid"), '')
                    FROM "ClientCaseCourtInstances" AS i
                    WHERE i."ClientCaseId" = "ClientCases"."Id" AND trim(i."JudicialUid") <> ''
                    ORDER BY i."Id"
                    LIMIT 1
                ), '')
                WHERE trim("JudicialUid") = '';
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
