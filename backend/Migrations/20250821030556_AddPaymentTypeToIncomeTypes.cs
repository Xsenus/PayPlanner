using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTypeToIncomeTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PaymentType",
                table: "IncomeTypes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTypes_IsActive_PaymentType",
                table: "IncomeTypes",
                columns: new[] { "IsActive", "PaymentType" });

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTypes_IsActive_PaymentType_Name",
                table: "IncomeTypes",
                columns: new[] { "IsActive", "PaymentType", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IncomeTypes_IsActive_PaymentType",
                table: "IncomeTypes");

            migrationBuilder.DropIndex(
                name: "IX_IncomeTypes_IsActive_PaymentType_Name",
                table: "IncomeTypes");

            migrationBuilder.DropColumn(
                name: "PaymentType",
                table: "IncomeTypes");
        }
    }
}
