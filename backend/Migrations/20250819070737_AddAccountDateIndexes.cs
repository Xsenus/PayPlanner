using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountDateIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AccountDate",
                table: "Payments",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Account_AccountDate",
                table: "Payments",
                columns: new[] { "Account", "AccountDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_AccountDate",
                table: "Payments",
                column: "AccountDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_Account_AccountDate",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_AccountDate",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AccountDate",
                table: "Payments");
        }
    }
}
