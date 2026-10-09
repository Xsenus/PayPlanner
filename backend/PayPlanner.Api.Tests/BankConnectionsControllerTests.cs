using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public class BankConnectionsControllerTests
{
    private sealed class Bank : IBankProvider
    {
        public string Key => BankProviderKeys.Sber;
        public List<BankAccountInfo> Accounts { get; } = [];
        public HashSet<string> Failing { get; } = [];

        public Task<BankTokens> RefreshTokensAsync(string refreshToken, CancellationToken ct)
            => Task.FromResult(new BankTokens("a", "r", DateTime.UtcNow.AddHours(1)));

        public Task<IReadOnlyList<BankAccountInfo>> ListAccountsAsync(BankCredentials credentials, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<BankAccountInfo>>(Accounts);

        public Task<BankStatementPage> GetStatementPageAsync(BankCredentials credentials, string externalAccountId, DateTime from, DateTime to, string? cursor, CancellationToken ct)
        {
            if (Failing.Contains(externalAccountId)) throw new InvalidOperationException("bank unavailable");
            return Task.FromResult(new BankStatementPage(
                [new BankOperationInfo(externalAccountId + "-1", new DateTime(2026, 10, 2), 100m, true, true, "7707083893", "Клиент", null, "Оплата")], null));
        }
    }

    private static async Task<(PaymentContext Db, BankConnectionsController Controller, Bank Bank, BankConnection Connection)> CreateAsync()
    {
        var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(sqlite).Options);
        await db.Database.EnsureCreatedAsync();
        var company = new LegalEntity { ShortName = "Своя", Inn = "7707083893" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        var bank = new Bank();
        var service = new BankSyncService(db, [bank], new EphemeralDataProtectionProvider());
        var connection = new BankConnection { LegalEntityId = company.Id, Provider = BankProviderKeys.Sber };
        service.StoreTokens(connection, "secret-access", "secret-refresh", DateTime.UtcNow.AddHours(1));
        connection.RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(5);
        db.BankConnections.Add(connection);
        await db.SaveChangesAsync();
        return (db, new BankConnectionsController(db, service), bank, connection);
    }

    private static BankAccountInfo Account(string id, string number)
        => new(id, number, "044525225", "RUB", BankAccountStatuses.Open, "7707083893");

    [Fact]
    public async Task List_ReturnsAccountsAndTokenExpiryWarning_WithoutAnyTokenData()
    {
        var (db, controller, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts.Add(Account("a", "40702810200000000001"));
        await controller.SyncAccounts(connection.Id, default);

        var list = Assert.IsType<List<BankConnectionDto>>(Assert.IsType<OkObjectResult>((await controller.List(null, default)).Result).Value);

        var item = Assert.Single(list);
        Assert.Equal(BankConnectionStatuses.TokenExpiring, item.Status);
        Assert.Single(item.Accounts);
        var json = System.Text.Json.JsonSerializer.Serialize(list);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("Protected", json);
    }

    [Fact]
    public async Task SyncAllStatements_ReportsPartialResult_AndIsolatesFailingAccount()
    {
        var (db, controller, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts.AddRange([Account("good", "40702810200000000001"), Account("bad", "40702810200000000002")]);
        await controller.SyncAccounts(connection.Id, default);
        bank.Failing.Add("bad");

        var result = await controller.SyncAllStatements(connection.Id, new BankStatementPeriodRequest { From = new DateTime(2026, 10, 1), To = new DateTime(2026, 10, 9) }, default);

        var run = Assert.IsType<BankStatementRunDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Partial", run.Status);
        Assert.Equal(1, run.Succeeded);
        Assert.Equal(2, run.Total);
        Assert.Equal(1, await db.BankStatementOperations.CountAsync());
    }

    [Theory]
    [InlineData(2026, 10, 9, 2026, 10, 1)]
    [InlineData(2024, 1, 1, 2026, 1, 1)]
    public async Task SyncStatements_RejectsInvertedOrTooLongPeriod(int fy, int fm, int fd, int ty, int tm, int td)
    {
        var (db, controller, _, connection) = await CreateAsync();
        await using var _db = db;

        var result = await controller.SyncAllStatements(connection.Id,
            new BankStatementPeriodRequest { From = new DateTime(fy, fm, fd), To = new DateTime(ty, tm, td) }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateAccount_ReturnsConflictForClosedDefault_AndNotFoundForUnknown()
    {
        var (db, controller, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts.Add(new BankAccountInfo("c", "40702810200000000003", "044525225", "RUB", BankAccountStatuses.Closed, "7707083893"));
        await controller.SyncAccounts(connection.Id, default);
        var id = (await db.BankConnectionAccounts.SingleAsync()).Id;

        var conflict = await controller.UpdateAccount(id, new BankAccountSettingsRequest { IsDefault = true }, default);
        var missing = await controller.UpdateAccount(999, new BankAccountSettingsRequest { IsDefault = true }, default);

        Assert.IsType<ConflictObjectResult>(conflict.Result);
        Assert.IsType<NotFoundResult>(missing.Result);
    }

    [Fact]
    public async Task OperationsAndLogs_ArePagedAndBounded()
    {
        var (db, controller, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts.Add(Account("a", "40702810200000000001"));
        await controller.SyncAccounts(connection.Id, default);
        var accountId = (await db.BankConnectionAccounts.SingleAsync()).Id;
        await controller.SyncStatement(accountId, new BankStatementPeriodRequest { From = new DateTime(2026, 10, 1), To = new DateTime(2026, 10, 9) }, default);

        var operations = Assert.IsType<BankOperationsPageDto>(Assert.IsType<OkObjectResult>((await controller.GetOperations(accountId, null, null, 1, 5000, default)).Result).Value);
        var logs = Assert.IsType<BankSyncLogPageDto>(Assert.IsType<OkObjectResult>((await controller.GetLogs(connection.Id, 1, 5000, default)).Result).Value);

        Assert.Equal(1, operations.Total);
        Assert.Equal(200, operations.PageSize);
        Assert.Equal(2, logs.Total);
        Assert.Equal("statement", logs.Items[0].Kind);
        Assert.IsType<NotFoundResult>((await controller.GetOperations(999, null, null, 1, 10, default)).Result);
    }
}
