using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddPaymentJournalReadIndexesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Payments_IsDeleted_Date_Id",
                table: "Payments",
                columns: new[] { "IsDeleted", "Date", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IsDeleted_Status_Date_Id",
                table: "Payments",
                columns: new[] { "IsDeleted", "Status", "Date", "Id" },
                descending: new[] { false, false, true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_IsDeleted_Date_Id",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_IsDeleted_Status_Date_Id",
                table: "Payments");
        }
    }
}
