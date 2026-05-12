using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskLegalEntityLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LegalEntityId",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_LegalEntityId",
                table: "TaskJournalItems",
                column: "LegalEntityId");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskJournalItems_LegalEntities_LegalEntityId",
                table: "TaskJournalItems",
                column: "LegalEntityId",
                principalTable: "LegalEntities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskJournalItems_LegalEntities_LegalEntityId",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_LegalEntityId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "LegalEntityId",
                table: "TaskJournalItems");
        }
    }
}
