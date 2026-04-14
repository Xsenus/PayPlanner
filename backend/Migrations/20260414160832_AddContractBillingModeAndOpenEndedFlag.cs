using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddContractBillingModeAndOpenEndedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingMode",
                table: "Contracts",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Fixed");

            migrationBuilder.AddColumn<bool>(
                name: "IsOpenEnded",
                table: "Contracts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BillingMode",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "IsOpenEnded",
                table: "Contracts");
        }
    }
}
