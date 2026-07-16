using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCalendarSyncLogCreatedAtIndexesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarSyncLogs_CreatedAt",
                table: "YandexCalendarSyncLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncLogs_CreatedAt",
                table: "GoogleCalendarSyncLogs",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_YandexCalendarSyncLogs_CreatedAt",
                table: "YandexCalendarSyncLogs");

            migrationBuilder.DropIndex(
                name: "IX_GoogleCalendarSyncLogs_CreatedAt",
                table: "GoogleCalendarSyncLogs");
        }
    }
}
