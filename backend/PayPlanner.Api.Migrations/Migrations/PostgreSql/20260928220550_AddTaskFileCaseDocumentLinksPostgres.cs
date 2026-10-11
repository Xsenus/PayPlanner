using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddTaskFileCaseDocumentLinksPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientCaseDocumentId",
                table: "TaskJournalFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalFiles_ClientCaseDocumentId",
                table: "TaskJournalFiles",
                column: "ClientCaseDocumentId");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskJournalFiles_ClientCaseDocuments_ClientCaseDocumentId",
                table: "TaskJournalFiles",
                column: "ClientCaseDocumentId",
                principalTable: "ClientCaseDocuments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Only one-to-one, exact upload metadata matches are recoverable.
            // Versions may differ when a task was linked to its case later.
            migrationBuilder.Sql("""
                UPDATE "TaskJournalFiles" AS f
                SET "ClientCaseDocumentId" = (
                    SELECT d."Id" FROM "ClientCaseDocuments" AS d
                    JOIN "TaskJournalItems" AS t ON t."Id" = f."TaskJournalItemId"
                    WHERE d."ClientCaseId" = t."ClientCaseId"
                      AND d."DocumentKey" = 'task-' || CAST(f."TaskJournalItemId" AS TEXT) || '-' ||
                          substr(f."DocumentKey", 1, 64 - length('task-' || CAST(f."TaskJournalItemId" AS TEXT) || '-'))
                      AND d."OriginalFileName" = f."OriginalFileName" AND d."ContentType" = f."ContentType"
                      AND d."SizeBytes" = f."SizeBytes" AND d."UploadedAt" = f."UploadedAt"
                      AND (d."UploadedByUserId" = f."UploadedByUserId" OR
                          (d."UploadedByUserId" IS NULL AND f."UploadedByUserId" IS NULL))
                )
                WHERE f."ClientCaseDocumentId" IS NULL
                  AND 1 = (
                    SELECT count(*) FROM "ClientCaseDocuments" AS d
                    JOIN "TaskJournalItems" AS t ON t."Id" = f."TaskJournalItemId"
                    WHERE d."ClientCaseId" = t."ClientCaseId"
                      AND d."DocumentKey" = 'task-' || CAST(f."TaskJournalItemId" AS TEXT) || '-' ||
                          substr(f."DocumentKey", 1, 64 - length('task-' || CAST(f."TaskJournalItemId" AS TEXT) || '-'))
                      AND d."OriginalFileName" = f."OriginalFileName" AND d."ContentType" = f."ContentType"
                      AND d."SizeBytes" = f."SizeBytes" AND d."UploadedAt" = f."UploadedAt"
                      AND (d."UploadedByUserId" = f."UploadedByUserId" OR
                          (d."UploadedByUserId" IS NULL AND f."UploadedByUserId" IS NULL))
                  )
                  AND 1 = (
                    SELECT count(*) FROM "TaskJournalFiles" AS other
                    WHERE other."TaskJournalItemId" = f."TaskJournalItemId"
                      AND substr(other."DocumentKey", 1, 64 - length('task-' || CAST(f."TaskJournalItemId" AS TEXT) || '-')) =
                          substr(f."DocumentKey", 1, 64 - length('task-' || CAST(f."TaskJournalItemId" AS TEXT) || '-'))
                      AND other."OriginalFileName" = f."OriginalFileName" AND other."ContentType" = f."ContentType"
                      AND other."SizeBytes" = f."SizeBytes" AND other."UploadedAt" = f."UploadedAt"
                      AND (other."UploadedByUserId" = f."UploadedByUserId" OR
                          (other."UploadedByUserId" IS NULL AND f."UploadedByUserId" IS NULL))
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskJournalFiles_ClientCaseDocuments_ClientCaseDocumentId",
                table: "TaskJournalFiles");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalFiles_ClientCaseDocumentId",
                table: "TaskJournalFiles");

            migrationBuilder.DropColumn(
                name: "ClientCaseDocumentId",
                table: "TaskJournalFiles");
        }
    }
}
