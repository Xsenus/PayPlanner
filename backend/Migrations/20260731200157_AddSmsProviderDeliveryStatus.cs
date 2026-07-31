using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSmsProviderDeliveryStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                table: "NotificationDeliveries",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderMessageId",
                table: "NotificationDeliveries",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderStatus",
                table: "NotificationDeliveries",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderStatusCheckedAt",
                table: "NotificationDeliveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Channel_Status_ProviderChecked",
                table: "NotificationDeliveries",
                columns: new[] { "Channel", "Status", "ProviderStatusCheckedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_Provider_Message",
                table: "NotificationDeliveries",
                columns: new[] { "ProviderKey", "ProviderMessageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationDeliveries_Channel_Status_ProviderChecked",
                table: "NotificationDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_NotificationDeliveries_Provider_Message",
                table: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "ProviderKey",
                table: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "ProviderMessageId",
                table: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "ProviderStatus",
                table: "NotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "ProviderStatusCheckedAt",
                table: "NotificationDeliveries");
        }
    }
}
