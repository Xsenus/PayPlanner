using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCashOrderReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CashOrders_Date",
                table: "CashOrders");

            migrationBuilder.DropIndex(
                name: "IX_CashOrders_Status",
                table: "CashOrders");

            migrationBuilder.DropIndex(
                name: "IX_CashOrders_Type",
                table: "CashOrders");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Account_Date_Id",
                table: "CashOrders",
                columns: new[] { "Account", "Date", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Date_Id",
                table: "CashOrders",
                columns: new[] { "Date", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Status_Date_Id",
                table: "CashOrders",
                columns: new[] { "Status", "Date", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Type_Date_Id",
                table: "CashOrders",
                columns: new[] { "Type", "Date", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CashOrders_Account_Date_Id",
                table: "CashOrders");

            migrationBuilder.DropIndex(
                name: "IX_CashOrders_Date_Id",
                table: "CashOrders");

            migrationBuilder.DropIndex(
                name: "IX_CashOrders_Status_Date_Id",
                table: "CashOrders");

            migrationBuilder.DropIndex(
                name: "IX_CashOrders_Type_Date_Id",
                table: "CashOrders");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Date",
                table: "CashOrders",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Status",
                table: "CashOrders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CashOrders_Type",
                table: "CashOrders",
                column: "Type");
        }
    }
}
