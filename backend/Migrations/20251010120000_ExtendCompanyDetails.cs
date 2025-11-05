using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class ExtendCompanyDetails : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Address",
                table: "Companies",
                newName: "ActualAddress");

            migrationBuilder.AddColumn<string>(
                name: "FullName",
                table: "Companies",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Inn",
                table: "Companies",
                type: "TEXT",
                maxLength: 12,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Kpp",
                table: "Companies",
                type: "TEXT",
                maxLength: 9,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LegalAddress",
                table: "Companies",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ShortName",
                table: "Companies",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                "UPDATE \"Companies\" SET \"ShortName\" = CASE WHEN LENGTH(IFNULL(\"Name\", '')) = 0 THEN '' ELSE \"Name\" END;");

            migrationBuilder.Sql(
                "UPDATE \"Companies\" SET \"FullName\" = CASE WHEN LENGTH(IFNULL(\"FullName\", '')) = 0 THEN CASE WHEN LENGTH(IFNULL(\"Name\", '')) = 0 THEN '' ELSE \"Name\" END ELSE \"FullName\" END;");

            migrationBuilder.Sql(
                "UPDATE \"Companies\" SET \"LegalAddress\" = CASE WHEN LENGTH(IFNULL(\"LegalAddress\", '')) = 0 THEN \"ActualAddress\" ELSE \"LegalAddress\" END;");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_Inn",
                table: "Companies",
                column: "Inn");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Companies_Inn",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "FullName",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Inn",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Kpp",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "LegalAddress",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "ShortName",
                table: "Companies");

            migrationBuilder.RenameColumn(
                name: "ActualAddress",
                table: "Companies",
                newName: "Address");
        }
    }
}
