using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddDatabaseBackupCatalogPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseBackupSets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BackupRelativePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    DatabaseProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BackupFormat = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ManifestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseBackupSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseBackupCopies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BackupSetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConnectionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Locator = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastVerifiedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseBackupCopies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseBackupCopies_DatabaseBackupSets_BackupSetId",
                        column: x => x.BackupSetId,
                        principalTable: "DatabaseBackupSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBackupCopies_BackupSetId_ProviderKey_ConnectionId",
                table: "DatabaseBackupCopies",
                columns: new[] { "BackupSetId", "ProviderKey", "ConnectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBackupCopies_ProviderKey_Status_UpdatedAt",
                table: "DatabaseBackupCopies",
                columns: new[] { "ProviderKey", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBackupSets_BackupRelativePath",
                table: "DatabaseBackupSets",
                column: "BackupRelativePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBackupSets_Status_CreatedAt",
                table: "DatabaseBackupSets",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseBackupCopies");

            migrationBuilder.DropTable(
                name: "DatabaseBackupSets");
        }
    }
}
