using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class AddUserProfilesAndActivityLogs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add is_activated column to users table
            migrationBuilder.AddColumn<bool>(
                name: "is_activated",
                table: "users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Update existing users to be activated
            migrationBuilder.Sql("UPDATE users SET is_activated = 1, is_active = 1");

            // Create user_profiles table
            migrationBuilder.CreateTable(
                name: "user_profiles",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    last_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    first_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    patronymic = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    date_of_birth = table.Column<DateTime>(type: "TEXT", nullable: true),
                    hire_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    termination_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_profiles", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_profiles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Create user_contacts table
            migrationBuilder.CreateTable(
                name: "user_contacts",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    contact_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    contact_value = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    is_primary = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_contacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_contacts_user_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "user_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Create user_social_profiles table
            migrationBuilder.CreateTable(
                name: "user_social_profiles",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    platform = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    profile_url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_social_profiles", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_social_profiles_user_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "user_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Create activity_logs table
            migrationBuilder.CreateTable(
                name: "activity_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    action_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    section = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    details = table.Column<string>(type: "TEXT", nullable: true),
                    ip_address = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    user_agent = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_logs", x => x.id);
                    table.ForeignKey(
                        name: "FK_activity_logs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Create indexes
            migrationBuilder.CreateIndex(
                name: "IX_user_profiles_user_id",
                table: "user_profiles",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_contacts_profile_id",
                table: "user_contacts",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_social_profiles_profile_id",
                table: "user_social_profiles",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_activity_logs_user_id",
                table: "activity_logs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_activity_logs_timestamp",
                table: "activity_logs",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_activity_logs_action_type",
                table: "activity_logs",
                column: "action_type");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "user_contacts");
            migrationBuilder.DropTable(name: "user_social_profiles");
            migrationBuilder.DropTable(name: "activity_logs");
            migrationBuilder.DropTable(name: "user_profiles");

            migrationBuilder.DropColumn(
                name: "is_activated",
                table: "users");
        }
    }
}
