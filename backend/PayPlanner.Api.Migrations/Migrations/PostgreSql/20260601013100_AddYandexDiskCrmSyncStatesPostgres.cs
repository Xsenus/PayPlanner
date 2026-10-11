using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddYandexDiskCrmSyncStatesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YandexDiskCrmSyncStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ObjectType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ObjectId = table.Column<int>(type: "integer", nullable: false),
                    ParentObjectId = table.Column<int>(type: "integer", nullable: true),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    YandexDiskPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
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
