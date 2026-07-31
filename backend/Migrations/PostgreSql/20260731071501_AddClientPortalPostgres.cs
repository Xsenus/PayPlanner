using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientPortalPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientPortalAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AccountType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    LegalEntityId = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RevokedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPortalAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPortalAccounts_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientPortalAccounts_LegalEntities_LegalEntityId",
                        column: x => x.LegalEntityId,
                        principalTable: "LegalEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ClientPortalAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientPortalAccountId = table.Column<int>(type: "integer", nullable: true),
                    ClientPortalCaseGrantId = table.Column<int>(type: "integer", nullable: true),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPortalAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPortalAuditEvents_ClientPortalAccounts_ClientPortalAc~",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ClientPortalCaseGrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientPortalAccountId = table.Column<int>(type: "integer", nullable: false),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    CanViewCaseSummary = table.Column<bool>(type: "boolean", nullable: false),
                    CanViewDeadlines = table.Column<bool>(type: "boolean", nullable: false),
                    CanViewDocuments = table.Column<bool>(type: "boolean", nullable: false),
                    CanViewFinance = table.Column<bool>(type: "boolean", nullable: false),
                    CanViewApprovals = table.Column<bool>(type: "boolean", nullable: false),
                    CanViewMessages = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPortalCaseGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPortalCaseGrants_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClientPortalCaseGrants_ClientPortalAccounts_ClientPortalAcc~",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClientPortalLoginChallenges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientPortalAccountId = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OtpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPortalLoginChallenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPortalLoginChallenges_ClientPortalAccounts_ClientPort~",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientPortalSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientPortalAccountId = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    IpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPortalSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPortalSessions_ClientPortalAccounts_ClientPortalAccou~",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientPortalDocumentGrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientPortalCaseGrantId = table.Column<int>(type: "integer", nullable: false),
                    ClientCaseDocumentId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPortalDocumentGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPortalDocumentGrants_ClientCaseDocuments_ClientCaseDo~",
                        column: x => x.ClientCaseDocumentId,
                        principalTable: "ClientCaseDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClientPortalDocumentGrants_ClientPortalCaseGrants_ClientPor~",
                        column: x => x.ClientPortalCaseGrantId,
                        principalTable: "ClientPortalCaseGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalAccounts_ClientId",
                table: "ClientPortalAccounts",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalAccounts_Email_IsActive",
                table: "ClientPortalAccounts",
                columns: new[] { "Email", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalAccounts_LegalEntityId",
                table: "ClientPortalAccounts",
                column: "LegalEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalAccounts_PublicId",
                table: "ClientPortalAccounts",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalAuditEvents_ClientPortalAccountId_OccurredAt",
                table: "ClientPortalAuditEvents",
                columns: new[] { "ClientPortalAccountId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalAuditEvents_EventType_OccurredAt",
                table: "ClientPortalAuditEvents",
                columns: new[] { "EventType", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalCaseGrants_ClientCaseId_RevokedAt",
                table: "ClientPortalCaseGrants",
                columns: new[] { "ClientCaseId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalCaseGrants_ClientPortalAccountId_ClientCaseId",
                table: "ClientPortalCaseGrants",
                columns: new[] { "ClientPortalAccountId", "ClientCaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalCaseGrants_PublicId",
                table: "ClientPortalCaseGrants",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalDocumentGrants_ClientCaseDocumentId",
                table: "ClientPortalDocumentGrants",
                column: "ClientCaseDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalDocumentGrants_ClientPortalCaseGrantId_ClientCa~",
                table: "ClientPortalDocumentGrants",
                columns: new[] { "ClientPortalCaseGrantId", "ClientCaseDocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalDocumentGrants_PublicId",
                table: "ClientPortalDocumentGrants",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalLoginChallenges_ClientPortalAccountId_ExpiresAt",
                table: "ClientPortalLoginChallenges",
                columns: new[] { "ClientPortalAccountId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalLoginChallenges_PublicId",
                table: "ClientPortalLoginChallenges",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalSessions_ClientPortalAccountId_RevokedAt_Expire~",
                table: "ClientPortalSessions",
                columns: new[] { "ClientPortalAccountId", "RevokedAt", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalSessions_PublicId",
                table: "ClientPortalSessions",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPortalSessions_TokenHash",
                table: "ClientPortalSessions",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientPortalAuditEvents");

            migrationBuilder.DropTable(
                name: "ClientPortalDocumentGrants");

            migrationBuilder.DropTable(
                name: "ClientPortalLoginChallenges");

            migrationBuilder.DropTable(
                name: "ClientPortalSessions");

            migrationBuilder.DropTable(
                name: "ClientPortalCaseGrants");

            migrationBuilder.DropTable(
                name: "ClientPortalAccounts");
        }
    }
}
