using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddNextHearingConfirmationPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextHearingConfirmedAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NextHearingConfirmedByName",
                table: "ClientCases",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "NextHearingConfirmedByUserId",
                table: "ClientCases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextHearingConfirmedForAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextHearingConfirmedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "NextHearingConfirmedByName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "NextHearingConfirmedByUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "NextHearingConfirmedForAt",
                table: "ClientCases");
        }
    }
}
