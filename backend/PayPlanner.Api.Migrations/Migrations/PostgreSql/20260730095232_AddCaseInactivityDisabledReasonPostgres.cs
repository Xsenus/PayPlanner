using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCaseInactivityDisabledReasonPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InactivityRemindersDisabledReason",
                table: "ClientCases",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE "ClientCases"
                SET "InactivityRemindersDisabledReason" = 'Отключено до введения обязательной причины'
                WHERE "InactivityRemindersEnabled" = FALSE
                  AND "InactivityRemindersDisabledReason" = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InactivityRemindersDisabledReason",
                table: "ClientCases");
        }
    }
}
