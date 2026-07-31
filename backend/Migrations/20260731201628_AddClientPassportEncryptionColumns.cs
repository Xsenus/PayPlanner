using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientPassportEncryptionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BirthPlaceFiasEncrypted",
                table: "ClientPassports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BirthPlacePassportEncrypted",
                table: "ClientPassports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DepartmentCodeEncrypted",
                table: "ClientPassports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedAtEncrypted",
                table: "ClientPassports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedByEncrypted",
                table: "ClientPassports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NumberBlindIndex",
                table: "ClientPassports",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NumberEncrypted",
                table: "ClientPassports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesBlindIndex",
                table: "ClientPassports",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesEncrypted",
                table: "ClientPassports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassports_ClientId_SeriesBlind_NumberBlind",
                table: "ClientPassports",
                columns: new[] { "ClientId", "SeriesBlindIndex", "NumberBlindIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientPassports_ClientId_SeriesBlind_NumberBlind",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "BirthPlaceFiasEncrypted",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "BirthPlacePassportEncrypted",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "DepartmentCodeEncrypted",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "IssuedAtEncrypted",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "IssuedByEncrypted",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "NumberBlindIndex",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "NumberEncrypted",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "SeriesBlindIndex",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "SeriesEncrypted",
                table: "ClientPassports");
        }
    }
}
