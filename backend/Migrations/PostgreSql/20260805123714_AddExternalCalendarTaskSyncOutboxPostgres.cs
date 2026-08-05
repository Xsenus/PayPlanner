using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddExternalCalendarTaskSyncOutboxPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalCalendarTaskSyncRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskJournalItemId = table.Column<int>(type: "integer", nullable: false),
                    SourceProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalCalendarTaskSyncRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalCalendarTaskSyncRequests_Status_NextAttempt_Id",
                table: "ExternalCalendarTaskSyncRequests",
                columns: new[] { "Status", "NextAttemptAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalCalendarTaskSyncRequests_Task_Source_Id",
                table: "ExternalCalendarTaskSyncRequests",
                columns: new[] { "TaskJournalItemId", "SourceProvider", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalCalendarTaskSyncRequests");
        }
    }
}
