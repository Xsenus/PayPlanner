using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCourtEventExternalKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalKey",
                table: "ClientCaseCourtEvents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_ClientCaseCourtEvents_Case_Source_ExternalKey",
                table: "ClientCaseCourtEvents",
                columns: new[] { "ClientCaseId", "SourceType", "ExternalKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ClientCaseCourtEvents_Case_Source_ExternalKey",
                table: "ClientCaseCourtEvents");

            migrationBuilder.DropColumn(
                name: "ExternalKey",
                table: "ClientCaseCourtEvents");
        }
    }
}
