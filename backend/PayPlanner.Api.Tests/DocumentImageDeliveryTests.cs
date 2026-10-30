using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Acts;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class ActContractImagePrintTests
{
    private static async Task<Fixture> DeliveryFixtureAsync()
    {
        var fixture = await DefaultsFixtureAsync();
        fixture.User.Role!.Permissions.Add(new RolePermission { Section = RolePermissionCatalog.YandexDisk, CanView = true, CanCreate = true, ViewScope = DataVisibilityScope.All });
        await fixture.Db.SaveChangesAsync();
        return fixture;
    }

    private static TestServer DeliveryServer(Fixture fixture, DeliveryMail mail, DeliveryDisk disk, DeliveryPdf pdf)
        => new(new WebHostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(fixture.Db);
            services.AddSingleton<IEmailService>(mail);
            services.AddSingleton<IYandexDiskService>(disk);
            services.AddSingleton<IInvoiceDocumentPdfService>(pdf);
            services.AddSingleton<IFormRequirementsService>(new NoRequirements());
            services.AddAuthentication("print-test").AddScheme<AuthenticationSchemeOptions, PrintAuthentication>("print-test", _ => { });
            services.AddAuthorization();
            services.AddControllers().AddApplicationPart(typeof(ActsController).Assembly);
        }).Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));

    private static Task<HttpResponseMessage> DeliverAsync(HttpClient client, string section, int id, bool disk, string format = "pdf", bool? combined = null)
        => disk ? client.PostAsync($"/api/{section}/{id}/yandex-disk?format={format}" + (combined.HasValue ? $"&includeSignatureAndSeal={combined.Value.ToString().ToLowerInvariant()}" : ""), null)
            : client.PostAsJsonAsync($"/api/{section}/{id}/send-email", new SendInvoiceEmailRequest { Email = "recipient@example.test", Format = format });

    [Theory]
    [MemberData(nameof(Masks))]
    public async Task Delivery_DefaultsReachAllSixAttachmentsAndKeepFinancialData(int mask)
    {
        await using var fixture = await DeliveryFixtureAsync(); await SetDefaultsAsync(fixture, mask);
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture)) foreach (var toDisk in new[] { false, true })
            {
                using var response = await DeliverAsync(client, section, id, toDisk);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var file = toDisk ? disk.Files.Last() : mail.Files.Last();
                Assert.Equal("application/pdf", file.ContentType); Assert.EndsWith(".pdf", file.Name);
                AssertImages(Encoding.UTF8.GetString(file.Bytes), (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
            }
        Assert.Equal(3, mail.Files.Count); Assert.Equal(3, disk.Files.Count);
        Assert.All(mail.Recipients, recipient => Assert.Equal("recipient@example.test", recipient));
        Assert.NotNull(await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.NotNull(await fixture.Db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.NotNull(await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(100m, await fixture.Db.Payments.AsNoTracking().Select(row => row.Amount).SingleAsync());
        Assert.Equal(100m, await fixture.Db.Acts.AsNoTracking().Select(row => row.Amount).SingleAsync());
        Assert.Equal(fixture.Act.InvoiceId, await fixture.Db.Acts.AsNoTracking().Select(row => row.InvoiceId).SingleAsync());
        Assert.Equal(100m, await fixture.Db.Contracts.AsNoTracking().Select(row => row.Amount).SingleAsync());
        Assert.Equal(1, await fixture.Db.ClientContracts.CountAsync());
        Assert.Empty(await fixture.Db.MoneyOperations.ToArrayAsync()); Assert.Empty(await fixture.Db.TaskJournalItems.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delivery_ExplicitOldDiskChoiceKeepsPriority(bool signed)
    {
        await using var fixture = await DeliveryFixtureAsync(); await SetDefaultsAsync(fixture, 5);
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture).Where(document => document.Section != "contracts"))
        {
            Assert.Equal(HttpStatusCode.OK, (await DeliverAsync(client, section, id, true, combined: signed)).StatusCode);
            AssertImages(Encoding.UTF8.GetString(disk.Files.Last().Bytes), section == "invoices", signed, signed);
        }
        Assert.Empty(mail.Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delivery_CapturedDefaultsSurviveLaterProfileChangesUnderBothPolicies(bool onCreate)
    {
        await using var fixture = await DeliveryFixtureAsync(); await SetDefaultsAsync(fixture, 5);
        if (onCreate)
        {
            fixture.Act.Invoice!.SellerSnapshotPolicy = fixture.Act.SellerSnapshotPolicy = fixture.Contract.SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.OnCreate;
            await fixture.Db.SaveChangesAsync();
            var seller = new DocumentSellerService(fixture.Db);
            foreach (var document in new IDocumentSeller[] { fixture.Act.Invoice, fixture.Act, fixture.Contract })
                await seller.ResolveAsync(document, fixture.Company, true, default);
        }
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture)) Assert.Equal(HttpStatusCode.OK, (await DeliverAsync(client, section, id, false)).StatusCode);
        var snapshots = await DeliverySnapshotsAsync(fixture);
        fixture.Company.ShortName = "Новый профиль"; fixture.Company.LogoDataUrl = fixture.Company.SealDataUrl = fixture.Company.DirectorSignatureDataUrl = null;
        await SetDefaultsAsync(fixture, 2);
        foreach (var (section, id) in DefaultsDocuments(fixture)) foreach (var toDisk in new[] { false, true })
            {
                Assert.Equal(HttpStatusCode.OK, (await DeliverAsync(client, section, id, toDisk)).StatusCode);
                var html = Encoding.UTF8.GetString(toDisk ? disk.Files.Last().Bytes : mail.Files.Last().Bytes);
                AssertImages(html, true, false, true); Assert.Contains(Image, html); Assert.DoesNotContain("Новый профиль", html);
            }
        Assert.Equal(snapshots, await DeliverySnapshotsAsync(fixture));
    }

    [Fact]
    public async Task Delivery_WordFilesContainSelectedImagesThroughBothChannels()
    {
        await using var fixture = await DeliveryFixtureAsync(); await SetDefaultsAsync(fixture, 5);
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture)) foreach (var toDisk in new[] { false, true })
            {
                Assert.Equal(HttpStatusCode.OK, (await DeliverAsync(client, section, id, toDisk, "word")).StatusCode);
                var file = toDisk ? disk.Files.Last() : mail.Files.Last();
                if (section == "invoices")
                {
                    Assert.EndsWith(".docx", file.Name);
                    using var archive = new ZipArchive(new MemoryStream(file.Bytes));
                    using var document = archive.GetEntry("word/document.xml")!.Open();
                    Assert.Equal(2, XDocument.Load(document).Descendants().Count(element => element.Name.LocalName == "drawing"));
                }
                else { Assert.EndsWith(".doc", file.Name); AssertImages(Encoding.UTF8.GetString(file.Bytes), true, false, true); }
            }
        Assert.Equal(0, pdf.Calls);
    }

    [Theory]
    [InlineData("no-export")]
    [InlineData("no-view")]
    [InlineData("hidden")]
    [InlineData("disk-create")]
    [InlineData("disk-view")]
    public async Task Delivery_InitialRightsAndVisibilityRefuseBeforeCapture(string denial)
    {
        await using var fixture = await DeliveryFixtureAsync();
        foreach (var permission in fixture.User.Role!.Permissions)
        {
            if (denial == "no-export") permission.CanExport = false;
            if (denial == "no-view") permission.CanView = false;
            if (denial == "hidden" && permission.Section != RolePermissionCatalog.YandexDisk) permission.ViewScope = DataVisibilityScope.Own;
            if (denial == "disk-create" && permission.Section == RolePermissionCatalog.YandexDisk) permission.CanCreate = false;
            if (denial == "disk-view" && permission.Section == RolePermissionCatalog.YandexDisk) permission.CanView = false;
        }
        if (denial == "hidden") { fixture.Act.Responsible = null; fixture.Act.ResponsibleId = null; fixture.Act.Client!.ResponsibleUser = null; fixture.Act.Client.ResponsibleUserId = null; }
        await fixture.Db.SaveChangesAsync();
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture)) foreach (var toDisk in denial.StartsWith("disk-", StringComparison.Ordinal) ? new[] { true } : new[] { false, true })
                Assert.Equal(denial == "hidden" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, (await DeliverAsync(client, section, id, toDisk)).StatusCode);
        Assert.Empty(mail.Files); Assert.Empty(disk.Files); Assert.Equal(0, pdf.Calls);
        Assert.All(await DeliverySnapshotsAsync(fixture), snapshot => Assert.Null(snapshot));
    }

    [Theory]
    [InlineData("invoices", false, false)]
    [InlineData("acts", false, false)]
    [InlineData("contracts", false, false)]
    [InlineData("invoices", true, false)]
    [InlineData("acts", true, false)]
    [InlineData("contracts", true, false)]
    [InlineData("invoices", false, true)]
    [InlineData("acts", false, true)]
    [InlineData("contracts", false, true)]
    [InlineData("invoices", true, true)]
    [InlineData("acts", true, true)]
    [InlineData("contracts", true, true)]
    public async Task Delivery_LateExportOrVisibilityRevocationStopsTheReadyFile(string section, bool toDisk, bool visibility)
    {
        await using var fixture = await DeliveryFixtureAsync(); await SetDefaultsAsync(fixture, 5);
        var mail = new DeliveryMail(); var disk = new DeliveryDisk();
        var pdf = new DeliveryPdf
        {
            AfterRender = async ct =>
        {
            foreach (var permission in fixture.User.Role!.Permissions.Where(permission => permission.Section != RolePermissionCatalog.YandexDisk))
                if (visibility) permission.ViewScope = DataVisibilityScope.Own; else permission.CanExport = false;
            fixture.Act.Responsible = null; fixture.Act.ResponsibleId = null; fixture.Act.Client!.ResponsibleUser = null; fixture.Act.Client.ResponsibleUserId = null;
            await fixture.Db.SaveChangesAsync(ct);
        }
        };
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        var id = DefaultsDocuments(fixture).Single(document => document.Section == section).Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await DeliverAsync(client, section, id, toDisk)).StatusCode);
        Assert.Equal(1, pdf.Calls); Assert.Empty(mail.Files); Assert.Empty(disk.Files);
    }

    [Theory]
    [InlineData("invoices", false)]
    [InlineData("acts", false)]
    [InlineData("contracts", false)]
    [InlineData("invoices", true)]
    [InlineData("acts", true)]
    [InlineData("contracts", true)]
    public async Task Delivery_LateDiskPermissionRevocationStopsUpload(string section, bool view)
    {
        await using var fixture = await DeliveryFixtureAsync();
        var mail = new DeliveryMail(); var disk = new DeliveryDisk();
        var pdf = new DeliveryPdf
        {
            AfterRender = async ct =>
        {
            var permission = fixture.User.Role!.Permissions.Single(permission => permission.Section == RolePermissionCatalog.YandexDisk);
            if (view) permission.CanView = false; else permission.CanCreate = false;
            await fixture.Db.SaveChangesAsync(ct);
        }
        };
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        var id = DefaultsDocuments(fixture).Single(document => document.Section == section).Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await DeliverAsync(client, section, id, true)).StatusCode);
        Assert.Empty(disk.Files); Assert.Empty(mail.Files);
    }

    [Fact]
    public async Task Delivery_InvalidRecipientMissingDocumentAndAnonymousNeverSend()
    {
        await using var fixture = await DeliveryFixtureAsync();
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture))
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/{section}/{id}/send-email", new SendInvoiceEmailRequest { Email = "invalid" })).StatusCode);
            foreach (var toDisk in new[] { false, true }) Assert.Equal(HttpStatusCode.NotFound, (await DeliverAsync(client, section, int.MaxValue, toDisk)).StatusCode);
        }
        client.DefaultRequestHeaders.Clear();
        foreach (var (section, id) in DefaultsDocuments(fixture)) foreach (var toDisk in new[] { false, true }) Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverAsync(client, section, id, toDisk)).StatusCode);
        Assert.Empty(mail.Files); Assert.Empty(disk.Files); Assert.Equal(0, pdf.Calls);
    }

    [Fact]
    public async Task Delivery_DamagedDefaultsFailClosedBeforeExternalPreparation()
    {
        await using var fixture = await DeliveryFixtureAsync();
        fixture.Db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(fixture.Company.Id), Value = "broken" }); await fixture.Db.SaveChangesAsync();
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture)) foreach (var toDisk in new[] { false, true }) Assert.Equal(HttpStatusCode.BadRequest, (await DeliverAsync(client, section, id, toDisk)).StatusCode);
        Assert.Empty(mail.Files); Assert.Empty(disk.Files); Assert.Equal(0, pdf.Calls);
        Assert.All(await DeliverySnapshotsAsync(fixture), snapshot => Assert.Null(snapshot));
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
    }

    [Fact]
    public async Task Delivery_ExpenseInvoiceAndActDoNotAcquireOurImagesOrUnusedDefaults()
    {
        await using var fixture = await DeliveryFixtureAsync(); fixture.Act.Invoice!.Type = PaymentType.Expense;
        fixture.Db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(fixture.Company.Id), Value = "broken" }); await fixture.Db.SaveChangesAsync();
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture).Where(document => document.Section != "contracts")) foreach (var toDisk in new[] { false, true })
            {
                Assert.Equal(HttpStatusCode.OK, (await DeliverAsync(client, section, id, toDisk)).StatusCode);
                Assert.DoesNotContain(Image, Encoding.UTF8.GetString(toDisk ? disk.Files.Last().Bytes : mail.Files.Last().Bytes));
            }
        Assert.Null(await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(100m, await fixture.Db.Acts.AsNoTracking().Select(row => row.Amount).SingleAsync());
    }

    [Fact]
    public async Task Delivery_LegacySnapshotsNeverReceiveLiveDefaults()
    {
        await using var fixture = await DeliveryFixtureAsync(); await SetDefaultsAsync(fixture, 7);
        const string legacy = "{\"ShortName\":\"Legacy\",\"ImagesFrozen\":true}";
        fixture.Act.Invoice!.SellerSnapshotJson = fixture.Act.SellerSnapshotJson = fixture.Contract.SellerSnapshotJson = legacy; await fixture.Db.SaveChangesAsync();
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture)) foreach (var toDisk in new[] { false, true })
            {
                Assert.Equal(HttpStatusCode.OK, (await DeliverAsync(client, section, id, toDisk)).StatusCode);
                var html = Encoding.UTF8.GetString(toDisk ? disk.Files.Last().Bytes : mail.Files.Last().Bytes);
                Assert.Contains("Legacy", html); AssertImages(html, false, false, false);
            }
        Assert.All(await DeliverySnapshotsAsync(fixture), snapshot => Assert.Equal(legacy, snapshot));
    }

    [Fact]
    public async Task Delivery_PdfFailurePreservesErrorContractsAndNeverCallsExternalServices()
    {
        await using var fixture = await DeliveryFixtureAsync();
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf { Fail = true };
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture)) foreach (var toDisk in new[] { false, true })
                Assert.Equal(section == "contracts" && !toDisk ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable, (await DeliverAsync(client, section, id, toDisk)).StatusCode);
        Assert.Empty(mail.Files); Assert.Empty(disk.Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delivery_MissingCompanyDefaultsRetainCommonTemplateMode(bool signed)
    {
        await using var fixture = await DeliveryFixtureAsync();
        var invoiceSettings = new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = InvoiceDocumentTemplateEngine.DefaultTemplateKey,
            PrintWithSignatureAndSealByDefault = signed,
            Templates = [new InvoiceDocumentTemplateRequest { Key = InvoiceDocumentTemplateEngine.DefaultTemplateKey, Name = "Счёт", Html = InvoiceDocumentTemplateEngine.CreateDefaultSettings().DefaultTemplateHtml }]
        };
        var actSettings = new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = ActDocumentTemplateEngine.DefaultTemplateKey,
            PrintWithSignatureAndSealByDefault = signed,
            Templates = [new InvoiceDocumentTemplateRequest { Key = ActDocumentTemplateEngine.DefaultTemplateKey, Name = "Акт", Html = ActDocumentTemplateEngine.CreateDefaultSettings().DefaultTemplateHtml }]
        };
        fixture.Db.SystemSettings.AddRange(new SystemSetting { Key = "invoices.documentTemplates", Value = InvoiceDocumentTemplateEngine.Serialize(InvoiceDocumentTemplateEngine.NormalizeRequest(invoiceSettings)) },
            new SystemSetting { Key = "acts.documentTemplates", Value = ActDocumentTemplateEngine.Serialize(ActDocumentTemplateEngine.NormalizeRequest(actSettings)) });
        await fixture.Db.SaveChangesAsync();
        var mail = new DeliveryMail(); var disk = new DeliveryDisk(); var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture)) foreach (var toDisk in new[] { false, true })
            {
                Assert.Equal(HttpStatusCode.OK, (await DeliverAsync(client, section, id, toDisk)).StatusCode);
                AssertImages(Encoding.UTF8.GetString(toDisk ? disk.Files.Last().Bytes : mail.Files.Last().Bytes), section == "invoices", section != "contracts" && signed, section != "contracts" && signed);
            }
    }

    [Theory]
    [InlineData("invoices", false)]
    [InlineData("acts", false)]
    [InlineData("contracts", false)]
    [InlineData("invoices", true)]
    [InlineData("acts", true)]
    [InlineData("contracts", true)]
    public async Task Delivery_CancellationAfterRenderPreventsExternalCall(string section, bool toDisk)
    {
        await using var fixture = await DeliveryFixtureAsync();
        using var cancellation = new CancellationTokenSource();
        var mail = new DeliveryMail(); var disk = new DeliveryDisk();
        var pdf = new DeliveryPdf { AfterRender = _ => { cancellation.Cancel(); return Task.CompletedTask; } };
        using var server = DeliveryServer(fixture, mail, disk, pdf);
        ControllerBase controller = section switch
        {
            "invoices" => ActivatorUtilities.CreateInstance<InvoicesController>(server.Services),
            "acts" => ActivatorUtilities.CreateInstance<ActsController>(server.Services),
            _ => ActivatorUtilities.CreateInstance<ContractsController>(server.Services),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, fixture.User.Id.ToString())], "print-test"))
            }
        };
        var id = DefaultsDocuments(fixture).Single(document => document.Section == section).Id;
        var request = new SendInvoiceEmailRequest { Email = "recipient@example.test", Format = "pdf" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller switch
        {
            InvoicesController invoice => toDisk ? (Task)invoice.UploadDocumentToYandexDisk(id, ct: cancellation.Token) : invoice.SendInvoiceEmail(id, request, cancellation.Token),
            ActsController act => toDisk ? (Task)act.UploadDocumentToYandexDisk(id, ct: cancellation.Token) : act.SendActEmail(id, request, cancellation.Token),
            ContractsController contract => toDisk ? (Task)contract.UploadDocumentToYandexDisk(id, ct: cancellation.Token) : contract.SendEmail(id, request, cancellation.Token),
            _ => throw new InvalidOperationException(),
        });
        Assert.Equal(1, pdf.Calls); Assert.Empty(mail.Files); Assert.Empty(disk.Files);
    }

    [Fact]
    public async Task Delivery_DiskFailureKeepsExistingErrorAndNeverReportsAFileAsSent()
    {
        await using var fixture = await DeliveryFixtureAsync(); await SetDefaultsAsync(fixture, 5);
        var mail = new DeliveryMail(); var disk = new DeliveryDisk { Fail = true }; var pdf = new DeliveryPdf();
        using var server = DeliveryServer(fixture, mail, disk, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture))
        {
            using var response = await DeliverAsync(client, section, id, true);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Синтетический отказ диска", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(disk.Files); Assert.Empty(mail.Files);
    }

    private static async Task<string?[]> DeliverySnapshotsAsync(Fixture fixture) =>
    [await fixture.Db.Payments.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync(),
        await fixture.Db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync(),
        await fixture.Db.Contracts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync()];

    private sealed record DeliveryFile(string Name, string ContentType, byte[] Bytes);
    private sealed class DeliveryPdf : IInvoiceDocumentPdfService
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public Func<CancellationToken, Task>? AfterRender { get; init; }
        public async Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
        {
            ++Calls; if (Fail) throw new InvalidOperationException("Синтетический отказ PDF");
            if (AfterRender is not null) await AfterRender(ct);
            return Encoding.UTF8.GetBytes(html);
        }
    }
    private sealed class DeliveryMail : IEmailService
    {
        public List<DeliveryFile> Files { get; } = [];
        public List<string> Recipients { get; } = [];
        public Task SendEmailWithAttachmentAsync(string to, string subject, string body, string name, byte[] bytes, string contentType)
        { Recipients.Add(to); Files.Add(new(name, contentType, bytes)); return Task.CompletedTask; }
        public Task SendRegistrationPendingEmailAsync(string email, string name) => throw new NotSupportedException();
        public Task SendRegistrationApprovedEmailAsync(string email, string name) => throw new NotSupportedException();
        public Task SendRegistrationRejectedEmailAsync(string email, string name, string? reason) => throw new NotSupportedException();
        public Task SendAdminNewRegistrationNotificationAsync(string email, string name, string newEmail) => throw new NotSupportedException();
    }
    private sealed class DeliveryDisk : IYandexDiskService
    {
        public bool Fail { get; init; }
        public List<DeliveryFile> Files { get; } = [];
        public async Task<YandexDiskOperationResultDto> UploadCrmDocumentAsync(YandexDiskCrmDocumentUpload upload, CancellationToken ct = default)
        {
            if (Fail) throw new YandexDiskOperationException(403, "Синтетический отказ диска");
            using var memory = new MemoryStream(); await upload.Content.CopyToAsync(memory, ct);
            Files.Add(new(upload.OriginalFileName, upload.ContentType!, memory.ToArray()));
            return new YandexDiskOperationResultDto { Success = true, Path = "/CRM/" + upload.OriginalFileName };
        }
        public Task<YandexDiskSettingsDto> GetSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskSettingsDto> UpdateSettingsAsync(UpdateYandexDiskSettingsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskConnectionTestDto> TestConnectionAsync(TestYandexDiskConnectionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskListResponseDto> ListAsync(string? path, int limit, int offset, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskOperationResultDto> CreateFolderAsync(string? path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskOperationResultDto> CopyAsync(string? from, string? to, bool overwrite, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskOperationResultDto> MoveAsync(string? from, string? to, bool overwrite, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskOperationResultDto> DeleteAsync(string? path, bool permanently, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskOperationResultDto> UploadAsync(string? path, Stream content, string? contentType, bool overwrite, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskDownload> DownloadAsync(string? path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskTextFileDto> GetTextFileAsync(string? path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskOperationResultDto> SaveTextFileAsync(UpdateYandexDiskTextFileRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskCrmSyncSettingsDto> GetCrmSyncSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskCrmSyncSettingsDto> UpdateCrmSyncSettingsAsync(UpdateYandexDiskCrmSyncSettingsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<YandexDiskCrmSyncPreviewDto> PreviewCrmSyncAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
