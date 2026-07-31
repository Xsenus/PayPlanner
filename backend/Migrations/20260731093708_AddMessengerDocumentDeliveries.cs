using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMessengerDocumentDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessengerRecipientBindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientPortalAccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProtectedAddress = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AddressHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MaskedAddress = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderRecipientId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    VerificationStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "pending"),
                    VerificationMethod = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VerifiedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessengerRecipientBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessengerRecipientBindings_ClientPortalAccounts_ClientPortalAccountId",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MessengerRecipientBindings_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MessengerRecipientBindings_Users_VerifiedByUserId",
                        column: x => x.VerifiedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MessengerDocumentDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientBindingId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: true),
                    DocumentType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    SnapshotPath = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "prepared"),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LinkTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LinkExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstOpenedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOpenedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DownloadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    MessagePreview = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessengerDocumentDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessengerDocumentDeliveries_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MessengerDocumentDeliveries_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MessengerDocumentDeliveries_MessengerRecipientBindings_RecipientBindingId",
                        column: x => x.RecipientBindingId,
                        principalTable: "MessengerRecipientBindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessengerDocumentDeliveries_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessengerDocumentDeliveries_ClientCaseId",
                table: "MessengerDocumentDeliveries",
                column: "ClientCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_MessengerDocumentDeliveries_ClientId",
                table: "MessengerDocumentDeliveries",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_MessengerDocumentDeliveries_CreatedByUserId",
                table: "MessengerDocumentDeliveries",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MessengerDocumentDeliveries_Document_Version",
                table: "MessengerDocumentDeliveries",
                columns: new[] { "DocumentType", "DocumentId", "DocumentVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_MessengerDocumentDeliveries_IdempotencyKey",
                table: "MessengerDocumentDeliveries",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessengerDocumentDeliveries_PublicId",
                table: "MessengerDocumentDeliveries",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessengerDocumentDeliveries_Recipient_CreatedAt",
                table: "MessengerDocumentDeliveries",
                columns: new[] { "RecipientBindingId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MessengerDocumentDeliveries_Status_NextAttemptAt",
                table: "MessengerDocumentDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MessengerRecipientBindings_Channel_AddressHash",
                table: "MessengerRecipientBindings",
                columns: new[] { "Channel", "AddressHash" });

            migrationBuilder.CreateIndex(
                name: "IX_MessengerRecipientBindings_Client_Active_Channel",
                table: "MessengerRecipientBindings",
                columns: new[] { "ClientId", "IsActive", "Channel" });

            migrationBuilder.CreateIndex(
                name: "IX_MessengerRecipientBindings_ClientPortalAccountId",
                table: "MessengerRecipientBindings",
                column: "ClientPortalAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MessengerRecipientBindings_VerifiedByUserId",
                table: "MessengerRecipientBindings",
                column: "VerifiedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessengerDocumentDeliveries");

            migrationBuilder.DropTable(
                name: "MessengerRecipientBindings");
        }
    }
}
