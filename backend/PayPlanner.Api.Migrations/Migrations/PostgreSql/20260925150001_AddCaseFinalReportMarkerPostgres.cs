using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCaseFinalReportMarkerPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCaseFinalReport",
                table: "TaskJournalItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_ClientCaseId_IsCaseFinalReport",
                table: "TaskJournalItems",
                columns: new[] { "ClientCaseId", "IsCaseFinalReport" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_ClientCaseId_IsCaseFinalReport",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "IsCaseFinalReport",
                table: "TaskJournalItems");
        }
    }
}
