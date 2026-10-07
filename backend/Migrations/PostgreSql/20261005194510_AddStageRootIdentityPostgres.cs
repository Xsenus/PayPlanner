using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql;

public partial class AddStageRootIdentityPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>("StageRootIdentity", "StorageCapacityReservations",
            type: "character varying(160)", maxLength: 160, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "StorageCapacityReservations" WHERE "StageRootIdentity" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Cannot discard durable stage root identities';
                END IF;
            END $$;
            """);
        migrationBuilder.DropColumn("StageRootIdentity", "StorageCapacityReservations");
    }
}
