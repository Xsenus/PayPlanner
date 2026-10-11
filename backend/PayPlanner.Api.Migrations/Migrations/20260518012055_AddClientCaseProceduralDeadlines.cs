using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCaseProceduralDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CassationDeadlineAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CourtDecisionAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_CassationDeadlineAt",
                table: "ClientCases",
                column: "CassationDeadlineAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_CourtDecisionAt",
                table: "ClientCases",
                column: "CourtDecisionAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientCases_CassationDeadlineAt",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_CourtDecisionAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CassationDeadlineAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtDecisionAt",
                table: "ClientCases");
        }
    }
}
