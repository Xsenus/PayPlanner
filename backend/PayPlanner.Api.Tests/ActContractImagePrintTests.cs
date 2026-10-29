using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class ActContractImagePrintTests
{
    private const string Image = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    public static IEnumerable<object[]> Masks() => Enumerable.Range(0, 8).Select(mask => new object[] { mask });
    private static bool HasImage(string html, string kind) => Regex.IsMatch(html, $"<img\\b[^>]*\\bclass=\"{kind}\"", RegexOptions.CultureInvariant);
    private static void AssertImages(string html, bool logo, bool seal, bool signature)
    {
        Assert.Equal(logo, HasImage(html, "logo"));
        Assert.Equal(seal, HasImage(html, "seal"));
        Assert.Equal(signature, HasImage(html, "signature"));
    }

    private sealed record Fixture(PaymentContext Db, User User, LegalEntity Company, Act Act, Contract Contract) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var db = await SqliteTestContextFactory.CreateAsync();
        var role = new Role { Name = "image-print-" + Guid.NewGuid().ToString("N") };
        foreach (var section in new[] { RolePermissionCatalog.Acts, RolePermissionCatalog.Contracts })
            role.Permissions.Add(new RolePermission { Section = section, CanView = true, CanExport = true, ViewScope = DataVisibilityScope.All });
        var user = new User { Email = "image@example.test", PasswordHash = "test", FullName = "Синтетический пользователь", IsActive = true, IsApproved = true, Role = role };
        var client = new Client { Name = "Синтетический клиент", ResponsibleUser = user };
        var company = new LegalEntity { ShortName = "Синтетический продавец", LogoDataUrl = Image, SealDataUrl = Image, DirectorSignatureDataUrl = Image };
        var date = new DateTime(2026, 10, 29);
        var invoice = new Payment { Account = "CMP-03", Type = PaymentType.Income, Date = date, Amount = 100m, Client = client };
        var act = new Act { Number = "CMP-03", Date = date, Amount = 100m, Client = client, Invoice = invoice, Responsible = user };
        act.LineItems.Add(new ActLineItem { Name = "Синтетическая услуга", Quantity = 1m, Price = 100m, Unit = "усл." });
        var contract = new Contract { Number = "CMP-03", Date = date, Amount = 100m, BillingMode = ContractBillingMode.Fixed };
        contract.ClientContracts.Add(new ClientContract { Client = client });
        db.AddRange(user, company, act, contract);
        await db.SaveChangesAsync();
        db.SystemSettings.AddRange(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" },
            new SystemSetting { Key = "legalEntities.defaultMainCompanyId", Value = company.Id.ToString() },
            new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        await db.SaveChangesAsync();
        return new(db, user, company, act, contract);
    }

    private static TestServer Server(Fixture fixture, PdfRecorder? pdf = null, PayPlanner.Api.Services.Companies.IDocumentSellerService? documentSeller = null) => new(new WebHostBuilder().ConfigureServices(services =>
    {
        services.AddSingleton(fixture.Db);
        services.AddSingleton<IEmailService>(new NoEmail());
        services.AddSingleton<IFormRequirementsService>(new NoRequirements());
        if (pdf is not null) services.AddSingleton<IInvoiceDocumentPdfService>(pdf);
        if (documentSeller is not null) services.AddSingleton(documentSeller);
        services.AddAuthentication("print-test").AddScheme<AuthenticationSchemeOptions, PrintAuthentication>("print-test", _ => { });
        services.AddAuthorization();
        services.AddControllers().AddApplicationPart(typeof(ActsController).Assembly);
    }).Configure(app =>
    {
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => endpoints.MapControllers());
    }));

    private static string Query(int mask) => $"includeLogo={((mask & 1) != 0).ToString().ToLowerInvariant()}&includeSeal={((mask & 2) != 0).ToString().ToLowerInvariant()}&includeSignature={((mask & 4) != 0).ToString().ToLowerInvariant()}";
    private static IEnumerable<(string Section, int Id)> Documents(Fixture fixture) => [("acts", fixture.Act.Id), ("contracts", fixture.Contract.Id)];

    [Theory]
    [MemberData(nameof(Masks))]
    public async Task Http_BindsEveryImageCombinationAndKeepsFinancialData(int mask)
    {
        await using var fixture = await CreateFixtureAsync();
        using var server = Server(fixture);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in Documents(fixture))
        {
            using var response = await client.GetAsync($"/api/{section}/{id}/document?format=html&includeSignatureAndSeal=true&{Query(mask)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertImages(await response.Content.ReadAsStringAsync(), (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        }
        fixture.Act.Invoice!.Type = PaymentType.Expense;
        await fixture.Db.SaveChangesAsync();
        using var expense = await client.GetAsync($"/api/acts/{fixture.Act.Id}/document?format=html&{Query(mask)}");
        Assert.Equal(HttpStatusCode.OK, expense.StatusCode);
        AssertImages(await expense.Content.ReadAsStringAsync(), false, false, false);
        var savedAct = await fixture.Db.Acts.AsNoTracking().SingleAsync();
        var savedContract = await fixture.Db.Contracts.AsNoTracking().SingleAsync();
        Assert.Equal(100m, savedAct.Amount);
        Assert.Equal("CMP-03", savedAct.Number);
        Assert.Equal(fixture.Act.InvoiceId, savedAct.InvoiceId);
        Assert.Equal(100m, savedContract.Amount);
        Assert.Equal("CMP-03", savedContract.Number);
        Assert.Null(savedAct.SellerSnapshotJson);
        Assert.Null(savedContract.SellerSnapshotJson);
        Assert.Equal(1, await fixture.Db.ClientContracts.CountAsync());
    }

    [Fact]
    public async Task Http_RejectsInvalidFlagsAndRechecksExportRevocationAndAuthentication()
    {
        await using var fixture = await CreateFixtureAsync();
        using var server = Server(fixture);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in Documents(fixture))
        {
            foreach (var flag in new[] { "includeLogo", "includeSeal", "includeSignature" })
            {
                using var invalid = await client.GetAsync($"/api/{section}/{id}/document?format=html&{flag}=invalid");
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            }
            using var missing = await client.GetAsync($"/api/{section}/2147483647/document?format=html&{Query(7)}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        foreach (var permission in fixture.User.Role!.Permissions) permission.CanExport = false;
        await fixture.Db.SaveChangesAsync();
        foreach (var (section, id) in Documents(fixture))
        {
            using var denied = await client.GetAsync($"/api/{section}/{id}/document?format=html&{Query(7)}");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        client.DefaultRequestHeaders.Remove("X-Print-User");
        foreach (var (section, id) in Documents(fixture))
        {
            using var anonymous = await client.GetAsync($"/api/{section}/{id}/document?format=html&{Query(7)}");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
    }

    private static Task<IActionResult> Render(Fixture fixture, string section, string format = "html", bool? logo = null,
        bool? seal = null, bool? signature = null, bool? combined = null, PdfRecorder? pdf = null, CancellationToken ct = default)
    {
        var context = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, fixture.User.Id.ToString())], "print-test"))
            }
        };
        if (section == "acts")
            return new ActsController(fixture.Db, new NoRequirements(), pdf, new NoEmail()) { ControllerContext = context }
                .GetDocument(fixture.Act.Id, format, combined, ct, logo, seal, signature);
        return new ContractsController(fixture.Db, new NoRequirements(), pdf, new NoEmail()) { ControllerContext = context }
            .GetDocument(fixture.Contract.Id, format, null, ct, logo, seal, signature);
    }

    [Fact]
    public async Task ExplicitFlags_DoNotExposeDocumentsOutsideOwnVisibilityOrWithoutView()
    {
        await using var fixture = await CreateFixtureAsync();
        foreach (var permission in fixture.User.Role!.Permissions) permission.ViewScope = DataVisibilityScope.Own;
        fixture.Act.Responsible = null;
        fixture.Act.ResponsibleId = null;
        fixture.Act.Client!.ResponsibleUserId = null;
        await fixture.Db.SaveChangesAsync();
        foreach (var (section, _) in Documents(fixture)) Assert.IsType<NotFoundResult>(await Render(fixture, section, logo: true, seal: true, signature: true));
        foreach (var permission in fixture.User.Role.Permissions) permission.CanView = false;
        await fixture.Db.SaveChangesAsync();
        foreach (var (section, _) in Documents(fixture))
        {
            var denied = await Render(fixture, section, logo: true, seal: true, signature: true);
            Assert.True(denied is ForbidResult or NotFoundResult);
        }
    }

    [Fact]
    public async Task LegacyCallsAndPartialOverrides_KeepPreviousDefaults()
    {
        await using var fixture = await CreateFixtureAsync();
        AssertImages(Assert.IsType<ContentResult>(await Render(fixture, "acts", combined: true)).Content!, false, true, true);
        AssertImages(Assert.IsType<ContentResult>(await Render(fixture, "contracts")).Content!, false, false, false);
        AssertImages(Assert.IsType<ContentResult>(await Render(fixture, "acts", combined: true, seal: false)).Content!, false, false, true);
        AssertImages(Assert.IsType<ContentResult>(await Render(fixture, "acts", combined: false, logo: true)).Content!, true, false, false);
        AssertImages(Assert.IsType<ContentResult>(await Render(fixture, "contracts", seal: true)).Content!, false, true, false);
    }

    [Theory]
    [InlineData("acts")]
    [InlineData("contracts")]
    public async Task WordAndPdf_UseSelectedFrozenImagesAndPassCancellation(string section)
    {
        await using var fixture = await CreateFixtureAsync();
        var word = Assert.IsType<FileContentResult>(await Render(fixture, section, "word", logo: false, seal: true, signature: false));
        Assert.Equal("application/msword", word.ContentType);
        AssertImages(Encoding.UTF8.GetString(word.FileContents), false, true, false);
        fixture.Company.LogoDataUrl = fixture.Company.SealDataUrl = fixture.Company.DirectorSignatureDataUrl = "data:image/png;base64,AAAA";
        await fixture.Db.SaveChangesAsync();
        var pdf = new PdfRecorder();
        using var cancellation = new CancellationTokenSource();
        var file = Assert.IsType<FileContentResult>(await Render(fixture, section, "pdf", true, false, true, pdf: pdf, ct: cancellation.Token));
        Assert.Equal("application/pdf", file.ContentType);
        AssertImages(pdf.Html, true, false, true);
        Assert.Contains(Image, pdf.Html);
        Assert.DoesNotContain("data:image/png;base64,AAAA", pdf.Html);
        Assert.Equal(cancellation.Token, pdf.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Render(fixture, section, logo: true, ct: cancellation.Token));
    }

    [Theory]
    [InlineData("acts")]
    [InlineData("contracts")]
    public async Task PdfFailure_KeepsExistingErrorContract(string section)
    {
        await using var fixture = await CreateFixtureAsync();
        var result = Assert.IsAssignableFrom<ObjectResult>(await Render(fixture, section, "pdf", true, true, true, pdf: new PdfRecorder { Fail = true }));
        Assert.Equal(section == "acts" ? 503 : 400, result.StatusCode);
    }

    private sealed class PdfRecorder : IInvoiceDocumentPdfService
    {
        public string Html { get; private set; } = string.Empty;
        public CancellationToken Token { get; private set; }
        public bool Fail { get; init; }
        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("Синтетический отказ PDF");
            Html = html;
            Token = ct;
            return Task.FromResult(new byte[] { 1, 2, 3 });
        }
    }
    private sealed class NoRequirements : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
    private sealed class NoEmail : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }
    private sealed class PrintAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Print-User"], out var userId)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
