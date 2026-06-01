using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddGoogleCalendarSyncPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GoogleCalendarAuthRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReturnUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleCalendarAuthRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarAuthRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GoogleCalendarConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    GoogleUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GoogleEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AccessTokenProtected = table.Column<string>(type: "text", nullable: true),
                    RefreshTokenProtected = table.Column<string>(type: "text", nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Scope = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SyncEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CalendarMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "personal"),
                    ConflictResolutionMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "latestWins"),
                    DefaultCalendarId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DefaultCalendarName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastFullSyncAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastIncrementalSyncAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleCalendarConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarConnections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GoogleCalendarSyncCalendars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectionId = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<int>(type: "integer", nullable: true),
                    GoogleCalendarId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SyncToken = table.Column<string>(type: "text", nullable: true),
                    WatchChannelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    WatchResourceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    WatchToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    WatchExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleCalendarSyncCalendars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarSyncCalendars_GoogleCalendarConnections_Conne~",
                        column: x => x.ConnectionId,
                        principalTable: "GoogleCalendarConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarSyncCalendars_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "GoogleCalendarSyncLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectionId = table.Column<int>(type: "integer", nullable: true),
                    CalendarSyncCalendarId = table.Column<int>(type: "integer", nullable: true),
                    TaskJournalItemId = table.Column<int>(type: "integer", nullable: true),
                    Direction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DetailsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleCalendarSyncLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarSyncLogs_GoogleCalendarConnections_Connection~",
                        column: x => x.ConnectionId,
                        principalTable: "GoogleCalendarConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarSyncLogs_GoogleCalendarSyncCalendars_Calendar~",
                        column: x => x.CalendarSyncCalendarId,
                        principalTable: "GoogleCalendarSyncCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarSyncLogs_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "GoogleCalendarTaskLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectionId = table.Column<int>(type: "integer", nullable: false),
                    CalendarSyncCalendarId = table.Column<int>(type: "integer", nullable: false),
                    TaskJournalItemId = table.Column<int>(type: "integer", nullable: false),
                    GoogleCalendarId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    GoogleEventId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    GoogleEtag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastCrmHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastGoogleHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastDirection = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SyncStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "active"),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleCalendarTaskLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarTaskLinks_GoogleCalendarConnections_Connectio~",
                        column: x => x.ConnectionId,
                        principalTable: "GoogleCalendarConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarTaskLinks_GoogleCalendarSyncCalendars_Calenda~",
                        column: x => x.CalendarSyncCalendarId,
                        principalTable: "GoogleCalendarSyncCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GoogleCalendarTaskLinks_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarAuthRequests_State",
                table: "GoogleCalendarAuthRequests",
                column: "State",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarAuthRequests_User_Status",
                table: "GoogleCalendarAuthRequests",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarConnections_GoogleUserId",
                table: "GoogleCalendarConnections",
                column: "GoogleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarConnections_UserId",
                table: "GoogleCalendarConnections",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncCalendars_Connection_GoogleCalendar",
                table: "GoogleCalendarSyncCalendars",
                columns: new[] { "ConnectionId", "GoogleCalendarId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncCalendars_Connection_Owner",
                table: "GoogleCalendarSyncCalendars",
                columns: new[] { "ConnectionId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncCalendars_OwnerUserId",
                table: "GoogleCalendarSyncCalendars",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncCalendars_WatchChannelId",
                table: "GoogleCalendarSyncCalendars",
                column: "WatchChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncLogs_CalendarSyncCalendarId",
                table: "GoogleCalendarSyncLogs",
                column: "CalendarSyncCalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncLogs_Connection_CreatedAt",
                table: "GoogleCalendarSyncLogs",
                columns: new[] { "ConnectionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarSyncLogs_TaskJournalItemId",
                table: "GoogleCalendarSyncLogs",
                column: "TaskJournalItemId");

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarTaskLinks_CalendarSyncCalendarId",
                table: "GoogleCalendarTaskLinks",
                column: "CalendarSyncCalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarTaskLinks_Connection_Task",
                table: "GoogleCalendarTaskLinks",
                columns: new[] { "ConnectionId", "TaskJournalItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarTaskLinks_GoogleEvent",
                table: "GoogleCalendarTaskLinks",
                columns: new[] { "GoogleCalendarId", "GoogleEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_GoogleCalendarTaskLinks_TaskJournalItemId",
                table: "GoogleCalendarTaskLinks",
                column: "TaskJournalItemId");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoogleCalendarAuthRequests");

            migrationBuilder.DropTable(
                name: "GoogleCalendarSyncLogs");

            migrationBuilder.DropTable(
                name: "GoogleCalendarTaskLinks");

            migrationBuilder.DropTable(
                name: "GoogleCalendarSyncCalendars");

            migrationBuilder.DropTable(
                name: "GoogleCalendarConnections");
        }
    }
}
