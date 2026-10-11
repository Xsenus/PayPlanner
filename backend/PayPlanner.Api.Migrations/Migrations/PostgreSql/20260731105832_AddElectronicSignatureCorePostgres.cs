using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddElectronicSignatureCorePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ElectronicSignatureEnvelopes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DocumentId = table.Column<int>(type: "integer", nullable: false),
                    DocumentVersion = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: true),
                    SignatureType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SigningOrder = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderEnvelopeId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    SnapshotPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArchiveRetentionYears = table.Column<int>(type: "integer", nullable: false),
                    RequiresMachineReadablePowerOfAttorney = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicSignatureEnvelopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElectronicSignatureEnvelopes_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ElectronicSignatureEnvelopes_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ElectronicSignatureEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnvelopeId = table.Column<int>(type: "integer", nullable: false),
                    ProviderEventId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicSignatureEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElectronicSignatureEvents_ElectronicSignatureEnvelopes_Enve~",
                        column: x => x.EnvelopeId,
                        principalTable: "ElectronicSignatureEnvelopes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ElectronicSignatureSigners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnvelopeId = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    PartyType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClientPortalAccountId = table.Column<int>(type: "integer", nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProtectedContact = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MaskedContact = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProviderSignerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequiresMachineReadablePowerOfAttorney = table.Column<bool>(type: "boolean", nullable: false),
                    PowerOfAttorneyReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CertificateThumbprint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CertificateSubject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CertificateIssuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CertificateValidFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CertificateValidTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CertificateRevocationStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InvitedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    SignedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeclinedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeclineReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicSignatureSigners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElectronicSignatureSigners_ClientPortalAccounts_ClientPorta~",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ElectronicSignatureSigners_ElectronicSignatureEnvelopes_Env~",
                        column: x => x.EnvelopeId,
                        principalTable: "ElectronicSignatureEnvelopes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ElectronicSignatureArtifacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnvelopeId = table.Column<int>(type: "integer", nullable: false),
                    SignerId = table.Column<int>(type: "integer", nullable: true),
                    ArtifactType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VerificationStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    VerificationDetails = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicSignatureArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElectronicSignatureArtifacts_ElectronicSignatureEnvelopes_E~",
                        column: x => x.EnvelopeId,
                        principalTable: "ElectronicSignatureEnvelopes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ElectronicSignatureArtifacts_ElectronicSignatureSigners_Sig~",
                        column: x => x.SignerId,
                        principalTable: "ElectronicSignatureSigners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureArtifacts_ChecksumSha256",
                table: "ElectronicSignatureArtifacts",
                column: "ChecksumSha256");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureArtifacts_EnvelopeId_ArtifactType",
                table: "ElectronicSignatureArtifacts",
                columns: new[] { "EnvelopeId", "ArtifactType" });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureArtifacts_SignerId",
                table: "ElectronicSignatureArtifacts",
                column: "SignerId");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureEnvelopes_ClientId",
                table: "ElectronicSignatureEnvelopes",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureEnvelopes_CreatedByUserId",
                table: "ElectronicSignatureEnvelopes",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureEnvelopes_DocumentType_DocumentId_Docume~",
                table: "ElectronicSignatureEnvelopes",
                columns: new[] { "DocumentType", "DocumentId", "DocumentVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureEnvelopes_IdempotencyKey",
                table: "ElectronicSignatureEnvelopes",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureEnvelopes_Provider_ProviderEnvelopeId",
                table: "ElectronicSignatureEnvelopes",
                columns: new[] { "Provider", "ProviderEnvelopeId" },
                unique: true,
                filter: "\"ProviderEnvelopeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureEnvelopes_PublicId",
                table: "ElectronicSignatureEnvelopes",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureEvents_EnvelopeId_OccurredAt",
                table: "ElectronicSignatureEvents",
                columns: new[] { "EnvelopeId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureEvents_EnvelopeId_ProviderEventId",
                table: "ElectronicSignatureEvents",
                columns: new[] { "EnvelopeId", "ProviderEventId" },
                unique: true,
                filter: "\"ProviderEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureSigners_ClientPortalAccountId",
                table: "ElectronicSignatureSigners",
                column: "ClientPortalAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureSigners_EnvelopeId_Order",
                table: "ElectronicSignatureSigners",
                columns: new[] { "EnvelopeId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicSignatureSigners_EnvelopeId_Status",
                table: "ElectronicSignatureSigners",
                columns: new[] { "EnvelopeId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElectronicSignatureArtifacts");

            migrationBuilder.DropTable(
                name: "ElectronicSignatureEvents");

            migrationBuilder.DropTable(
                name: "ElectronicSignatureSigners");

            migrationBuilder.DropTable(
                name: "ElectronicSignatureEnvelopes");
        }
    }
}
