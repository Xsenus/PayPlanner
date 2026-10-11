using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class BackfillMoneyAccountsFromOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO MoneyAccounts (Name, Source, Currency, IsDefault, IsActive, CreatedAt)
                SELECT AccountName, Source, Currency, 0, 1, CURRENT_TIMESTAMP
                FROM (
                    SELECT
                        TRIM(AccountName) AS AccountName,
                        Source,
                        COALESCE(MAX(NULLIF(TRIM(Currency), '')), 'RUB') AS Currency
                    FROM MoneyOperations
                    WHERE MoneyAccountId IS NULL
                      AND AccountName IS NOT NULL
                      AND TRIM(AccountName) <> ''
                      AND Source IN ('Bank', 'Cash', 'Card', 'EMoney')
                    GROUP BY Source, TRIM(AccountName)
                ) AS sourceAccounts;
                """);

            migrationBuilder.Sql("""
                UPDATE MoneyAccounts
                SET IsDefault = 1
                WHERE Id IN (
                    SELECT MIN(candidate.Id)
                    FROM MoneyAccounts AS candidate
                    WHERE candidate.Source IN ('Bank', 'Cash', 'Card', 'EMoney')
                      AND candidate.IsActive = 1
                      AND NOT EXISTS (
                          SELECT 1
                          FROM MoneyAccounts AS existing
                          WHERE existing.Source = candidate.Source
                            AND existing.IsDefault = 1
                      )
                    GROUP BY candidate.Source
                );
                """);

            migrationBuilder.Sql("""
                UPDATE MoneyOperations
                SET MoneyAccountId = (
                    SELECT account.Id
                    FROM MoneyAccounts AS account
                    WHERE account.Source = MoneyOperations.Source
                      AND account.Name = TRIM(MoneyOperations.AccountName)
                    LIMIT 1
                )
                WHERE MoneyAccountId IS NULL
                  AND AccountName IS NOT NULL
                  AND TRIM(AccountName) <> ''
                  AND Source IN ('Bank', 'Cash', 'Card', 'EMoney');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE MoneyOperations
                SET MoneyAccountId = NULL
                WHERE MoneyAccountId IS NOT NULL;
                """);
        }
    }
}
