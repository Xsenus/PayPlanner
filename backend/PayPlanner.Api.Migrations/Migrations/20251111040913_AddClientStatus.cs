using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientStatusId",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientStatuses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_ClientStatusId",
                table: "Clients",
                column: "ClientStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientStatuses_IsActive",
                table: "ClientStatuses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ClientStatuses_IsActive_Name",
                table: "ClientStatuses",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientStatuses_Name",
                table: "ClientStatuses",
                column: "Name");

            migrationBuilder.AddForeignKey(
                name: "FK_Clients_ClientStatuses_ClientStatusId",
                table: "Clients",
                column: "ClientStatusId",
                principalTable: "ClientStatuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Clients_ClientStatuses_ClientStatusId",
                table: "Clients");

            migrationBuilder.DropTable(
                name: "ClientStatuses");

            migrationBuilder.DropIndex(
                name: "IX_Clients_ClientStatusId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ClientStatusId",
                table: "Clients");
        }
    }
}
