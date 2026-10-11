using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentWorkflowStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvoiceWorkflowStatus",
                table: "Payments",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AlterColumn<DateTime>(
                name: "TerminationDate",
                table: "Contracts",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "Contracts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledReason",
                table: "Contracts",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Contracts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<string>(
                name: "TerminationFinancialTerms",
                table: "Contracts",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TerminationReason",
                table: "Contracts",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TerminationTerms",
                table: "Contracts",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowStatus",
                table: "Acts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.Sql("""
                UPDATE Payments
                SET InvoiceWorkflowStatus = CASE
                    WHEN PaidAmount > 0 AND PaidAmount < Amount THEN 'PartiallyPaid'
                    WHEN Status = 'Completed' THEN 'Paid'
                    WHEN Status = 'Overdue' THEN 'Overdue'
                    WHEN Status = 'Cancelled' THEN 'Cancelled'
                    WHEN Status = 'Processing' THEN 'OnSigning'
                    ELSE 'Draft'
                END
                WHERE DocumentKind = 'Invoice'
                """);

            migrationBuilder.Sql("""
                UPDATE Acts
                SET WorkflowStatus = CASE
                    WHEN Status = 'Transferred' THEN 'OnSigning'
                    WHEN Status = 'Signed' THEN 'Signed'
                    WHEN Status = 'Terminated' THEN 'Cancelled'
                    ELSE 'Draft'
                END
                """);

            migrationBuilder.Sql("""
                UPDATE Contracts
                SET Status = CASE
                    WHEN TerminationDate IS NOT NULL THEN 'Terminated'
                    ELSE 'Draft'
                END
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_InvoiceWorkflowStatus",
                table: "Payments",
                column: "InvoiceWorkflowStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_Status",
                table: "Contracts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Acts_WorkflowStatus",
                table: "Acts",
                column: "WorkflowStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_InvoiceWorkflowStatus",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_Status",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Acts_WorkflowStatus",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "InvoiceWorkflowStatus",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "CancelledReason",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "TerminationFinancialTerms",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "TerminationReason",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "TerminationTerms",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "WorkflowStatus",
                table: "Acts");

            migrationBuilder.AlterColumn<DateTime>(
                name: "TerminationDate",
                table: "Contracts",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "date",
                oldNullable: true);
        }
    }
}
