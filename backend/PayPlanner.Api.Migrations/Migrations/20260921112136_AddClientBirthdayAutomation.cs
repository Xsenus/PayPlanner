using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientBirthdayAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BirthDate",
                table: "Clients",
                type: "date",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Clients"
                SET "BirthDate" = CASE
                    WHEN json_valid(substr("Notes", 26))
                    THEN date(json_extract(substr("Notes", 26), '$.birthDate'))
                    ELSE NULL
                END
                WHERE "Notes" LIKE '__PP_CLIENT_PROFILE_V2__:%';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Clients_BirthDate",
                table: "Clients",
                column: "BirthDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Clients_BirthDate",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "BirthDate",
                table: "Clients");
        }
    }
}
