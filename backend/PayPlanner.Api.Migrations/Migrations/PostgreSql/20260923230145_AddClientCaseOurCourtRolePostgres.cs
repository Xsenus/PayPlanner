using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientCaseOurCourtRolePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OurCourtRole",
                table: "ClientCases",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_OurCourtRole",
                table: "ClientCases",
                column: "OurCourtRole");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientCases_OurCourtRole",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "OurCourtRole",
                table: "ClientCases");
        }
    }
}
