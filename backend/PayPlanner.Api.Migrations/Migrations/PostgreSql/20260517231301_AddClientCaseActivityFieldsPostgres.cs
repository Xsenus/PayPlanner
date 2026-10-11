using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientCaseActivityFieldsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivityConfirmedAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActivityConfirmedByUserId",
                table: "ClientCases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivityConfirmedByUserName",
                table: "ClientCases",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "InactivityRemindersEnabled",
                table: "ClientCases",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastViewedAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastViewedByUserId",
                table: "ClientCases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastViewedByUserName",
                table: "ClientCases",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_InactivityRemindersEnabled",
                table: "ClientCases",
                column: "InactivityRemindersEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_LastViewedAt",
                table: "ClientCases",
                column: "LastViewedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientCases_InactivityRemindersEnabled",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_LastViewedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ActivityConfirmedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ActivityConfirmedByUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ActivityConfirmedByUserName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "InactivityRemindersEnabled",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "LastViewedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "LastViewedByUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "LastViewedByUserName",
                table: "ClientCases");
        }
    }
}
