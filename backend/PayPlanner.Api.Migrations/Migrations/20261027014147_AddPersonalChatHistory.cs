using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalChatHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HistoryHiddenThroughMessageId",
                table: "InternalChatMembers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HistoryVisibilityRevision",
                table: "InternalChatMembers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HistoryHiddenThroughMessageId",
                table: "InternalChatMembers");

            migrationBuilder.DropColumn(
                name: "HistoryVisibilityRevision",
                table: "InternalChatMembers");
        }
    }
}
