using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RecipientUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RecipientAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    BodyPreview = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_CreatedAt",
                table: "NotificationDeliveries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Event_Channel_User",
                table: "NotificationDeliveries",
                columns: new[] { "EventKey", "Channel", "RecipientUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_EventKey",
                table: "NotificationDeliveries",
                column: "EventKey");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_RecipientUserId",
                table: "NotificationDeliveries",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Status",
                table: "NotificationDeliveries",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDeliveries");
        }
    }
}
