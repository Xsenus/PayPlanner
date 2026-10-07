using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddStageWriterAdmissionPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StageWriterAdmissionPending",
                table: "StorageCapacityReservations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "StorageCapacityReservations"
                        WHERE "Status" = 'pending' AND "StageWriterAdmissionPending") THEN
                        RAISE EXCEPTION 'Cannot remove a preparing stage writer admission';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "StageWriterAdmissionPending",
                table: "StorageCapacityReservations");
        }
    }
}
