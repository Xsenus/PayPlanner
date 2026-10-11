using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMaxMessengerLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaxInboundEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdateType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MaxUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    MaxChatId = table.Column<long>(type: "INTEGER", nullable: true),
                    ExternalMessageId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CallbackId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProcessingStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaxInboundEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaxRecipientLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientPortalAccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    MaxChatId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LinkTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LinkTokenExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OptedOutAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaxRecipientLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaxRecipientLinks_ClientPortalAccounts_ClientPortalAccountId",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaxRecipientLinks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundEvents_ExpiresAt",
                table: "MaxInboundEvents",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundEvents_Fingerprint",
                table: "MaxInboundEvents",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundEvents_MaxUserId_ReceivedAt",
                table: "MaxInboundEvents",
                columns: new[] { "MaxUserId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MaxRecipientLinks_ClientPortalAccountId_Status",
                table: "MaxRecipientLinks",
                columns: new[] { "ClientPortalAccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MaxRecipientLinks_LinkTokenExpiresAt",
                table: "MaxRecipientLinks",
                column: "LinkTokenExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_MaxRecipientLinks_LinkTokenHash",
                table: "MaxRecipientLinks",
                column: "LinkTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaxRecipientLinks_MaxUserId_Status",
                table: "MaxRecipientLinks",
                columns: new[] { "MaxUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MaxRecipientLinks_PublicId",
                table: "MaxRecipientLinks",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaxRecipientLinks_UserId_Status",
                table: "MaxRecipientLinks",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaxInboundEvents");

            migrationBuilder.DropTable(
                name: "MaxRecipientLinks");
        }
    }
}
