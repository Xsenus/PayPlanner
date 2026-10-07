using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseDocumentNotificationDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaseDocumentNotificationDispatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserNotificationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ToastEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseDocumentNotificationDispatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseDocumentNotificationDispatches_UserNotifications_UserNotificationId",
                        column: x => x.UserNotificationId,
                        principalTable: "UserNotifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseDocumentNotificationDispatches_Status_LeaseExpiresAt_Id",
                table: "CaseDocumentNotificationDispatches",
                columns: new[] { "Status", "LeaseExpiresAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseDocumentNotificationDispatches_Status_NextAttemptAt_Id",
                table: "CaseDocumentNotificationDispatches",
                columns: new[] { "Status", "NextAttemptAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseDocumentNotificationDispatches_UserNotificationId_Channel",
                table: "CaseDocumentNotificationDispatches",
                columns: new[] { "UserNotificationId", "Channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseDocumentNotificationDispatches");
        }
    }
}
