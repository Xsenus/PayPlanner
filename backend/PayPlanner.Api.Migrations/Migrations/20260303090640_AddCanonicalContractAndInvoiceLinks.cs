using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCanonicalContractAndInvoiceLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContractId",
                table: "Payments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentKind",
                table: "Payments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "InvoiceId",
                table: "Acts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ContractId",
                table: "Payments",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_DocumentKind",
                table: "Payments",
                column: "DocumentKind");

            migrationBuilder.CreateIndex(
                name: "IX_Acts_InvoiceId",
                table: "Acts",
                column: "InvoiceId");

            migrationBuilder.AddForeignKey(
                name: "FK_Acts_Payments_InvoiceId",
                table: "Acts",
                column: "InvoiceId",
                principalTable: "Payments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Contracts_ContractId",
                table: "Payments",
                column: "ContractId",
                principalTable: "Contracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Acts_Payments_InvoiceId",
                table: "Acts");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Contracts_ContractId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ContractId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_DocumentKind",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Acts_InvoiceId",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "DocumentKind",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "InvoiceId",
                table: "Acts");
        }
    }
}
