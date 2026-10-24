using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    public static IEnumerable<object[]> MoneyDocumentReferenceCases =>
        from postgres in new[] { false, true }
        from origin in new[] { "custom", "payment" }
        from scenario in new[] { "denied", "contract-only", "act-only", "allowed", "hidden-users",
            "foreign-company", "unknown-company", "branch-mismatch", "admin-mismatch", "admin-own", "admin-foreign-match", "legacy-own-branch" }
        where scenario != "legacy-own-branch" || origin == "payment"
        select new object[] { postgres, origin, scenario };

    [Theory]
    [MemberData(nameof(MoneyDocumentReferenceCases))]
    public async Task MoneyDocumentReferences_ProtectIndependentSectionsAndCompanyWithoutChangingMoney(
        bool postgres, string origin, string scenario)
    {
        await using var fixture = await CreateAccountSessionsAsync(postgres, admin: scenario.StartsWith("admin-", StringComparison.Ordinal));
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        const string contractNumber = "DOC-CONTRACT-ALPHA-481";
        const string contractTitle = "DOC-TITLE-BRAVO-481";
        const string actNumber = "DOC-ACT-CHARLIE-481";
        const string moneyPurpose = "OWN-MONEY-ONLY-002";
        var hidden = scenario == "hidden-users";
        Payment documentPayment;
        MoneyOperation? operation = null;
        if (origin == "custom")
        {
            var invoice = await PrepareInvoiceScopeAsync(db, seed, hidden ? "hidden-header" : null);
            operation = invoice.Operation;
            operation.LinkedInvoiceId = invoice.Header.InvoiceId;
            operation.Purpose = moneyPurpose;
            documentPayment = invoice.Header;
        }
        else
        {
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = seed.Actor.RoleId,
                Section = RolePermissionCatalog.Accounts,
                CanView = true,
                ViewScope = DataVisibilityScope.All
            });
            documentPayment = new Payment
            {
                DocumentKind = DocumentKind.Payment,
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                SellerCompanyId = seed.Company.Id,
                Amount = 37m,
                PaidAmount = 37m,
                Date = BankScopeDate,
                PaidDate = BankScopeDate,
                Account = seed.Own.Name,
                Description = moneyPurpose,
                CounterpartyName = "Синтетический контрагент",
            };
            if (!hidden) documentPayment.Responsibles.Add(new PaymentResponsible { UserId = seed.Actor.Id });
            db.Payments.Add(documentPayment);
        }
        await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId && item.Section == RolePermissionCatalog.Payments)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanExport, true));
        // The invoice is readable. This alone must not grant the independent sections.
        await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId && item.Section == RolePermissionCatalog.Accounts)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ViewScope, DataVisibilityScope.All));
        var contractPermission = scenario is not ("denied" or "act-only" or "admin-own");
        var actPermission = scenario is not ("denied" or "contract-only" or "admin-own");
        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = seed.Actor.RoleId,
                Section = RolePermissionCatalog.Contracts,
                CanView = contractPermission,
                ViewScope = hidden ? DataVisibilityScope.Own : DataVisibilityScope.All
            },
            new RolePermission
            {
                RoleId = seed.Actor.RoleId,
                Section = RolePermissionCatalog.Acts,
                CanView = actPermission,
                ViewScope = hidden ? DataVisibilityScope.Own : DataVisibilityScope.All
            });
        int? company = scenario == "unknown-company" ? null
            : scenario is "foreign-company" or "admin-mismatch" or "admin-foreign-match" ? seed.Other.Id : seed.Company.Id;
        if (scenario == "admin-foreign-match")
        {
            documentPayment.SellerCompanyId = seed.Other.Id;
            if (operation is not null)
            {
                operation.MoneyAccountId = seed.Foreign.Id;
                operation.AccountName = seed.Foreign.Name;
            }
        }
        int? branchId = null;
        if (scenario is "branch-mismatch" or "legacy-own-branch")
        {
            var branch = new CompanyBranch
            {
                LegalEntityId = scenario == "branch-mismatch" ? seed.Other.Id : seed.Company.Id,
                Name = "Синтетический филиал проверки"
            };
            db.CompanyBranches.Add(branch);
            await db.SaveChangesAsync();
            branchId = branch.Id;
            if (scenario == "legacy-own-branch")
            {
                documentPayment.SellerCompanyId = null;
                documentPayment.SellerBranchId = branch.Id;
            }
        }
        var contract = new Contract
        {
            Number = contractNumber,
            Title = contractTitle,
            Date = BankScopeDate,
            SellerCompanyId = company,
            SellerBranchId = branchId
        };
        documentPayment.Contract = contract;
        var act = new Act
        {
            Number = actNumber,
            Date = BankScopeDate.AddDays(1),
            Status = ActStatus.Signed,
            Invoice = documentPayment,
            SellerCompanyId = company,
            SellerBranchId = branchId,
            ResponsibleId = hidden ? null : seed.Actor.Id,
            Amount = 10m
        };
        // Read a stored historical FK, including the readonly Payment projection.
        // This fixture does not authorize creating new document links through UI/API.
        db.Acts.Add(act);
        await db.SaveChangesAsync();
        var before = await CaptureDocumentReferenceFactsAsync(db);
        var contractAllowed = scenario is "contract-only" or "allowed" or "admin-own" or "admin-foreign-match" or "legacy-own-branch";
        var actAllowed = scenario is "act-only" or "allowed" or "admin-own" or "admin-foreign-match" or "legacy-own-branch";
        var expectedId = origin == "custom" ? operation!.Id : -documentPayment.Id;
        var amount = origin == "custom" ? 10m : 37m;
        var errors = new List<string>();
        void Check(string label, Action assertion)
        {
            try { assertion(); }
            catch (Xunit.Sdk.XunitException error) { errors.Add(label + ": " + error.Message); }
        }
        void CheckRow(string label, MoneyOperationListItemDto row)
        {
            Assert.Equal(expectedId, row.Id);
            Assert.Equal(amount, row.Amount);
            Assert.Equal(moneyPurpose, row.Purpose);
            Check(label + "/contract-id", () => Assert.Equal(contractAllowed ? contract.Id : (int?)null, row.LinkedContractId));
            Check(label + "/contract-number", () => Assert.Equal(contractAllowed ? contractNumber : null, row.LinkedContractNumber));
            Check(label + "/act-id", () => Assert.Equal(actAllowed ? act.Id : (int?)null, row.LinkedActId));
            Check(label + "/act-number", () => Assert.Equal(actAllowed ? actNumber : null, row.LinkedActNumber));
            Check(label + "/act-date", () => Assert.Equal(actAllowed ? act.Date : (DateTime?)null, row.LinkedActDate));
        }
        var controller = CreateController(db, seed.Actor.Id, isAdminClaim: false);
        var list = Assert.IsAssignableFrom<IEnumerable<MoneyOperationListItemDto>>(
            Assert.IsType<OkObjectResult>(await controller.Get(null, null, null, null, null)).Value);
        CheckRow("list", Assert.Single(list));
        var pageAction = await controller.GetPaged(null, null, null, null, null);
        var page = pageAction.Value ?? Assert.IsType<MoneyOperationsPageResponse>(Assert.IsType<OkObjectResult>(pageAction.Result).Value);
        Assert.Equal(1, page.Total);
        Assert.Equal(amount, page.Summary.Income);
        Assert.Equal(0m, page.Summary.Expense);
        CheckRow("paged", Assert.Single(page.Items));
        foreach (var marker in new[] { contractNumber, contractTitle })
        {
            var rows = Assert.IsAssignableFrom<IEnumerable<MoneyOperationListItemDto>>(
                Assert.IsType<OkObjectResult>(await controller.Get(null, null, null, null, null, search: marker)).Value).ToArray();
            Check("search/" + marker, () => Assert.Equal(contractAllowed ? 1 : 0, rows.Length));
            var searched = await controller.GetPaged(null, null, null, null, null, search: marker);
            var searchedPage = searched.Value ?? Assert.IsType<MoneyOperationsPageResponse>(Assert.IsType<OkObjectResult>(searched.Result).Value);
            Check("search-total/" + marker, () => Assert.Equal(contractAllowed ? 1 : 0, searchedPage.Total));
            Check("search-money/" + marker, () => Assert.Equal(contractAllowed ? amount : 0m, searchedPage.Summary.Income));
        }
        var csv = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(await controller.ExportCsv(null, null, null, null, null)).FileContents);
        Assert.Contains(moneyPurpose, csv, StringComparison.Ordinal);
        Check("csv/contract", () => Assert.Equal(contractAllowed, csv.Contains(contractNumber, StringComparison.Ordinal)));
        Check("csv/act", () => Assert.Equal(actAllowed, csv.Contains(actNumber, StringComparison.Ordinal)));
        Assert.Equal(before, await CaptureDocumentReferenceFactsAsync(db));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static async Task<string> CaptureDocumentReferenceFactsAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Amount, item.Date, item.Currency, item.LinkedInvoiceId, item.MoneyAccountId }).ToArrayAsync(),
        Payments = await db.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Amount, item.PaidAmount, item.Date, item.Status, item.InvoiceId, item.ContractId, item.SellerCompanyId }).ToArrayAsync(),
        Contracts = await db.Contracts.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Number, item.Title, item.Date, item.SellerCompanyId, item.SellerBranchId }).ToArrayAsync(),
        Acts = await db.Acts.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Number, item.Date, item.Amount, item.InvoiceId, item.SellerCompanyId, item.SellerBranchId }).ToArrayAsync(),
    });
}
