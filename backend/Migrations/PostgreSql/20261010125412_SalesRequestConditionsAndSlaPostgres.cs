using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class SalesRequestConditionsAndSlaPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ConditionAmountGreaterThan",
                table: "SalesRequestStatusTaskRules",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConditionSource",
                table: "SalesRequestStatusTaskRules",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConditionType",
                table: "SalesRequestStatusTaskRules",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SlaMinutes",
                table: "SalesRequestStatuses",
                type: "integer",
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
