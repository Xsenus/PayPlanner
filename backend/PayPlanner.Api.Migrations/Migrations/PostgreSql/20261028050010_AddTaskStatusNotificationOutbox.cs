using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddTaskStatusNotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskStatusNotificationOutboxes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskJournalItemId = table.Column<int>(type: "integer", nullable: false),
                    EventKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    EventType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ActorUserId = table.Column<int>(type: "integer", nullable: false),
                    ActorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LeaseToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskStatusNotificationOutboxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskStatusNotificationOutboxes_TaskJournalItems_TaskJournal~",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskStatusNotificationOutboxes_CompletedAt_LeaseUntil_Id",
                table: "TaskStatusNotificationOutboxes",
                columns: new[] { "CompletedAt", "LeaseUntil", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskStatusNotificationOutboxes_EventKey",
                table: "TaskStatusNotificationOutboxes",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskStatusNotificationOutboxes_TaskJournalItemId_Id",
                table: "TaskStatusNotificationOutboxes",
                columns: new[] { "TaskJournalItemId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskStatusNotificationOutboxes");
        }
    }
}
