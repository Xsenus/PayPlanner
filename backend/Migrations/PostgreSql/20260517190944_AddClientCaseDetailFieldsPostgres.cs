using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientCaseDetailFieldsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AppealDeadlineAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimFiledAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Claimant",
                table: "ClientCases",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CourtSecretaryContacts",
                table: "ClientCases",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Courtroom",
                table: "ClientCases",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Defendant",
                table: "ClientCases",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DocumentsSummary",
                table: "ClientCases",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EventHistory",
                table: "ClientCases",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "JudgeName",
                table: "ClientCases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "MotionDeadlineAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ObjectionDeadlineAt",
                table: "ClientCases",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Representatives",
                table: "ClientCases",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Strategy",
                table: "ClientCases",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ThirdParties",
                table: "ClientCases",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_AppealDeadlineAt",
                table: "ClientCases",
                column: "AppealDeadlineAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_ClaimFiledAt",
                table: "ClientCases",
                column: "ClaimFiledAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_MotionDeadlineAt",
                table: "ClientCases",
                column: "MotionDeadlineAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_ObjectionDeadlineAt",
                table: "ClientCases",
                column: "ObjectionDeadlineAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientCases_AppealDeadlineAt",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_ClaimFiledAt",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_MotionDeadlineAt",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_ObjectionDeadlineAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "AppealDeadlineAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ClaimFiledAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "Claimant",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtSecretaryContacts",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "Courtroom",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "Defendant",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "DocumentsSummary",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "EventHistory",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "JudgeName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "MotionDeadlineAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ObjectionDeadlineAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "Representatives",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "Strategy",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ThirdParties",
                table: "ClientCases");
        }
    }
}
