using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations;

public partial class AddStageWriterMarkerIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>("StageWriterMarkerIdentity", "StorageCapacityReservations",
            type: "TEXT", maxLength: 160, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE "StageWriterMarkerIdentityDownGuard" (
                "Identities" INTEGER NOT NULL CHECK ("Identities" = 0));
            INSERT INTO "StageWriterMarkerIdentityDownGuard" ("Identities")
            SELECT COUNT(*) FROM "StorageCapacityReservations" WHERE "StageWriterMarkerIdentity" IS NOT NULL;
            DROP TABLE "StageWriterMarkerIdentityDownGuard";
            """);
        migrationBuilder.DropColumn("StageWriterMarkerIdentity", "StorageCapacityReservations");
    }
}
