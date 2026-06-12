using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentAdvanceReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdvanceReviewReason",
                table: "Payments",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdvanceReviewStatus",
                table: "Payments",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTime>(
                name: "AdvanceReviewedAt",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdvanceReviewedByUserId",
                table: "Payments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdvanceReviewedByUserName",
                table: "Payments",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_AdvanceReviewStatus",
                table: "Payments",
                column: "AdvanceReviewStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_AdvanceReviewStatus",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AdvanceReviewReason",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AdvanceReviewStatus",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AdvanceReviewedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AdvanceReviewedByUserId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AdvanceReviewedByUserName",
                table: "Payments");
        }
    }
}
