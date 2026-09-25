using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseFinalReportMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCaseFinalReport",
                table: "TaskJournalItems",
                type: "INTEGER",
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
