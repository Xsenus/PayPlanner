using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationBotSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationBotSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NotificationSenderId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    TelegramUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationBotSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationBotSubscriptions_NotificationSenders_NotificationSenderId",
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
