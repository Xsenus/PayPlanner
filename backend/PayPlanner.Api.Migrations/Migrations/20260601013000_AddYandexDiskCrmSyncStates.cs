using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddYandexDiskCrmSyncStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YandexDiskCrmSyncStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocumentKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObjectType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentObjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    YandexDiskPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YandexDiskCrmSyncStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_YandexDiskCrmSyncStates_Document_Object",
                table: "YandexDiskCrmSyncStates",
                columns: new[] { "DocumentKind", "ObjectType", "ObjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YandexDiskCrmSyncStates_LastSyncedAt",
                table: "YandexDiskCrmSyncStates",
                column: "LastSyncedAt");

            migrationBuilder.CreateIndex(
                name: "IX_YandexDiskCrmSyncStates_Status",
                table: "YandexDiskCrmSyncStates",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "YandexDiskCrmSyncStates");
        }
    }
}
