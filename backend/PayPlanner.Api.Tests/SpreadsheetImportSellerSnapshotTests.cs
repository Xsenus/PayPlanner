using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class SpreadsheetImportServiceTests
{
    [Theory]
    [InlineData(false, "first-file")]
    [InlineData(true, "first-file")]
    [InlineData(false, "on-create")]
    [InlineData(true, "on-create")]
    public async Task ApplyAsync_NewInvoiceAndActCaptureTheBatchCreationPolicy(bool postgres, string timing)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(postgres, bankModelTemplate);
        var db = fixture.Db;
        var company = await SeedSpreadsheetSellerAsync(db, timing);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var service = new SpreadsheetImportService(new CompanyHistoryContextFactory(db, postgres, accessor), accessor,
            NullLogger<SpreadsheetImportService>.Instance);
        var files = SellerSnapshotSpreadsheetFiles();
        var preview = await service.PreviewAsync(files);
        var selection = preview.Sections.SelectMany(section => section.Rows).Where(row => row.IsSelectable)
            .Select(row => row.PreviewId).ToList();
        Assert.Equal(3, selection.Count);

        var result = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest { SelectedPreviewIds = selection });

        Assert.Equal(SpreadsheetImportBatchStatus.Applied, result.Status);
        Assert.Equal(0, result.ErrorRows);
        var invoice = await db.Payments.AsNoTracking().SingleAsync();
        var act = await db.Acts.AsNoTracking().SingleAsync();
        Assert.Equal(100m, invoice.Amount);
        Assert.Equal(100m, act.Amount);
        Assert.Equal(invoice.Id, act.InvoiceId);
        foreach (var document in new IDocumentSeller[] { invoice, act })
        {
            Assert.Equal(timing, document.SellerSnapshotPolicy);
            if (timing == DocumentSellerSnapshotPolicy.OnCreate)
            {
                Assert.Equal(company.Id, document.SellerCompanyId);
                var metadata = Assert.IsType<SellerSnapshotMetadata>(SellerLegalEntityComposer.ReadMetadata(document.SellerSnapshotJson));
                Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, metadata.FreezeReason);
                Assert.NotNull(metadata.FrozenAtUtc);
                Assert.Contains("Продавец табличного импорта", document.SellerSnapshotJson);
            }
            else
            {
                Assert.Null(document.SellerCompanyId);
                Assert.Null(document.SellerSnapshotJson);
            }
        }
        Assert.Equal(timing == DocumentSellerSnapshotPolicy.OnCreate ? 3 : 0, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false, "invoice")]
    [InlineData(true, "invoice")]
    [InlineData(false, "act")]
    [InlineData(true, "act")]
    public async Task ApplyAsync_SnapshotSizeFailureRollsBackWholeBatchIncludingImagesAndEarlierCounterparty(
        bool postgres, string documentKind)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(postgres, bankModelTemplate);
        var db = fixture.Db;
        var company = await SeedSpreadsheetSellerAsync(db, DocumentSellerSnapshotPolicy.OnCreate);
        // FreezeImagesAsync stages all three versions before the snapshot-size guard.
        // The earlier counterparty/batch writes share the same ApplyAsync transaction.
        SellerSnapshotBoundaryTestData.MakeOversizedProfile(company);
        await db.SaveChangesAsync();
        var before = await CaptureSpreadsheetSellerStateAsync(db);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var service = new SpreadsheetImportService(new CompanyHistoryContextFactory(db, postgres, accessor), accessor,
            NullLogger<SpreadsheetImportService>.Instance);
        var files = documentKind == "invoice" ? new[] { SellerSnapshotSpreadsheetFiles()[0] } :
            new[] { CreateActFile(["Дата акта", "№ акта", "Сумма", "Контрагент", "Статус", "Номенклатурная часть"],
                ["21.10.2026", "SNAPSHOT-ACT", "100", "Импортируемый контрагент", "Подписан", "Услуга 1 шт x 100 р. = 100 р."]) };
        var preview = await service.PreviewAsync(files);
        var selection = preview.Sections.SelectMany(section => section.Rows).Where(row => row.IsSelectable)
            .Select(row => row.PreviewId).ToList();
        Assert.Equal(2, selection.Count);

        await Assert.ThrowsAsync<SellerSnapshotTooLargeException>(() => service.ApplyAsync(files,
            new SpreadsheetImportApplyRequest { SelectedPreviewIds = selection }));

        Assert.Equal(before, await CaptureSpreadsheetSellerStateAsync(db));
        Assert.Empty(await db.Clients.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.Payments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.Acts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.CompanyBrandImageVersions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.SpreadsheetImportBatches.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.SpreadsheetImportBatchOperations.AsNoTracking().ToArrayAsync());
    }

    private static async Task<LegalEntity> SeedSpreadsheetSellerAsync(PaymentContext db, string timing)
    {
        var company = new LegalEntity
        {
            ShortName = "Продавец табличного импорта",
            Inn = "7707083893",
            ProfileStatus = CompanyProfileStatuses.Active,
            LogoDataUrl = "data:image/png;base64,TE9HTw==",
            SealDataUrl = "data:image/png;base64,U0VBTA==",
            DirectorSignatureDataUrl = "data:image/png;base64,U0lHTg==",
        };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.AddRange(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" },
            new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = timing });
        await db.SaveChangesAsync();
        return company;
    }

    private static SpreadsheetImportUploadFile[] SellerSnapshotSpreadsheetFiles() =>
    [
        CreateInvoiceFile(["Дата счета", "№ счета", "Сумма", "Контрагент", "Статус", "Номенклатурная часть"],
            ["20.10.2026", "SNAPSHOT-INVOICE", "100", "Импортируемый контрагент", "Ожидает", "Услуга 1 шт x 100 р. = 100 р."]),
        CreateActFile(["Дата акта", "№ акта", "Сумма", "Контрагент", "Статус", "Счёт №", "Номенклатурная часть"],
            ["21.10.2026", "SNAPSHOT-ACT", "100", "Импортируемый контрагент", "Подписан", "SNAPSHOT-INVOICE", "Услуга 1 шт x 100 р. = 100 р."]),
    ];

    private static async Task<string> CaptureSpreadsheetSellerStateAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Clients = await db.Clients.CountAsync(),
        Payments = await db.Payments.CountAsync(),
        Invoices = await db.Invoices.CountAsync(),
        Acts = await db.Acts.CountAsync(),
        Lines = await db.Set<ActLineItem>().CountAsync(),
        Versions = await db.CompanyBrandImageVersions.CountAsync(),
        Batches = await db.SpreadsheetImportBatches.CountAsync(),
        BatchFiles = await db.Set<SpreadsheetImportBatchFile>().CountAsync(),
        Operations = await db.SpreadsheetImportBatchOperations.CountAsync(),
        Companies = await db.LegalEntities.AsNoTracking().OrderBy(company => company.Id)
            .Select(company => new { company.Id, company.ShortName, company.Address, company.LogoDataUrl, company.SealDataUrl, company.DirectorSignatureDataUrl }).ToArrayAsync(),
        Settings = await db.SystemSettings.AsNoTracking().OrderBy(setting => setting.Key).Select(setting => new { setting.Key, setting.Value }).ToArrayAsync(),
    });
}
