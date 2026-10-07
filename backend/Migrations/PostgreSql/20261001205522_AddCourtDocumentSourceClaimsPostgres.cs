using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCourtDocumentSourceClaimsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CourtDocumentSourceScopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    BindingUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    BindingSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    InputSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastAcceptedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastActorId = table.Column<int>(type: "integer", nullable: false),
                    LastActorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourtDocumentSourceScopes", x => x.Id);
                    table.UniqueConstraint("AK_CourtDocumentSourceScopes_Id_ClientCaseId", x => new { x.Id, x.ClientCaseId });
                    table.CheckConstraint("CK_CourtSourceScopes_Proof", "\"Version\" > 0 AND \"LastActorId\" > 0 AND length(\"BindingSha256\") = 64 AND length(\"InputSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_CourtDocumentSourceScopes_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CourtDocumentSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    DocumentKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AcceptedScopeVersion = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentAttemptVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourtDocumentSources", x => x.Id);
                    table.CheckConstraint("CK_CourtSources_Identity", "\"AcceptedScopeVersion\" > 0 AND \"CurrentAttemptVersion\" > 0 AND length(\"DocumentKey\") = 42 AND \"DocumentKey\" LIKE 'sudrf-act-%'");
                    table.ForeignKey(
                        name: "FK_CourtDocumentSources_CourtDocumentSourceScopes_ScopeId_Clie~",
                        columns: x => new { x.ScopeId, x.ClientCaseId },
                        principalTable: "CourtDocumentSourceScopes",
                        principalColumns: new[] { "Id", "ClientCaseId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CourtDocumentSourceAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<int>(type: "integer", nullable: false),
                    ActorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FrozenSnapshotJson = table.Column<string>(type: "text", nullable: false),
                    FrozenSnapshotSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourtDocumentSourceAttempts", x => x.Id);
                    table.CheckConstraint("CK_CourtSourceAttempts_Proof", "\"Version\" > 0 AND \"ActorId\" > 0 AND length(\"FrozenSnapshotJson\") BETWEEN 1 AND 48000 AND length(\"FrozenSnapshotSha256\") = 64 AND length(\"RequestSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_CourtDocumentSourceAttempts_CourtDocumentSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "CourtDocumentSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CourtDocumentSourceAttempts_SourceId_Version",
                table: "CourtDocumentSourceAttempts",
                columns: new[] { "SourceId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CourtDocumentSources_ClientCaseId_DocumentKey",
                table: "CourtDocumentSources",
                columns: new[] { "ClientCaseId", "DocumentKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CourtDocumentSources_ScopeId_ClientCaseId",
                table: "CourtDocumentSources",
                columns: new[] { "ScopeId", "ClientCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_CourtDocumentSourceScopes_ClientCaseId_BindingSha256",
                table: "CourtDocumentSourceScopes",
                columns: new[] { "ClientCaseId", "BindingSha256" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "CourtDocumentSourceScopes") OR EXISTS (SELECT 1 FROM "CourtDocumentSources")
                        OR EXISTS (SELECT 1 FROM "CourtDocumentSourceAttempts") THEN
                        RAISE EXCEPTION 'Court source claims cannot be discarded' USING ERRCODE = '23514';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "CourtDocumentSourceAttempts");

            migrationBuilder.DropTable(
                name: "CourtDocumentSources");

            migrationBuilder.DropTable(
                name: "CourtDocumentSourceScopes");
        }
    }
}
