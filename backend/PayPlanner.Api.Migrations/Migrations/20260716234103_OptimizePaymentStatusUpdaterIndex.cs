using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class OptimizePaymentStatusUpdaterIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_IsPaid_Status",
                table: "Payments");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IsPaid_Status_Date",
                table: "Payments",
                columns: new[] { "IsPaid", "Status", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_IsPaid_Status_Date",
                table: "Payments");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IsPaid_Status",
                table: "Payments",
                columns: new[] { "IsPaid", "Status" });
        }
    }
}
