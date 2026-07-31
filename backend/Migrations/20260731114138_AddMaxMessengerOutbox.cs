using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMaxMessengerOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaxOutboxMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Scenario = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RecipientLinkId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipientUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProtectedText = table.Column<string>(type: "TEXT", maxLength: 12000, nullable: false),
                    ProtectedActionUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ActionText = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BodyPreview = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaxOutboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaxOutboxMessages_MaxRecipientLinks_RecipientLinkId",
                        column: x => x.RecipientLinkId,
                        principalTable: "MaxRecipientLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaxOutboxMessages_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaxOutboxMessages_IdempotencyKey",
                table: "MaxOutboxMessages",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaxOutboxMessages_PublicId",
                table: "MaxOutboxMessages",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaxOutboxMessages_RecipientLinkId_CreatedAt",
                table: "MaxOutboxMessages",
                columns: new[] { "RecipientLinkId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MaxOutboxMessages_RecipientUserId",
                table: "MaxOutboxMessages",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MaxOutboxMessages_Status_NextAttemptAt",
                table: "MaxOutboxMessages",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaxOutboxMessages");
        }
    }
}
