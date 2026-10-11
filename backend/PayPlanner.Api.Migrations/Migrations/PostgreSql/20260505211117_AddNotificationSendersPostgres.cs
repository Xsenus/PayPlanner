using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddNotificationSendersPostgres : Migration
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
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderName",
                table: "NotificationDeliveries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "NotificationSenders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PurposesJson = table.Column<string>(type: "text", nullable: false),
                    ConfigJson = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
                name: "FK_NotificationDeliveries_NotificationSenders_NotificationSend~",
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
                name: "FK_NotificationDeliveries_NotificationSenders_NotificationSend~",
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
