using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCaseRelatedPartyArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArchiveReason",
                table: "ClientCaseRelatedParties",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "ClientCaseRelatedParties",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArchivedByUserId",
                table: "ClientCaseRelatedParties",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchivedByUserName",
                table: "ClientCaseRelatedParties",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "ClientCaseRelatedParties",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseRelatedParties_ClientCaseId_IsArchived",
                table: "ClientCaseRelatedParties",
                columns: new[] { "ClientCaseId", "IsArchived" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientCaseRelatedParties_ClientCaseId_IsArchived",
                table: "ClientCaseRelatedParties");

            migrationBuilder.DropColumn(
                name: "ArchiveReason",
                table: "ClientCaseRelatedParties");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "ClientCaseRelatedParties");

            migrationBuilder.DropColumn(
                name: "ArchivedByUserId",
                table: "ClientCaseRelatedParties");

            migrationBuilder.DropColumn(
                name: "ArchivedByUserName",
                table: "ClientCaseRelatedParties");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "ClientCaseRelatedParties");
        }
    }
}
