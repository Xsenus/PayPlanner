using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCaseCourtAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CourtAwardedAmount",
                table: "ClientCases",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CourtClaimAmount",
                table: "ClientCases",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CourtStateFeeAmount",
                table: "ClientCases",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CourtAwardedAmount",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtClaimAmount",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CourtStateFeeAmount",
                table: "ClientCases");
        }
    }
}
