using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddYandexCalendarSyncPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YandexCalendarAuthRequests",
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    YandexUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    YandexEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AccessTokenProtected = table.Column<string>(type: "text", nullable: true),
                    RefreshTokenProtected = table.Column<string>(type: "text", nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Scope = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SyncEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CalendarMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "personal"),
                    ConflictResolutionMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "latestWins"),
                    DefaultCalendarId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DefaultCalendarName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastFullSyncAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastIncrementalSyncAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectionId = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<int>(type: "integer", nullable: true),
                    YandexCalendarId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CalendarUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SyncToken = table.Column<string>(type: "text", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
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
                        name: "FK_YandexCalendarSyncCalendars_YandexCalendarConnections_Conne~",
                        column: x => x.ConnectionId,
                        principalTable: "YandexCalendarConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "YandexCalendarSyncLogs",
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
                    table.PrimaryKey("PK_YandexCalendarSyncLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_YandexCalendarSyncLogs_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_YandexCalendarSyncLogs_YandexCalendarConnections_Connection~",
                        column: x => x.ConnectionId,
                        principalTable: "YandexCalendarConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_YandexCalendarSyncLogs_YandexCalendarSyncCalendars_Calendar~",
                        column: x => x.CalendarSyncCalendarId,
                        principalTable: "YandexCalendarSyncCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "YandexCalendarTaskLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectionId = table.Column<int>(type: "integer", nullable: false),
                    CalendarSyncCalendarId = table.Column<int>(type: "integer", nullable: false),
                    TaskJournalItemId = table.Column<int>(type: "integer", nullable: false),
                    YandexCalendarId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    YandexEventId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    YandexEventUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    YandexEtag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastCrmHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastYandexHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastDirection = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SyncStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "active"),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
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
                        name: "FK_YandexCalendarTaskLinks_YandexCalendarConnections_Connectio~",
                        column: x => x.ConnectionId,
                        principalTable: "YandexCalendarConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_YandexCalendarTaskLinks_YandexCalendarSyncCalendars_Calenda~",
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
