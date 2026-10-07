using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStageWriterAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StageWriterAdmissionPending",
                table: "StorageCapacityReservations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "StageWriterAdmissionDownGuard" (
                    "Preparing" INTEGER NOT NULL CHECK ("Preparing" = 0));
                INSERT INTO "StageWriterAdmissionDownGuard" ("Preparing")
                SELECT COUNT(*) FROM "StorageCapacityReservations"
                WHERE "Status" = 'pending' AND "StageWriterAdmissionPending" = 1;
                DROP TABLE "StageWriterAdmissionDownGuard";
                """);
            migrationBuilder.DropColumn(
                name: "StageWriterAdmissionPending",
                table: "StorageCapacityReservations");
        }
    }
}
