using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskRecurrence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecurrenceDayOfMonth",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecurrenceDayOfWeek",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RecurrenceEnabled",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceFrequency",
                table: "TaskJournalItems",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecurrenceInterval",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "RecurrenceMonth",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecurrenceNextAt",
                table: "TaskJournalItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecurrencePreviousTaskId",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecurrenceSequence",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RecurrenceSkipWeekends",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RecurrenceTimeMinutes",
                table: "TaskJournalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceTimeZoneId",
                table: "TaskJournalItems",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_RecurrenceNextAt",
                table: "TaskJournalItems",
                column: "RecurrenceNextAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskJournalItems_RecurrencePreviousTaskId",
                table: "TaskJournalItems",
                column: "RecurrencePreviousTaskId",
                unique: true,
                filter: "\"RecurrencePreviousTaskId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_RecurrenceNextAt",
                table: "TaskJournalItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskJournalItems_RecurrencePreviousTaskId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceDayOfMonth",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceDayOfWeek",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceEnabled",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceFrequency",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceInterval",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceMonth",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceNextAt",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrencePreviousTaskId",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceSequence",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceSkipWeekends",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceTimeMinutes",
                table: "TaskJournalItems");

            migrationBuilder.DropColumn(
                name: "RecurrenceTimeZoneId",
                table: "TaskJournalItems");
        }
    }
}
