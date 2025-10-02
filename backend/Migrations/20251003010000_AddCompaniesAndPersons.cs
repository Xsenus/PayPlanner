using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <summary>
    /// ADDITIVE ONLY: Creates companies and persons tables
    /// PRESERVES: Original Clients table unchanged
    /// Purpose: Split client entities into legal entities (companies) and individuals (persons)
    /// </summary>
    public partial class AddCompaniesAndPersons : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create companies table
            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    legal_name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    registration_number = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    tax_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    legal_address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    legacy_client_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.id);
                });

            // Create persons table
            migrationBuilder.CreateTable(
                name: "persons",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    first_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    middle_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    company_id = table.Column<int>(type: "INTEGER", nullable: true),
                    position = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    legacy_client_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_persons", x => x.id);
                    table.ForeignKey(
                        name: "FK_persons_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            // Add new optional columns to Cases table (non-destructive)
            migrationBuilder.AddColumn<int>(
                name: "company_id",
                table: "Cases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "person_id",
                table: "Cases",
                type: "INTEGER",
                nullable: true);

            // Create indexes
            migrationBuilder.CreateIndex(
                name: "IX_companies_legacy_client_id",
                table: "companies",
                column: "legacy_client_id");

            migrationBuilder.CreateIndex(
                name: "IX_persons_company_id",
                table: "persons",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_persons_legacy_client_id",
                table: "persons",
                column: "legacy_client_id");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_company_id",
                table: "Cases",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_person_id",
                table: "Cases",
                column: "person_id");

            // Create compatibility view for legacy queries
            migrationBuilder.Sql(@"
                CREATE VIEW vw_legacy_clients AS
                SELECT
                    c.Id,
                    c.Name,
                    c.Company,
                    c.Email,
                    c.Phone,
                    c.Address,
                    c.Notes,
                    c.IsActive,
                    c.CreatedAt,
                    CASE
                        WHEN comp.id IS NOT NULL THEN comp.id
                        WHEN p.id IS NOT NULL THEN p.id
                        ELSE NULL
                    END as new_entity_id,
                    CASE
                        WHEN comp.id IS NOT NULL THEN 'company'
                        WHEN p.id IS NOT NULL THEN 'person'
                        ELSE NULL
                    END as entity_type
                FROM Clients c
                LEFT JOIN companies comp ON comp.legacy_client_id = c.Id
                LEFT JOIN persons p ON p.legacy_client_id = c.Id;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS vw_legacy_clients;");

            migrationBuilder.DropIndex(name: "IX_Cases_company_id", table: "Cases");
            migrationBuilder.DropIndex(name: "IX_Cases_person_id", table: "Cases");

            migrationBuilder.DropColumn(name: "company_id", table: "Cases");
            migrationBuilder.DropColumn(name: "person_id", table: "Cases");

            migrationBuilder.DropTable(name: "persons");
            migrationBuilder.DropTable(name: "companies");
        }
    }
}
