using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskClientLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientCaseId",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClientId",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_ClientCaseId",
                table: "TaskJournalItems",
                column: "ClientCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_ClientId",
                table: "TaskJournalItems",
                column: "ClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskJournalItems_ClientCases_ClientCaseId",
                table: "TaskJournalItems",
                column: "ClientCaseId",
                principalTable: "ClientCases",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TaskJournalItems_Clients_ClientId",
                table: "TaskJournalItems",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskJournalItems_ClientCases_ClientCaseId",
                table: "TaskJournalItems");

            migrationBuilder.DropForeignKey(
                name: "FK_TaskJournalItems_Clients_ClientId",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_ClientCaseId",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_ClientId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ClientCaseId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "TaskJournalItems");
        }
    }
}
