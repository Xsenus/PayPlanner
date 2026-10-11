using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStickyNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StickyNoteBoards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Visibility = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false, defaultValue: "personal"),
                    OwnerUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    OwnerUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Background = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "cork"),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeletedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StickyNoteBoards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StickyNoteBoards_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "StickyNoteBoardMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BoardId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false, defaultValue: "editor"),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StickyNoteBoardMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StickyNoteBoardMembers_StickyNoteBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "StickyNoteBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StickyNoteBoardMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StickyNotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BoardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false, defaultValue: "yellow"),
                    X = table.Column<double>(type: "REAL", nullable: false),
                    Y = table.Column<double>(type: "REAL", nullable: false),
                    Width = table.Column<double>(type: "REAL", nullable: false),
                    Height = table.Column<double>(type: "REAL", nullable: false),
                    ZIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AuthorUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeletedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StickyNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StickyNotes_StickyNoteBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "StickyNoteBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StickyNotes_Users_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StickyNoteBoardMembers_Board_User",
                table: "StickyNoteBoardMembers",
                columns: new[] { "BoardId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StickyNoteBoardMembers_UserId",
                table: "StickyNoteBoardMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNoteBoard_DeletedAt_Audit",
                table: "StickyNoteBoards",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNoteBoard_IsDeleted",
                table: "StickyNoteBoards",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNoteBoard_UpdatedAt_Audit",
                table: "StickyNoteBoards",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNoteBoards_OwnerUserId",
                table: "StickyNoteBoards",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNoteBoards_Visibility",
                table: "StickyNoteBoards",
                column: "Visibility");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNoteBoards_Visibility_SortOrder",
                table: "StickyNoteBoards",
                columns: new[] { "Visibility", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_StickyNote_DeletedAt_Audit",
                table: "StickyNotes",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNote_IsDeleted",
                table: "StickyNotes",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNote_UpdatedAt_Audit",
                table: "StickyNotes",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNotes_AuthorUserId",
                table: "StickyNotes",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNotes_BoardId",
                table: "StickyNotes",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_StickyNotes_BoardId_ZIndex",
                table: "StickyNotes",
                columns: new[] { "BoardId", "ZIndex" });

            migrationBuilder.Sql("""
                INSERT INTO RolePermissions (
                    RoleId,
                    Section,
                    CanView,
                    CanCreate,
                    CanEdit,
                    CanDelete,
                    CanExport,
                    CanViewAnalytics,
                    ViewScope
                )
                SELECT
                    r.Id,
                    'notes',
                    1,
                    1,
                    1,
                    1,
                    0,
                    0,
                    'all'
                FROM Roles r
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM RolePermissions rp
                    WHERE rp.RoleId = r.Id AND rp.Section = 'notes'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM RolePermissions WHERE Section = 'notes';");

            migrationBuilder.DropTable(
                name: "StickyNoteBoardMembers");

            migrationBuilder.DropTable(
                name: "StickyNotes");

            migrationBuilder.DropTable(
                name: "StickyNoteBoards");
        }
    }
}
