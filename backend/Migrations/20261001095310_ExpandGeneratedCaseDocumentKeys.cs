using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class ExpandGeneratedCaseDocumentKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // TEXT has no physical length limit, but the preceding application
            // cannot recover identities longer than 64. Do not lose that proof.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "__TemplateKeyDowngradeGuard" ("Blocked" INTEGER NOT NULL CHECK ("Blocked" = 0));
                INSERT INTO "__TemplateKeyDowngradeGuard" ("Blocked")
                SELECT 1 WHERE
                    EXISTS (SELECT 1 FROM "ClientCaseDocuments" WHERE length("DocumentKey") > 64)
                    OR EXISTS (
                        SELECT 1 FROM "StorageCapacityReservations"
                        WHERE "Status" = 'pending' AND "ProductContextJson" IS NOT NULL
                            AND "ProductContextJson" LIKE '%caseId%'
                            AND CASE WHEN json_valid("ProductContextJson")
                                THEN coalesce(length(json_extract("ProductContextJson", '$.documentKey')) > 64, 0)
                                ELSE 1 END)
                    OR EXISTS (
                        SELECT 1 FROM "StorageOperations" AS op
                        JOIN "StorageObjects" AS obj ON obj."Id" = op."StorageObjectId"
                        WHERE op."OperationType" = 'commit' AND obj."DataClass" = 'case-document'
                            AND op."PayloadJson" IS NOT NULL
                            AND CASE WHEN json_valid(op."PayloadJson")
                                THEN coalesce(length(json_extract(op."PayloadJson", '$.documentKey')) > 64, 0)
                                ELSE 1 END);
                DROP TABLE "__TemplateKeyDowngradeGuard";
                """);
        }
    }
}
