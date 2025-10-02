using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <summary>
    /// ADDITIVE ONLY: Creates expenses table with unified tracking
    /// NO DATA DELETION - Coexists with existing Payment tracking
    /// </summary>
    public partial class AddExpensesModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "expenses",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    expense_date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    purpose_text = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    payee_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    payee_id = table.Column<int>(type: "INTEGER", nullable: false),
                    employee_id = table.Column<int>(type: "INTEGER", nullable: true),
                    case_id = table.Column<int>(type: "INTEGER", nullable: true),
                    client_id = table.Column<int>(type: "INTEGER", nullable: true),
                    company_id = table.Column<int>(type: "INTEGER", nullable: true),
                    person_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expenses", x => x.id);
                    table.ForeignKey(
                        name: "FK_expenses_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_expenses_Cases_case_id",
                        column: x => x.case_id,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_expenses_Clients_client_id",
                        column: x => x.client_id,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_expenses_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_expenses_persons_person_id",
                        column: x => x.person_id,
                        principalTable: "persons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            // Indexes for performance
            migrationBuilder.CreateIndex(
                name: "IX_expenses_employee_id_expense_date",
                table: "expenses",
                columns: new[] { "employee_id", "expense_date" });

            migrationBuilder.CreateIndex(
                name: "IX_expenses_case_id_expense_date",
                table: "expenses",
                columns: new[] { "case_id", "expense_date" });

            migrationBuilder.CreateIndex(
                name: "IX_expenses_client_id_expense_date",
                table: "expenses",
                columns: new[] { "client_id", "expense_date" });

            migrationBuilder.CreateIndex(
                name: "IX_expenses_category",
                table: "expenses",
                column: "category");

            // Create view for employee expenses (deduplicated)
            migrationBuilder.Sql(@"
                CREATE VIEW vw_expenses_by_employee AS
                SELECT DISTINCT
                    e.id,
                    e.employee_id,
                    e.expense_date,
                    e.amount,
                    e.category,
                    e.purpose_text,
                    e.case_id,
                    emp.first_name || ' ' || emp.last_name as employee_name
                FROM expenses e
                INNER JOIN employees emp ON e.employee_id = emp.id
                WHERE e.employee_id IS NOT NULL;
            ");

            // Create view for case expenses
            migrationBuilder.Sql(@"
                CREATE VIEW vw_expenses_by_case AS
                SELECT DISTINCT
                    e.id,
                    e.case_id,
                    e.expense_date,
                    e.amount,
                    e.category,
                    e.purpose_text,
                    e.employee_id,
                    c.CaseNumber as case_number
                FROM expenses e
                INNER JOIN Cases c ON e.case_id = c.Id
                WHERE e.case_id IS NOT NULL;
            ");

            // Create view for expenses not tied to cases
            migrationBuilder.Sql(@"
                CREATE VIEW vw_expenses_out_of_case AS
                SELECT DISTINCT
                    e.id,
                    e.employee_id,
                    e.expense_date,
                    e.amount,
                    e.category,
                    e.purpose_text
                FROM expenses e
                WHERE e.case_id IS NULL;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS vw_expenses_out_of_case;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS vw_expenses_by_case;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS vw_expenses_by_employee;");

            migrationBuilder.DropTable(name: "expenses");
        }
    }
}
