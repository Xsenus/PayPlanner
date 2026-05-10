using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskJournalComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskJournalComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskJournalItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AuthorName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "comment"),
                    Text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskJournalComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskJournalComments_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskJournalComments_Users_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalComments_AuthorUserId",
                table: "TaskJournalComments",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalComments_Kind",
                table: "TaskJournalComments",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalComments_Task_CreatedAt",
                table: "TaskJournalComments",
                columns: new[] { "TaskJournalItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalComments_TaskJournalItemId",
                table: "TaskJournalComments",
                column: "TaskJournalItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskJournalComments");
        }
    }
}
