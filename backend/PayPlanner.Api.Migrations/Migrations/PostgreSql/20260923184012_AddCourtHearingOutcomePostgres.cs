using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCourtHearingOutcomePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HearingOutcome",
                table: "ClientCaseCourtEvents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HearingOutcomeNote",
                table: "ClientCaseCourtEvents",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HearingOutcomeRecordedAt",
                table: "ClientCaseCourtEvents",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HearingOutcomeRecordedByUserId",
                table: "ClientCaseCourtEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HearingOutcomeRecordedByUserName",
                table: "ClientCaseCourtEvents",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HearingOutcome",
                table: "ClientCaseCourtEvents");

            migrationBuilder.DropColumn(
                name: "HearingOutcomeNote",
                table: "ClientCaseCourtEvents");

            migrationBuilder.DropColumn(
                name: "HearingOutcomeRecordedAt",
                table: "ClientCaseCourtEvents");

            migrationBuilder.DropColumn(
                name: "HearingOutcomeRecordedByUserId",
                table: "ClientCaseCourtEvents");

            migrationBuilder.DropColumn(
                name: "HearingOutcomeRecordedByUserName",
                table: "ClientCaseCourtEvents");
        }
    }
}
