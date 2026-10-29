using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Documents;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class ActContractImagePrintTests
{
    private static IEnumerable<(string Section, int Id)> HtmlPrintDocuments(Fixture fixture)
        => [("invoices", fixture.Act.Invoice!.Id), ("contracts", fixture.Contract.Id)];

    [Theory]
    [MemberData(nameof(Masks))]
    public async Task PrintIssueHttp_ExplicitConfirmationFreezesWithoutChangingPreviewOrFinancialData(int mask)
    {
        await using var fixture = await DefaultsFixtureAsync();
        await SetDefaultsAsync(fixture, mask);
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in HtmlPrintDocuments(fixture))
        {
            using var preview = await client.GetAsync($"/api/{section}/{id}/document?format=html&{Query(mask)}");
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }
        Assert.Null(await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Null(await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
        foreach (var (section, id) in HtmlPrintDocuments(fixture))
        {
            using var issue = await client.PostAsync($"/api/{section}/{id}/print?{Query(mask)}", null);
            Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
            Assert.Equal("text/html", issue.Content.Headers.ContentType!.MediaType);
            Assert.True(issue.Headers.CacheControl!.NoStore);
            AssertImages(await issue.Content.ReadAsStringAsync(), (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        }
        foreach (var snapshot in new[] {
            await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync(),
            await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync() })
        {
            var metadata = Assert.IsType<SellerSnapshotMetadata>(SellerLegalEntityComposer.ReadMetadata(snapshot));
            Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, metadata.FreezeReason);
            Assert.True(metadata.ImagePrintDefaultsFrozen);
            Assert.NotNull(metadata.FrozenAtUtc);
        }
        Assert.Null(await fixture.Db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        var payment = await fixture.Db.Payments.AsNoTracking().Include(row => row.Invoice).SingleAsync();
        Assert.Equal(100m, payment.Amount); Assert.Equal(PaymentType.Income, payment.Type);
        Assert.Equal("CMP-03", payment.Account); Assert.Equal("DEFAULTS", payment.Invoice!.Number);
        Assert.Equal(100m, await fixture.Db.Contracts.AsNoTracking().Select(row => row.Amount).SingleAsync());
        Assert.Equal(1, await fixture.Db.ClientContracts.CountAsync());
        Assert.Empty(await fixture.Db.MoneyOperations.ToArrayAsync());
        Assert.Empty(await fixture.Db.TaskJournalItems.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrintIssueHttp_ReusesFrozenSellerAndDefaultsUnderBothPolicies(bool onCreate)
    {
        await using var fixture = await DefaultsFixtureAsync();
        await SetDefaultsAsync(fixture, 5);
        if (onCreate)
        {
            fixture.Act.Invoice!.SellerSnapshotPolicy = fixture.Contract.SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.OnCreate;
            await fixture.Db.SaveChangesAsync();
            var seller = new DocumentSellerService(fixture.Db);
            await seller.ResolveAsync(fixture.Act.Invoice, fixture.Company, true, default);
            await seller.ResolveAsync(fixture.Contract, fixture.Company, true, default);
        }
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in HtmlPrintDocuments(fixture))
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/{section}/{id}/print", null)).StatusCode);
        var invoice = await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync();
        var contract = await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync();
        fixture.Company.ShortName = "Новый профиль";
        fixture.Company.LogoDataUrl = fixture.Company.SealDataUrl = fixture.Company.DirectorSignatureDataUrl = null;
        await SetDefaultsAsync(fixture, 2);
        foreach (var (section, id) in HtmlPrintDocuments(fixture))
        {
            using var repeated = await client.PostAsync($"/api/{section}/{id}/print", null);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            var html = await repeated.Content.ReadAsStringAsync();
            AssertImages(html, true, false, true); Assert.Contains(Image, html); Assert.DoesNotContain("Новый профиль", html);
        }
        Assert.Equal(invoice, await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(contract, await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(onCreate ? DocumentSellerSnapshotPolicy.OnCreate : DocumentSellerSnapshotPolicy.FirstFile,
            SellerLegalEntityComposer.ReadMetadata(invoice)!.FreezeReason);
    }

    [Fact]
    public async Task PrintIssueHttp_InvalidMissingDeniedAnonymousRequestsNeverCapture()
    {
        await using var fixture = await DefaultsFixtureAsync();
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in HtmlPrintDocuments(fixture))
        {
            foreach (var flag in new[] { "includeLogo", "includeSeal", "includeSignature" })
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/{section}/{id}/print?{flag}=invalid", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/{section}/2147483647/print", null)).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync($"/api/{section}/{id}/print")).StatusCode);
        }
        foreach (var permission in fixture.User.Role!.Permissions) permission.CanExport = false;
        await fixture.Db.SaveChangesAsync();
        foreach (var (section, id) in HtmlPrintDocuments(fixture))
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/{section}/{id}/print", null)).StatusCode);
        foreach (var permission in fixture.User.Role.Permissions) { permission.CanExport = true; permission.CanView = false; }
        await fixture.Db.SaveChangesAsync();
        foreach (var (section, id) in HtmlPrintDocuments(fixture))
            Assert.Contains((await client.PostAsync($"/api/{section}/{id}/print", null)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        client.DefaultRequestHeaders.Clear();
        foreach (var (section, id) in HtmlPrintDocuments(fixture))
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/{section}/{id}/print", null)).StatusCode);
        Assert.Null(await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Null(await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
    }

    [Theory]
    [InlineData("invoices")]
    [InlineData("contracts")]
    public async Task PrintIssueHttp_DamagedDefaultsFailWithoutCapturing(string section)
    {
        await using var fixture = await DefaultsFixtureAsync();
        fixture.Db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(fixture.Company.Id), Value = "broken" });
        await fixture.Db.SaveChangesAsync();
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        var id = HtmlPrintDocuments(fixture).Single(document => document.Section == section).Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/{section}/{id}/print", null)).StatusCode);
        Assert.Null(await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Null(await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
    }

    [Theory]
    [InlineData("invoices")]
    [InlineData("contracts")]
    public async Task PrintIssue_CancelledBeforePreparationDoesNotCapture(string section)
    {
        await using var fixture = await DefaultsFixtureAsync();
        var context = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, fixture.User.Id.ToString())], "print-test"))
            }
        };
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => section == "invoices"
            ? new InvoicesController(fixture.Db, new NoEmail(), new NoRequirements()) { ControllerContext = context }
                .PreparePrintDocument(fixture.Act.Invoice!.Id, ct: cancellation.Token)
            : new ContractsController(fixture.Db, new NoRequirements()) { ControllerContext = context }
                .PreparePrintDocument(fixture.Contract.Id, ct: cancellation.Token));
        Assert.Null(await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Null(await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
    }

    [Theory]
    [InlineData("invoices", false)]
    [InlineData("contracts", false)]
    [InlineData("invoices", true)]
    [InlineData("contracts", true)]
    public async Task PrintIssueHttp_RechecksRevocationInsideCapture(string section, bool visibility)
    {
        await using var fixture = await DefaultsFixtureAsync();
        var hook = new PrintIssueRaceHook(fixture.Db, async ct =>
        {
            foreach (var permission in fixture.User.Role!.Permissions)
                if (visibility) permission.ViewScope = DataVisibilityScope.Own; else permission.CanExport = false;
            fixture.Act.Client!.ResponsibleUserId = null; fixture.Act.Client.ResponsibleUser = null;
            await fixture.Db.SaveChangesAsync(ct);
        });
        using var server = Server(fixture, documentSeller: hook); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        var id = HtmlPrintDocuments(fixture).Single(document => document.Section == section).Id;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/{section}/{id}/print", null)).StatusCode);
        Assert.Null(await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Null(await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
    }

    [Fact]
    public async Task PrintIssueHttp_KeepsExpenseInvoiceAndLegacySnapshotsUnchanged()
    {
        await using var fixture = await DefaultsFixtureAsync();
        var legacy = "{\"ShortName\":\"Legacy\",\"ImagesFrozen\":true}";
        fixture.Act.Invoice!.SellerSnapshotJson = fixture.Contract.SellerSnapshotJson = legacy;
        await fixture.Db.SaveChangesAsync();
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in HtmlPrintDocuments(fixture))
        {
            using var response = await client.PostAsync($"/api/{section}/{id}/print", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Legacy", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(legacy, await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(legacy, await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        fixture.Act.Invoice.Type = PaymentType.Expense; fixture.Act.Invoice.SellerSnapshotJson = null;
        await fixture.Db.SaveChangesAsync();
        using var expense = await client.PostAsync($"/api/invoices/{fixture.Act.Invoice.Id}/print?{Query(7)}", null);
        Assert.Equal(HttpStatusCode.OK, expense.StatusCode);
        Assert.DoesNotContain(Image, await expense.Content.ReadAsStringAsync());
        Assert.Null(await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
    }

    private sealed class PrintIssueRaceHook(PayPlanner.Api.Data.PaymentContext db, Func<CancellationToken, Task> beforeCapture) : IDocumentSellerService
    {
        private readonly DocumentSellerService inner = new(db);
        public Task<DocumentImagePrintOptions?> ReadImagePrintDefaultsAsync(IDocumentSeller document, CancellationToken ct) => inner.ReadImagePrintDefaultsAsync(document, ct);
        public Task<string> ReadCreationPolicyAsync(CancellationToken ct) => inner.ReadCreationPolicyAsync(ct);
        public Task InitializeForCreationAsync(IDocumentSeller document, LegalEntity? company, CancellationToken ct) => inner.InitializeForCreationAsync(document, company, ct);
        public Task<string?> ValidateSelectionAsync(int? company, int? branch, CancellationToken ct) => inner.ValidateSelectionAsync(company, branch, ct);
        public async Task<LegalEntity?> ResolveAsync(IDocumentSeller document, LegalEntity? company, bool freeze, CancellationToken ct,
            Func<CancellationToken, Task<bool>>? canStillRead = null)
        {
            if (freeze) await beforeCapture(ct);
            return await inner.ResolveAsync(document, company, freeze, ct, canStillRead);
        }
        public Task UpdateSelectionAsync(IDocumentSeller document, int? company, int? branch, CancellationToken ct,
            Func<CancellationToken, Task<bool>> canStillEdit) => inner.UpdateSelectionAsync(document, company, branch, ct, canStillEdit);
    }
}
