using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSigningWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "InvoiceSentForSigningAt",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InvoiceSignedAt",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConcludedAt",
                table: "Contracts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentForSigningAt",
                table: "Contracts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentForSigningAt",
                table: "Acts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedAt",
                table: "Acts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoiceSentForSigningAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "InvoiceSignedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ConcludedAt",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SentForSigningAt",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SentForSigningAt",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "SignedAt",
                table: "Acts");
        }
    }
}
