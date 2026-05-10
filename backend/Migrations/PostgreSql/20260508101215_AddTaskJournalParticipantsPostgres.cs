using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddTaskJournalParticipantsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskJournalParticipants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskJournalItemId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "assignee"),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, defaultValue: ""),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    Resolution = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskJournalParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskJournalParticipants_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskJournalParticipants_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalParticipants_Task_Role",
                table: "TaskJournalParticipants",
                columns: new[] { "TaskJournalItemId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalParticipants_Task_Role_Order",
                table: "TaskJournalParticipants",
                columns: new[] { "TaskJournalItemId", "Role", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalParticipants_TaskJournalItemId",
                table: "TaskJournalParticipants",
                column: "TaskJournalItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalParticipants_UserId",
                table: "TaskJournalParticipants",
                column: "UserId");

            migrationBuilder.Sql("""
                INSERT INTO "TaskJournalParticipants"
                    ("TaskJournalItemId", "UserId", "Role", "DisplayName", "Email", "SortOrder", "ApprovalStatus", "CreatedAt", "UpdatedAt")
                SELECT
                    t."Id",
                    t."AssigneeUserId",
                    'assignee',
                    COALESCE(NULLIF(t."AssigneeName", ''), NULLIF(u."FullName", ''), NULLIF(u."Email", ''), 'Исполнитель'),
                    COALESCE(u."Email", ''),
                    0,
                    'pending',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM "TaskJournalItems" t
                LEFT JOIN "Users" u ON u."Id" = t."AssigneeUserId"
                WHERE t."AssigneeUserId" IS NOT NULL OR t."AssigneeName" <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskJournalParticipants");
        }
    }
}
