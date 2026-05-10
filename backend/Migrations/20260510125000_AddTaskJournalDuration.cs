using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(PaymentContext))]
    [Migration("20260510125000_AddTaskJournalDuration")]
    public partial class AddTaskJournalDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PlannedDurationMinutes",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlannedDurationMinutes",
                table: "TaskJournalItems");
        }
    }
}
