using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <summary>
    /// ADDITIVE ONLY: Creates employees and case_employees tables
    /// NO DATA DELETION - Existing data preserved
    /// </summary>
    public partial class AddEmployeesDirectory : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "employees",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    first_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    middle_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    role = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, defaultValue: "active"),
                    hire_date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    termination_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employees", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "case_employees",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    case_id = table.Column<int>(type: "INTEGER", nullable: false),
                    employee_id = table.Column<int>(type: "INTEGER", nullable: false),
                    role_in_case = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    assigned_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_employees", x => x.id);
                    table.ForeignKey(
                        name: "FK_case_employees_Cases_case_id",
                        column: x => x.case_id,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_case_employees_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_employees_user_id",
                table: "employees",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_employees_status",
                table: "employees",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_case_employees_case_id",
                table: "case_employees",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "IX_case_employees_employee_id",
                table: "case_employees",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_case_employees_case_id_employee_id",
                table: "case_employees",
                columns: new[] { "case_id", "employee_id" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "case_employees");
            migrationBuilder.DropTable(name: "employees");
        }
    }
}
