using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientPortal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientPortalAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AccountType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    LegalEntityId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RevokedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RevocationReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientPortalAccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientPortalCaseGrantId = table.Column<int>(type: "INTEGER", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IpHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPortalAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPortalAuditEvents_ClientPortalAccounts_ClientPortalAccountId",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ClientPortalCaseGrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientPortalAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    CanViewCaseSummary = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanViewDeadlines = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanViewDocuments = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanViewFinance = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanViewApprovals = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanViewMessages = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RevocationReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
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
                        name: "FK_ClientPortalCaseGrants_ClientPortalAccounts_ClientPortalAccountId",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClientPortalLoginChallenges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientPortalAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OtpHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPortalLoginChallenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPortalLoginChallenges_ClientPortalAccounts_ClientPortalAccountId",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientPortalSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientPortalAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IpHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPortalSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPortalSessions_ClientPortalAccounts_ClientPortalAccountId",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientPortalDocumentGrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientPortalCaseGrantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientCaseDocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPortalDocumentGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPortalDocumentGrants_ClientCaseDocuments_ClientCaseDocumentId",
                        column: x => x.ClientCaseDocumentId,
                        principalTable: "ClientCaseDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClientPortalDocumentGrants_ClientPortalCaseGrants_ClientPortalCaseGrantId",
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
                name: "IX_ClientPortalDocumentGrants_ClientPortalCaseGrantId_ClientCaseDocumentId",
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
                name: "IX_ClientPortalSessions_ClientPortalAccountId_RevokedAt_ExpiresAt",
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
