using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddMaxMessengerOutboxPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaxOutboxMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scenario = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecipientLinkId = table.Column<int>(type: "integer", nullable: false),
                    RecipientUserId = table.Column<int>(type: "integer", nullable: true),
                    ProtectedText = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    ProtectedActionUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ActionText = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BodyPreview = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
