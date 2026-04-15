using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceLineItemServiceReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ServiceId",
                table: "InvoiceLineItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLineItems_ServiceId",
                table: "InvoiceLineItems",
                column: "ServiceId");

            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceLineItems_Services_ServiceId",
                table: "InvoiceLineItems",
                column: "ServiceId",
                principalTable: "Services",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceLineItems_Services_ServiceId",
                table: "InvoiceLineItems");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLineItems_ServiceId",
                table: "InvoiceLineItems");

            migrationBuilder.DropColumn(
                name: "ServiceId",
                table: "InvoiceLineItems");
        }
    }
}
