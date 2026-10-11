using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddDocumentSigningWorkflowPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "InvoiceSentForSigningAt",
                table: "Payments",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InvoiceSignedAt",
                table: "Payments",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConcludedAt",
                table: "Contracts",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentForSigningAt",
                table: "Contracts",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentForSigningAt",
                table: "Acts",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedAt",
                table: "Acts",
                type: "timestamp without time zone",
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
