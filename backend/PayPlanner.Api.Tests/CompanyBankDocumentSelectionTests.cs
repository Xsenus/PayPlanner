using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;
using Core = PayPlanner.Api.Tests.CompanyBankAccountBindingCommandTests;

namespace PayPlanner.Api.Tests;

public sealed class CompanyBankDocumentSelectionTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> Documents()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var kind in new[] { "invoice", "act", "contract" })
                foreach (var policy in new[] { DocumentSellerSnapshotPolicy.FirstFile, DocumentSellerSnapshotPolicy.OnCreate })
                    yield return [postgres, kind, policy];
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public Task EveryDocumentAndPolicyCheckAndFreezeTheAuthoritativeSource(bool postgres, string kind, string policy)
        => RunAsync(postgres, async db =>
        {
            var (f, binding) = await SeedAsync(db); var before = await Core.HistoryAsync(db);
            IDocumentSeller document = kind switch { "invoice" => new Payment { Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice }, "act" => new Act(), _ => new Contract() };
            document.SellerCompanyId = f.Company; document.SellerSnapshotPolicy = policy;
            var service = new DocumentSellerService(db);
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await service.InitializeForCreationAsync(document, null, default);
                Assert.Equal(policy == DocumentSellerSnapshotPolicy.OnCreate, document.SellerSnapshotJson is not null);
                await service.ResolveAsync(document, null, true, default);
                var source = Source(document); Assert.Equal(binding, source.BindingId); Assert.Equal(f.Bank, source.BankAccountId);
                Assert.Equal(f.Money, source.MoneyAccountId); Assert.Equal("synthetic", source.ExternalAccountId); Assert.Equal("RUB", source.Currency);
                await transaction.CommitAsync();
            }
            Assert.Equal(before, await Core.HistoryAsync(db));
            var issued = document.SellerSnapshotJson;
            (await db.BankConnectionAccounts.SingleAsync()).Status = BankAccountStatuses.Closed;
            (await db.MoneyAccounts.SingleAsync()).IsActive = false;
            (await db.LegalEntities.SingleAsync()).ProfileStatus = CompanyProfileStatuses.Archived;
            (await db.SystemSettings.SingleAsync(x => x.Key == "legalEntities.mainCompanyIds")).Value = "[]";
            await db.SaveChangesAsync();
            var changedHistory = await Core.HistoryAsync(db);
            await service.ResolveAsync(document, null, false, default);
            await service.ResolveAsync(document, null, true, default);
            Assert.Equal(issued, document.SellerSnapshotJson); Assert.Equal(binding, Source(document).BindingId);
            Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
            Assert.Equal(changedHistory, await Core.HistoryAsync(db));
        });

    public static IEnumerable<object[]> Refusals()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var mode in new[] { "closed", "limited", "pending", "missing-bank", "hidden", "connection", "number", "bik", "currency", "provider", "external", "foreign", "money-owner", "money-source", "money-currency", "money-inactive", "profile-currency", "profile-archive", "profile-missing", "bad-json", "duplicate-id" })
                yield return [postgres, mode];
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public Task UnavailableSourceNeverCreatesOrFreezesANewDocument(bool postgres, string mode)
        => RunAsync(postgres, async db =>
        {
            var (f, _) = await SeedAsync(db); var bank = await db.BankConnectionAccounts.SingleAsync();
            var company = await db.LegalEntities.SingleAsync(); var money = await db.MoneyAccounts.SingleAsync();
            switch (mode)
            {
                case "closed": bank.Status = BankAccountStatuses.Closed; break;
                case "limited": bank.Status = BankAccountStatuses.Limited; break;
                case "pending": bank.Status = BankAccountStatuses.Pending; break;
                case "missing-bank": bank.Status = BankAccountStatuses.NotFoundInBank; break;
                case "hidden": bank.IsHidden = true; break;
                case "connection": (await db.BankConnections.SingleAsync()).Status = BankConnectionStatuses.Disconnected; break;
                case "number": bank.AccountNumber = "40702810200000000002"; break;
                case "bik": bank.Bik = "044525104"; break;
                case "currency": bank.Currency = "USD"; break;
                case "provider": (await db.BankConnections.SingleAsync()).Provider = BankProviderKeys.Tochka; break;
                case "external": bank.ExternalAccountId = "replacement"; break;
                case "foreign":
                    var foreign = new LegalEntity { ShortName = "Foreign" }; db.LegalEntities.Add(foreign); await db.SaveChangesAsync();
                    (await db.BankConnections.SingleAsync()).LegalEntityId = foreign.Id; break;
                case "money-owner": money.CompanyId = null; break;
                case "money-source": money.Source = MoneyOperationSource.Cash; break;
                case "money-currency": money.Currency = "USD"; break;
                case "money-inactive": money.IsActive = false; break;
                case "profile-currency": company.BankAccountsJson = Core.Profile().Replace("RUB", "USD"); break;
                case "profile-archive": company.BankAccountsJson = Core.Profile().Replace("\"isArchived\":false", "\"isArchived\":true"); break;
                case "profile-missing": company.BankAccountsJson = "[]"; break;
                case "bad-json": company.BankAccountsJson = "{bad"; break;
                case "duplicate-id": company.BankAccountsJson = Core.Profile()[..^1] + "," + Core.Profile()[1..]; break;
            }
            await db.SaveChangesAsync(); var history = await Core.HistoryAsync(db);
            var service = new DocumentSellerService(db); var document = new Act { SellerCompanyId = f.Company };
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await Assert.ThrowsAsync<SellerSelectionException>(() => service.InitializeForCreationAsync(document, null, default));
                await transaction.RollbackAsync();
            }
            await Assert.ThrowsAsync<SellerSelectionException>(() => service.ResolveAsync(document, null, true, default));
            Assert.Null(document.SellerSnapshotJson); Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
            Assert.Equal(0, await db.Acts.CountAsync()); Assert.Equal(history, await Core.HistoryAsync(db));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MissingExplicitWalletIsNotReplacedWithAMatchingManualWallet(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var (f, _) = await SeedAsync(db); (await db.MoneyAccounts.SingleAsync()).BankConnectionAccountId = null; await db.SaveChangesAsync();
            var history = await Core.HistoryAsync(db); var document = new Act { SellerCompanyId = f.Company };
            await new DocumentSellerService(db).ResolveAsync(document, null, true, default);
            Assert.Null(Source(document).MoneyAccountId); Assert.Equal(history, await Core.HistoryAsync(db));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExplicitUnboundPrimaryAndRevokedLinkRetainManualSemantics(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var (f, binding) = await SeedAsync(db);
            var company = await db.LegalEntities.SingleAsync();
            company.BankAccountsJson = Core.Profile().Replace("\"isPrimary\":true", "\"isPrimary\":false")[..^1] + "," + Core.Profile("manual")[1..];
            await db.SaveChangesAsync(); var history = await Core.HistoryAsync(db);
            var manual = new Act { SellerCompanyId = f.Company };
            await new DocumentSellerService(db).ResolveAsync(manual, null, true, default);
            Assert.Null(ReadSource(manual)); Assert.Contains("manual", manual.SellerSnapshotJson);
            Assert.Equal(history, await Core.HistoryAsync(db));
            await new CompanyBankAccountBindingService(db).RevokeAsync(Core.Principal(f.Actor), f.Connection, binding, await Core.VersionAsync(db, f), default);
            (await db.BankConnectionAccounts.SingleAsync()).Status = BankAccountStatuses.Closed; await db.SaveChangesAsync();
            var changedHistory = await Core.HistoryAsync(db);
            var unbound = new Act { SellerCompanyId = f.Company };
            await new DocumentSellerService(db).ResolveAsync(unbound, null, true, default); Assert.Null(ReadSource(unbound));
            Assert.Equal(changedHistory, await Core.HistoryAsync(db));
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task UnavailableConfirmedBranchNeverFallsBackToAnotherPrimary(bool postgres, bool archived)
        => RunAsync(postgres, async db =>
        {
            var (f, _) = await SeedAsync(db); var company = await db.LegalEntities.SingleAsync();
            var branch = new CompanyBranch { LegalEntityId = f.Company, Name = "Bound branch", BankAccountId = "local-id" }; db.CompanyBranches.Add(branch);
            company.BankAccountsJson = (archived ? Core.Profile().Replace("\"isArchived\":false", "\"isArchived\":true") : "[]")[..^1] + (archived ? "," : "") + Core.Profile("manual")[1..];
            await db.SaveChangesAsync(); var document = new Act { SellerCompanyId = f.Company, SellerBranchId = branch.Id };
            await Assert.ThrowsAsync<SellerSelectionException>(() => new DocumentSellerService(db).ResolveAsync(document, null, true, default)); Assert.Null(document.SellerSnapshotJson);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PersistedIssuedActRetainsItsBankSnapshotAfterRevocationAndArchive(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var (f, binding) = await SeedAsync(db); var service = new DocumentSellerService(db);
            var document = new Act { Number = "BOUND-ISSUED", SellerCompanyId = f.Company, SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.OnCreate };
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await service.InitializeForCreationAsync(document, null, default); db.Acts.Add(document); await db.SaveChangesAsync(); await transaction.CommitAsync();
            }
            var snapshot = document.SellerSnapshotJson;
            (await db.BankConnectionAccounts.SingleAsync()).Status = BankAccountStatuses.Closed; await db.SaveChangesAsync();
            await new CompanyBankAccountBindingService(db).RevokeAsync(Core.Principal(f.Actor), f.Connection, binding, await Core.VersionAsync(db, f), default);
            (await db.LegalEntities.SingleAsync()).ProfileStatus = CompanyProfileStatuses.Archived;
            (await db.SystemSettings.SingleAsync(x => x.Key == "legalEntities.mainCompanyIds")).Value = "[]"; await db.SaveChangesAsync();
            var history = await Core.HistoryAsync(db);
            var seller = await service.ResolveAsync(document, null, true, default, _ => Task.FromResult(true)); Assert.NotNull(seller);
            Assert.Equal(snapshot, document.SellerSnapshotJson); Assert.Equal(snapshot, (await db.Acts.AsNoTracking().SingleAsync()).SellerSnapshotJson);
            Assert.Equal(binding, Source(document).BindingId); Assert.Equal(history, await Core.HistoryAsync(db));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AgreedForeignCurrencyIsNotSilentlyPrintedAsARubleDocument(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await Core.SeedAsync(db); (await db.LegalEntities.SingleAsync()).BankAccountsJson = Core.Profile().Replace("RUB", "USD");
            (await db.BankConnectionAccounts.SingleAsync()).Currency = "USD"; (await db.MoneyAccounts.SingleAsync()).Currency = "USD"; await db.SaveChangesAsync();
            await new CompanyBankAccountBindingService(db).BindAsync(Core.Principal(f.Actor), f.Connection, f.Bank, "local-id", await Core.VersionAsync(db, f), default);
            var document = new Payment { SellerCompanyId = f.Company, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice };
            await Assert.ThrowsAsync<SellerSelectionException>(() => new DocumentSellerService(db).ResolveAsync(document, null, true, default)); Assert.Null(document.SellerSnapshotJson);
            // Contract templates do not establish a ruble document currency. The
            // three authoritative account currencies still have to agree exactly.
            var contract = new Contract { SellerCompanyId = f.Company };
            await new DocumentSellerService(db).ResolveAsync(contract, null, true, default);
            Assert.Equal("USD", Source(contract).Currency);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DeferredBoundCreationRequiresTheCreatingTransaction(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var (f, _) = await SeedAsync(db); var document = new Act { SellerCompanyId = f.Company };
            await Assert.ThrowsAsync<InvalidOperationException>(() => new DocumentSellerService(db).InitializeForCreationAsync(document, null, default));
            Assert.Null(db.Database.CurrentTransaction); Assert.Null(document.SellerSnapshotJson);
        });

    [Fact]
    public async Task NativeSelectionHoldsSourceWalletAndCompanyUntilOuterCommit()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Context(database.ConnectionString);
        var (f, _) = await SeedAsync(db); var document = new Act { SellerCompanyId = f.Company, SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.OnCreate };
        await using var transaction = await db.Database.BeginTransactionAsync();
        await new DocumentSellerService(db).InitializeForCreationAsync(document, null, default);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var timeout = new NpgsqlCommand("SET lock_timeout = '250ms'", connection)) await timeout.ExecuteNonQueryAsync();
        foreach (var sql in new[] { $"UPDATE \"BankConnections\" SET \"Status\"='Disconnected' WHERE \"Id\"={f.Connection}", $"UPDATE \"BankConnectionAccounts\" SET \"IsHidden\"=TRUE WHERE \"Id\"={f.Bank}", $"UPDATE \"MoneyAccounts\" SET \"Currency\"='USD' WHERE \"Id\"={f.Money}" })
        {
            await using var command = new NpgsqlCommand(sql, connection); Assert.Equal("55P03", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
        await using var other = Context(database.ConnectionString);
        var currentVersion = await Core.VersionAsync(other, f);
        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new CompanyBankAccountBindingService(other).RevokeAsync(Core.Principal(f.Actor), f.Connection, Source(document).BindingId, currentVersion, default));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task NativeMissingWalletCannotAcquireAPhantomForeignLinkBeforeCommit()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Context(database.ConnectionString);
        var (f, _) = await SeedAsync(db); (await db.MoneyAccounts.SingleAsync()).BankConnectionAccountId = null; await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(); var document = new Act { SellerCompanyId = f.Company };
        await new DocumentSellerService(db).ResolveAsync(document, null, true, default); Assert.Null(Source(document).MoneyAccountId);
        await using var other = Context(database.ConnectionString); await other.Database.OpenConnectionAsync();
        await other.Database.ExecuteSqlRawAsync("SET lock_timeout = '250ms'");
        other.MoneyAccounts.Add(new() { Name = "Foreign phantom", Source = MoneyOperationSource.Bank, BankConnectionAccountId = f.Bank, Currency = "RUB" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => other.SaveChangesAsync());
        var update = Assert.IsType<DbUpdateException>(error.InnerException);
        Assert.Equal("55P03", Assert.IsType<PostgresException>(update.InnerException).SqlState);
        Assert.False(await other.MoneyAccounts.AsNoTracking().AnyAsync(x => x.BankConnectionAccountId == f.Bank));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task NativeOlderSnapshotCannotFreezeAChangedBank()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Context(database.ConnectionString);
        var (f, _) = await SeedAsync(db); await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await db.BankConnectionAccounts.AsNoTracking().SingleAsync();
        await using (var other = Context(database.ConnectionString)) { (await other.BankConnectionAccounts.SingleAsync()).Status = BankAccountStatuses.Closed; await other.SaveChangesAsync(); }
        var document = new Act { SellerCompanyId = f.Company };
        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new DocumentSellerService(db).ResolveAsync(document, null, true, default));
        Assert.Null(document.SellerSnapshotJson); await transaction.RollbackAsync(); Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BankReturnAndBranchChoiceUseTheCurrentStableSource(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var (f, binding) = await SeedAsync(db); var service = new DocumentSellerService(db);
            var bank = await db.BankConnectionAccounts.SingleAsync(); bank.Status = BankAccountStatuses.NotFoundInBank; await db.SaveChangesAsync();
            var unavailable = new Act { SellerCompanyId = f.Company };
            await Assert.ThrowsAsync<SellerSelectionException>(() => service.ResolveAsync(unavailable, null, true, default));
            Assert.Null(unavailable.SellerSnapshotJson); Assert.Null(db.Database.CurrentTransaction);
            bank.Status = BankAccountStatuses.Open; await db.SaveChangesAsync();
            var returned = new Act { SellerCompanyId = f.Company }; await service.ResolveAsync(returned, null, true, default);
            Assert.Equal(binding, Source(returned).BindingId); Assert.Equal(f.Bank, Source(returned).BankAccountId);
            var company = await db.LegalEntities.SingleAsync();
            company.BankAccountsJson = Core.Profile().Replace("\"isPrimary\":true", "\"isPrimary\":false")[..^1] + "," + Core.Profile("manual")[1..];
            var branch = new CompanyBranch { LegalEntityId = f.Company, Name = "Explicit bank branch", BankAccountId = "local-id" }; db.CompanyBranches.Add(branch); await db.SaveChangesAsync();
            var branchDocument = new Act { SellerCompanyId = f.Company, SellerBranchId = branch.Id };
            await service.ResolveAsync(branchDocument, null, true, default); Assert.Equal(binding, Source(branchDocument).BindingId);
            Assert.DoesNotContain("manual", SellerLegalEntityComposer.FromSnapshot(branchDocument.SellerSnapshotJson, null)!.BankAccountsJson);
            var issued = branchDocument.SellerSnapshotJson;
            branch.BankAccountId = "manual"; await db.SaveChangesAsync();
            var newBranchDocument = new Act { SellerCompanyId = f.Company, SellerBranchId = branch.Id };
            await service.ResolveAsync(newBranchDocument, null, true, default); Assert.Null(ReadSource(newBranchDocument));
            await service.ResolveAsync(branchDocument, null, true, default); Assert.Equal(issued, branchDocument.SellerSnapshotJson);
            Assert.Equal(1, await db.CompanyBankAccountBindings.CountAsync());
        });

    private static async Task<(Core.Fixture Fixture, int Binding)> SeedAsync(PaymentContext db)
    {
        var f = await Core.SeedAsync(db); var bound = await new CompanyBankAccountBindingService(db).BindAsync(Core.Principal(f.Actor), f.Connection, f.Bank, "local-id", await Core.VersionAsync(db, f), default);
        return (f, bound.BindingId);
    }
    private static CompanyBankDocumentSelection Source(IDocumentSeller document) => Assert.IsType<CompanyBankDocumentSelection>(ReadSource(document));
    private static CompanyBankDocumentSelection? ReadSource(IDocumentSeller document)
    {
        using var json = JsonDocument.Parse(document.SellerSnapshotJson!);
        return json.RootElement.GetProperty("BankSelection").Deserialize<CompanyBankDocumentSelection>();
    }
    private static PostgreSqlPaymentContext Context(string connection) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection).Options);
    private async Task RunAsync(bool postgres, Func<PaymentContext, Task> test)
    {
        if (postgres) { await using var database = await template.CreateDatabaseAsync(); await using var db = Context(database.ConnectionString); await test(db); }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(); await test(db); }
    }
}
