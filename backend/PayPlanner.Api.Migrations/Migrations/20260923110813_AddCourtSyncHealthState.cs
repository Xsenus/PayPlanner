using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCourtSyncHealthState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CourtSyncFailureStartedAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CourtSyncLastAttemptAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CourtSyncLastError",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "CourtSyncLastSuccessAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CourtSyncMonitoringEnabled",
                table: "ClientCases",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_CourtSyncMonitoringEnabled_FailureStartedAt",
                table: "ClientCases",
                columns: new[] { "CourtSyncMonitoringEnabled", "CourtSyncFailureStartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientCases_CourtSyncMonitoringEnabled_FailureStartedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtSyncFailureStartedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtSyncLastAttemptAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtSyncLastError",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtSyncLastSuccessAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtSyncMonitoringEnabled",
                table: "ClientCases");
        }
    }
}
