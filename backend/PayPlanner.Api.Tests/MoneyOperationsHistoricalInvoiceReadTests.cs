using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    public static IEnumerable<object[]> HistoricalInvoiceReadCases =>
        from postgres in new[] { false, true }
        from reason in new[] { "foreign-company", "unknown-company", "no-view", "hidden-header", "no-document", "cancelled", "foreign-installment", "allowed", "inherited-installment", "no-edit", "own-scope-no-view", "own-scope-allowed" }
        from endpoint in new[] { "list", "paged", "detail", "search", "invoice-filter", "csv" }
        select new object[] { postgres, reason, endpoint };

    [Theory]
    [MemberData(nameof(HistoricalInvoiceReadCases))]
    public async Task HistoricalInvoiceRead_KeepsOwnMoneyWithoutDisclosingInaccessibleDocument(bool postgres, string reason, string endpoint)
    {
        await using var fixture = await CreateAccountSessionsAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var ownScope = reason.StartsWith("own-scope-", StringComparison.Ordinal);
        var documentReason = ownScope ? reason["own-scope-".Length..] : reason;
        var setup = await PrepareInvoiceScopeAsync(db, seed, documentReason == "allowed" ? null : documentReason);
        // This matrix isolates invoice access. Give the separately controlled
        // contract its explicit own-scope permission; independent contract denial
        // remains covered by MoneyOperationsDocumentReferenceScopeTests.
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = seed.Actor.RoleId,
            Section = RolePermissionCatalog.Contracts,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });
        const string invoiceMarker = "SYNTHETIC-PRIVATE-INVOICE-927413";
        const string contractMarker = "SYNTHETIC-PRIVATE-CONTRACT-927413";
        setup.Header.Invoice!.Number = invoiceMarker;
        var contract = new Contract { Number = contractMarker, Title = contractMarker, Date = BankScopeDate, SellerCompanyId = setup.Header.SellerCompanyId };
        db.Contracts.Add(contract);
        if (reason != "no-document") setup.Header.Contract = contract;
        // Seed a pre-existing historical association directly; new link endpoints are tested elsewhere.
        setup.Operation.LinkedInvoiceId = setup.Header.InvoiceId;
        setup.Operation.ResponsibleUserId = null;
        setup.Operation.Purpose = "Сведения исходной банковской строки";
        await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanExport, true));
        if (ownScope)
            await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId && item.Section == RolePermissionCatalog.Payments)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ViewScope, DataVisibilityScope.Own));
        await db.SaveChangesAsync();
        var before = await CaptureInvoiceScopeAsync(db);
        var controller = CreateController(db, seed.Actor.Id);
        var allowed = documentReason is "allowed" or "inherited-installment" or "no-edit";

        void CheckRow(MoneyOperationListItemDto row)
        {
            Assert.Equal(setup.Operation.Id, row.Id);
            Assert.Equal(seed.Own.Id, row.MoneyAccountId);
            Assert.Equal(10m, row.Amount);
            Assert.Equal("RUB", row.Currency);
            Assert.Equal(setup.Operation.Date, row.Date);
            Assert.Equal(setup.Operation.Purpose, row.Purpose);
            Assert.Equal(allowed ? setup.Header.InvoiceId : null, row.LinkedInvoiceId);
            Assert.Equal(!allowed, row.IsInvoiceLinkProtected);
            Assert.Equal(allowed ? invoiceMarker : null, row.LinkedInvoiceNumber);
            Assert.Equal(allowed ? setup.Header.Invoice!.Date : (DateTime?)null, row.LinkedInvoiceDate);
            Assert.Equal(allowed ? contract.Id : (int?)null, row.LinkedContractId);
            Assert.Equal(allowed ? contractMarker : null, row.LinkedContractNumber);
            Assert.Equal(allowed ? seed.Actor.Id : (int?)null, row.ResponsibleUserId);
            if (!allowed) Assert.Null(row.ResponsibleName);
        }

        if (endpoint == "detail")
        {
            var result = Assert.IsType<OkObjectResult>(await controller.GetById(setup.Operation.Id, default));
            var row = Assert.IsType<MoneyOperation>(result.Value);
            Assert.Equal(10m, row.Amount);
            Assert.Equal(seed.Own.Id, row.MoneyAccountId);
            Assert.Equal(setup.Operation.Purpose, row.Purpose);
            Assert.Equal(allowed ? setup.Header.InvoiceId : null, row.LinkedInvoiceId);
            Assert.Equal(!allowed, row.IsInvoiceLinkProtected);
        }
        else if (endpoint == "paged")
        {
            var result = await controller.GetPaged(null, null, null, null, null);
            var page = result.Value ?? Assert.IsType<MoneyOperationsPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
            CheckRow(Assert.Single(page.Items));
            Assert.Equal(1, page.Total);
            Assert.Equal(10m, page.Summary.Income);
            Assert.Equal(0m, page.Summary.Expense);
            Assert.Equal(10m, page.Summary.EndBalance);
        }
        else if (endpoint == "csv")
        {
            var file = Assert.IsType<FileContentResult>(await controller.ExportCsv(null, null, null, null, null));
            var csv = Encoding.UTF8.GetString(file.FileContents);
            Assert.Contains(setup.Operation.Purpose!, csv, StringComparison.Ordinal);
            if (allowed)
            {
                Assert.Contains(invoiceMarker, csv, StringComparison.Ordinal);
                Assert.Contains(contractMarker, csv, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain(invoiceMarker, csv, StringComparison.Ordinal);
                Assert.DoesNotContain(contractMarker, csv, StringComparison.Ordinal);
            }
        }
        else
        {
            var filtered = endpoint is "search" or "invoice-filter";
            var result = Assert.IsType<OkObjectResult>(await controller.Get(null, null, null, null, null,
                linkedInvoiceId: endpoint == "invoice-filter" ? setup.Header.InvoiceId : null,
                search: endpoint == "search" ? invoiceMarker : null));
            var rows = Assert.IsAssignableFrom<IEnumerable<MoneyOperationListItemDto>>(result.Value);
            if (filtered && !allowed) Assert.Empty(rows);
            else CheckRow(Assert.Single(rows));
        }
        // Reading or redacting must never rewrite the historical association or money.
        Assert.Equal(before, await CaptureInvoiceScopeAsync(db));
        Assert.Equal(setup.Header.InvoiceId, await db.MoneyOperations.AsNoTracking()
            .Where(item => item.Id == setup.Operation.Id).Select(item => item.LinkedInvoiceId).SingleAsync());
    }
}
