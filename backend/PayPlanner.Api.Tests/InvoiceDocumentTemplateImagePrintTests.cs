using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
using PayPlanner.Api.Services.Documents;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class InvoiceDocumentTemplateTests
{
    private const string PrintImage = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private static bool HasPrintedImage(string html, string kind) => Regex.IsMatch(html, $"<img\\b[^>]*\\bclass=\"{kind}\"", RegexOptions.CultureInvariant);

    private static async Task<(int Id, User User)> CreateImagePrintFixtureAsync(PaymentContext db)
    {
        var id = await CreateInvoiceDocumentFixtureAsync(db);
        await CreateMainCompanyAsync(db);
        var company = await db.LegalEntities.SingleAsync();
        company.ShortName = company.FullName = "Синтетический продавец";
        company.Inn = company.Address = company.DocumentDirectorJson = company.ChiefAccountantJson = null;
        company.BankAccountsJson = company.TreasuryCorrAccountNumber = null;
        company.LogoDataUrl = company.SealDataUrl = company.DirectorSignatureDataUrl = PrintImage;
        await db.SaveChangesAsync();
        return (id, await CreateAccountsUserAsync(db, "image-print", canExport: true));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task GetDocument_ImagePrintHonorsIndependentFlagsForAllInvoiceTemplates(int mask)
    {
        await using var db = await CreateContextAsync();
        var fixture = await CreateImagePrintFixtureAsync(db);
        var controller = CreateController(db, false, userId: fixture.User.Id);
        var before = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == fixture.Id);
        foreach (var template in new[] { InvoiceDocumentTemplateEngine.DefaultTemplateKey, InvoiceDocumentTemplateEngine.OfferTemplateKey, InvoiceDocumentTemplateEngine.OrderTemplateKey })
        {
            var content = Assert.IsType<ContentResult>(await controller.GetDocument(fixture.Id, "html", template, true, CancellationToken.None,
                includeLogo: (mask & 1) != 0, includeSeal: (mask & 2) != 0, includeSignature: (mask & 4) != 0));
            Assert.Equal((mask & 1) != 0 && template != InvoiceDocumentTemplateEngine.OrderTemplateKey, HasPrintedImage(content.Content!, "logo"));
            Assert.Equal((mask & 2) != 0, HasPrintedImage(content.Content!, "seal"));
            Assert.Equal((mask & 4) != 0, HasPrintedImage(content.Content!, "signature"));
        }
        var after = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == fixture.Id);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.InvoiceId, after.InvoiceId);
        Assert.Equal(before.SellerSnapshotJson, after.SellerSnapshotJson);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GetDocument_ImagePrintPartialOverridesPreserveUnspecifiedLegacyDefaults(bool combined)
    {
        Assert.Null(DocumentImagePrintOptions.FromOverrides(true, combined, null, null, null));
        Assert.Equal(new DocumentImagePrintOptions(false, combined, combined), DocumentImagePrintOptions.FromOverrides(true, combined, false, null, null));
        Assert.Equal(new DocumentImagePrintOptions(true, false, combined), DocumentImagePrintOptions.FromOverrides(true, combined, null, false, null));
        Assert.Equal(new DocumentImagePrintOptions(true, combined, false), DocumentImagePrintOptions.FromOverrides(true, combined, null, null, false));
    }

    [Fact]
    public async Task GetDocument_ImagePrintDoesNotBypassExportPermissionOrInvoiceVisibility()
    {
        await using var db = await CreateContextAsync();
        var fixture = await CreateImagePrintFixtureAsync(db);
        var denied = await CreateAccountsUserAsync(db, "image-denied", canExport: false);
        var forbidden = CreateController(db, false, userId: denied.Id);
        Assert.IsType<ForbidResult>(await forbidden.GetDocument(fixture.Id, "html", ct: CancellationToken.None, includeLogo: true, includeSeal: true, includeSignature: true));
        var limited = await CreateAccountsUserAsync(db, "image-own", canExport: true, viewScope: DataVisibilityScope.Own);
        var payment = await db.Payments.Include(item => item.Client).SingleAsync(item => item.Id == fixture.Id);
        payment.Client!.ResponsibleUserId = fixture.User.Id;
        await db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await CreateController(db, false, userId: limited.Id).GetDocument(fixture.Id, "html", ct: CancellationToken.None, includeLogo: true, includeSeal: true, includeSignature: true));
    }

    [Fact]
    public async Task GetDocument_ImagePrintPdfUsesChosenImagesAndCancellation()
    {
        await using var db = await CreateContextAsync();
        var fixture = await CreateImagePrintFixtureAsync(db);
        var pdf = new ImagePrintPdfRecorder();
        var controller = CreateController(db, false, pdfService: pdf, userId: fixture.User.Id);
        var file = Assert.IsType<FileContentResult>(await controller.GetDocument(fixture.Id, "pdf", ct: CancellationToken.None, includeLogo: false, includeSeal: true, includeSignature: false));
        Assert.Equal(new byte[] { 1, 2, 3 }, file.FileContents);
        Assert.Contains("class=\"seal\"", pdf.Html);
        Assert.DoesNotContain("class=\"logo\"", pdf.Html);
        Assert.DoesNotContain("class=\"signature\"", pdf.Html);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.GetDocument(fixture.Id, "html", ct: cancelled.Token, includeLogo: false, includeSeal: false, includeSignature: false));
    }

    [Fact]
    public async Task GetDocument_ImagePrintHttpBindsFalseFlagsAndRechecksRevokedAccess()
    {
        await using var db = await CreateContextAsync();
        var fixture = await CreateImagePrintFixtureAsync(db);
        using var server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(db);
            services.AddSingleton<IEmailService>(new NoopEmailService());
            services.AddSingleton<IFormRequirementsService>(new NoopFormRequirementsService());
            services.AddAuthentication("image-test").AddScheme<AuthenticationSchemeOptions, ImagePrintAuthenticationHandler>("image-test", _ => { });
            services.AddAuthorization();
            services.AddControllers().AddApplicationPart(typeof(InvoicesController).Assembly);
        }).Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Image-Test-User", fixture.User.Id.ToString());
        for (var mask = 0; mask < 8; mask++)
        {
            using var response = await client.GetAsync($"/api/invoices/{fixture.Id}/document?format=html&includeSignatureAndSeal=true&includeLogo={((mask & 1) != 0).ToString().ToLowerInvariant()}&includeSeal={((mask & 2) != 0).ToString().ToLowerInvariant()}&includeSignature={((mask & 4) != 0).ToString().ToLowerInvariant()}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Equal((mask & 1) != 0, HasPrintedImage(html, "logo"));
            Assert.Equal((mask & 2) != 0, HasPrintedImage(html, "seal"));
            Assert.Equal((mask & 4) != 0, HasPrintedImage(html, "signature"));
        }
        using var invalid = await client.GetAsync($"/api/invoices/{fixture.Id}/document?format=html&includeLogo=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var permission = await db.RolePermissions.SingleAsync(item => item.RoleId == fixture.User.RoleId && item.Section == RolePermissionCatalog.Accounts);
        permission.CanExport = false; await db.SaveChangesAsync();
        using var revoked = await client.GetAsync($"/api/invoices/{fixture.Id}/document?format=html&includeLogo=true&includeSeal=true&includeSignature=true");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Image-Test-User");
        using var anonymous = await client.GetAsync($"/api/invoices/{fixture.Id}/document?format=html&includeLogo=true");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private sealed class ImagePrintPdfRecorder : IInvoiceDocumentPdfService
    {
        public string Html { get; private set; } = string.Empty;
        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default) { Html = html; return Task.FromResult(new byte[] { 1, 2, 3 }); }
    }

    private sealed class ImagePrintAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Image-Test-User"], out var userId)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
