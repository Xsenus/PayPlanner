using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCourtSyncProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SourceCheckedAt",
                table: "ClientCaseProceduralDeadlines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceConfidence",
                table: "ClientCaseProceduralDeadlines",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "ClientCaseProceduralDeadlines",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceCheckedAt",
                table: "ClientCaseDocuments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceConfidence",
                table: "ClientCaseDocuments",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "ClientCaseDocuments",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "ClientCaseDocuments",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceCheckedAt",
                table: "ClientCaseCourtParties",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceConfidence",
                table: "ClientCaseCourtParties",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "ClientCaseCourtParties",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "ClientCaseCourtParties",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceCheckedAt",
                table: "ClientCaseCourtEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceConfidence",
                table: "ClientCaseCourtEvents",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "ClientCaseCourtEvents",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceCheckedAt",
                table: "ClientCaseProceduralDeadlines");

            migrationBuilder.DropColumn(
                name: "SourceConfidence",
                table: "ClientCaseProceduralDeadlines");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "ClientCaseProceduralDeadlines");

            migrationBuilder.DropColumn(
                name: "SourceCheckedAt",
                table: "ClientCaseDocuments");

            migrationBuilder.DropColumn(
                name: "SourceConfidence",
                table: "ClientCaseDocuments");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "ClientCaseDocuments");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "ClientCaseDocuments");

            migrationBuilder.DropColumn(
                name: "SourceCheckedAt",
                table: "ClientCaseCourtParties");

            migrationBuilder.DropColumn(
                name: "SourceConfidence",
                table: "ClientCaseCourtParties");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "ClientCaseCourtParties");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "ClientCaseCourtParties");

            migrationBuilder.DropColumn(
                name: "SourceCheckedAt",
                table: "ClientCaseCourtEvents");

            migrationBuilder.DropColumn(
                name: "SourceConfidence",
                table: "ClientCaseCourtEvents");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "ClientCaseCourtEvents");
        }
    }
}
