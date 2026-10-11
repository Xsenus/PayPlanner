using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddManualControlFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "VatRate",
                table: "MoneyOperations",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,2)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "InvoiceLineItems",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "ClientCaseWorkOrderItems",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AddColumn<DateTime>(
                name: "ControlMarkedAt",
                table: "TaskJournalItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ControlMarkedByUserId",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlMarkedByUserName",
                table: "TaskJournalItems",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsUnderControl",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ControlMarkedAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ControlMarkedByUserId",
                table: "ClientCases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlMarkedByUserName",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsUnderControl",
                table: "ClientCases",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_ControlMarkedByUserId",
                table: "TaskJournalItems",
                column: "ControlMarkedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_IsUnderControl",
                table: "TaskJournalItems",
                column: "IsUnderControl");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_ControlMarkedByUserId",
                table: "ClientCases",
                column: "ControlMarkedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_IsUnderControl",
                table: "ClientCases",
                column: "IsUnderControl");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientCases_Users_ControlMarkedByUserId",
                table: "ClientCases",
                column: "ControlMarkedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TaskJournalItems_Users_ControlMarkedByUserId",
                table: "TaskJournalItems",
                column: "ControlMarkedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientCases_Users_ControlMarkedByUserId",
                table: "ClientCases");

            migrationBuilder.DropForeignKey(
                name: "FK_TaskJournalItems_Users_ControlMarkedByUserId",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_ControlMarkedByUserId",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_IsUnderControl",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_ControlMarkedByUserId",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_IsUnderControl",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ControlMarkedAt",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ControlMarkedByUserId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ControlMarkedByUserName",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "IsUnderControl",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ControlMarkedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ControlMarkedByUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ControlMarkedByUserName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "IsUnderControl",
                table: "ClientCases");

            migrationBuilder.AlterColumn<decimal>(
                name: "VatRate",
                table: "MoneyOperations",
                type: "decimal(5,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,4)",
                oldPrecision: 18,
                oldScale: 4,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "InvoiceLineItems",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "ClientCaseWorkOrderItems",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,4)",
                oldPrecision: 18,
                oldScale: 4);
        }
    }
}
