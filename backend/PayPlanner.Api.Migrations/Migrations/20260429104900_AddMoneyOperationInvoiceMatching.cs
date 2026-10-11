using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMoneyOperationInvoiceMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LinkedInvoiceId",
                table: "MoneyOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchComment",
                table: "MoneyOperations",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchStatus",
                table: "MoneyOperations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "MatchedAt",
                table: "MoneyOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_LinkedInvoiceId",
                table: "MoneyOperations",
                column: "LinkedInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_MatchStatus",
                table: "MoneyOperations",
                column: "MatchStatus");

            migrationBuilder.AddForeignKey(
                name: "FK_MoneyOperations_Invoices_LinkedInvoiceId",
                table: "MoneyOperations",
                column: "LinkedInvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MoneyOperations_Invoices_LinkedInvoiceId",
                table: "MoneyOperations");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOperations_LinkedInvoiceId",
                table: "MoneyOperations");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOperations_MatchStatus",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "LinkedInvoiceId",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "MatchComment",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "MatchStatus",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "MatchedAt",
                table: "MoneyOperations");
        }
    }
}
