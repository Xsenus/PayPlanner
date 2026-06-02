using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCalendarSyncFromDatePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SyncFromDate",
                table: "YandexCalendarConnections",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncFromDate",
                table: "GoogleCalendarConnections",
                type: "timestamp without time zone",
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
