using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql;

public partial class AddStageFileReceiptPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>("StageFileReceiptJson", "StorageCapacityReservations",
            type: "character varying(1024)", maxLength: 1024, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "StorageCapacityReservations" WHERE "StageFileReceiptJson" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Cannot discard durable stage file receipts';
                END IF;
            END $$;
            """);
        migrationBuilder.DropColumn("StageFileReceiptJson", "StorageCapacityReservations");
    }
}
