using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Documents;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class InvoiceDocumentTemplateTests
{
    private const string PositionImage = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private const string PositionTemplate = "<html><body><h1>Счёт {{invoice.number}}</h1><p>{{seller.shortName}}</p><table><tr><td width=\"60%\">Неизменённый текст</td><td width=\"40%\" align=\"right\">{{seller.sealHtml}}</td></tr></table><div style=\"page-break-before:always\">Конец документа</div></body></html>";

    private static Task<IActionResult> IssueCustomWord(InvoicesController controller, int id, string flow, CancellationToken ct = default)
        => flow switch
        {
            "download" => controller.GetDocument(id, "word", "custom-position", null, ct),
            "email" => controller.SendInvoiceEmail(id, new SendInvoiceEmailRequest { Format = "word", Email = "position@example.test" }, ct),
            _ => IssueCustomDisk(controller, id, ct),
        };

    private static async Task<IActionResult> IssueCustomDisk(InvoicesController controller, int id, CancellationToken ct)
    {
        var result = await controller.UploadDocumentToYandexDisk(id, "word", "custom-position", null, null, ct);
        return result.Result ?? new OkObjectResult(Assert.IsType<YandexDiskOperationResultDto>(result.Value));
    }

    [Theory]
    [InlineData("download")]
    [InlineData("email")]
    [InlineData("disk")]
    public async Task CustomWord_PositionIsKeptByEveryDeliveryAndFrozenSellerIsNotReplaced(string flow)
    {
        await using var db = await CreateContextAsync();
        var id = await CreateInvoiceDocumentFixtureAsync(db);
        await CreateMainCompanyAsync(db);
        var company = await db.LegalEntities.SingleAsync();
        company.ShortName = company.FullName = "Сохранённая компания";
        company.LogoDataUrl = company.SealDataUrl = company.DirectorSignatureDataUrl = PositionImage;
        await db.SaveChangesAsync();
        var email = new RecordingEmailService(); var disk = new RecordingYandexDiskService();
        var controller = CreateController(db, isAdmin: true, emailService: email, yandexDiskService: disk);
        var settings = await controller.UpsertDocumentTemplateSettings(new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = "custom-position",
            PrintWithSignatureAndSealByDefault = true,
            Templates = [new InvoiceDocumentTemplateRequest { Key = "custom-position", Name = "Положение печати", Html = PositionTemplate }],
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(settings.Result);
        Assert.IsType<FileContentResult>(await IssueCustomWord(controller, id, "download"));
        var payment = await db.Payments.FindAsync(id);
        Assert.NotNull(payment!.SellerSnapshotJson);
        var snapshot = payment.SellerSnapshotJson;
        var financial = JsonSerializer.Serialize(new { payment.Amount, payment.ClientId, payment.ContractId, payment.Account, payment.Type });
        var tasks = await db.TaskJournalItems.CountAsync(); var operations = await db.MoneyOperations.CountAsync();
        var versions = await db.CompanyBrandImageVersions.CountAsync();
        company.ShortName = company.FullName = "Новый профиль"; company.SealDataUrl = null;
        await db.SaveChangesAsync();

        var result = await IssueCustomWord(controller, id, flow);
        var bytes = flow switch
        {
            "download" => Assert.IsType<FileContentResult>(result).FileContents,
            "email" => email.AttachmentBytes!,
            _ => disk.UploadBytes!,
        };
        if (flow != "download") Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(bytes);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var htmlReader = new StreamReader(zip.GetEntry("word/afchunk.html")!.Open());
        var html = await htmlReader.ReadToEndAsync();
        Assert.Contains("align=\"right\"", html);
        Assert.Contains("page-break-before:always", html);
        Assert.Contains(PositionImage, html);
        Assert.Contains("Сохранённая компания", html);
        Assert.DoesNotContain("Новый профиль", html);
        using var documentReader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        Assert.DoesNotContain("<w:drawing>", await documentReader.ReadToEndAsync());
        if (flow == "email") Assert.Equal(HtmlWordDocumentBuilder.ContentType, email.AttachmentContentType);
        if (flow == "disk") Assert.Equal(HtmlWordDocumentBuilder.ContentType, disk.Upload!.ContentType);
        Assert.Equal(snapshot, payment.SellerSnapshotJson);
        Assert.Equal(financial, JsonSerializer.Serialize(new { payment.Amount, payment.ClientId, payment.ContractId, payment.Account, payment.Type }));
        Assert.Equal(tasks, await db.TaskJournalItems.CountAsync()); Assert.Equal(operations, await db.MoneyOperations.CountAsync());
        Assert.Equal(versions, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData("download")]
    [InlineData("email")]
    [InlineData("disk")]
    public async Task CustomWord_ExportDenialDoesNotGenerateOrDeliver(string flow)
    {
        await using var db = await CreateContextAsync();
        var id = await CreateInvoiceDocumentFixtureAsync(db);
        var user = await CreateAccountsUserAsync(db, "position-no-export", canExport: false, canCreateYandexDisk: true);
        var email = new RecordingEmailService(); var disk = new RecordingYandexDiskService();
        var controller = CreateController(db, isAdmin: false, emailService: email, yandexDiskService: disk, userId: user.Id);
        Assert.IsType<ForbidResult>(await IssueCustomWord(controller, id, flow));
        Assert.Null(email.AttachmentBytes); Assert.Null(disk.UploadBytes);
        Assert.Null((await db.Payments.FindAsync(id))!.SellerSnapshotJson);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("email")]
    [InlineData("disk")]
    public async Task CustomWord_CancelledRequestDoesNotDeliver(string flow)
    {
        await using var db = await CreateContextAsync();
        var id = await CreateInvoiceDocumentFixtureAsync(db);
        var email = new RecordingEmailService(); var disk = new RecordingYandexDiskService();
        var controller = CreateController(db, isAdmin: true, emailService: email, yandexDiskService: disk);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IssueCustomWord(controller, id, flow, cancellation.Token));
        Assert.Null(email.AttachmentBytes); Assert.Null(disk.UploadBytes);
        Assert.Null((await db.Payments.FindAsync(id))!.SellerSnapshotJson);
    }
}
