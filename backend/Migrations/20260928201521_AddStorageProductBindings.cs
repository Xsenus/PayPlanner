using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageProductBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageProductBindings",
                columns: table => new
                {
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceId = table.Column<long>(type: "INTEGER", nullable: false),
                    StorageObjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageProductBindings", x => new { x.Source, x.SourceId });
                    table.ForeignKey(
                        name: "FK_StorageProductBindings_StorageObjects_StorageObjectId",
                        column: x => x.StorageObjectId,
                        principalTable: "StorageObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageProductBindings_StorageObjectId",
                table: "StorageProductBindings",
                column: "StorageObjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageProductBindings");
        }
    }
}
