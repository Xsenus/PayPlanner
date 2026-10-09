using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public class BankSyncServiceTests
{
    private sealed class FakeBank : IBankProvider
    {
        public string Key => BankProviderKeys.Tochka;
        public List<BankAccountInfo> Accounts { get; set; } = [];
        public Dictionary<string, List<List<BankOperationInfo>>> Pages { get; } = new();
        public HashSet<string> FailingAccounts { get; } = [];
        public bool Unauthorized { get; set; }
        public bool RefreshFails { get; set; }
        public int RefreshCalls { get; private set; }
        public string? LastAccessToken { get; private set; }

        public Task<BankTokens> RefreshTokensAsync(string refreshToken, CancellationToken ct)
        {
            RefreshCalls++;
            if (RefreshFails) throw new BankAuthorizationException("refresh rejected");
            return Task.FromResult(new BankTokens("new-access", "new-refresh", DateTime.UtcNow.AddHours(1)));
        }
        public int StatementCalls { get; private set; }
        public int? FailOnPage { get; set; }
        public DateTime? LastFrom { get; private set; }

        public Task<IReadOnlyList<BankAccountInfo>> ListAccountsAsync(BankCredentials credentials, CancellationToken ct)
        {
            LastAccessToken = credentials.AccessToken;
            if (Unauthorized) throw new BankAuthorizationException("token revoked");
            return Task.FromResult<IReadOnlyList<BankAccountInfo>>(Accounts);
        }

        public Task<BankStatementPage> GetStatementPageAsync(BankCredentials credentials, string externalAccountId, DateTime from, DateTime to, string? cursor, CancellationToken ct)
        {
            StatementCalls++;
            LastFrom = from;
            if (Unauthorized) throw new BankAuthorizationException("token revoked");
            if (FailingAccounts.Contains(externalAccountId)) throw new InvalidOperationException("bank unavailable");
            var pages = Pages.GetValueOrDefault(externalAccountId) ?? [];
            var index = cursor is null ? 0 : int.Parse(cursor);
            if (FailOnPage == index) throw new InvalidOperationException("bank unavailable");
            var items = index < pages.Count ? pages[index] : [];
            return Task.FromResult(new BankStatementPage(items, index + 1 < pages.Count ? (index + 1).ToString() : null));
        }
    }

    private static BankOperationInfo Op(string id, int day, decimal amount = 100m, bool booked = true)
        => new(id, new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc), amount, true, booked, "7707083893", "ООО Клиент", null, "Оплата по счёту");

    private static async Task<(PaymentContext Db, BankSyncService Service, FakeBank Bank, BankConnection Connection)> CreateAsync(string companyInn = "7707083893")
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var company = new LegalEntity { ShortName = "Своя", Inn = companyInn };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();

        var bank = new FakeBank();
        var service = new BankSyncService(db, [bank], new EphemeralDataProtectionProvider());
        var entity = new BankConnection { LegalEntityId = company.Id, Provider = BankProviderKeys.Tochka };
        service.StoreTokens(entity, "access-secret", "refresh-secret", DateTime.UtcNow.AddHours(1));
        db.BankConnections.Add(entity);
        await db.SaveChangesAsync();
        return (db, service, bank, entity);
    }

    private static BankAccountInfo Account(string id, string number = "40702810200000000001", string status = BankAccountStatuses.Open, string? inn = "7707083893")
        => new(id, number, "044525225", "RUB", status, inn);

    [Fact]
    public async Task StoreTokens_NeverKeepsPlainTextInDatabase()
    {
        var (db, _, _, connection) = await CreateAsync();
        await using var _ = db;

        Assert.DoesNotContain("access-secret", connection.AccessTokenProtected);
        Assert.DoesNotContain("refresh-secret", connection.RefreshTokenProtected);
    }

    [Fact]
    public async Task SyncAccounts_IsIdempotent_UpdatesStatus_AndNeverDeletesMissingAccounts()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts = [Account("a"), Account("b", "40702810200000000002")];

        var first = await service.SyncAccountsAsync(connection.Id, default);
        var second = await service.SyncAccountsAsync(connection.Id, default);

        Assert.Equal(2, first.Added);
        Assert.Equal(0, second.Added);
        Assert.Equal(2, await db.BankConnectionAccounts.CountAsync());

        // Счёт «b» пропал из ответа, «a» стал ограниченным: ничего не удаляется и не закрывается.
        bank.Accounts = [Account("a", status: BankAccountStatuses.Limited)];
        var third = await service.SyncAccountsAsync(connection.Id, default);

        Assert.Equal(1, third.Updated);
        var accounts = await db.BankConnectionAccounts.AsNoTracking().OrderBy(a => a.ExternalAccountId).ToListAsync();
        Assert.Equal(BankAccountStatuses.Limited, accounts[0].Status);
        Assert.Equal(BankAccountStatuses.Open, accounts[1].Status);
        Assert.False(BankAccountStatuses.AllowsNewDocuments(accounts[0].Status));
    }

    [Fact]
    public async Task SyncAccounts_SkipsAccountsOfAnotherInn_AndMapsUnknownStatusToUnavailable()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts = [Account("own"), Account("foreign", inn: "5000000000"), Account("odd", "40702810200000000003", status: "Weird")];

        var result = await service.SyncAccountsAsync(connection.Id, default);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(2, result.Added);
        Assert.Equal(BankAccountStatuses.Unavailable, (await db.BankConnectionAccounts.SingleAsync(a => a.ExternalAccountId == "odd")).Status);
        Assert.DoesNotContain(await db.BankConnectionAccounts.ToListAsync(), a => a.ExternalAccountId == "foreign");
    }

    [Fact]
    public async Task SyncAccounts_MarksConnectionReauthRequired_WhenBankRejectsToken()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Unauthorized = true;

        var result = await service.SyncAccountsAsync(connection.Id, default);

        Assert.NotNull(result.Error);
        Assert.Equal(BankConnectionStatuses.ReauthRequired, (await db.BankConnections.AsNoTracking().SingleAsync()).Status);

        bank.Unauthorized = false;
        var blocked = await service.SyncAccountsAsync(connection.Id, default);
        Assert.NotNull(blocked.Error);
    }

    [Fact]
    public async Task SyncStatement_ReadsAllPages_WithoutDuplicatesOnRepeatAndOverlap()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts = [Account("a")];
        await service.SyncAccountsAsync(connection.Id, default);
        var accountId = (await db.BankConnectionAccounts.SingleAsync()).Id;
        bank.Pages["a"] = [[Op("1", 1), Op("2", 2)], [Op("3", 3), Op("2", 2)]];

        var first = await service.SyncStatementAsync(accountId, new DateTime(2026, 10, 1), new DateTime(2026, 10, 9), default);
        var repeat = await service.SyncStatementAsync(accountId, new DateTime(2026, 10, 1), new DateTime(2026, 10, 9), default);

        Assert.Equal(3, first.Inserted);
        Assert.Equal(0, repeat.Inserted);
        Assert.Equal(3, await db.BankStatementOperations.CountAsync());
        Assert.Equal(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), first.Watermark);
    }

    [Fact]
    public async Task SyncStatement_UpdatesPendingOperationOnce_AndHoldsWatermarkBeforeIt()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts = [Account("a")];
        await service.SyncAccountsAsync(connection.Id, default);
        var accountId = (await db.BankConnectionAccounts.SingleAsync()).Id;
        bank.Pages["a"] = [[Op("1", 1), Op("2", 5, booked: false), Op("3", 7)]];

        var first = await service.SyncStatementAsync(accountId, new DateTime(2026, 10, 1), new DateTime(2026, 10, 9), default);
        Assert.True(first.Watermark < new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));

        bank.Pages["a"] = [[Op("1", 1), Op("2", 5, amount: 150m), Op("3", 7)]];
        var second = await service.SyncStatementAsync(accountId, new DateTime(2026, 10, 1), new DateTime(2026, 10, 9), default);

        Assert.Equal(1, second.Updated);
        var op = await db.BankStatementOperations.AsNoTracking().SingleAsync(o => o.ExternalOperationId == "2");
        Assert.True(op.IsBooked);
        Assert.Equal(150m, op.Amount);
        Assert.Equal(3, await db.BankStatementOperations.CountAsync());
        Assert.Equal(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), second.Watermark);
    }

    [Fact]
    public async Task SyncStatement_KeepsWatermark_WhenBankFailsMidway()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts = [Account("a")];
        await service.SyncAccountsAsync(connection.Id, default);
        var accountId = (await db.BankConnectionAccounts.SingleAsync()).Id;
        bank.Pages["a"] = [[Op("1", 1)]];
        await service.SyncStatementAsync(accountId, new DateTime(2026, 10, 1), new DateTime(2026, 10, 9), default);
        var before = (await db.BankConnectionAccounts.AsNoTracking().SingleAsync()).StatementWatermark;

        bank.FailingAccounts.Add("a");
        var failed = await service.SyncStatementAsync(accountId, new DateTime(2026, 10, 1), new DateTime(2026, 10, 9), default);

        Assert.NotNull(failed.Error);
        var account = await db.BankConnectionAccounts.AsNoTracking().SingleAsync();
        Assert.Equal(before, account.StatementWatermark);
        Assert.NotNull(account.LastError);
    }

    [Fact]
    public async Task SyncAll_IsolatesFailingAccount_AndSkipsDisabledOnes()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts = [Account("bad"), Account("good", "40702810200000000002"), Account("off", "40702810200000000003")];
        await service.SyncAccountsAsync(connection.Id, default);
        (await db.BankConnectionAccounts.SingleAsync(a => a.ExternalAccountId == "off")).StatementsEnabled = false;
        await db.SaveChangesAsync();
        bank.FailingAccounts.Add("bad");
        bank.Pages["good"] = [[Op("1", 1)]];

        var results = await service.SyncAllStatementsAsync(connection.Id, new DateTime(2026, 10, 9), default);

        Assert.Equal(2, results.Count);
        var byExternal = await db.BankConnectionAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.ExternalAccountId);
        Assert.NotNull(results.Single(r => byExternal[r.Key] == "bad").Value.Error);
        Assert.Null(results.Single(r => byExternal[r.Key] == "good").Value.Error);
        Assert.Equal(1, await db.BankStatementOperations.CountAsync());
    }

    [Fact]
    public async Task SyncStatement_RefusesUnavailableAccount()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts = [Account("a", status: BankAccountStatuses.Unavailable)];
        await service.SyncAccountsAsync(connection.Id, default);
        var accountId = (await db.BankConnectionAccounts.SingleAsync()).Id;

        var result = await service.SyncStatementAsync(accountId, new DateTime(2026, 10, 1), new DateTime(2026, 10, 9), default);

        Assert.NotNull(result.Error);
        Assert.Equal(0, bank.StatementCalls);
    }

    [Fact]
    public async Task ExpiredAccessToken_IsRefreshedAndStored_BeforeCallingBank()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        bank.Accounts = [Account("a")];

        var result = await service.SyncAccountsAsync(connection.Id, default);

        Assert.Null(result.Error);
        Assert.Equal(1, bank.RefreshCalls);
        Assert.Equal("new-access", bank.LastAccessToken);
        var stored = await db.BankConnections.AsNoTracking().SingleAsync();
        Assert.True(stored.AccessTokenExpiresAt > DateTime.UtcNow);
        Assert.Equal(BankConnectionStatuses.Active, stored.Status);
    }

    [Fact]
    public async Task RejectedRefresh_RequiresReauthorization_WithoutCallingBankData()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        bank.RefreshFails = true;
        bank.Accounts = [Account("a")];

        var result = await service.SyncAccountsAsync(connection.Id, default);

        Assert.NotNull(result.Error);
        Assert.Null(bank.LastAccessToken);
        Assert.Equal(BankConnectionStatuses.ReauthRequired, (await db.BankConnections.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task SyncAccounts_SkipsAccountsWithOversizedIdentifiers_InsteadOfFailingTheWholeRun()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts = [Account(new string('x', 200)), Account("ok", "40702810200000000009")];

        var result = await service.SyncAccountsAsync(connection.Id, default);

        Assert.Null(result.Error);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task SyncStatement_SavesEveryPage_SoLateFailureKeepsEarlierOperations()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts = [Account("a")];
        await service.SyncAccountsAsync(connection.Id, default);
        var accountId = (await db.BankConnectionAccounts.SingleAsync()).Id;
        bank.Pages["a"] = [[Op("1", 1)], [Op("2", 2)]];
        bank.FailOnPage = 1;

        var result = await service.SyncStatementAsync(accountId, new DateTime(2026, 10, 1), new DateTime(2026, 10, 9), default);

        Assert.NotNull(result.Error);
        Assert.Equal(1, await db.BankStatementOperations.CountAsync());
        Assert.Null((await db.BankConnectionAccounts.AsNoTracking().SingleAsync()).StatementWatermark);

        bank.FailOnPage = null;
        var retry = await service.SyncStatementAsync(accountId, new DateTime(2026, 10, 1), new DateTime(2026, 10, 9), default);
        Assert.Null(retry.Error);
        Assert.Equal(2, await db.BankStatementOperations.CountAsync());
    }

    [Fact]
    public async Task SyncStatement_DoesNotRereadWholeHistory_WhenAccountHasNoBookedOperationsYet()
    {
        var (db, service, bank, connection) = await CreateAsync();
        await using var _ = db;
        bank.Accounts = [Account("a")];
        await service.SyncAccountsAsync(connection.Id, default);
        var accountId = (await db.BankConnectionAccounts.SingleAsync()).Id;

        await service.SyncStatementAsync(accountId, new DateTime(2026, 1, 1), DateTime.UtcNow, default);
        await service.SyncStatementAsync(accountId, new DateTime(2026, 1, 1), DateTime.UtcNow, default);

        Assert.True(bank.LastFrom > new DateTime(2026, 1, 1));
    }
}
