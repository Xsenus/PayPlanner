using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;
using Core = PayPlanner.Api.Tests.CompanyBankAccountBindingCommandTests;

namespace PayPlanner.Api.Tests;

public sealed class CompanyBankBindingLifecycleTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> Sources()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var provider in new[] { BankProviderKeys.Sber, BankProviderKeys.Tochka })
                foreach (var linkedWallet in new[] { false, true }) yield return [postgres, provider, linkedWallet];
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public Task SameNumbersAcrossCompaniesAndSourcesRemainExplicitThroughDocumentAndRevocation(bool postgres, string provider, bool linkedWallet)
        => RunAsync(postgres, async db =>
        {
            var f = await Core.SeedAsync(db);
            (await db.BankConnections.SingleAsync()).Provider = provider;
            if (!linkedWallet) (await db.MoneyAccounts.SingleAsync()).BankConnectionAccountId = null;
            var other = new LegalEntity { ShortName = "Other own company", BankAccountsJson = Core.Profile(), ProfileStatus = CompanyProfileStatuses.Active };
            db.LegalEntities.Add(other); await db.SaveChangesAsync();
            var peer = new BankConnection { LegalEntityId = f.Company, Provider = provider == BankProviderKeys.Sber ? BankProviderKeys.Tochka : BankProviderKeys.Sber, Status = BankConnectionStatuses.Active };
            var foreign = new BankConnection { LegalEntityId = other.Id, Provider = provider, Status = BankConnectionStatuses.Active };
            db.BankConnections.AddRange(peer, foreign); await db.SaveChangesAsync();
            var peerBank = Bank(peer.Id); var foreignBank = Bank(foreign.Id);
            db.BankConnectionAccounts.AddRange(peerBank, foreignBank);
            db.MoneyAccounts.Add(new() { Name = "Unselected manual duplicate", CompanyId = f.Company, Source = MoneyOperationSource.Bank, BankAccountNumber = Core.Number, BankBik = Core.Bik, Currency = "RUB" });
            (await db.SystemSettings.SingleAsync(x => x.Key == "legalEntities.mainCompanyIds")).Value = $"[{f.Company},{other.Id}]";
            await db.SaveChangesAsync(); var history = await Core.HistoryAsync(db);
            var sync = CompanyBankAdminAccessTests.SyncProbe.Create();
            using var server = CompanyBankAdminAccessTests.Server(db, sync);
            // Match Program's wire contract: optional null properties are omitted.
            server.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            using var client = server.CreateClient();
            client.DefaultRequestHeaders.Add("X-Fixture-Identity", f.Actor.ToString()); client.DefaultRequestHeaders.Add("X-Fixture-Admin", "true");
            var initial = await ReportAsync(client, f.Connection, f.Company);
            var row = Assert.Single(initial.Accounts); Assert.Equal(f.Bank, row.BankAccountId); Assert.Equal(provider, row.Provider);
            var group = Assert.Single(initial.Groups); Assert.Equal(new[] { f.Bank, peerBank.Id }.Order(), group.BankAccountIds.Order());
            Assert.DoesNotContain(foreignBank.Id, group.BankAccountIds); Assert.Contains("MultipleBankSources", row.Issues);
            Assert.Contains("ManualMoneyCandidatesRequireReview", row.Issues);
            Assert.Equal(linkedWallet ? new[] { f.Money } : [], row.LinkedMoneyAccountIds);
            var otherReport = await ReportAsync(client, foreign.Id, other.Id);
            Assert.Equal(foreignBank.Id, Assert.Single(otherReport.Accounts).BankAccountId);
            Assert.Equal(new[] { foreignBank.Id }, Assert.Single(otherReport.Groups).BankAccountIds);
            Assert.Empty(initial.ActiveBindings); Assert.Empty(otherReport.ActiveBindings);
            using (var wrong = await BindAsync(client, f.Connection, foreignBank.Id, initial.Version)) Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
            Assert.Equal(history, await Core.HistoryAsync(db)); Assert.Empty(await db.CompanyBankAccountBindings.ToArrayAsync());
            var selected = await BindOkAsync(client, f.Connection, f.Bank, initial.Version);
            var current = await ReportAsync(client, f.Connection, f.Company);
            Assert.Equal(selected.BindingId, Assert.Single(current.ActiveBindings).Id);
            Assert.Empty((await ReportAsync(client, foreign.Id, other.Id)).ActiveBindings);
            var independent = await BindOkAsync(client, foreign.Id, foreignBank.Id, otherReport.Version);
            Assert.NotEqual(selected.BindingId, independent.BindingId);
            Assert.Equal(current.Version, (await ReportAsync(client, f.Connection, f.Company)).Version);
            Assert.Equal(selected.BindingId, Assert.Single((await ReportAsync(client, peer.Id, f.Company)).ActiveBindings).Id);
            var seller = new DocumentSellerService(db); var issued = new Act { SellerCompanyId = f.Company };
            await seller.ResolveAsync(issued, null, true, default);
            var source = Source(issued); Assert.Equal(selected.BindingId, source!.BindingId); Assert.Equal(f.Bank, source.BankAccountId);
            Assert.Equal(provider, source.Provider); Assert.Equal(linkedWallet ? f.Money : (int?)null, source.MoneyAccountId);
            var snapshot = issued.SellerSnapshotJson;
            var otherDocument = new Act { SellerCompanyId = other.Id }; await seller.ResolveAsync(otherDocument, null, true, default);
            Assert.Equal(independent.BindingId, Source(otherDocument)!.BindingId); Assert.Equal(foreignBank.Id, Source(otherDocument)!.BankAccountId);
            (await db.BankConnectionAccounts.SingleAsync(x => x.Id == f.Bank)).Status = BankAccountStatuses.NotFoundInBank; await db.SaveChangesAsync();
            var absent = new Act { SellerCompanyId = f.Company };
            await Assert.ThrowsAsync<SellerSelectionException>(() => seller.ResolveAsync(absent, null, true, default)); Assert.Null(absent.SellerSnapshotJson);
            await seller.ResolveAsync(issued, null, true, default); Assert.Equal(snapshot, issued.SellerSnapshotJson);
            (await db.BankConnectionAccounts.SingleAsync(x => x.Id == f.Bank)).Status = BankAccountStatuses.Open; await db.SaveChangesAsync();
            var returned = new Act { SellerCompanyId = f.Company }; await seller.ResolveAsync(returned, null, true, default);
            Assert.Equal(selected.BindingId, Source(returned)!.BindingId); Assert.Equal(f.Bank, Source(returned)!.BankAccountId);
            var foreignCurrent = await ReportAsync(client, foreign.Id, other.Id);
            using (var wrongRevoke = await RevokeAsync(client, foreign.Id, selected.BindingId, foreignCurrent.Version)) Assert.Equal(HttpStatusCode.NotFound, wrongRevoke.StatusCode);
            current = await ReportAsync(client, f.Connection, f.Company);
            using (var revoke = await RevokeAsync(client, f.Connection, selected.BindingId, current.Version)) Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            Assert.Empty((await ReportAsync(client, f.Connection, f.Company)).ActiveBindings);
            Assert.Equal(independent.BindingId, Assert.Single((await ReportAsync(client, foreign.Id, other.Id)).ActiveBindings).Id);
            await seller.ResolveAsync(issued, null, true, default); Assert.Equal(snapshot, issued.SellerSnapshotJson);
            var afterRevoke = new Act { SellerCompanyId = f.Company }; await seller.ResolveAsync(afterRevoke, null, true, default);
            Assert.Null(Source(afterRevoke)); // Explicit revocation restores existing manual-account semantics.
            var stored = await db.CompanyBankAccountBindings.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
            Assert.Equal(2, stored.Length); Assert.NotNull(stored.Single(x => x.Id == selected.BindingId).RevokedAtUtc);
            Assert.Null(stored.Single(x => x.Id == independent.BindingId).RevokedAtUtc);
            Assert.Equal(history, await Core.HistoryAsync(db)); Assert.Empty(((CompanyBankAdminAccessTests.SyncProbe)(object)sync).Calls);
        });

    private static BankConnectionAccount Bank(int connectionId) => new() { BankConnectionId = connectionId, ExternalAccountId = "synthetic", AccountNumber = Core.Number, Bik = Core.Bik, Currency = "RUB", Status = BankAccountStatuses.Open };
    private static Task<HttpResponseMessage> BindAsync(HttpClient client, int connectionId, int bankId, string version)
        => client.PostAsJsonAsync($"/api/bank-connections/{connectionId}/profile-account-bindings", new { bankAccountId = bankId, profileAccountId = "local-id", expectedVersion = version });
    private static async Task<CompanyBankBindingReceipt> BindOkAsync(HttpClient client, int connectionId, int bankId, string version)
    {
        using var response = await BindAsync(client, connectionId, bankId, version); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore); return (await response.Content.ReadFromJsonAsync<CompanyBankBindingReceipt>())!;
    }
    private static Task<HttpResponseMessage> RevokeAsync(HttpClient client, int connectionId, int bindingId, string version)
        => client.PostAsJsonAsync($"/api/bank-connections/{connectionId}/profile-account-bindings/{bindingId}/revoke", new { expectedVersion = version });
    private static async Task<CompanyBankAccountMatchReport> ReportAsync(HttpClient client, int connectionId, int companyId)
    {
        using var response = await client.GetAsync($"/api/bank-connections/{connectionId}/profile-account-matches");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var wire = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("synthetic-not-a-token", wire);
        var report = JsonSerializer.Deserialize<CompanyBankAccountMatchReport>(wire, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(companyId, report.CompanyId); Assert.Equal(connectionId, report.BankConnectionId); return report;
    }
    private static CompanyBankDocumentSelection? Source(IDocumentSeller document)
    {
        using var json = JsonDocument.Parse(document.SellerSnapshotJson!); return json.RootElement.GetProperty("BankSelection").Deserialize<CompanyBankDocumentSelection>();
    }
    private async Task RunAsync(bool postgres, Func<PaymentContext, Task> test)
    {
        if (postgres) { await using var database = await template.CreateDatabaseAsync(); await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await test(db); }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(); await test(db); }
    }
}
