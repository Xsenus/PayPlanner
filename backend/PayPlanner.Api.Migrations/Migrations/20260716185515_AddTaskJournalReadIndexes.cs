using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskJournalReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_IsDeleted_DueAt_Id",
                table: "TaskJournalItems",
                columns: new[] { "IsDeleted", "DueAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_IsDeleted_Status_DueAt_Id",
                table: "TaskJournalItems",
                columns: new[] { "IsDeleted", "Status", "DueAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_IsDeleted_DueAt_Id",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_IsDeleted_Status_DueAt_Id",
                table: "TaskJournalItems");
        }
    }
}
