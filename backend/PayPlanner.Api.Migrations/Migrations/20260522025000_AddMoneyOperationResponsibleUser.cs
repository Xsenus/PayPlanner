using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    [Migration("20260522025000_AddMoneyOperationResponsibleUser")]
    [DbContext(typeof(PaymentContext))]
    public partial class AddMoneyOperationResponsibleUser : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ResponsibleUserId",
                table: "MoneyOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_ResponsibleUserId",
                table: "MoneyOperations",
                column: "ResponsibleUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_MoneyOperations_Users_ResponsibleUserId",
                table: "MoneyOperations",
                column: "ResponsibleUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MoneyOperations_Users_ResponsibleUserId",
                table: "MoneyOperations");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOperations_ResponsibleUserId",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "ResponsibleUserId",
                table: "MoneyOperations");
        }
    }
}
