using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class BackfillMoneyAccountsFromOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "MoneyAccounts" ("Name", "Source", "Currency", "IsDefault", "IsActive", "CreatedAt")
                SELECT "AccountName", "Source", "Currency", FALSE, TRUE, CURRENT_TIMESTAMP
                FROM (
                    SELECT
                        BTRIM("AccountName") AS "AccountName",
                        "Source",
                        COALESCE(MAX(NULLIF(BTRIM("Currency"), '')), 'RUB') AS "Currency"
                    FROM "MoneyOperations"
                    WHERE "MoneyAccountId" IS NULL
                      AND "AccountName" IS NOT NULL
                      AND BTRIM("AccountName") <> ''
                      AND "Source" IN ('Bank', 'Cash', 'Card', 'EMoney')
                    GROUP BY "Source", BTRIM("AccountName")
                ) AS source_accounts
                ON CONFLICT ("Source", "Name") DO NOTHING;
                """);

            migrationBuilder.Sql("""
                UPDATE "MoneyAccounts" AS target
                SET "IsDefault" = TRUE
                WHERE target."Id" IN (
                    SELECT MIN(candidate."Id")
                    FROM "MoneyAccounts" AS candidate
                    WHERE candidate."Source" IN ('Bank', 'Cash', 'Card', 'EMoney')
                      AND candidate."IsActive" = TRUE
                      AND NOT EXISTS (
                          SELECT 1
                          FROM "MoneyAccounts" AS existing
                          WHERE existing."Source" = candidate."Source"
                            AND existing."IsDefault" = TRUE
                      )
                    GROUP BY candidate."Source"
                );
                """);

            migrationBuilder.Sql("""
                UPDATE "MoneyOperations" AS operation
                SET "MoneyAccountId" = (
                    SELECT account."Id"
                    FROM "MoneyAccounts" AS account
                    WHERE account."Source" = operation."Source"
                      AND account."Name" = BTRIM(operation."AccountName")
                    LIMIT 1
                )
                WHERE operation."MoneyAccountId" IS NULL
                  AND operation."AccountName" IS NOT NULL
                  AND BTRIM(operation."AccountName") <> ''
                  AND operation."Source" IN ('Bank', 'Cash', 'Card', 'EMoney');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "MoneyOperations"
                SET "MoneyAccountId" = NULL
                WHERE "MoneyAccountId" IS NOT NULL;
                """);
        }
    }
}
