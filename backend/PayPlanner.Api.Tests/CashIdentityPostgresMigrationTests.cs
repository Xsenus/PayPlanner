using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations.PostgreSql;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CashIdentityPostgresMigrationTests
{
    [Fact]
    public async Task ActualCashIdentityMigration_UpgradesSyntheticLegacyRowsOnPostgres()
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        var password = Environment.GetEnvironmentVariable("PAYPLANNER_LOCAL_TEST_PG_PASSWORD");
        var builder = !string.IsNullOrWhiteSpace(configured) ? new NpgsqlConnectionStringBuilder(configured)
            : new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 5433, Username = "payplanner_ci", Password = password };
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") || builder.Username != "payplanner_ci" || string.IsNullOrWhiteSpace(builder.Password))
            throw new InvalidOperationException("A loopback PostgreSQL endpoint with the isolated payplanner_ci role is required.");
        builder.Database = "postgres";
        builder.Pooling = false;
        var databaseName = "pp_cash_identity_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^pp_cash_identity_[a-f0-9]{32}$", databaseName);
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var verify = new NpgsqlCommand("SELECT rolcreatedb AND NOT rolsuper AND NOT rolcreaterole FROM pg_roles WHERE rolname = current_user", admin))
            Assert.Equal(true, await verify.ExecuteScalarAsync());
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync();
            created = true;
            builder.Database = databaseName;
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using (var seed = new NpgsqlCommand("""
                CREATE TABLE "LegalEntities" ("Id" integer PRIMARY KEY);
                CREATE TABLE "MoneyAccounts" ("Id" integer PRIMARY KEY);
                CREATE TABLE "CashOrders" ("Id" integer PRIMARY KEY, "Date" timestamp with time zone NOT NULL, "Amount" numeric NOT NULL);
                CREATE TABLE "CashInventories" ("Id" integer PRIMARY KEY, "Date" timestamp with time zone NOT NULL,
                    "CorrectionCashOrderId" integer REFERENCES "CashOrders"("Id"));
                CREATE TABLE "__EFMigrationsHistory" ("MigrationId" character varying(150) PRIMARY KEY, "ProductVersion" character varying(32) NOT NULL);
                INSERT INTO "LegalEntities" VALUES (1);
                INSERT INTO "MoneyAccounts" VALUES (2);
                INSERT INTO "CashOrders" VALUES (10, '2026-10-28T00:00:00Z', 123.45);
                INSERT INTO "CashInventories" VALUES (20, '2026-10-28T00:00:00Z', 10);
                """, connection)) await seed.ExecuteNonQueryAsync();
            var migrationAssembly = typeof(CashInventoryAccountIdentityPostgreSql).Assembly.GetName().Name!;
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(builder.ConnectionString, npgsql => npgsql.MigrationsAssembly(migrationAssembly)).Options;
            await using (var context = new PostgreSqlPaymentContext(options))
            {
                const string previous = "20261028070010_BindApprovalNotificationStage";
                var from = context.Database.GetMigrations().Contains(previous) ? previous : "0";
                var sql = context.GetService<IMigrator>().GenerateScript(from, "20261028080010_CashInventoryAccountIdentityPostgreSql");
                await using var migrate = new NpgsqlCommand(sql, connection);
                await migrate.ExecuteNonQueryAsync();
            }
            await using (var check = new NpgsqlCommand("""
                SELECT o."Amount", o."MoneyAccountId", o."CompanyId", i."CorrectionCashOrderId", i."MoneyAccountId", i."CompanyId"
                FROM "CashOrders" o JOIN "CashInventories" i ON i."CorrectionCashOrderId" = o."Id" WHERE o."Id" = 10;
                """, connection))
            await using (var reader = await check.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(123.45m, reader.GetDecimal(0));
                Assert.True(reader.IsDBNull(1));
                Assert.True(reader.IsDBNull(2));
                Assert.Equal(10, reader.GetInt32(3));
                Assert.True(reader.IsDBNull(4));
                Assert.True(reader.IsDBNull(5));
                Assert.False(await reader.ReadAsync());
            }
            await using (var assign = new NpgsqlCommand("UPDATE \"CashOrders\" SET \"MoneyAccountId\" = 2, \"CompanyId\" = 1 WHERE \"Id\" = 10", connection))
                Assert.Equal(1, await assign.ExecuteNonQueryAsync());
            await using (var invalid = new NpgsqlCommand("UPDATE \"CashInventories\" SET \"MoneyAccountId\" = 999 WHERE \"Id\" = 20", connection))
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
            }
            await using (var referenced = new NpgsqlCommand("DELETE FROM \"MoneyAccounts\" WHERE \"Id\" = 2", connection))
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => referenced.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
            }
            await using (var indexes = new NpgsqlCommand("""
                SELECT count(*) FROM pg_indexes WHERE schemaname = current_schema()
                AND indexname IN ('IX_CashOrders_CompanyId', 'IX_CashOrders_MoneyAccount_Date',
                    'IX_CashInventories_Company_Account_Date', 'IX_CashInventories_MoneyAccountId');
                """, connection)) Assert.Equal(4L, await indexes.ExecuteScalarAsync());
            await using (var checkHistory = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20261028080010_CashInventoryAccountIdentityPostgreSql'", connection))
                Assert.Equal(1L, await checkHistory.ExecuteScalarAsync());
        }
        finally
        {
            if (created)
            {
                Assert.Matches("^pp_cash_identity_[a-f0-9]{32}$", databaseName);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }
}
