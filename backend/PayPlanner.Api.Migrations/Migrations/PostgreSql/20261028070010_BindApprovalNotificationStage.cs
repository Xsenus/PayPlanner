using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    public partial class BindApprovalNotificationStage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "ApprovalParticipantId", table: "TaskStatusNotificationOutboxes", type: "integer", nullable: true);
            migrationBuilder.CreateIndex(name: "IX_TaskStatusNotificationOutboxes_ApprovalParticipantId", table: "TaskStatusNotificationOutboxes", column: "ApprovalParticipantId");
            migrationBuilder.AddForeignKey(name: "FK_TaskStatusNotificationOutboxes_TaskJournalParticipants_ApprovalParticipantId", table: "TaskStatusNotificationOutboxes", column: "ApprovalParticipantId", principalTable: "TaskJournalParticipants", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_TaskStatusNotificationOutboxes_TaskJournalParticipants_ApprovalParticipantId", table: "TaskStatusNotificationOutboxes");
            migrationBuilder.DropIndex(name: "IX_TaskStatusNotificationOutboxes_ApprovalParticipantId", table: "TaskStatusNotificationOutboxes");
            migrationBuilder.DropColumn(name: "ApprovalParticipantId", table: "TaskStatusNotificationOutboxes");
        }
    }
}
