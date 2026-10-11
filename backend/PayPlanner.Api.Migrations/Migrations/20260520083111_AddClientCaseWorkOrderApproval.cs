using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCaseWorkOrderApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalComment",
                table: "ClientCaseWorkOrders",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalDecisionAt",
                table: "ClientCaseWorkOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalDecisionByUserId",
                table: "ClientCaseWorkOrders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalDecisionByUserName",
                table: "ClientCaseWorkOrders",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalRequestedAt",
                table: "ClientCaseWorkOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalRequestedByUserId",
                table: "ClientCaseWorkOrders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalRequestedByUserName",
                table: "ClientCaseWorkOrders",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseWorkOrders_ApprovalDecisionAt",
                table: "ClientCaseWorkOrders",
                column: "ApprovalDecisionAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseWorkOrders_ApprovalRequestedAt",
                table: "ClientCaseWorkOrders",
                column: "ApprovalRequestedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientCaseWorkOrders_ApprovalDecisionAt",
                table: "ClientCaseWorkOrders");

            migrationBuilder.DropIndex(
                name: "IX_ClientCaseWorkOrders_ApprovalRequestedAt",
                table: "ClientCaseWorkOrders");

            migrationBuilder.DropColumn(
                name: "ApprovalComment",
                table: "ClientCaseWorkOrders");

            migrationBuilder.DropColumn(
                name: "ApprovalDecisionAt",
                table: "ClientCaseWorkOrders");

            migrationBuilder.DropColumn(
                name: "ApprovalDecisionByUserId",
                table: "ClientCaseWorkOrders");

            migrationBuilder.DropColumn(
                name: "ApprovalDecisionByUserName",
                table: "ClientCaseWorkOrders");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestedAt",
                table: "ClientCaseWorkOrders");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestedByUserId",
                table: "ClientCaseWorkOrders");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestedByUserName",
                table: "ClientCaseWorkOrders");
        }
    }
}
