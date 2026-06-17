using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class RepairPostgresTaskContractorPaymentColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "TaskJournalItems"
                ADD COLUMN IF NOT EXISTS "ContractorPaymentAmount" numeric(18,2) NULL,
                ADD COLUMN IF NOT EXISTS "ContractorPaymentDueDate" date NULL,
                ADD COLUMN IF NOT EXISTS "ContractorPaymentEnabled" boolean NOT NULL DEFAULT false,
                ADD COLUMN IF NOT EXISTS "ContractorPaymentId" integer NULL,
                ADD COLUMN IF NOT EXISTS "ContractorPaymentOnResult" boolean NOT NULL DEFAULT false;
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_TaskJournalItems_ContractorPaymentDueDate"
                ON "TaskJournalItems" ("ContractorPaymentDueDate");
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_TaskJournalItems_ContractorPaymentId"
                ON "TaskJournalItems" ("ContractorPaymentId");
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE conname = 'FK_TaskJournalItems_Payments_ContractorPaymentId'
                    ) THEN
                        ALTER TABLE "TaskJournalItems"
                        ADD CONSTRAINT "FK_TaskJournalItems_Payments_ContractorPaymentId"
                        FOREIGN KEY ("ContractorPaymentId")
                        REFERENCES "Payments" ("Id")
                        ON DELETE SET NULL;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Repair migration: keep rollback non-destructive for databases where these columns existed before this migration.
        }
    }
}
