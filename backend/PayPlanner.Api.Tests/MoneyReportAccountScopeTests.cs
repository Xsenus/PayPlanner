using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    [Theory]
    [InlineData(false, "cash-flow")]
    [InlineData(true, "cash-flow")]
    [InlineData(false, "counterparty-csv")]
    [InlineData(true, "counterparty-csv")]
    public async Task MoneyReportAccountScope_ViewAllDoesNotGrantAnotherCompany(bool postgres, string endpoint)
        => await AssertCompanyReportAsync(postgres, endpoint, "assigned");

    public static IEnumerable<object[]> MoneyReportAdditionalCompanyCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var endpoint in new[] { "cash-flow", "counterparty-csv" })
                foreach (var scenario in new[] { "admin", "both", "none", "closed", "inactive", "archived",
                     "unknown", "incoherent", "legacy", "imported-unknown", "non-employee" })
                    yield return new object[] { postgres, endpoint, scenario };
    }

    [Theory]
    [MemberData(nameof(MoneyReportAdditionalCompanyCases))]
    public async Task MoneyReportAccountScope_PreservesPermittedHistory(bool postgres, string endpoint, string scenario)
        => await AssertCompanyReportAsync(postgres, endpoint, scenario);

    private async Task AssertCompanyReportAsync(bool postgres, string endpoint, string scenario)
    {
        await using var fixture = await CreateAccountSessionsAsync(postgres, admin: scenario == "admin");
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        const string ownMarker = "SYNTHETIC-REPORT-OWN-COUNTERPARTY-92641";
        const string foreignMarker = "SYNTHETIC-REPORT-FOREIGN-COUNTERPARTY-92641";
        const string sameName = "SYNTHETIC-SAME-ACCOUNT-NAME";
        var day = DateTime.SpecifyKind(BankScopeDate.Date, DateTimeKind.Utc);
        await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId && item.Section == RolePermissionCatalog.Payments)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.CanView, true).SetProperty(item => item.CanExport, true)
                .SetProperty(item => item.ViewScope, DataVisibilityScope.All));
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = seed.Actor.RoleId,
            Section = RolePermissionCatalog.Reports,
            CanView = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.All,
        });
        await db.MoneyAccounts.Where(item => item.Id == seed.Own.Id || item.Id == seed.Foreign.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Name, sameName));
        MoneyOperation Operation(int accountId, decimal amount, string counterparty) => new()
        {
            MoneyAccountId = accountId,
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Currency = "RUB",
            Date = day,
            AccountName = sameName,
            Amount = amount,
            CounterpartyName = counterparty,
            ResponsibleUserId = seed.Actor.Id,
        };
        var ownOperation = Operation(seed.Own.Id, 10m, ownMarker);
        if (scenario == "both")
            db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = seed.Actor.Id, LegalEntityId = seed.Other.Id });
        if (scenario == "none")
            await db.UserCompanyAccesses.Where(item => item.UserId == seed.Actor.Id).ExecuteDeleteAsync();
        if (scenario == "non-employee")
            await db.Users.Where(item => item.Id == seed.Actor.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsEmployee, false));
        if (scenario == "closed")
            await db.BankConnectionAccounts.Where(item => item.Id == seed.Own.BankConnectionAccountId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, BankAccountStatuses.Closed));
        if (scenario == "inactive")
            await db.MoneyAccounts.Where(item => item.Id == seed.Own.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        if (scenario == "archived")
            await db.LegalEntities.Where(item => item.Id == seed.Company.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.ProfileStatus, CompanyProfileStatuses.Archived));
        if (scenario == "unknown") ownOperation.MoneyAccountId = seed.Unknown.Id;
        if (scenario == "incoherent") ownOperation.MoneyAccountId = seed.Incoherent.Id;
        if (scenario is "legacy" or "imported-unknown") ownOperation.MoneyAccountId = null;
        if (scenario == "imported-unknown") ownOperation.SourceMovementKey = "SYNTHETIC-UNATTRIBUTED-IMPORT-92641";
        db.MoneyOperations.AddRange(ownOperation, Operation(seed.Foreign.Id, 20m, foreignMarker));
        await db.SaveChangesAsync();
        if (scenario == "assigned")
            Assert.Equal(seed.Company.Id, await db.UserCompanyAccesses.Where(item => item.UserId == seed.Actor.Id)
                .Select(item => item.LegalEntityId).SingleAsync());
        var seesOwn = scenario is not ("none" or "unknown" or "incoherent" or "imported-unknown" or "non-employee");
        var seesForeign = scenario is "admin" or "both";
        var expectedIncome = (seesOwn ? 10m : 0m) + (seesForeign ? 20m : 0m);
        var expectedCount = (seesOwn ? 1 : 0) + (seesForeign ? 1 : 0);
        var before = await CaptureHistoricalInvoiceMoneyAsync(db);
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server, claim: false);
        var date = Uri.EscapeDataString(day.ToString("O", CultureInfo.InvariantCulture));
        var url = endpoint == "cash-flow" ? "/api/reports/cash-flow" : "/api/reports/management.csv?report=counterparties";
        url += (endpoint == "cash-flow" ? "?" : "&") + $"from={date}&to={date}&source=Bank";
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var errors = new List<string>();
        if (endpoint == "cash-flow")
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.GetProperty("income").GetDecimal() != expectedIncome) errors.Add("Income differs from permitted company history.");
            if (root.GetProperty("incomeCount").GetInt32() != expectedCount) errors.Add("IncomeCount differs from permitted company history.");
            if (root.GetProperty("closingBalance").GetDecimal() != expectedIncome) errors.Add("ClosingBalance differs from permitted company history.");
        }
        else
        {
            if (text.Contains(ownMarker, StringComparison.Ordinal) != seesOwn) errors.Add("Own counterparty CSV visibility differs from the permitted history.");
            if (text.Contains(foreignMarker, StringComparison.Ordinal) != seesForeign) errors.Add("Other company counterparty CSV visibility differs from company grants.");
        }
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureHistoricalInvoiceMoneyAsync(verify));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    public static IEnumerable<object[]> MoneyReportRevokedPermissionCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var endpoint in new[] { "cash-flow", "counterparty-csv" })
                foreach (var reason in new[] { "payments-view", "payments-export", "reports-view", "reports-export", "inactive", "unapproved" })
                    yield return new object[] { postgres, endpoint, reason };
    }

    [Theory]
    [MemberData(nameof(MoneyReportRevokedPermissionCases))]
    public async Task MoneyReportAccountScope_UsesFreshPermissions(bool postgres, string endpoint, string reason)
    {
        await using var fixture = await CreateAccountSessionsAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var actor = fixture.Seed.Actor;
        await db.RolePermissions.Where(item => item.RoleId == actor.RoleId && item.Section == RolePermissionCatalog.Payments)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.CanView, true).SetProperty(item => item.CanExport, true));
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = actor.RoleId,
            Section = RolePermissionCatalog.Reports,
            CanView = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.All,
        });
        await db.SaveChangesAsync();
        using var server = fixture.HttpServer();
        // A stale admin claim must never replace the actor's actual role in the database.
        using var client = fixture.Client(server, claim: true);
        var url = endpoint == "cash-flow" ? "/api/reports/cash-flow" : "/api/reports/management.csv?report=counterparties";
        using var before = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        if (reason == "payments-view")
            await db.RolePermissions.Where(item => item.RoleId == actor.RoleId && item.Section == RolePermissionCatalog.Payments)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.CanView, false));
        if (reason == "payments-export")
            await db.RolePermissions.Where(item => item.RoleId == actor.RoleId && item.Section == RolePermissionCatalog.Payments)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.CanExport, false));
        if (reason == "reports-view")
            await db.RolePermissions.Where(item => item.RoleId == actor.RoleId && item.Section == RolePermissionCatalog.Reports)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.CanView, false));
        if (reason == "reports-export")
            await db.RolePermissions.Where(item => item.RoleId == actor.RoleId && item.Section == RolePermissionCatalog.Reports)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.CanExport, false));
        if (reason == "inactive")
            await db.Users.Where(item => item.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        if (reason == "unapproved")
            await db.Users.Where(item => item.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsApproved, false));
        var fingerprint = await CaptureHistoricalInvoiceMoneyAsync(db);
        var denied = reason is "payments-view" or "inactive" or "unapproved"
            || endpoint == "cash-flow" && reason == "reports-view"
            || endpoint == "counterparty-csv" && reason is "payments-export" or "reports-export";
        using var after = await client.GetAsync(url);
        await using var verify = fixture.Open();
        Assert.Equal(fingerprint, await CaptureHistoricalInvoiceMoneyAsync(verify));
        Assert.Equal(denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK, after.StatusCode);
    }
}
