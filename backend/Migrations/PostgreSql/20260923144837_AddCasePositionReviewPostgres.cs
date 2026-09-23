using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCasePositionReviewPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PositionReviewedAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PositionReviewedByName",
                table: "ClientCases",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PositionReviewedByUserId",
                table: "ClientCases",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PositionReviewedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "PositionReviewedByName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "PositionReviewedByUserId",
                table: "ClientCases");
        }
    }
}
