using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class UnifyContactsDirectoryPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactType",
                table: "ExternalContacts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Person");

            migrationBuilder.AddColumn<string>(
                name: "Inn",
                table: "ExternalContacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkedClientId",
                table: "ExternalContacts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkedLegalEntityId",
                table: "ExternalContacts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParentContactId",
                table: "ExternalContacts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RolesJson",
                table: "ExternalContacts",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContacts_ContactType",
                table: "ExternalContacts",
                column: "ContactType");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContacts_Inn",
                table: "ExternalContacts",
                column: "Inn");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContacts_LinkedClientId",
                table: "ExternalContacts",
                column: "LinkedClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContacts_LinkedLegalEntityId",
                table: "ExternalContacts",
                column: "LinkedLegalEntityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContacts_ParentContactId",
                table: "ExternalContacts",
                column: "ParentContactId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExternalContacts_Clients_LinkedClientId",
                table: "ExternalContacts",
                column: "LinkedClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ExternalContacts_ExternalContacts_ParentContactId",
                table: "ExternalContacts",
                column: "ParentContactId",
                principalTable: "ExternalContacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ExternalContacts_LegalEntities_LinkedLegalEntityId",
                table: "ExternalContacts",
                column: "LinkedLegalEntityId",
                principalTable: "LegalEntities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExternalContacts_Clients_LinkedClientId",
                table: "ExternalContacts");

            migrationBuilder.DropForeignKey(
                name: "FK_ExternalContacts_ExternalContacts_ParentContactId",
                table: "ExternalContacts");

            migrationBuilder.DropForeignKey(
                name: "FK_ExternalContacts_LegalEntities_LinkedLegalEntityId",
                table: "ExternalContacts");

            migrationBuilder.DropIndex(
                name: "IX_ExternalContacts_ContactType",
                table: "ExternalContacts");

            migrationBuilder.DropIndex(
                name: "IX_ExternalContacts_Inn",
                table: "ExternalContacts");

            migrationBuilder.DropIndex(
                name: "IX_ExternalContacts_LinkedClientId",
                table: "ExternalContacts");

            migrationBuilder.DropIndex(
                name: "IX_ExternalContacts_LinkedLegalEntityId",
                table: "ExternalContacts");

            migrationBuilder.DropIndex(
                name: "IX_ExternalContacts_ParentContactId",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "ContactType",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "Inn",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "LinkedClientId",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "LinkedLegalEntityId",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "ParentContactId",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "RolesJson",
                table: "ExternalContacts");
        }
    }
}
