using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    [Migration("20260521173030_AddClientCaseCalendarAuditColumnsPostgres")]
    [DbContext(typeof(PostgreSqlPaymentContext))]
    public partial class AddClientCaseCalendarAuditColumnsPostgres : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            AddAuditColumns(migrationBuilder, "ClientCaseProceduralDeadlines", "ClientCaseProceduralDeadline");
            AddAuditColumns(migrationBuilder, "ClientCaseCourtEvents", "ClientCaseCourtEvent");
            AddAuditColumns(migrationBuilder, "ClientCaseCourtParties", "ClientCaseCourtParty");
            AddAuditColumns(migrationBuilder, "ClientCaseCourts", "ClientCaseCourt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropAuditColumns(migrationBuilder, "ClientCaseProceduralDeadlines", "ClientCaseProceduralDeadline");
            DropAuditColumns(migrationBuilder, "ClientCaseCourtEvents", "ClientCaseCourtEvent");
            DropAuditColumns(migrationBuilder, "ClientCaseCourtParties", "ClientCaseCourtParty");
            DropAuditColumns(migrationBuilder, "ClientCaseCourts", "ClientCaseCourt");
        }

        private static void AddAuditColumns(MigrationBuilder migrationBuilder, string tableName, string entityName)
        {
            migrationBuilder.Sql($"""
                ALTER TABLE "{tableName}" ADD COLUMN IF NOT EXISTS "IsDeleted" boolean NOT NULL DEFAULT FALSE;
                ALTER TABLE "{tableName}" ADD COLUMN IF NOT EXISTS "DeletedAt" timestamp with time zone NULL;
                ALTER TABLE "{tableName}" ADD COLUMN IF NOT EXISTS "DeletedByUserId" integer NULL;
                ALTER TABLE "{tableName}" ADD COLUMN IF NOT EXISTS "DeletedByUserName" character varying(256) NULL;
                CREATE INDEX IF NOT EXISTS "IX_{entityName}_IsDeleted" ON "{tableName}" ("IsDeleted");
                CREATE INDEX IF NOT EXISTS "IX_{entityName}_DeletedAt_Audit" ON "{tableName}" ("DeletedAt");
                CREATE INDEX IF NOT EXISTS "IX_{entityName}_UpdatedAt_Audit" ON "{tableName}" ("UpdatedAt");
                """);
        }

        private static void DropAuditColumns(MigrationBuilder migrationBuilder, string tableName, string entityName)
        {
            migrationBuilder.Sql($"""
                DROP INDEX IF EXISTS "IX_{entityName}_UpdatedAt_Audit";
                DROP INDEX IF EXISTS "IX_{entityName}_DeletedAt_Audit";
                DROP INDEX IF EXISTS "IX_{entityName}_IsDeleted";
                ALTER TABLE "{tableName}" DROP COLUMN IF EXISTS "DeletedByUserName";
                ALTER TABLE "{tableName}" DROP COLUMN IF EXISTS "DeletedByUserId";
                ALTER TABLE "{tableName}" DROP COLUMN IF EXISTS "DeletedAt";
                ALTER TABLE "{tableName}" DROP COLUMN IF EXISTS "IsDeleted";
                """);
        }
    }
}
