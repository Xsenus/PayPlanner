using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <summary>
    /// ADDITIVE ONLY: Creates feature_flags table
    /// NO DATA DELETION - All existing tables preserved
    /// </summary>
    public partial class AddFeatureFlags : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feature_flags",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    flag_key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_flags", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_feature_flags_flag_key",
                table: "feature_flags",
                column: "flag_key",
                unique: true);

            // Initialize feature flags (all disabled by default for safety)
            migrationBuilder.Sql(@"
                INSERT INTO feature_flags (flag_key, enabled, description, created_at, updated_at)
                VALUES
                    ('expenses_module_enabled', 0, 'Enable new unified expenses functionality', datetime('now'), datetime('now')),
                    ('clients_split_enabled', 0, 'Enable companies/persons split from legacy clients', datetime('now'), datetime('now')),
                    ('partial_payments_enabled', 0, 'Enable payment schedule and partial payment tracking', datetime('now'), datetime('now')),
                    ('activity_logging_ui_clicks', 0, 'Log granular UI interactions (high volume)', datetime('now'), datetime('now')),
                    ('employees_directory_enabled', 0, 'Enable employee management and case assignments', datetime('now'), datetime('now'));
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "feature_flags");
        }
    }
}
