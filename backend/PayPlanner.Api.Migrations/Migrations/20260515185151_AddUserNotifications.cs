using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUserNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecipientUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    BodyPreview = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Tone = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ObjectType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    TaskJournalItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserNotifications_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UserNotifications_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_TaskJournalItemId",
                table: "UserNotifications",
                column: "TaskJournalItemId");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_User_Archived_Deleted_Created",
                table: "UserNotifications",
                columns: new[] { "RecipientUserId", "IsArchived", "DeletedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_User_EventKey",
                table: "UserNotifications",
                columns: new[] { "RecipientUserId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_User_Read_Deleted",
                table: "UserNotifications",
                columns: new[] { "RecipientUserId", "IsRead", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_User_Type_Deleted",
                table: "UserNotifications",
                columns: new[] { "RecipientUserId", "Type", "DeletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserNotifications");
        }
    }
}
