using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLegal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LegalEntityId",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LegalEntities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShortName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FullName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Inn = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Kpp = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Ogrn = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Director = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalEntities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_LegalEntityId",
                table: "Clients",
                column: "LegalEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_Inn",
                table: "LegalEntities",
                column: "Inn");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_Kpp",
                table: "LegalEntities",
                column: "Kpp");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_ShortName",
                table: "LegalEntities",
                column: "ShortName");

            migrationBuilder.AddForeignKey(
                name: "FK_Clients_LegalEntities_LegalEntityId",
                table: "Clients",
                column: "LegalEntityId",
                principalTable: "LegalEntities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Clients_LegalEntities_LegalEntityId",
                table: "Clients");

            migrationBuilder.DropTable(
                name: "LegalEntities");

            migrationBuilder.DropIndex(
                name: "IX_Clients_LegalEntityId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "LegalEntityId",
                table: "Clients");
        }
    }
}
