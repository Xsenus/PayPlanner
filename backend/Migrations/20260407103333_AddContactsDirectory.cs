using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddContactsDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FullName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Position = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PowerOfAttorney = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PhonesJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    EmailsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    LegalEntityId = table.Column<int>(type: "INTEGER", nullable: true),
                    ResponsibleUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncToPhonebook = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalContacts_LegalEntities_LegalEntityId",
                        column: x => x.LegalEntityId,
                        principalTable: "LegalEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExternalContacts_Users_ResponsibleUserId",
                        column: x => x.ResponsibleUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContacts_FullName",
                table: "ExternalContacts",
                column: "FullName");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContacts_IsActive",
                table: "ExternalContacts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContacts_LegalEntityId",
                table: "ExternalContacts",
                column: "LegalEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContacts_ResponsibleUserId",
                table: "ExternalContacts",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContacts_SyncToPhonebook",
                table: "ExternalContacts",
                column: "SyncToPhonebook");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalContacts");
        }
    }
}
