using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddGeneratedFinancialClaimsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PublishedDocumentId",
                table: "GeneratedFinancialDocumentAttempts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GeneratedFinancialActLineClaims",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderItemId = table.Column<int>(type: "integer", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedFinancialActLineClaims", x => new { x.AttemptId, x.WorkOrderItemId });
                    table.CheckConstraint("CK_GeneratedFinancialActLineClaims_Identity", "\"WorkOrderItemId\" > 0 AND length(\"SourceKey\") BETWEEN 1 AND 128");
                    table.ForeignKey(
                        name: "FK_GeneratedFinancialActLineClaims_GeneratedFinancialDocumentA~",
                        column: x => x.AttemptId,
                        principalTable: "GeneratedFinancialDocumentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeneratedFinancialNumberClaims",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DateKey = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedFinancialNumberClaims", x => x.AttemptId);
                    table.CheckConstraint("CK_GeneratedFinancialNumberClaims_Identity", "\"Kind\" IN ('invoice', 'act', 'contract') AND length(\"DateKey\") = 10 AND length(\"Number\") BETWEEN 1 AND 120");
                    table.ForeignKey(
                        name: "FK_GeneratedFinancialNumberClaims_GeneratedFinancialDocumentAt~",
                        column: x => x.AttemptId,
                        principalTable: "GeneratedFinancialDocumentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeneratedFinancialNumberScopes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
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
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "GeneratedFinancialDocumentAttempts" a WHERE NOT COALESCE(
                        (a."FrozenDomainJson"::jsonb->>'version')::int = 1
                        AND (a."FrozenDomainJson"::jsonb->>'caseId')::int = a."ClientCaseId"
                        AND (a."FrozenDomainJson"::jsonb->>'workOrderId')::int = a."WorkOrderId"
                        AND (a."FrozenDomainJson"::jsonb->>'actorId')::int = a."ActorId"
                        AND a."FrozenDomainJson"::jsonb->>'actorName' = a."ActorName"
                        AND (a."FrozenDomainJson"::jsonb->'identity'->>'entityId')::int = a."EntityId"
                        AND (a."FrozenDomainJson"::jsonb->'identity'->>'invoiceEntityId')::int IS NOT DISTINCT FROM a."InvoiceEntityId"
                        AND (a."FrozenDomainJson"::jsonb->>'kind')::int = CASE a."Kind" WHEN 'invoice' THEN 0 WHEN 'act' THEN 1 ELSE 2 END
                        AND length(a."FrozenDomainJson"::jsonb->>'number') BETWEEN 1 AND 120
                        AND substring(a."FrozenDomainJson"::jsonb->>'date', 1, 10) ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
                        AND jsonb_typeof(a."FrozenDomainJson"::jsonb->'lines') = 'array' AND jsonb_array_length(a."FrozenDomainJson"::jsonb->'lines') > 0
                        AND EXISTS (SELECT 1 FROM "GeneratedFinancialDocumentContents" c WHERE c."AttemptId" = a."Id" AND length(c."Bytes") = a."ContentSizeBytes"), FALSE)) THEN
                        RAISE EXCEPTION 'Accepted financial attempt is structurally invalid';
                    END IF;
                END $$;
                INSERT INTO "GeneratedFinancialNumberClaims" ("AttemptId", "Kind", "DateKey", "Number")
                SELECT a."Id", a."Kind", substring(a."FrozenDomainJson"::jsonb->>'date', 1, 10), a."FrozenDomainJson"::jsonb->>'number'
                FROM "GeneratedFinancialDocumentAttempts" a
                WHERE NOT EXISTS (SELECT 1 FROM "CaseDocumentUploadRetirements" r WHERE r."CaseId" = a."ClientCaseId" AND r."ActorId" = a."ActorId" AND r."UploadKey" = a."UploadKey");
                INSERT INTO "GeneratedFinancialActLineClaims" ("AttemptId", "WorkOrderItemId", "SourceKey")
                SELECT a."Id", (l.value->>'workOrderItemId')::int, 'work-order:' || a."WorkOrderId"::text || ':item:' || (l.value->>'workOrderItemId')::int::text
                FROM "GeneratedFinancialDocumentAttempts" a CROSS JOIN LATERAL jsonb_array_elements(a."FrozenDomainJson"::jsonb->'lines') l(value)
                WHERE a."Kind" = 'act' AND NOT EXISTS (SELECT 1 FROM "CaseDocumentUploadRetirements" r WHERE r."CaseId" = a."ClientCaseId" AND r."ActorId" = a."ActorId" AND r."UploadKey" = a."UploadKey");
                DO $$ BEGIN
                    IF EXISTS (SELECT "SourceKey" FROM "GeneratedFinancialActLineClaims" GROUP BY "SourceKey" HAVING COUNT(*) > 1) THEN
                        RAISE EXCEPTION 'Accepted financial acts contain overlapping source lines';
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "GeneratedFinancialDocumentAttempts") THEN
                        RAISE EXCEPTION 'Cannot remove financial claims while accepted attempts exist';
                    END IF;
                END $$;
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
