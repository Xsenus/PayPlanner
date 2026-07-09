using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddTaskDeadlineChangeRequestsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskJournalDeadlineChangeRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskJournalItemId = table.Column<int>(type: "integer", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RequestedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ReviewerUserId = table.Column<int>(type: "integer", nullable: true),
                    ReviewerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CurrentDueAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RequestedDueAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    DecisionDueAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DecidedByUserId = table.Column<int>(type: "integer", nullable: true),
                    DecidedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedCommentId = table.Column<int>(type: "integer", nullable: true),
                    DecisionCommentId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskJournalDeadlineChangeRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskJournalDeadlineChangeRequests_TaskJournalComments_Creat~",
                        column: x => x.CreatedCommentId,
                        principalTable: "TaskJournalComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskJournalDeadlineChangeRequests_TaskJournalComments_Decis~",
                        column: x => x.DecisionCommentId,
                        principalTable: "TaskJournalComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskJournalDeadlineChangeRequests_TaskJournalItems_TaskJour~",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskJournalDeadlineChangeRequests_Users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskJournalDeadlineChangeRequests_Users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskJournalDeadlineChangeRequests_Users_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeadlineChangeRequests_CreatedCommentId",
                table: "TaskJournalDeadlineChangeRequests",
                column: "CreatedCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeadlineChangeRequests_DecidedByUserId",
                table: "TaskJournalDeadlineChangeRequests",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeadlineChangeRequests_DecisionCommentId",
                table: "TaskJournalDeadlineChangeRequests",
                column: "DecisionCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeadlineChangeRequests_RequestedByUserId",
                table: "TaskJournalDeadlineChangeRequests",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeadlineChangeRequests_Reviewer_Status_Created",
                table: "TaskJournalDeadlineChangeRequests",
                columns: new[] { "ReviewerUserId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeadlineChangeRequests_ReviewerUserId",
                table: "TaskJournalDeadlineChangeRequests",
                column: "ReviewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeadlineChangeRequests_Task_Status",
                table: "TaskJournalDeadlineChangeRequests",
                columns: new[] { "TaskJournalItemId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeadlineChangeRequests_TaskJournalItemId",
                table: "TaskJournalDeadlineChangeRequests",
                column: "TaskJournalItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskJournalDeadlineChangeRequests");
        }
    }
}
