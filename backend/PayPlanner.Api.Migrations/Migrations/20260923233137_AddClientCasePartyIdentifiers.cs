using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCasePartyIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimantInn",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimantOgrn",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefendantInn",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefendantOgrn",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 15,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimantInn",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "ClaimantOgrn",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "DefendantInn",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "DefendantOgrn",
                table: "ClientCases");
        }
    }
}
