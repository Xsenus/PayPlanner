using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    [DbContext(typeof(PostgreSqlPaymentContext))]
    [Migration("20260602053100_AddCalendarSyncFromDatePostgres")]
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
