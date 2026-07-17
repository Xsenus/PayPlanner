using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class OptimizeStickyNoteReminderIndexPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StickyNotes_ReminderAt_ReminderSentAt",
                table: "StickyNotes");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNotes_ReminderSentAt_ReminderAt_Id",
                table: "StickyNotes",
                columns: new[] { "ReminderSentAt", "ReminderAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StickyNotes_ReminderSentAt_ReminderAt_Id",
                table: "StickyNotes");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNotes_ReminderAt_ReminderSentAt",
                table: "StickyNotes",
                columns: new[] { "ReminderAt", "ReminderSentAt" });
        }
    }
}
