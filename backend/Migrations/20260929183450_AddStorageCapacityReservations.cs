using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageCapacityReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageCapacityEpochs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageCapacityEpochs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageCapacityReservations",
                columns: table => new
                {
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    ResourceKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BindingHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ObjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalLocator = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageCapacityReservations", x => new { x.IdempotencyKey, x.ResourceKey });
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
