using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientPassportEncryptionColumnsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BirthPlaceFiasEncrypted",
                table: "ClientPassports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BirthPlacePassportEncrypted",
                table: "ClientPassports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DepartmentCodeEncrypted",
                table: "ClientPassports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedAtEncrypted",
                table: "ClientPassports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedByEncrypted",
                table: "ClientPassports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NumberBlindIndex",
                table: "ClientPassports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NumberEncrypted",
                table: "ClientPassports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesBlindIndex",
                table: "ClientPassports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesEncrypted",
                table: "ClientPassports",
                type: "text",
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
