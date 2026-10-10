using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class SalesRequestConditionsAndSla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ConditionAmountGreaterThan",
                table: "SalesRequestStatusTaskRules",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConditionSource",
                table: "SalesRequestStatusTaskRules",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConditionType",
                table: "SalesRequestStatusTaskRules",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SlaMinutes",
                table: "SalesRequestStatuses",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConditionAmountGreaterThan",
                table: "SalesRequestStatusTaskRules");

            migrationBuilder.DropColumn(
                name: "ConditionSource",
                table: "SalesRequestStatusTaskRules");

            migrationBuilder.DropColumn(
                name: "ConditionType",
                table: "SalesRequestStatusTaskRules");

            migrationBuilder.DropColumn(
                name: "SlaMinutes",
                table: "SalesRequestStatuses");
        }
    }
}
