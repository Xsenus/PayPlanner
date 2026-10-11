using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class PerfPaymentSummaryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Payments_ClientCaseId_Date_Type_Status",
                table: "Payments",
                columns: new[] { "ClientCaseId", "Date", "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ClientId_Date_Type_Status",
                table: "Payments",
                columns: new[] { "ClientId", "Date", "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Type_Status_Date",
                table: "Payments",
                columns: new[] { "Type", "Status", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_ClientCaseId_Date_Type_Status",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ClientId_Date_Type_Status",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Type_Status_Date",
                table: "Payments");
        }
    }
}
