using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddYandexCalendarSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YandexCalendarAuthRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ReturnUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "pending"),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YandexCalendarAuthRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_YandexCalendarAuthRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "YandexCalendarConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    YandexUserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    YandexEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AccessTokenProtected = table.Column<string>(type: "TEXT", nullable: true),
                    RefreshTokenProtected = table.Column<string>(type: "TEXT", nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    SyncEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CalendarMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "personal"),
                    ConflictResolutionMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "latestWins"),
                    DefaultCalendarId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    DefaultCalendarName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LastFullSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastIncrementalSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YandexCalendarConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_YandexCalendarConnections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "YandexCalendarSyncCalendars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    YandexCalendarId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CalendarUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SyncToken = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YandexCalendarSyncCalendars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_YandexCalendarSyncCalendars_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_YandexCalendarSyncCalendars_YandexCalendarConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "YandexCalendarConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "YandexCalendarSyncLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    CalendarSyncCalendarId = table.Column<int>(type: "INTEGER", nullable: true),
                    TaskJournalItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    DetailsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YandexCalendarSyncLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_YandexCalendarSyncLogs_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_YandexCalendarSyncLogs_YandexCalendarConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "YandexCalendarConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_YandexCalendarSyncLogs_YandexCalendarSyncCalendars_CalendarSyncCalendarId",
                        column: x => x.CalendarSyncCalendarId,
                        principalTable: "YandexCalendarSyncCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "YandexCalendarTaskLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    CalendarSyncCalendarId = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskJournalItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    YandexCalendarId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    YandexEventId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    YandexEventUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    YandexEtag = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LastCrmHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastYandexHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastDirection = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SyncStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "active"),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YandexCalendarTaskLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_YandexCalendarTaskLinks_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_YandexCalendarTaskLinks_YandexCalendarConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "YandexCalendarConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_YandexCalendarTaskLinks_YandexCalendarSyncCalendars_CalendarSyncCalendarId",
                        column: x => x.CalendarSyncCalendarId,
                        principalTable: "YandexCalendarSyncCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarAuthRequests_State",
                table: "YandexCalendarAuthRequests",
                column: "State",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarAuthRequests_User_Status",
                table: "YandexCalendarAuthRequests",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarConnections_UserId",
                table: "YandexCalendarConnections",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarConnections_YandexUserId",
                table: "YandexCalendarConnections",
                column: "YandexUserId");

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarSyncCalendars_Connection_Owner",
                table: "YandexCalendarSyncCalendars",
                columns: new[] { "ConnectionId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarSyncCalendars_Connection_YandexCalendar",
                table: "YandexCalendarSyncCalendars",
                columns: new[] { "ConnectionId", "YandexCalendarId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarSyncCalendars_OwnerUserId",
                table: "YandexCalendarSyncCalendars",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarSyncLogs_CalendarSyncCalendarId",
                table: "YandexCalendarSyncLogs",
                column: "CalendarSyncCalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarSyncLogs_Connection_CreatedAt",
                table: "YandexCalendarSyncLogs",
                columns: new[] { "ConnectionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarSyncLogs_TaskJournalItemId",
                table: "YandexCalendarSyncLogs",
                column: "TaskJournalItemId");

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarTaskLinks_CalendarSyncCalendarId",
                table: "YandexCalendarTaskLinks",
                column: "CalendarSyncCalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarTaskLinks_Connection_Task",
                table: "YandexCalendarTaskLinks",
                columns: new[] { "ConnectionId", "TaskJournalItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarTaskLinks_TaskJournalItemId",
                table: "YandexCalendarTaskLinks",
                column: "TaskJournalItemId");

            migrationBuilder.CreateIndex(
                name: "IX_YandexCalendarTaskLinks_YandexEvent",
                table: "YandexCalendarTaskLinks",
                columns: new[] { "YandexCalendarId", "YandexEventId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "YandexCalendarAuthRequests");

            migrationBuilder.DropTable(
                name: "YandexCalendarSyncLogs");

            migrationBuilder.DropTable(
                name: "YandexCalendarTaskLinks");

            migrationBuilder.DropTable(
                name: "YandexCalendarSyncCalendars");

            migrationBuilder.DropTable(
                name: "YandexCalendarConnections");
        }
    }
}
