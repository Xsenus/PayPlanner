using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddNotificationBotSubscriptionsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationBotSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NotificationSenderId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    TelegramUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationBotSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationBotSubscriptions_NotificationSenders_Notificati~",
                        column: x => x.NotificationSenderId,
                        principalTable: "NotificationSenders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationBotSubscriptions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationBotSubscriptions_ChatId",
                table: "NotificationBotSubscriptions",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationBotSubscriptions_Sender_TelegramUser",
                table: "NotificationBotSubscriptions",
                columns: new[] { "NotificationSenderId", "TelegramUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationBotSubscriptions_Sender_User",
                table: "NotificationBotSubscriptions",
                columns: new[] { "NotificationSenderId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationBotSubscriptions_UserId",
                table: "NotificationBotSubscriptions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationBotSubscriptions");
        }
    }
}
