using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarSyncFromDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SyncFromDate",
                table: "YandexCalendarConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncFromDate",
                table: "GoogleCalendarConnections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncFromDate",
                table: "YandexCalendarConnections");

            migrationBuilder.DropColumn(
                name: "SyncFromDate",
                table: "GoogleCalendarConnections");
        }
    }
}
