using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    public static IEnumerable<object[]> StandalonePaymentCompanyCases =>
        from postgres in new[] { false, true }
        from scenario in new[] { "own", "foreign", "unregistered", "admin-foreign", "own-branch", "foreign-branch", "legacy", "legacy-own-branch", "legacy-foreign-branch", "deleted-company" }
        from endpoint in new[] { "list", "paged", "search", "csv" }
        select new object[] { postgres, scenario, endpoint };

    [Theory]
    [MemberData(nameof(StandalonePaymentCompanyCases))]
    public async Task StandalonePaymentCompany_ReadsNeverMixKnownCompanies(bool postgres, string scenario, string endpoint)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: scenario == "admin-foreign");
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        const string marker = "SYNTHETIC-STANDALONE-PAYMENT-682194";
        int? companyId = scenario switch
        {
            "own" or "own-branch" or "foreign-branch" or "deleted-company" => seed.Company.Id,
            "legacy" or "legacy-own-branch" or "legacy-foreign-branch" => null,
            "unregistered" => seed.Supplier.Id,
            _ => seed.Other.Id,
        };
        var payment = new Payment
        {
            DocumentKind = DocumentKind.Payment,
            SellerCompanyId = companyId,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Date = BankScopeDate,
            PaidDate = BankScopeDate,
            Amount = 37m,
            PaidAmount = 37m,
            Description = marker,
            CounterpartyName = marker,
            // A matching label must not grant access to another company's payment.
            Account = seed.Own.Name,
        };
        if (scenario is "own-branch" or "foreign-branch" or "legacy-own-branch" or "legacy-foreign-branch")
        {
            var branch = new CompanyBranch
            {
                LegalEntityId = scenario is "own-branch" or "legacy-own-branch" ? seed.Company.Id : seed.Other.Id,
                Name = "Синтетический филиал",
                Status = scenario == "own-branch" ? CompanyProfileStatuses.Archived : CompanyProfileStatuses.Active,
            };
            db.CompanyBranches.Add(branch);
            await db.SaveChangesAsync();
            payment.SellerBranchId = branch.Id;
        }
        if (scenario == "own-branch")
            await db.LegalEntities.Where(company => company.Id == seed.Company.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(company => company.ProfileStatus, CompanyProfileStatuses.Archived));
        if (scenario == "deleted-company")
            await db.LegalEntities.Where(company => company.Id == seed.Company.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(company => EF.Property<bool>(company, "IsDeleted"), true));
        db.Payments.Add(payment);
        await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId && item.Section == RolePermissionCatalog.Payments)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanExport, true));
        await db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(await db.Payments.AsNoTracking().Where(item => item.Id == payment.Id)
            .Select(item => new { item.Id, item.SellerCompanyId, item.SellerBranchId, item.Amount, item.PaidAmount, item.Status, item.Date }).SingleAsync());
        var allowed = scenario is "own" or "own-branch" or "admin-foreign" or "legacy" or "legacy-own-branch";
        var controller = CreateController(db, seed.Actor.Id);
        if (endpoint == "csv")
        {
            var file = Assert.IsType<FileContentResult>(await controller.ExportCsv(null, null, null, null, null));
            Assert.Equal(allowed, Encoding.UTF8.GetString(file.FileContents).Contains(marker, StringComparison.Ordinal));
        }
        else if (endpoint == "paged")
        {
            var result = await controller.GetPaged(null, null, null, null, null);
            var page = result.Value ?? Assert.IsType<MoneyOperationsPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal(allowed ? 1 : 0, page.Total);
            Assert.Equal(allowed ? 37m : 0m, page.Summary.Income);
            Assert.Equal(0m, page.Summary.Expense);
            Assert.Equal(allowed ? 37m : 0m, page.Summary.EndBalance);
            if (allowed) Assert.Equal(-payment.Id, Assert.Single(page.Items).Id);
            else Assert.Empty(page.Items);
        }
        else
        {
            var result = Assert.IsType<OkObjectResult>(await controller.Get(null, null, null, null, null,
                search: endpoint == "search" ? marker : null));
            var rows = Assert.IsAssignableFrom<IEnumerable<MoneyOperationListItemDto>>(result.Value).ToArray();
            if (allowed)
            {
                Assert.Equal(-payment.Id, Assert.Single(rows).Id);
                Assert.Equal(37m, rows[0].Amount);
            }
            else Assert.Empty(rows);
        }
        Assert.Equal(before, JsonSerializer.Serialize(await db.Payments.AsNoTracking().Where(item => item.Id == payment.Id)
            .Select(item => new { item.Id, item.SellerCompanyId, item.SellerBranchId, item.Amount, item.PaidAmount, item.Status, item.Date }).SingleAsync()));
    }
}
