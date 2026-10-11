using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseBackupAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseBackupAttempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RootIdentity = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ScheduleSlot = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackupSetId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseBackupAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseBackupAttempts_DatabaseBackupSets_BackupSetId",
                        column: x => x.BackupSetId,
                        principalTable: "DatabaseBackupSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBackupAttempts_BackupSetId",
                table: "DatabaseBackupAttempts",
                column: "BackupSetId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBackupAttempts_RootIdentity_Status",
                table: "DatabaseBackupAttempts",
                columns: new[] { "RootIdentity", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBackupAttempts_StartedAt_Id",
                table: "DatabaseBackupAttempts",
                columns: new[] { "StartedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseBackupAttempts");
        }
    }
}
