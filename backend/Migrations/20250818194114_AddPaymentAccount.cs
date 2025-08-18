using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Account",
                table: "Payments",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Account",
                table: "Payments",
                column: "Account");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_Account",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Account",
                table: "Payments");
        }
    }
}
