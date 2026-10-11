using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCasePositionReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PositionReviewedAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PositionReviewedByName",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PositionReviewedByUserId",
                table: "ClientCases",
                type: "INTEGER",
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
