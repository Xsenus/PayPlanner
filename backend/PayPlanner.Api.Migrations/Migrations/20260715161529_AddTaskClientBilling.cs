using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskClientBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BillingContractId",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BillingDecisionAt",
                table: "TaskJournalItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BillingInvoiceId",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingModeSnapshot",
                table: "TaskJournalItems",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientBillingClassification",
                table: "TaskJournalItems",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_BillingContractId",
                table: "TaskJournalItems",
                column: "BillingContractId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_BillingInvoiceId",
                table: "TaskJournalItems",
                column: "BillingInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_ClientBillingClassification",
                table: "TaskJournalItems",
                column: "ClientBillingClassification");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskJournalItems_Contracts_BillingContractId",
                table: "TaskJournalItems",
                column: "BillingContractId",
                principalTable: "Contracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TaskJournalItems_Payments_BillingInvoiceId",
                table: "TaskJournalItems",
                column: "BillingInvoiceId",
                principalTable: "Payments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskJournalItems_Contracts_BillingContractId",
                table: "TaskJournalItems");

            migrationBuilder.DropForeignKey(
                name: "FK_TaskJournalItems_Payments_BillingInvoiceId",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_BillingContractId",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_BillingInvoiceId",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_ClientBillingClassification",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "BillingContractId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "BillingDecisionAt",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "BillingInvoiceId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "BillingModeSnapshot",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ClientBillingClassification",
                table: "TaskJournalItems");
        }
    }
}
