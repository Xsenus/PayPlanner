using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class BankImportDetailPostgresTests
{
    [Fact]
    public async Task Detail_PostgresPreservesCurrenciesAndHistoryAcrossPagesAndAccessRevocation()
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        var builder = !string.IsNullOrWhiteSpace(configured) ? new NpgsqlConnectionStringBuilder(configured)
            : new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = 5433, Username = "payplanner_ci",
                Password = Environment.GetEnvironmentVariable("PAYPLANNER_LOCAL_TEST_PG_PASSWORD"),
            };
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") || builder.Username != "payplanner_ci"
            || string.IsNullOrWhiteSpace(builder.Password))
            throw new InvalidOperationException("A loopback PostgreSQL endpoint with the isolated payplanner_ci role is required.");
        builder.Database = "postgres";
        builder.Pooling = false;
        var databaseName = "pp_bank_detail_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^pp_bank_detail_[a-f0-9]{32}$", databaseName);
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var verify = new NpgsqlCommand("SELECT rolcreatedb AND NOT rolsuper AND NOT rolcreaterole FROM pg_roles WHERE rolname = current_user", admin))
            Assert.Equal(true, await verify.ExecuteScalarAsync());
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
                await create.ExecuteNonQueryAsync();
            created = true;
            builder.Database = databaseName;
            await VerifyDetailAsync(builder.ConnectionString);
        }
        finally
        {
            if (created)
            {
                Assert.Matches("^pp_bank_detail_[a-f0-9]{32}$", databaseName);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task VerifyDetailAsync(string connectionString)
    {
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(connectionString).Options);
        // Current-model query qualification only; full migration-chain checks are separate.
        await db.Database.EnsureCreatedAsync();
        var actor = new User
        {
            Email = "bank-detail@fixture.local", FullName = "Synthetic employee", PasswordHash = "hash",
            Role = new Role { Name = "bank-detail", Description = "Synthetic read-only employee" },
            IsActive = true, IsApproved = true, IsEmployee = true,
        };
        var own = new LegalEntity { ShortName = "Своя компания" };
        var foreign = new LegalEntity { ShortName = "Чужая компания" };
        var account = new MoneyAccount { Name = "Банк", Source = MoneyOperationSource.Bank, Currency = "RUB", Company = own, IsActive = true };
        var foreignAccount = new MoneyAccount { Name = "Банк", Source = MoneyOperationSource.Bank, Currency = "RUB", Company = foreign, IsActive = true };
        db.Users.Add(actor);
        db.MoneyAccounts.AddRange(account, foreignAccount);
        await db.SaveChangesAsync();
        var permission = new RolePermission { RoleId = actor.RoleId, Section = RolePermissionCatalog.Payments,
            CanView = true, ViewScope = DataVisibilityScope.Own };
        db.RolePermissions.Add(permission);
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = actor.Id, LegalEntityId = own.Id });
        db.SystemSettings.AddRange(
            new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = JsonSerializer.Serialize(new[] { own.Id, foreign.Id }) },
            new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        var batch = BuildBatch(account, actor.Id, "own", 106);
        var foreignBatch = BuildBatch(foreignAccount, actor.Id, "foreign", 1);
        db.BankStatementImportBatches.AddRange(batch, foreignBatch);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()) }, "Test")),
            } },
        };
        var before = await CaptureHistoryAsync(connectionString);
        async Task<BankStatementImportBatchDetailDto> ReadAsync(int page, int pageSize = 50)
            => Assert.IsType<BankStatementImportBatchDetailDto>(Assert.IsType<OkObjectResult>(
                (await controller.GetBankStatementImport(batch.Id, CancellationToken.None, page, pageSize)).Result).Value);
        var first = await ReadAsync(1);
        Assert.Equal(106, first.RowsTotal);
        Assert.Equal(1, first.RowsPage);
        Assert.Equal(50, first.RowsPageSize);
        Assert.Equal(Enumerable.Range(1, 50), first.Rows.Select(row => row.RowNumber));
        Assert.True(first.HasReprocessableRows); // The only error is on the last page.
        Assert.Equal(new string?[] { "RUB", "USD", null, "" }, first.Rows.Take(4).Select(row => row.Currency));
        Assert.Equal(2.25m, first.Rows[1].Amount);
        Assert.Equal("RUB", account.Currency); // Account currency must not replace row currency.
        var last = await ReadAsync(3);
        Assert.Equal(Enumerable.Range(101, 6), last.Rows.Select(row => row.RowNumber));
        Assert.Equal(BankStatementImportRowStatus.Error, last.Rows[^1].Status);
        var capped = await ReadAsync(1, int.MaxValue);
        Assert.Equal(100, capped.RowsPageSize);
        Assert.Equal(100, capped.Rows.Count);
        var minimum = await ReadAsync(0, 0);
        Assert.Equal(1, minimum.RowsPage);
        Assert.Equal(1, minimum.RowsPageSize);
        Assert.Single(minimum.Rows);
        var beyond = await ReadAsync(int.MaxValue);
        Assert.Equal(1_000_000, beyond.RowsPage);
        Assert.Empty(beyond.Rows);
        Assert.IsType<NotFoundResult>((await controller.GetBankStatementImport(foreignBatch.Id, CancellationToken.None)).Result);
        Assert.Equal(before, await CaptureHistoryAsync(connectionString));

        await db.UserCompanyAccesses.Where(item => item.UserId == actor.Id).ExecuteDeleteAsync();
        Assert.IsType<NotFoundResult>((await controller.GetBankStatementImport(batch.Id, CancellationToken.None, 3)).Result);
        await db.RolePermissions.Where(item => item.Id == permission.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.CanView, false));
        Assert.IsType<ForbidResult>((await controller.GetBankStatementImport(batch.Id, CancellationToken.None)).Result);
        Assert.Equal(before, await CaptureHistoryAsync(connectionString));
    }

    private static BankStatementImportBatch BuildBatch(MoneyAccount account, int actorId, string key, int count)
    {
        var batch = new BankStatementImportBatch { CompanyId = account.CompanyId, MoneyAccountId = account.Id,
            ImportedByUserId = actorId, ResponsibleUserId = actorId, Source = BankStatementImportSources.File,
            FileName = key + ".csv", FileHash = key, RowsRead = count, CreatedRows = count - 1, ErrorRows = 1 };
        string?[] currencies = ["RUB", "USD", null, ""];
        for (var number = 1; number <= count; number++)
            batch.Rows.Add(new BankStatementImportRow { RowNumber = number, ImportKey = key + number,
                Status = number == count ? BankStatementImportRowStatus.Error : BankStatementImportRowStatus.Created,
                Currency = currencies[(number - 1) % currencies.Length], Amount = number + 0.25m,
                Date = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc), AccountName = account.Name });
        return batch;
    }

    private static async Task<string> CaptureHistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var snapshots = new List<string>();
        foreach (var table in new[] { "BankStatementImportBatches", "BankStatementImportRows", "MoneyOperations", "MoneyAccounts", "LegalEntities" })
        {
            await using var command = new NpgsqlCommand($"SELECT COALESCE(json_agg(t ORDER BY \"Id\")::text, '[]') FROM \"{table}\" t", connection);
            snapshots.Add((string)(await command.ExecuteScalarAsync())!);
        }
        return string.Join('\n', snapshots);
    }
}
