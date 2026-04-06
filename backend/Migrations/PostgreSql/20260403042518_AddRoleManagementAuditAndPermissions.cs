using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddRoleManagementAuditAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "Roles",
                type: "timestamp without time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP",
                oldClrType: typeof(DateTime),
                oldType: "timestamp without time zone");

            migrationBuilder.AddColumn<DateTime>(
                name: "PermissionsUpdatedAt",
                table: "Roles",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PermissionsUpdatedByUserId",
                table: "Roles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PermissionsUpdatedByUserName",
                table: "Roles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_PermissionsUpdatedAt",
                table: "Roles",
                column: "PermissionsUpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Roles_PermissionsUpdatedAt",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "PermissionsUpdatedAt",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "PermissionsUpdatedByUserId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "PermissionsUpdatedByUserName",
                table: "Roles");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "Roles",
                type: "timestamp without time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp without time zone",
                oldDefaultValueSql: "CURRENT_TIMESTAMP");
        }
    }
}
