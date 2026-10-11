using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql;

public partial class AddStageWriterMarkerIdentityPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>("StageWriterMarkerIdentity", "StorageCapacityReservations",
            type: "character varying(160)", maxLength: 160, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "StorageCapacityReservations" WHERE "StageWriterMarkerIdentity" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Cannot discard durable stage marker identities';
                END IF;
            END $$;
            """);
        migrationBuilder.DropColumn("StageWriterMarkerIdentity", "StorageCapacityReservations");
    }
}
