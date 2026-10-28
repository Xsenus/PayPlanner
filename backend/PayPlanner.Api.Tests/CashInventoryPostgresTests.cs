using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CashInventoryPostgresTests
{
    private sealed class Gate
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => _arrivals;
        public async Task WaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    private sealed class CompanyLockGate(Gate gate) : DbCommandInterceptor
    {
        private bool _waited;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_waited && command.CommandText.Contains("UPDATE \"LegalEntities\"", StringComparison.Ordinal))
            {
                _waited = true;
                await gate.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task TwoConcurrentInventories_PostgresCommitsAtMostOneCorrection()
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        var localPassword = Environment.GetEnvironmentVariable("PAYPLANNER_LOCAL_TEST_PG_PASSWORD");
        var builder = !string.IsNullOrWhiteSpace(configured) ? new NpgsqlConnectionStringBuilder(configured)
            : new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 5433, Username = "payplanner_ci", Password = localPassword };
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
            var connectionString = builder.ConnectionString;
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connectionString).Options;
            int userId, cashId;
            await using (var seed = new PostgreSqlPaymentContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var user = new User { Email = "cash-postgres@fixture.local", FullName = "Synthetic", PasswordHash = "hash",
                    Role = new Role { Name = "admin", Description = "Synthetic" }, IsActive = true, IsApproved = true };
                var company = new LegalEntity { ShortName = "Синтетическая компания" };
                var cash = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Currency = "RUB", Company = company, IsActive = true };
                seed.Users.Add(user);
                seed.MoneyAccounts.Add(cash);
                await seed.SaveChangesAsync();
                seed.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
                seed.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                await seed.SaveChangesAsync();
                userId = user.Id;
                cashId = cash.Id;
            }
            var gate = new Gate();
            CashOrdersController Controller(PostgreSqlPaymentContext db) => new(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } } };
            CashInventoryCreateRequest Request() => new() { AccountName = "Касса", MoneyAccountId = cashId, Date = new DateTime(2026, 10, 28),
                ActualBalance = 100m, CreateCorrection = true };
            async Task<IActionResult> InvokeAsync()
            {
                var requestOptions = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connectionString)
                    .AddInterceptors(new CompanyLockGate(gate)).Options;
                await using var db = new PostgreSqlPaymentContext(requestOptions);
                return await Controller(db).CreateInventory(Request(), CancellationToken.None);
            }
            var results = await Task.WhenAll(Task.Run(InvokeAsync), Task.Run(InvokeAsync));
            Assert.Equal(2, gate.Arrivals);
            Assert.Single(results, result => result is CreatedResult);
            Assert.Single(results, result => result is ConflictObjectResult);
            await using (var verify = new PostgreSqlPaymentContext(options))
            {
                var adjustment = Assert.Single(await verify.CashOrders.ToListAsync());
                Assert.Equal(100m, adjustment.Amount);
                Assert.Equal(cashId, adjustment.MoneyAccountId);
                Assert.Single(await verify.CashInventories.Where(item => item.CorrectionCashOrderId == adjustment.Id).ToListAsync());
                var retry = Assert.IsType<CashInventoryDto>(Assert.IsType<CreatedResult>(await Controller(verify).CreateInventory(Request(), CancellationToken.None)).Value);
                Assert.Equal(0m, retry.Difference);
                Assert.Null(retry.CorrectionCashOrderId);
                Assert.Single(await verify.CashOrders.ToListAsync());
            }
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
