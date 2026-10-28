using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class BankImportRowSearchTests
{
    private sealed class Recorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Sqlite_SearchesWholePermittedPackageWithBoundedPagesAndUnicode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var recorder = new Recorder();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection)
            .AddInterceptors(recorder).Options);
        await VerifyAsync(db, recorder, postgres: false);
    }

    [Fact]
    public async Task Postgres_SearchesWholePermittedPackageWithBoundedPagesAndUnicode()
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        var builder = !string.IsNullOrWhiteSpace(configured) ? new NpgsqlConnectionStringBuilder(configured)
            : new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = 5433,
                Username = "payplanner_ci",
                Password = Environment.GetEnvironmentVariable("PAYPLANNER_LOCAL_TEST_PG_PASSWORD")
            };
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") || builder.Username != "payplanner_ci"
            || string.IsNullOrWhiteSpace(builder.Password))
            throw new InvalidOperationException("A loopback PostgreSQL endpoint with the isolated payplanner_ci role is required.");
        builder.Database = "postgres";
        builder.Pooling = false;
        var databaseName = "pp_bank_search_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^pp_bank_search_[a-f0-9]{32}$", databaseName);
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var role = new NpgsqlCommand("SELECT rolcreatedb AND NOT rolsuper AND NOT rolcreaterole FROM pg_roles WHERE rolname = current_user", admin))
            Assert.Equal(true, await role.ExecuteScalarAsync());
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync();
            created = true;
            builder.Database = databaseName;
            var recorder = new Recorder();
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(builder.ConnectionString).AddInterceptors(recorder).Options);
            await VerifyAsync(db, recorder, postgres: true);
        }
        finally
        {
            if (created)
            {
                Assert.Matches("^pp_bank_search_[a-f0-9]{32}$", databaseName);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task VerifyAsync(PaymentContext db, Recorder recorder, bool postgres)
    {
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "bank-search@fixture.local",
            FullName = "Synthetic employee",
            PasswordHash = "hash",
            Role = new Role { Name = "bank-search", Description = "Synthetic read-only employee" },
            IsActive = true,
            IsApproved = true,
            IsEmployee = true
        };
        var own = new LegalEntity { ShortName = "Своя компания" };
        var foreign = new LegalEntity { ShortName = "Чужая компания" };
        var account = new MoneyAccount { Name = "Общий счёт", Source = MoneyOperationSource.Bank, Currency = "RUB", Company = own };
        var otherAccount = new MoneyAccount { Name = "Общий счёт", Source = MoneyOperationSource.Bank, Currency = "RUB", Company = foreign };
        db.Users.Add(user);
        db.MoneyAccounts.AddRange(account, otherAccount);
        await db.SaveChangesAsync();
        var permission = new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.Payments, CanView = true, ViewScope = DataVisibilityScope.Own };
        db.RolePermissions.Add(permission);
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = own.Id });
        db.SystemSettings.AddRange(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = JsonSerializer.Serialize(new[] { own.Id, foreign.Id }) },
            new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        var batch = Batch(account, user.Id, "own");
        for (var number = 1; number <= 130; number++)
            batch.Rows.Add(new BankStatementImportRow
            {
                RowNumber = number,
                ImportKey = "own-" + number,
                Status = number == 130 ? BankStatementImportRowStatus.Error : BankStatementImportRowStatus.Created,
                Currency = number == 2 ? "USD" : number == 3 ? null : "RUB",
                Amount = number + 0.25m,
                Date = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc),
                CounterpartyName = number <= 65 ? "ООО Ёжик" : "Другая организация",
                Purpose = number == 128 ? "Плата %_\\строка' &?" : number % 20 == 0 ? "Оплата услуг ёжИк" : "Оплата по договору",
                Message = number == 129 ? "Ошибка ЁЖИК" : null,
                PaymentOrderNumber = number == 130 ? "ёжик-130" : number.ToString()
            });
        var foreignBatch = Batch(otherAccount, user.Id, "foreign");
        foreignBatch.Rows.Add(new BankStatementImportRow { RowNumber = 1, ImportKey = "foreign", CounterpartyName = "Ёжик", Amount = 999m });
        db.BankStatementImportBatches.AddRange(batch, foreignBatch);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db);
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) }, "Test")),
                }
            },
        };
        async Task<BankStatementImportBatchDetailDto> ReadAsync(string search, int page = 1, int pageSize = 50)
        {
            recorder.Commands.Clear();
            var result = Assert.IsType<BankStatementImportBatchDetailDto>(Assert.IsType<OkObjectResult>(
                (await controller.GetBankStatementImport(batch.Id, CancellationToken.None, page, pageSize, search)).Result).Value);
            Assert.Equal(string.IsNullOrWhiteSpace(search) ? null : search.Trim(), result.RowsSearch);
            Assert.InRange(result.Rows.Count, 0, Math.Clamp(pageSize, 1, 100));
            Assert.Equal(130, result.RowsTotal);
            Assert.True(result.HasReprocessableRows);
            Assert.Equal(string.IsNullOrWhiteSpace(search) ? 1 : postgres ? 3 : 2,
                recorder.Commands.Count(command => command.Contains("BankImportDetail.", StringComparison.Ordinal)));
            if (postgres && !string.IsNullOrWhiteSpace(search))
                Assert.Contains(recorder.Commands, command => command.Contains("BankImportDetail.SearchPage", StringComparison.Ordinal)
                    && command.Contains("LIMIT", StringComparison.Ordinal));
            return result;
        }
        var first = await ReadAsync("  ёЖиК  ");
        Assert.Equal(70, first.RowsMatched);
        Assert.Equal(Enumerable.Range(1, 50), first.Rows.Select(row => row.RowNumber));
        Assert.Equal("USD", first.Rows[1].Currency);
        Assert.Null(first.Rows[2].Currency);
        var last = await ReadAsync("ёжик", 2);
        Assert.Equal(Enumerable.Range(51, 15).Concat(new[] { 80, 100, 120, 129, 130 }), last.Rows.Select(row => row.RowNumber));
        Assert.Equal(70, last.RowsMatched);
        Assert.Empty((await ReadAsync("ёжик", int.MaxValue)).Rows);
        Assert.Equal(100, (await ReadAsync("ёжик", 1, int.MaxValue)).RowsPageSize);
        Assert.Equal(1, (await ReadAsync("ёжик", 0, 0)).RowsPage);
        Assert.Equal(65, (await ReadAsync("ооо ёжик")).RowsMatched);
        Assert.Equal(6, (await ReadAsync("УСЛУГ ЁЖИК")).RowsMatched);
        Assert.Equal(1, (await ReadAsync("ошибка ёжик")).RowsMatched);
        Assert.Equal(130, Assert.Single((await ReadAsync("ЁЖИК-130")).Rows).RowNumber);
        foreach (var literal in new[] { "%", "_", "\\", "%_\\", "'", " &?" })
            Assert.Equal(128, Assert.Single((await ReadAsync(literal)).Rows).RowNumber);
        var empty = await ReadAsync("Нет такой строки");
        Assert.Equal(0, empty.RowsMatched);
        Assert.Empty(empty.Rows);
        Assert.Empty((await ReadAsync("' OR 1=1 --")).Rows);
        Assert.Empty((await ReadAsync(new string('x', 200))).Rows);
        var reset = await ReadAsync("  ");
        Assert.Null(reset.RowsMatched);
        Assert.Equal(50, reset.Rows.Count);
        Assert.IsType<BadRequestObjectResult>((await controller.GetBankStatementImport(batch.Id, CancellationToken.None, search: new string('x', 201))).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetBankStatementImport(batch.Id, CancellationToken.None, search: "ёжик\0")).Result);
        Assert.IsType<NotFoundResult>((await controller.GetBankStatementImport(foreignBatch.Id, CancellationToken.None, search: "ёжик")).Result);
        Assert.Equal(before, await SnapshotAsync(db));
        await db.UserCompanyAccesses.Where(item => item.UserId == user.Id).ExecuteDeleteAsync();
        Assert.IsType<NotFoundResult>((await controller.GetBankStatementImport(batch.Id, CancellationToken.None, search: "ёжик")).Result);
        await db.RolePermissions.Where(item => item.Id == permission.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.CanView, false));
        Assert.IsType<ForbidResult>((await controller.GetBankStatementImport(batch.Id, CancellationToken.None, search: "ёжик")).Result);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    private static BankStatementImportBatch Batch(MoneyAccount account, int userId, string key)
        => new()
        {
            CompanyId = account.CompanyId,
            MoneyAccountId = account.Id,
            ImportedByUserId = userId,
            ResponsibleUserId = userId,
            Source = BankStatementImportSources.File,
            FileName = key + ".csv",
            FileHash = key,
            RowsRead = 130,
            CreatedRows = 129,
            ErrorRows = 1
        };

    private static async Task<string> SnapshotAsync(PaymentContext db)
        => JsonSerializer.Serialize(new
        {
            Batches = await db.BankStatementImportBatches.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).ToListAsync(),
            Rows = await db.BankStatementImportRows.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).ToListAsync(),
            Operations = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).ToListAsync(),
            Accounts = await db.MoneyAccounts.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).ToListAsync()
        });
}
