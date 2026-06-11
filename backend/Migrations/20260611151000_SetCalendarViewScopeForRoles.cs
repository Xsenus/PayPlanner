using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(PaymentContext))]
    [Migration("20260611151000_SetCalendarViewScopeForRoles")]
    public partial class SetCalendarViewScopeForRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "RolePermissions"
                SET "ViewScope" = 'own'
                WHERE "Section" = 'calendar'
                  AND "RoleId" NOT IN (
                      SELECT "Id"
                      FROM "Roles"
                      WHERE lower("Name") = 'admin'
                  )
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "RolePermissions"
                SET "ViewScope" = 'all'
                WHERE "Section" = 'calendar'
                  AND "RoleId" NOT IN (
                      SELECT "Id"
                      FROM "Roles"
                      WHERE lower("Name") = 'admin'
                  )
                """);
        }
    }
}
