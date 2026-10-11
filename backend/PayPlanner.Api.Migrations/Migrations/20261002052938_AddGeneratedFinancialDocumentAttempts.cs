using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedFinancialDocumentAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GeneratedFinancialDocumentAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UploadKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkOrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EntityId = table.Column<int>(type: "INTEGER", nullable: false),
                    InvoiceEntityId = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FrozenDomainJson = table.Column<string>(type: "text", nullable: false),
                    FrozenDomainSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedFinancialDocumentAttempts", x => x.Id);
                    table.CheckConstraint("CK_GeneratedFinancialAttempts_Proof", "\"WorkOrderId\" > 0 AND \"ActorId\" > 0 AND \"EntityId\" > 0 AND \"Kind\" IN ('invoice', 'act', 'contract') AND ((\"Kind\" = 'invoice' AND \"InvoiceEntityId\" IS NOT NULL AND \"InvoiceEntityId\" > 0) OR (\"Kind\" <> 'invoice' AND \"InvoiceEntityId\" IS NULL)) AND length(\"RequestSha256\") = 64 AND length(\"FrozenDomainSha256\") = 64 AND length(\"ContentSha256\") = 64 AND length(\"FrozenDomainJson\") BETWEEN 1 AND 16777216 AND \"ContentSizeBytes\" BETWEEN 1 AND 26214400");
                    table.ForeignKey(
                        name: "FK_GeneratedFinancialDocumentAttempts_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeneratedFinancialDocumentContents",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedFinancialDocumentContents", x => x.AttemptId);
                    table.CheckConstraint("CK_GeneratedFinancialContents_Bytes", "length(\"Bytes\") BETWEEN 1 AND 26214400");
                    table.ForeignKey(
                        name: "FK_GeneratedFinancialDocumentContents_GeneratedFinancialDocumentAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "GeneratedFinancialDocumentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedFinancialDocumentAttempts_ClientCaseId_ActorId_UploadKey",
                table: "GeneratedFinancialDocumentAttempts",
                columns: new[] { "ClientCaseId", "ActorId", "UploadKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "__FinancialAttemptDowngradeGuard" ("Blocked" INTEGER NOT NULL CHECK ("Blocked" = 0));
                INSERT INTO "__FinancialAttemptDowngradeGuard" ("Blocked")
                SELECT 1 WHERE EXISTS (SELECT 1 FROM "GeneratedFinancialDocumentAttempts")
                    OR EXISTS (SELECT 1 FROM "GeneratedFinancialDocumentContents");
                DROP TABLE "__FinancialAttemptDowngradeGuard";
                """);
            migrationBuilder.DropTable(
                name: "GeneratedFinancialDocumentContents");

            migrationBuilder.DropTable(
                name: "GeneratedFinancialDocumentAttempts");
        }
    }
}
