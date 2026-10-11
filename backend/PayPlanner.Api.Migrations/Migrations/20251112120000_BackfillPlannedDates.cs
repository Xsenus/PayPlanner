using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class BackfillPlannedDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"UPDATE Payments
SET PlannedDate = Date
WHERE PlannedDate IS NULL
   OR PlannedDate = '0001-01-01'
   OR PlannedDate = '0001-01-01 00:00:00';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"UPDATE Payments
SET PlannedDate = '0001-01-01'
WHERE PlannedDate = Date;");
        }
    }
}
