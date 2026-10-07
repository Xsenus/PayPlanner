using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations;

public partial class AddStageFileReceipt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>("StageFileReceiptJson", "StorageCapacityReservations",
            type: "TEXT", maxLength: 1024, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Even released files may survive until a verified reader is disposed.
        // Downgrade must not discard their only durable proof implicitly.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE "StageFileReceiptDownGuard" (
                "Receipts" INTEGER NOT NULL CHECK ("Receipts" = 0));
            INSERT INTO "StageFileReceiptDownGuard" ("Receipts")
            SELECT COUNT(*) FROM "StorageCapacityReservations" WHERE "StageFileReceiptJson" IS NOT NULL;
            DROP TABLE "StageFileReceiptDownGuard";
            """);
        migrationBuilder.DropColumn("StageFileReceiptJson", "StorageCapacityReservations");
    }
}
