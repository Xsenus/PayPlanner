using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddTaskTimeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskTimeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    UserNameSnapshot = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    StoppedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CorrectedSeconds = table.Column<int>(type: "integer", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTimeEntries", x => x.Id);
                    table.CheckConstraint("CK_TaskTimeEntries_Correction", "(\"CorrectedSeconds\" IS NULL OR \"CorrectedSeconds\" >= 0) AND \"Revision\" >= 0");
                    table.CheckConstraint("CK_TaskTimeEntries_OrderedInterval", "\"StoppedAtUtc\" IS NULL OR \"StoppedAtUtc\" >= \"StartedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_TaskTimeEntries_TaskJournalItems_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskTimeEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TaskTimeCorrections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousSeconds = table.Column<int>(type: "integer", nullable: true),
                    NewSeconds = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTimeCorrections", x => x.Id);
                    table.CheckConstraint("CK_TaskTimeCorrections_NonNegative", "\"NewSeconds\" >= 0 AND (\"PreviousSeconds\" IS NULL OR \"PreviousSeconds\" >= 0)");
                    table.ForeignKey(
                        name: "FK_TaskTimeCorrections_TaskTimeEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "TaskTimeEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskTimeCorrections_EntryId_Id",
                table: "TaskTimeCorrections",
                columns: new[] { "EntryId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskTimeEntries_OneRunningPerUser",
                table: "TaskTimeEntries",
                column: "UserId",
                unique: true,
                filter: "\"StoppedAtUtc\" IS NULL AND \"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskTimeEntries_TaskId_UserId_StartedAtUtc_Id",
                table: "TaskTimeEntries",
                columns: new[] { "TaskId", "UserId", "StartedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskTimeCorrections");

            migrationBuilder.DropTable(
                name: "TaskTimeEntries");
        }
    }
}
