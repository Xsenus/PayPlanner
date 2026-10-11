using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCaseActivityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivityConfirmedAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActivityConfirmedByUserId",
                table: "ClientCases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivityConfirmedByUserName",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "InactivityRemindersEnabled",
                table: "ClientCases",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastViewedAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastViewedByUserId",
                table: "ClientCases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastViewedByUserName",
                table: "ClientCases",
                type: "TEXT",
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
