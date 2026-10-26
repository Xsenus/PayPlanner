using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql;

/// <summary>Only nullable per-document policy; no historical snapshot rewrite or backfill.</summary>
public partial class AddDocumentSellerSnapshotPolicyPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "SellerSnapshotPolicy", table: "Payments", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<string>(name: "SellerSnapshotPolicy", table: "Acts", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<string>(name: "SellerSnapshotPolicy", table: "Contracts", type: "character varying(32)", maxLength: 32, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SellerSnapshotPolicy", table: "Payments");
        migrationBuilder.DropColumn(name: "SellerSnapshotPolicy", table: "Acts");
        migrationBuilder.DropColumn(name: "SellerSnapshotPolicy", table: "Contracts");
    }
}
