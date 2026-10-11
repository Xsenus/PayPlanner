using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedFinancialClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PublishedDocumentId",
                table: "GeneratedFinancialDocumentAttempts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GeneratedFinancialActLineClaims",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkOrderItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedFinancialActLineClaims", x => new { x.AttemptId, x.WorkOrderItemId });
                    table.CheckConstraint("CK_GeneratedFinancialActLineClaims_Identity", "\"WorkOrderItemId\" > 0 AND length(\"SourceKey\") BETWEEN 1 AND 128");
                    table.ForeignKey(
                        name: "FK_GeneratedFinancialActLineClaims_GeneratedFinancialDocumentAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "GeneratedFinancialDocumentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeneratedFinancialNumberClaims",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DateKey = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedFinancialNumberClaims", x => x.AttemptId);
                    table.CheckConstraint("CK_GeneratedFinancialNumberClaims_Identity", "\"Kind\" IN ('invoice', 'act', 'contract') AND length(\"DateKey\") = 10 AND length(\"Number\") BETWEEN 1 AND 120");
                    table.ForeignKey(
                        name: "FK_GeneratedFinancialNumberClaims_GeneratedFinancialDocumentAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "GeneratedFinancialDocumentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeneratedFinancialNumberScopes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedFinancialNumberScopes", x => x.Id);
                    table.CheckConstraint("CK_GeneratedFinancialNumberScopes_Id", "\"Id\" IN (1, 2, 3) AND \"Version\" = 0");
                });

            migrationBuilder.InsertData(
                table: "GeneratedFinancialNumberScopes",
                columns: new[] { "Id", "Version" },
                values: new object[,]
                {
                    { 1, 0L },
                    { 2, 0L },
                    { 3, 0L }
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedFinancialActLineClaims_SourceKey",
                table: "GeneratedFinancialActLineClaims",
                column: "SourceKey");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedFinancialNumberClaims_Kind_DateKey_Number",
                table: "GeneratedFinancialNumberClaims",
                columns: new[] { "Kind", "DateKey", "Number" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "__FinancialClaimsUpgradeGuard" ("Blocked" INTEGER NOT NULL CHECK ("Blocked" = 0));
                INSERT INTO "__FinancialClaimsUpgradeGuard" ("Blocked")
                SELECT 1 WHERE EXISTS (
                    SELECT 1 FROM "GeneratedFinancialDocumentAttempts" a
                    WHERE COALESCE(json_valid(a."FrozenDomainJson")
                        AND json_extract(a."FrozenDomainJson", '$.version') = 1
                        AND json_extract(a."FrozenDomainJson", '$.caseId') = a."ClientCaseId"
                        AND json_extract(a."FrozenDomainJson", '$.workOrderId') = a."WorkOrderId"
                        AND json_extract(a."FrozenDomainJson", '$.actorId') = a."ActorId"
                        AND json_extract(a."FrozenDomainJson", '$.actorName') = a."ActorName"
                        AND json_extract(a."FrozenDomainJson", '$.identity.entityId') = a."EntityId"
                        AND json_extract(a."FrozenDomainJson", '$.identity.invoiceEntityId') IS a."InvoiceEntityId"
                        AND json_extract(a."FrozenDomainJson", '$.kind') = CASE a."Kind" WHEN 'invoice' THEN 0 WHEN 'act' THEN 1 ELSE 2 END
                        AND length(json_extract(a."FrozenDomainJson", '$.number')) BETWEEN 1 AND 120
                        AND substr(json_extract(a."FrozenDomainJson", '$.date'), 1, 10) GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
                        AND json_type(a."FrozenDomainJson", '$.lines') = 'array' AND json_array_length(a."FrozenDomainJson", '$.lines') > 0
                        AND EXISTS (SELECT 1 FROM "GeneratedFinancialDocumentContents" c WHERE c."AttemptId" = a."Id" AND length(c."Bytes") = a."ContentSizeBytes"), 0) = 0);
                INSERT INTO "GeneratedFinancialNumberClaims" ("AttemptId", "Kind", "DateKey", "Number")
                SELECT a."Id", a."Kind", substr(json_extract(a."FrozenDomainJson", '$.date'), 1, 10), json_extract(a."FrozenDomainJson", '$.number')
                FROM "GeneratedFinancialDocumentAttempts" a
                WHERE NOT EXISTS (SELECT 1 FROM "CaseDocumentUploadRetirements" r WHERE r."CaseId" = a."ClientCaseId" AND r."ActorId" = a."ActorId" AND r."UploadKey" = a."UploadKey");
                INSERT INTO "GeneratedFinancialActLineClaims" ("AttemptId", "WorkOrderItemId", "SourceKey")
                SELECT a."Id", json_extract(l.value, '$.workOrderItemId'), 'work-order:' || a."WorkOrderId" || ':item:' || json_extract(l.value, '$.workOrderItemId')
                FROM "GeneratedFinancialDocumentAttempts" a, json_each(a."FrozenDomainJson", '$.lines') l
                WHERE a."Kind" = 'act' AND NOT EXISTS (SELECT 1 FROM "CaseDocumentUploadRetirements" r WHERE r."CaseId" = a."ClientCaseId" AND r."ActorId" = a."ActorId" AND r."UploadKey" = a."UploadKey");
                INSERT INTO "__FinancialClaimsUpgradeGuard" ("Blocked")
                SELECT 1 WHERE EXISTS (SELECT "SourceKey" FROM "GeneratedFinancialActLineClaims" GROUP BY "SourceKey" HAVING COUNT(*) > 1);
                DROP TABLE "__FinancialClaimsUpgradeGuard";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "__FinancialClaimsDowngradeGuard" ("Blocked" INTEGER NOT NULL CHECK ("Blocked" = 0));
                INSERT INTO "__FinancialClaimsDowngradeGuard" ("Blocked")
                SELECT 1 WHERE EXISTS (SELECT 1 FROM "GeneratedFinancialDocumentAttempts");
                DROP TABLE "__FinancialClaimsDowngradeGuard";
                """);
            migrationBuilder.DropTable(
                name: "GeneratedFinancialActLineClaims");

            migrationBuilder.DropTable(
                name: "GeneratedFinancialNumberClaims");

            migrationBuilder.DropTable(
                name: "GeneratedFinancialNumberScopes");

            migrationBuilder.DropColumn(
                name: "PublishedDocumentId",
                table: "GeneratedFinancialDocumentAttempts");
        }
    }
}
