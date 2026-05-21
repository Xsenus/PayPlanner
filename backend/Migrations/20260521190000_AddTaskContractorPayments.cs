using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class AddTaskContractorPayments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsExternalContractor",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ContractorPaymentAmount",
                table: "TaskJournalItems",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ContractorPaymentDueDate",
                table: "TaskJournalItems",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContractorPaymentEnabled",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ContractorPaymentId",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContractorPaymentOnResult",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "idx_users_isexternalcontractor",
                table: "Users",
                column: "IsExternalContractor");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_ContractorPaymentDueDate",
                table: "TaskJournalItems",
                column: "ContractorPaymentDueDate");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_ContractorPaymentId",
                table: "TaskJournalItems",
                column: "ContractorPaymentId");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskJournalItems_Payments_ContractorPaymentId",
                table: "TaskJournalItems",
                column: "ContractorPaymentId",
                principalTable: "Payments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskJournalItems_Payments_ContractorPaymentId",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "idx_users_isexternalcontractor",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_ContractorPaymentDueDate",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_ContractorPaymentId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "IsExternalContractor",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ContractorPaymentAmount",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ContractorPaymentDueDate",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ContractorPaymentEnabled",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ContractorPaymentId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ContractorPaymentOnResult",
                table: "TaskJournalItems");
        }
    }
}
