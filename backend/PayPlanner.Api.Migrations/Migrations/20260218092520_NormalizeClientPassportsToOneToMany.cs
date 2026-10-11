using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeClientPassportsToOneToMany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientPassports_ClientId",
                table: "ClientPassports");

            migrationBuilder.AddColumn<string>(
                name: "BirthPlaceFias",
                table: "ClientPassports",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BirthPlacePassport",
                table: "ClientPassports",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrimary",
                table: "ClientPassports",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidFrom",
                table: "ClientPassports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidTo",
                table: "ClientPassports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassports_ClientId",
                table: "ClientPassports",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassports_ClientId_IsPrimary",
                table: "ClientPassports",
                columns: new[] { "ClientId", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassports_ClientId_Series_Number",
                table: "ClientPassports",
                columns: new[] { "ClientId", "Series", "Number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientPassports_ClientId",
                table: "ClientPassports");

            migrationBuilder.DropIndex(
                name: "IX_ClientPassports_ClientId_IsPrimary",
                table: "ClientPassports");

            migrationBuilder.DropIndex(
                name: "IX_ClientPassports_ClientId_Series_Number",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "BirthPlaceFias",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "BirthPlacePassport",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "IsPrimary",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "ValidFrom",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "ValidTo",
                table: "ClientPassports");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassports_ClientId",
                table: "ClientPassports",
                column: "ClientId",
                unique: true);
        }
    }
}
