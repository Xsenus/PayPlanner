using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Documents;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class ActContractImagePrintTests
{
    [Theory]
    [MemberData(nameof(Masks))]
    public void DefaultsResolution_IndependentOverridesAndLegacyChoicesHaveExplicitPriority(int mask)
    {
        var flags = new DocumentImagePrintOptions((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        Assert.Equal(flags, DocumentImagePrintOptions.WithDefaults(flags, true, true, null, null, null, null));
        Assert.Equal(new(false, flags.IncludeSeal, flags.IncludeSignature), DocumentImagePrintOptions.WithDefaults(flags, true, true, null, false, null, null));
        Assert.Null(DocumentImagePrintOptions.WithDefaults(flags, true, true, false, null, null, null));
        Assert.Equal(new(true, false, true), DocumentImagePrintOptions.WithDefaults(flags, true, true, false, null, null, true));
        Assert.Null(DocumentImagePrintOptions.WithDefaults(null, true, true, null, null, null, null));
    }

    private static async Task<Fixture> DefaultsFixtureAsync()
    {
        var fixture = await CreateFixtureAsync();
        fixture.User.Role!.Permissions.Add(new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, CanExport = true, ViewScope = DataVisibilityScope.All });
        fixture.Act.Invoice!.DocumentKind = DocumentKind.Invoice;
        fixture.Act.Invoice.Invoice = new Invoice { Number = "DEFAULTS", Date = fixture.Act.Invoice.Date };
        await fixture.Db.SaveChangesAsync();
        return fixture;
    }

    private static IEnumerable<(string Section, int Id)> DefaultsDocuments(Fixture fixture)
        => Documents(fixture).Append(("invoices", fixture.Act.Invoice!.Id));

    private static async Task SetDefaultsAsync(Fixture fixture, int mask)
    {
        var flags = new DocumentImagePrintOptions((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        await new CompanyDocumentImagePrintDefaultsService(fixture.Db).StageAsync(fixture.Company.Id, new(flags, flags, flags), true);
        await fixture.Db.SaveChangesAsync();
    }

    [Theory]
    [MemberData(nameof(Masks))]
    public async Task DefaultsHttp_ReadsAllFlagsAndRendersWithoutFreezingOrProfilePermission(int mask)
    {
        await using var fixture = await DefaultsFixtureAsync();
        await SetDefaultsAsync(fixture, mask);
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture))
        {
            using var response = await client.GetAsync($"/api/{section}/{id}/image-print-defaults");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("\"includeLogo\"", await response.Content.ReadAsStringAsync());
            Assert.Equal(new DocumentImagePrintOptions((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0), await response.Content.ReadFromJsonAsync<DocumentImagePrintOptions>());
            using var html = await client.GetAsync($"/api/{section}/{id}/document?format=html");
            Assert.Equal(HttpStatusCode.OK, html.StatusCode);
            AssertImages(await html.Content.ReadAsStringAsync(), (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        }
        Assert.Null(await fixture.Db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Null(await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Null(await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
        Assert.Empty(await fixture.Db.MoneyOperations.ToArrayAsync());
        Assert.Empty(await fixture.Db.TaskJournalItems.ToArrayAsync());
    }

    [Fact]
    public async Task DefaultsHttp_ExplicitCopyFlagsAndLegacyCombinedChoiceStillWin()
    {
        await using var fixture = await DefaultsFixtureAsync();
        await SetDefaultsAsync(fixture, 5);
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture))
        {
            using var response = await client.GetAsync($"/api/{section}/{id}/document?format=html&{Query(2)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertImages(await response.Content.ReadAsStringAsync(), false, true, false);
            if (section == "contracts") continue;
            using var without = await client.GetAsync($"/api/{section}/{id}/document?format=html&includeSignatureAndSeal=false");
            Assert.Equal(HttpStatusCode.OK, without.StatusCode);
            AssertImages(await without.Content.ReadAsStringAsync(), section == "invoices", false, false);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultsHttp_FrozenOrLegacySnapshotsNeverReadTheChangedCompany(bool legacy)
    {
        await using var fixture = await DefaultsFixtureAsync();
        var flags = new DocumentImagePrintOptions(false, true, false);
        var json = SellerLegalEntityComposer.ToSnapshotJson(fixture.Company, imagePrintDefaults: legacy ? null : new(flags, flags, flags), imagePrintDefaultsFrozen: !legacy);
        fixture.Act.SellerSnapshotJson = fixture.Contract.SellerSnapshotJson = fixture.Act.Invoice!.SellerSnapshotJson = json;
        await fixture.Db.SaveChangesAsync();
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        var original = new Dictionary<string, string>();
        foreach (var (section, id) in DefaultsDocuments(fixture)) original[section] = await client.GetStringAsync($"/api/{section}/{id}/image-print-defaults");
        fixture.Db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(fixture.Company.Id), Value = "broken" });
        await fixture.Db.SaveChangesAsync();
        foreach (var (section, id) in DefaultsDocuments(fixture))
        {
            Assert.Equal(original[section], await client.GetStringAsync($"/api/{section}/{id}/image-print-defaults"));
            using var html = await client.GetAsync($"/api/{section}/{id}/document?format=html");
            Assert.Equal(HttpStatusCode.OK, html.StatusCode);
            if (!legacy) AssertImages(await html.Content.ReadAsStringAsync(), false, true, false);
        }
        Assert.Equal(json, await fixture.Db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(json, await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(json, await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
    }

    [Fact]
    public async Task DefaultsHttp_RequiresExportAndViewAndCurrentVisibilityBeforeReadingSettings()
    {
        await using var fixture = await DefaultsFixtureAsync();
        fixture.Db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(fixture.Company.Id), Value = "broken" });
        await fixture.Db.SaveChangesAsync();
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var permission in fixture.User.Role!.Permissions) permission.CanExport = false;
        await fixture.Db.SaveChangesAsync();
        foreach (var (section, id) in DefaultsDocuments(fixture))
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/{section}/{id}/image-print-defaults")).StatusCode);
        foreach (var permission in fixture.User.Role.Permissions) { permission.CanExport = true; permission.CanView = false; }
        await fixture.Db.SaveChangesAsync();
        foreach (var (section, id) in DefaultsDocuments(fixture))
            Assert.Contains((await client.GetAsync($"/api/{section}/{id}/image-print-defaults")).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        foreach (var permission in fixture.User.Role.Permissions) { permission.CanView = true; permission.ViewScope = DataVisibilityScope.Own; }
        fixture.Act.Responsible = null; fixture.Act.ResponsibleId = null;
        fixture.Act.Client!.ResponsibleUserId = null; fixture.Act.Client.ResponsibleUser = null;
        await fixture.Db.SaveChangesAsync();
        foreach (var (section, id) in DefaultsDocuments(fixture))
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/{section}/{id}/image-print-defaults")).StatusCode);
        client.DefaultRequestHeaders.Clear();
        foreach (var (section, id) in DefaultsDocuments(fixture))
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/{section}/{id}/image-print-defaults")).StatusCode);
    }

    [Fact]
    public async Task DefaultsHttp_DamagedLiveValueFailsWithoutWritesAndMissingDocumentStaysHidden()
    {
        await using var fixture = await DefaultsFixtureAsync();
        fixture.Db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(fixture.Company.Id), Value = "broken" });
        await fixture.Db.SaveChangesAsync();
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture))
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/{section}/{id}/image-print-defaults")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/{section}/2147483647/image-print-defaults")).StatusCode);
        }
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
        Assert.Null(await fixture.Db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
    }

    [Fact]
    public async Task DefaultsHttp_ExpenseSidesDoNotUseOurCompanyImagePreferences()
    {
        await using var fixture = await DefaultsFixtureAsync();
        fixture.Act.Invoice!.Type = PaymentType.Expense;
        fixture.Db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(fixture.Company.Id), Value = "broken" });
        await fixture.Db.SaveChangesAsync();
        using var server = Server(fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        Assert.Equal(new DocumentImagePrintOptions(false, false, false), await client.GetFromJsonAsync<DocumentImagePrintOptions>($"/api/acts/{fixture.Act.Id}/image-print-defaults"));
        var invoice = await client.GetFromJsonAsync<DocumentImagePrintOptions>($"/api/invoices/{fixture.Act.Invoice.Id}/image-print-defaults");
        Assert.True(invoice!.IncludeLogo);
        foreach (var section in new[] { "acts", "invoices" })
        {
            var id = section == "acts" ? fixture.Act.Id : fixture.Act.Invoice.Id;
            var html = await client.GetStringAsync($"/api/{section}/{id}/document?format=html");
            Assert.DoesNotContain(Image, html);
        }
    }

    [Fact]
    public async Task DefaultsHttp_FileIssueFreezesDefaultsAndLaterProfileChangesDoNotAffectTheCopy()
    {
        await using var fixture = await DefaultsFixtureAsync();
        await SetDefaultsAsync(fixture, 5);
        var pdf = new PdfRecorder();
        using var server = Server(fixture, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture))
        {
            using var file = await client.GetAsync($"/api/{section}/{id}/document?format=pdf");
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            AssertImages(pdf.Html, true, false, true);
            Assert.Contains(Image, pdf.Html);
        }
        var act = await fixture.Db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync();
        var contract = await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync();
        var invoice = await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync();
        foreach (var json in new[] { act, contract, invoice })
            Assert.Equal(new DocumentImagePrintOptions(true, false, true), SellerLegalEntityComposer.ReadMetadata(json)!.ImagePrintDefaults!.Invoice);
        fixture.Company.LogoDataUrl = fixture.Company.SealDataUrl = fixture.Company.DirectorSignatureDataUrl = "data:image/png;base64,AAAA";
        await SetDefaultsAsync(fixture, 2);
        foreach (var (section, id) in DefaultsDocuments(fixture))
        {
            Assert.Equal(new DocumentImagePrintOptions(true, false, true), await client.GetFromJsonAsync<DocumentImagePrintOptions>($"/api/{section}/{id}/image-print-defaults"));
            using var file = await client.GetAsync($"/api/{section}/{id}/document?format=pdf");
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            AssertImages(pdf.Html, true, false, true);
            Assert.Contains(Image, pdf.Html);
        }
        Assert.Equal(act, await fixture.Db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(contract, await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(invoice, await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
    }

    [Theory]
    [InlineData("invoices", false)]
    [InlineData("acts", false)]
    [InlineData("contracts", false)]
    [InlineData("invoices", true)]
    [InlineData("acts", true)]
    [InlineData("contracts", true)]
    public async Task DefaultsHttp_RevocationDuringReadPreventsPublishingTheOptions(string section, bool visibility)
    {
        await using var fixture = await DefaultsFixtureAsync();
        var calls = 0;
        var hooked = new DefaultsReadHook(async _ =>
        {
            ++calls;
            foreach (var permission in fixture.User.Role!.Permissions)
                if (visibility) permission.ViewScope = DataVisibilityScope.Own; else permission.CanExport = false;
            fixture.Act.Responsible = null; fixture.Act.ResponsibleId = null;
            fixture.Act.Client!.ResponsibleUser = null; fixture.Act.Client.ResponsibleUserId = null;
            await fixture.Db.SaveChangesAsync();
            return new(true, true, true);
        });
        var context = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, fixture.User.Id.ToString())], "print-test")) } };
        Task<ActionResult<DocumentImagePrintOptions>> pending = section switch
        {
            "invoices" => new InvoicesController(fixture.Db, new NoEmail(), new NoRequirements(), documentSellerService: hooked)
            { ControllerContext = context }.GetImagePrintDefaults(fixture.Act.Invoice!.Id, default),
            "acts" => new ActsController(fixture.Db, new NoRequirements(), documentSellerService: hooked)
            { ControllerContext = context }.GetImagePrintDefaults(fixture.Act.Id, default),
            _ => new ContractsController(fixture.Db, new NoRequirements(), documentSellerService: hooked)
            { ControllerContext = context }.GetImagePrintDefaults(fixture.Contract.Id, default),
        };
        Assert.IsType<ForbidResult>((await pending).Result);
        Assert.Equal(1, calls);
    }

    private sealed class DefaultsReadHook(Func<CancellationToken, Task<DocumentImagePrintOptions?>> read) : IDocumentSellerService
    {
        public Task<DocumentImagePrintOptions?> ReadImagePrintDefaultsAsync(IDocumentSeller document, CancellationToken ct) => read(ct);
        public Task<string> ReadCreationPolicyAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task InitializeForCreationAsync(IDocumentSeller document, LegalEntity? company, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ValidateSelectionAsync(int? company, int? branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<LegalEntity?> ResolveAsync(IDocumentSeller document, LegalEntity? company, bool freeze, CancellationToken ct,
            Func<CancellationToken, Task<bool>>? canStillRead = null) => throw new NotSupportedException();
        public Task UpdateSelectionAsync(IDocumentSeller document, int? company, int? branch, CancellationToken ct,
            Func<CancellationToken, Task<bool>> canStillEdit) => throw new NotSupportedException();
    }
}
