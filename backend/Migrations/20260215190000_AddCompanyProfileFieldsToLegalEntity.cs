using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyProfileFieldsToLegalEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseCurrency",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationForm",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceLanguage",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxSystem",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Timezone",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BaseCurrency", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Country", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "OrganizationForm", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "ServiceLanguage", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "TaxSystem", table: "LegalEntities");
            migrationBuilder.DropColumn(name: "Timezone", table: "LegalEntities");
        }
    }
}
