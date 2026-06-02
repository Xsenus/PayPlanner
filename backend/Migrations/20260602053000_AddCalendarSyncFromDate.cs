using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(PaymentContext))]
    [Migration("20260602053000_AddCalendarSyncFromDate")]
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
