using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddActCorrectionLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OriginalActId",
                table: "Acts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Acts_OriginalActId",
                table: "Acts",
                column: "OriginalActId");

            migrationBuilder.AddForeignKey(
                name: "FK_Acts_Acts_OriginalActId",
                table: "Acts",
                column: "OriginalActId",
                principalTable: "Acts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Acts_Acts_OriginalActId",
                table: "Acts");

            migrationBuilder.DropIndex(
                name: "IX_Acts_OriginalActId",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "OriginalActId",
                table: "Acts");
        }
    }
}
