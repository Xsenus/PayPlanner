using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCaseRegistryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaseNumber",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CaseType",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CourtCaseNumber",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CourtName",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CourtUrl",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "DueDate",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FeeAmount",
                table: "ClientCases",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextHearingAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "normal");

            migrationBuilder.AddColumn<int>(
                name: "ResponsibleUserId",
                table: "ClientCases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "StartDate",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_CaseNumber",
                table: "ClientCases",
                column: "CaseNumber");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_CaseType",
                table: "ClientCases",
                column: "CaseType");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_CourtName",
                table: "ClientCases",
                column: "CourtName");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_DueDate",
                table: "ClientCases",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_Priority",
                table: "ClientCases",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_ResponsibleUserId",
                table: "ClientCases",
                column: "ResponsibleUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientCases_Users_ResponsibleUserId",
                table: "ClientCases",
                column: "ResponsibleUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientCases_Users_ResponsibleUserId",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_CaseNumber",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_CaseType",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_CourtName",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_DueDate",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_Priority",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_ResponsibleUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CaseNumber",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CaseType",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtCaseNumber",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtUrl",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "FeeAmount",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "NextHearingAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ResponsibleUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "ClientCases");
        }
    }
}
