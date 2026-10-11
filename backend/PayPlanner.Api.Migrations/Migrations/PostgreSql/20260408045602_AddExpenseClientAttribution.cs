using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddExpenseClientAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpenseClientId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ExpenseClientId",
                table: "Payments",
                column: "ExpenseClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ExpenseClientId_Date",
                table: "Payments",
                columns: new[] { "ExpenseClientId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ExpenseClientId_Date_Type_Status",
                table: "Payments",
                columns: new[] { "ExpenseClientId", "Date", "Type", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Clients_ExpenseClientId",
                table: "Payments",
                column: "ExpenseClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Clients_ExpenseClientId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ExpenseClientId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ExpenseClientId_Date",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ExpenseClientId_Date_Type_Status",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ExpenseClientId",
                table: "Payments");
        }
    }
}
