using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientResponsibleUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ResponsibleUserId",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clients_ResponsibleUserId",
                table: "Clients",
                column: "ResponsibleUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Clients_Users_ResponsibleUserId",
                table: "Clients",
                column: "ResponsibleUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Clients_Users_ResponsibleUserId",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Clients_ResponsibleUserId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ResponsibleUserId",
                table: "Clients");
        }
    }
}
