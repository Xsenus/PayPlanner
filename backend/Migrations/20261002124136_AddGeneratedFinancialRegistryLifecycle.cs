using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedFinancialRegistryLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "StorageObjectId",
                table: "CaseDocumentUploadRetirements",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "UploadKeyText",
                table: "GeneratedFinancialDocumentAttempts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                computedColumnSql: "lower(replace(CAST(\"UploadKey\" AS TEXT), '-', ''))",
                stored: false);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedFinancialDocumentAttempts_ClientCaseId_ActorId_PublishedDocumentId_UploadKeyText",
                table: "GeneratedFinancialDocumentAttempts",
                columns: new[] { "ClientCaseId", "ActorId", "PublishedDocumentId", "UploadKeyText" });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedFinancialDocumentAttempts_ClientCaseId_WorkOrderId_Kind_PublishedDocumentId",
                table: "GeneratedFinancialDocumentAttempts",
                columns: new[] { "ClientCaseId", "WorkOrderId", "Kind", "PublishedDocumentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never fabricate a physical object to fit the older schema.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "GeneratedFinancialLifecycleDownGuard" (
                    "NoRegistryOnlyRetirements" INTEGER NOT NULL CHECK ("NoRegistryOnlyRetirements" = 0));
                INSERT INTO "GeneratedFinancialLifecycleDownGuard" ("NoRegistryOnlyRetirements")
                SELECT COUNT(*) FROM "CaseDocumentUploadRetirements" WHERE "StorageObjectId" IS NULL;
                DROP TABLE "GeneratedFinancialLifecycleDownGuard";
                """);
            migrationBuilder.DropIndex(
                name: "IX_GeneratedFinancialDocumentAttempts_ClientCaseId_ActorId_PublishedDocumentId_UploadKeyText",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_GeneratedFinancialDocumentAttempts_ClientCaseId_WorkOrderId_Kind_PublishedDocumentId",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.DropColumn(
                name: "UploadKeyText",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.AlterColumn<Guid>(
                name: "StorageObjectId",
                table: "CaseDocumentUploadRetirements",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
