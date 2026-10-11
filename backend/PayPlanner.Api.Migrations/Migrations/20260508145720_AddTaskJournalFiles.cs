using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskJournalFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskJournalFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskJournalItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    StoredFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, defaultValue: "application/octet-stream"),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UploadedByName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskJournalFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskJournalFiles_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskJournalFiles_Users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalFiles_Task_Document_Version",
                table: "TaskJournalFiles",
                columns: new[] { "TaskJournalItemId", "DocumentKey", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalFiles_TaskJournalItemId",
                table: "TaskJournalFiles",
                column: "TaskJournalItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalFiles_UploadedByUserId",
                table: "TaskJournalFiles",
                column: "UploadedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskJournalFiles");
        }
    }
}
