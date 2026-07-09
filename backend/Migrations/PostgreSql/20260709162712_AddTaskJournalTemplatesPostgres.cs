using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddTaskJournalTemplatesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskJournalTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TaskDescription = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "task"),
                    Priority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "normal"),
                    PlannedDurationMinutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 60),
                    DueOffsetMinutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 1440),
                    AssigneeUserId = table.Column<int>(type: "integer", nullable: true),
                    AssigneeName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, defaultValue: ""),
                    BindingType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "none"),
                    BindingKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, defaultValue: ""),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    RemindersJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    ChannelsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    TagsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, defaultValue: ""),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "integer", nullable: true),
                    DeletedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskJournalTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskJournalTemplates_Users_AssigneeUserId",
                        column: x => x.AssigneeUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskJournalTemplates_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TaskJournalTemplateParticipants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskJournalTemplateId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "observer"),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, defaultValue: ""),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskJournalTemplateParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskJournalTemplateParticipants_TaskJournalTemplates_TaskJo~",
                        column: x => x.TaskJournalTemplateId,
                        principalTable: "TaskJournalTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskJournalTemplateParticipants_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplateParticipants_Template_Role_Order",
                table: "TaskJournalTemplateParticipants",
                columns: new[] { "TaskJournalTemplateId", "Role", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplateParticipants_TemplateId",
                table: "TaskJournalTemplateParticipants",
                column: "TaskJournalTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplateParticipants_UserId",
                table: "TaskJournalTemplateParticipants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplate_DeletedAt_Audit",
                table: "TaskJournalTemplates",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplate_IsDeleted",
                table: "TaskJournalTemplates",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplate_UpdatedAt_Audit",
                table: "TaskJournalTemplates",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplates_AssigneeUserId",
                table: "TaskJournalTemplates",
                column: "AssigneeUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplates_Binding",
                table: "TaskJournalTemplates",
                columns: new[] { "BindingType", "BindingKey" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplates_CreatedByUserId",
                table: "TaskJournalTemplates",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplates_IsActive",
                table: "TaskJournalTemplates",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplates_Name",
                table: "TaskJournalTemplates",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplates_Priority",
                table: "TaskJournalTemplates",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalTemplates_Type",
                table: "TaskJournalTemplates",
                column: "Type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskJournalTemplateParticipants");

            migrationBuilder.DropTable(
                name: "TaskJournalTemplates");
        }
    }
}
