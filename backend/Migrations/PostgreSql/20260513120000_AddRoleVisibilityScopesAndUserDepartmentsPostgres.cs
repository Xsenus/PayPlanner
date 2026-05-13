using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddRoleVisibilityScopesAndUserDepartmentsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Department",
                table: "Users",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ViewScope",
                table: "RolePermissions",
                type: "character varying(32)",
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
