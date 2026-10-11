using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    [Migration("20260816010000_AlignLegacyTimestamptzColumnsWithUtcStoragePostgres")]
    [DbContext(typeof(PostgreSqlPaymentContext))]
    public partial class AlignLegacyTimestamptzColumnsWithUtcStoragePostgres : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConvertColumns(migrationBuilder, "AppReleaseSeen", "SeenAt");
            ConvertColumns(migrationBuilder, "AppReleases", "ReleasedAt", "CreatedAt", "UpdatedAt");
            ConvertColumns(migrationBuilder, "ClientCaseCourtEventHistory", "OccurredAt");
            ConvertColumns(
                migrationBuilder,
                "ClientCaseCourtEvents",
                "ScheduledAt",
                "CreatedAt",
                "UpdatedAt",
                "AuditCreatedAt",
                "AuditUpdatedAt",
                "AuditDeletedAt",
                "DeletedAt");
            ConvertColumns(
                migrationBuilder,
                "ClientCaseCourtParties",
                "CreatedAt",
                "UpdatedAt",
                "AuditCreatedAt",
                "AuditUpdatedAt",
                "AuditDeletedAt",
                "DeletedAt");
            ConvertColumns(migrationBuilder, "ClientCaseCourts", "CreatedAt", "UpdatedAt", "DeletedAt");
            ConvertColumns(migrationBuilder, "ClientCaseProceduralDeadlines", "DeletedAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConvertColumnsBack(migrationBuilder, "AppReleaseSeen", "SeenAt");
            ConvertColumnsBack(migrationBuilder, "AppReleases", "ReleasedAt", "CreatedAt", "UpdatedAt");
            ConvertColumnsBack(migrationBuilder, "ClientCaseCourtEventHistory", "OccurredAt");
            ConvertColumnsBack(
                migrationBuilder,
                "ClientCaseCourtEvents",
                "ScheduledAt",
                "CreatedAt",
                "UpdatedAt",
                "AuditCreatedAt",
                "AuditUpdatedAt",
                "AuditDeletedAt",
                "DeletedAt");
            ConvertColumnsBack(
                migrationBuilder,
                "ClientCaseCourtParties",
                "CreatedAt",
                "UpdatedAt",
                "AuditCreatedAt",
                "AuditUpdatedAt",
                "AuditDeletedAt",
                "DeletedAt");
            ConvertColumnsBack(migrationBuilder, "ClientCaseCourts", "CreatedAt", "UpdatedAt", "DeletedAt");
            ConvertColumnsBack(migrationBuilder, "ClientCaseProceduralDeadlines", "DeletedAt");
        }

        private static void ConvertColumns(MigrationBuilder migrationBuilder, string tableName, params string[] columnNames)
        {
            foreach (var columnName in columnNames)
            {
                migrationBuilder.Sql($$"""
                    ALTER TABLE "{{tableName}}"
                    ALTER COLUMN "{{columnName}}" TYPE timestamp without time zone
                    USING "{{columnName}}" AT TIME ZONE current_setting('TimeZone');
                    """);
            }
        }

        private static void ConvertColumnsBack(MigrationBuilder migrationBuilder, string tableName, params string[] columnNames)
        {
            foreach (var columnName in columnNames)
            {
                migrationBuilder.Sql($$"""
                    ALTER TABLE "{{tableName}}"
                    ALTER COLUMN "{{columnName}}" TYPE timestamp with time zone
                    USING "{{columnName}}" AT TIME ZONE current_setting('TimeZone');
                    """);
            }
        }
    }
}
