using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCaseHiddenAccessFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessRequestReason",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessRequestedAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccessRequestedByUserId",
                table: "ClientCases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessRequestedByUserName",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "HiddenFromExecutorAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HiddenFromExecutorByUserId",
                table: "ClientCases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HiddenFromExecutorByUserName",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsHiddenFromExecutor",
                table: "ClientCases",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_AccessRequestedAt",
                table: "ClientCases",
                column: "AccessRequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_IsHiddenFromExecutor",
                table: "ClientCases",
                column: "IsHiddenFromExecutor");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientCases_AccessRequestedAt",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_IsHiddenFromExecutor",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "AccessRequestReason",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "AccessRequestedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "AccessRequestedByUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "AccessRequestedByUserName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "HiddenFromExecutorAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "HiddenFromExecutorByUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "HiddenFromExecutorByUserName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "IsHiddenFromExecutor",
                table: "ClientCases");
        }
    }
}
