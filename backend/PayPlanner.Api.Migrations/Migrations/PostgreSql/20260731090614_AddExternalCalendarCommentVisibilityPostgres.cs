using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddExternalCalendarCommentVisibilityPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalAuthorTrust",
                table: "TaskJournalComments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalCommentId",
                table: "TaskJournalComments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExternalDeletedAt",
                table: "TaskJournalComments",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRevision",
                table: "TaskJournalComments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "TaskJournalComments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSyncHash",
                table: "TaskJournalComments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExternalSyncedAt",
                table: "TaskJournalComments",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsExternalCalendarVisible",
                table: "TaskJournalComments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalComments_ExternalSource_CommentId",
                table: "TaskJournalComments",
                columns: new[] { "ExternalSource", "ExternalCommentId" },
                unique: true,
                filter: "\"ExternalCommentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalComments_Task_ExternalVisible_CreatedAt",
                table: "TaskJournalComments",
                columns: new[] { "TaskJournalItemId", "IsExternalCalendarVisible", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskJournalComments_ExternalSource_CommentId",
                table: "TaskJournalComments");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalComments_Task_ExternalVisible_CreatedAt",
                table: "TaskJournalComments");

            migrationBuilder.DropColumn(
                name: "ExternalAuthorTrust",
                table: "TaskJournalComments");

            migrationBuilder.DropColumn(
                name: "ExternalCommentId",
                table: "TaskJournalComments");

            migrationBuilder.DropColumn(
                name: "ExternalDeletedAt",
                table: "TaskJournalComments");

            migrationBuilder.DropColumn(
                name: "ExternalRevision",
                table: "TaskJournalComments");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "TaskJournalComments");

            migrationBuilder.DropColumn(
                name: "ExternalSyncHash",
                table: "TaskJournalComments");

            migrationBuilder.DropColumn(
                name: "ExternalSyncedAt",
                table: "TaskJournalComments");

            migrationBuilder.DropColumn(
                name: "IsExternalCalendarVisible",
                table: "TaskJournalComments");
        }
    }
}
