using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(PaymentContext))]
    [Migration("20260513120000_AddRoleVisibilityScopesAndUserDepartments")]
    public partial class AddRoleVisibilityScopesAndUserDepartments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Department",
                table: "Users",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ViewScope",
                table: "RolePermissions",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "all");

            migrationBuilder.Sql(
                """
                UPDATE "RolePermissions"
                SET "ViewScope" = CASE
                    WHEN "RoleId" IN (SELECT "Id" FROM "Roles" WHERE "Name" = 'admin') THEN 'all'
                    WHEN "Section" IN ('tasks', 'legalEntities', 'clients', 'accounts', 'acts', 'payments', 'contracts') THEN 'own'
                    ELSE 'all'
                END
                """);

            migrationBuilder.CreateIndex(
                name: "idx_users_department",
                table: "Users",
                column: "Department");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_users_department",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Department",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ViewScope",
                table: "RolePermissions");
        }
    }
}
