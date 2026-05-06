using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationSenders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationDeliveries_Event_Channel_User",
                table: "NotificationDeliveries");

            migrationBuilder.AddColumn<int>(
                name: "NotificationSenderId",
                table: "NotificationDeliveries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderName",
                table: "NotificationDeliveries",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "NotificationSenders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PurposesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSenders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Event_Channel_User_Sender",
                table: "NotificationDeliveries",
                columns: new[] { "EventKey", "Channel", "RecipientUserId", "NotificationSenderId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_NotificationSenderId",
                table: "NotificationDeliveries",
                column: "NotificationSenderId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSenders_Channel",
                table: "NotificationSenders",
                column: "Channel");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSenders_Channel_Enabled_Order",
                table: "NotificationSenders",
                columns: new[] { "Channel", "IsEnabled", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationDeliveries_NotificationSenders_NotificationSenderId",
                table: "NotificationDeliveries",
                column: "NotificationSenderId",
                principalTable: "NotificationSenders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotificationDeliveries_NotificationSenders_NotificationSenderId",
                table: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "NotificationSenders");

            migrationBuilder.DropIndex(
                name: "IX_NotificationDeliveries_Event_Channel_User_Sender",
                table: "NotificationDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_NotificationDeliveries_NotificationSenderId",
                table: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "NotificationSenderId",
                table: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "SenderName",
                table: "NotificationDeliveries");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Event_Channel_User",
                table: "NotificationDeliveries",
                columns: new[] { "EventKey", "Channel", "RecipientUserId" });
        }
    }
}
