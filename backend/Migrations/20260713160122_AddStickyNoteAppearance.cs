using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStickyNoteAppearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FontSize",
                table: "StickyNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<bool>(
                name: "IsBold",
                table: "StickyNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FontSize",
                table: "StickyNotes");

            migrationBuilder.DropColumn(
                name: "IsBold",
                table: "StickyNotes");
        }
    }
}
