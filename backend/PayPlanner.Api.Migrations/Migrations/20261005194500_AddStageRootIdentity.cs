using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations;

public partial class AddStageRootIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>("StageRootIdentity", "StorageCapacityReservations",
            type: "TEXT", maxLength: 160, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE "StageRootIdentityDownGuard" (
                "Identities" INTEGER NOT NULL CHECK ("Identities" = 0));
            INSERT INTO "StageRootIdentityDownGuard" ("Identities")
            SELECT COUNT(*) FROM "StorageCapacityReservations" WHERE "StageRootIdentity" IS NOT NULL;
            DROP TABLE "StageRootIdentityDownGuard";
            """);
        migrationBuilder.DropColumn("StageRootIdentity", "StorageCapacityReservations");
    }
}
