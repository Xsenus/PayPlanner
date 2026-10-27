using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class AddSalesRequestServiceInvoice : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "ServiceInvoiceId", table: "SalesRequests", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "ServiceInvoiceAllocationAmount", table: "SalesRequests", type: "decimal(18,2)", nullable: true);
            migrationBuilder.CreateIndex(name: "IX_SalesRequests_ServiceInvoiceId", table: "SalesRequests", column: "ServiceInvoiceId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_SalesRequests_ServiceInvoiceId", table: "SalesRequests");
            migrationBuilder.DropColumn(name: "ServiceInvoiceAllocationAmount", table: "SalesRequests");
            migrationBuilder.DropColumn(name: "ServiceInvoiceId", table: "SalesRequests");
        }
    }
}
