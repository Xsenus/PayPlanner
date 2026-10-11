using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddStorageCapacityReservationsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageCapacityEpochs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageCapacityEpochs", x => x.Id);
                    table.CheckConstraint("CK_StorageCapacityEpochs_Singleton", "\"Id\" = 1");
                    table.CheckConstraint("CK_StorageCapacityEpochs_Version", "\"Version\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "StorageCapacityReservations",
                columns: table => new
                {
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    GroupId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BindingHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    ObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalLocator = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageCapacityReservations", x => new { x.IdempotencyKey, x.ResourceKey });
                    table.CheckConstraint("CK_StorageCapacityReservations_Bytes", "\"Bytes\" >= 0");
                    table.CheckConstraint("CK_StorageCapacityReservations_Status", "\"Status\" IN ('pending', 'released')");
                });

            migrationBuilder.InsertData(
                table: "StorageCapacityEpochs",
                columns: new[] { "Id", "Version" },
                values: new object[] { 1, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_StorageCapacityReservations_ObjectId",
                table: "StorageCapacityReservations",
                column: "ObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageCapacityReservations_Status_ResourceKey",
                table: "StorageCapacityReservations",
                columns: new[] { "Status", "ResourceKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageCapacityEpochs");

            migrationBuilder.DropTable(
                name: "StorageCapacityReservations");
        }
    }
}
