using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCourtDeadlineSourceIdentityPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceDocumentKey",
                table: "ClientCaseProceduralDeadlines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Preserve every row; ambiguous legacy identities are left unclaimed.
            migrationBuilder.Sql("""
                WITH candidates AS (
                    SELECT p."Id", p."ClientCaseId", p."DeadlineType", d."DocumentKey"
                    FROM "ClientCaseProceduralDeadlines" p
                    JOIN "ClientCaseDocuments" d ON d."Id" = p."ClientCaseDocumentId" AND d."ClientCaseId" = p."ClientCaseId"
                    WHERE p."SourceType" = 'sudrf' AND d."SourceType" = 'sudrf'
                        AND d."DocumentKey" ~ '^sudrf-act-([0-9a-f]{24}|[0-9a-f]{32})$'
                ), unique_sources AS (
                    SELECT min("Id") AS "DeadlineId", "DocumentKey"
                    FROM candidates GROUP BY "ClientCaseId", "DeadlineType", "DocumentKey" HAVING count(*) = 1
                )
                UPDATE "ClientCaseProceduralDeadlines" AS p SET "SourceDocumentKey" = u."DocumentKey"
                FROM unique_sources AS u WHERE p."Id" = u."DeadlineId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CaseDeadlines_CourtSourceIdentity",
                table: "ClientCaseProceduralDeadlines",
                columns: new[] { "ClientCaseId", "SourceType", "SourceDocumentKey", "DeadlineType" },
                unique: true,
                filter: "\"SourceDocumentKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "ClientCaseProceduralDeadlines" WHERE "SourceDocumentKey" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Court source identities must be retained; downgrade is unsafe.' USING ERRCODE = '23514';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropIndex(
                name: "IX_CaseDeadlines_CourtSourceIdentity",
                table: "ClientCaseProceduralDeadlines");

            migrationBuilder.DropColumn(
                name: "SourceDocumentKey",
                table: "ClientCaseProceduralDeadlines");
        }
    }
}
