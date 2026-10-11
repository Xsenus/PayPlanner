using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddExternalAuthLinkingAndProfileFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Intent",
                table: "TelegramAuthRequests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "signin");

            migrationBuilder.AddColumn<string>(
                name: "Intent",
                table: "ExternalAuthRequests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "signin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Intent",
                table: "TelegramAuthRequests");

            migrationBuilder.DropColumn(
                name: "Intent",
                table: "ExternalAuthRequests");
        }
    }
}
