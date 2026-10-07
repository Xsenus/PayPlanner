using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCourtDeadlineSourceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceDocumentKey",
                table: "ClientCaseProceduralDeadlines",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            // Preserve every row; ambiguous legacy identities are left unclaimed.
            migrationBuilder.Sql("""
                WITH candidates AS (
                    SELECT p."Id", p."ClientCaseId", p."DeadlineType", d."DocumentKey"
                    FROM "ClientCaseProceduralDeadlines" p
                    JOIN "ClientCaseDocuments" d ON d."Id" = p."ClientCaseDocumentId" AND d."ClientCaseId" = p."ClientCaseId"
                    WHERE p."SourceType" = 'sudrf' AND d."SourceType" = 'sudrf'
                        AND substr(d."DocumentKey", 1, 10) = 'sudrf-act-'
                        AND length(d."DocumentKey") IN (34, 42)
                        AND substr(d."DocumentKey", 11) NOT GLOB '*[^0-9a-f]*'
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
                CREATE TEMP TABLE "__CourtSourceDowngradeGuard" ("Blocked" INTEGER NOT NULL CHECK ("Blocked" = 0));
                INSERT INTO "__CourtSourceDowngradeGuard" ("Blocked")
                SELECT 1 WHERE EXISTS (SELECT 1 FROM "ClientCaseProceduralDeadlines" WHERE "SourceDocumentKey" IS NOT NULL);
                DROP TABLE "__CourtSourceDowngradeGuard";
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
