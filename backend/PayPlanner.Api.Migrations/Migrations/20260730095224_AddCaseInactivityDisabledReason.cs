using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseInactivityDisabledReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InactivityRemindersDisabledReason",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE "ClientCases"
                SET "InactivityRemindersDisabledReason" = 'Отключено до введения обязательной причины'
                WHERE "InactivityRemindersEnabled" = 0
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
