using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCourtHearingPreviousSchedulePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PreviousAllDay",
                table: "ClientCaseCourtEvents",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PreviousScheduledAt",
                table: "ClientCaseCourtEvents",
                type: "timestamp without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousAllDay",
                table: "ClientCaseCourtEvents");

            migrationBuilder.DropColumn(
                name: "PreviousScheduledAt",
                table: "ClientCaseCourtEvents");
        }
    }
}
