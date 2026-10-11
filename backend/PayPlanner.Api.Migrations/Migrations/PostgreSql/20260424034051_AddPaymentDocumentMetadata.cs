using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddPaymentDocumentMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentDocumentDate",
                table: "Payments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentOrderNumber",
                table: "Payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaymentDocumentDate",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PaymentOrderNumber",
                table: "Payments");
        }
    }
}
