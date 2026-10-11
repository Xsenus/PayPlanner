using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddGeneratedFinancialRegistryLifecyclePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "StorageObjectId",
                table: "CaseDocumentUploadRetirements",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "UploadKeyText",
                table: "GeneratedFinancialDocumentAttempts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                computedColumnSql: "lower(replace(CAST(\"UploadKey\" AS TEXT), '-', ''))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedFinancialDocumentAttempts_ClientCaseId_ActorId_Pub~",
                table: "GeneratedFinancialDocumentAttempts",
                columns: new[] { "ClientCaseId", "ActorId", "PublishedDocumentId", "UploadKeyText" });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedFinancialDocumentAttempts_ClientCaseId_WorkOrderId~",
                table: "GeneratedFinancialDocumentAttempts",
                columns: new[] { "ClientCaseId", "WorkOrderId", "Kind", "PublishedDocumentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "CaseDocumentUploadRetirements" WHERE "StorageObjectId" IS NULL) THEN
                        RAISE EXCEPTION 'Cannot downgrade while registry-only financial retirements exist';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropIndex(
                name: "IX_GeneratedFinancialDocumentAttempts_ClientCaseId_ActorId_Pub~",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_GeneratedFinancialDocumentAttempts_ClientCaseId_WorkOrderId~",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.DropColumn(
                name: "UploadKeyText",
                table: "GeneratedFinancialDocumentAttempts");

            migrationBuilder.AlterColumn<Guid>(
                name: "StorageObjectId",
                table: "CaseDocumentUploadRetirements",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
