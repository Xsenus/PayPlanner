using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientCaseHiddenAccessFieldsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessRequestReason",
                table: "ClientCases",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessRequestedAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccessRequestedByUserId",
                table: "ClientCases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessRequestedByUserName",
                table: "ClientCases",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "HiddenFromExecutorAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HiddenFromExecutorByUserId",
                table: "ClientCases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HiddenFromExecutorByUserName",
                table: "ClientCases",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsHiddenFromExecutor",
                table: "ClientCases",
                type: "boolean",
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
