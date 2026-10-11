using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddTaskJournalPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskJournalItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ObjectName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AssigneeUserId = table.Column<int>(type: "integer", nullable: true),
                    AssigneeName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AuthorUserId = table.Column<int>(type: "integer", nullable: true),
                    AuthorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "task"),
                    Priority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "normal"),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "new"),
                    Result = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RemindersJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    ChannelsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    TagsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "integer", nullable: true),
                    DeletedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskJournalItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskJournalItems_Users_AssigneeUserId",
                        column: x => x.AssigneeUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskJournalItems_Users_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItem_DeletedAt_Audit",
                table: "TaskJournalItems",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItem_IsDeleted",
                table: "TaskJournalItems",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItem_UpdatedAt_Audit",
                table: "TaskJournalItems",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_AssigneeUserId",
                table: "TaskJournalItems",
                column: "AssigneeUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_AuthorUserId",
                table: "TaskJournalItems",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_DueAt",
                table: "TaskJournalItems",
                column: "DueAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_Priority",
                table: "TaskJournalItems",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_Status",
                table: "TaskJournalItems",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_Type",
                table: "TaskJournalItems",
                column: "Type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskJournalItems");
        }
    }
}
