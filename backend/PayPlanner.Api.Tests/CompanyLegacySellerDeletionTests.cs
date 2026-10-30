using System.Data.Common;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Theory]
    [InlineData("invoice", false)]
    [InlineData("act", false)]
    [InlineData("contract", false)]
    [InlineData("invoice", true)]
    [InlineData("act", true)]
    [InlineData("contract", true)]
    public async Task CompanyDelete_LegacySellerRetainsImagesAndTextWithoutRewritingHistory(string kind, bool own)
    {
        await using var db = await CreateContextAsync();
        await AssertLegacySellerDeletionAsync(db, kind, own);
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("act")]
    [InlineData("contract")]
    public async Task LegacySeller_IssuedReadAfterDeletionDoesNotRefreezeTheDocument(string kind)
    {
        await using var db = await CreateContextAsync();
        await AssertLegacySellerDeletionAsync(db, kind, true, freeze: true);
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("act")]
    [InlineData("contract")]
    public async Task LegacySeller_PostgreSqlDeletedProfilePreservesImagesAndHistory(string kind)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var db = CompanyCreatePgContext(database.ConnectionString);
        await AssertLegacySellerDeletionAsync(db, kind, true, freeze: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(123456)]
    public async Task LegacySeller_MismatchedSnapshotNeverReadsAnotherCompanyImages(int snapshotId)
    {
        var capture = new LegacySellerQueryCapture();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: capture);
        var fixture = await CompanyDeleteFixture(db, true);
        var company = await db.LegalEntities.AsNoTracking().SingleAsync(row => row.Id == fixture.Company);
        company.LogoDataUrl = "data:image/png;base64,Y2FsbGVyLWRlZmF1bHQ=";
        var document = new Contract { SellerCompanyId = company.Id, SellerSnapshotJson = SellerLegalEntityComposer.ToSnapshotJson(new LegalEntity { Id = snapshotId, ShortName = "Historical" }) };
        capture.Commands.Clear(); db.ChangeTracker.Clear();
        var error = await Assert.ThrowsAsync<SellerSelectionException>(() => new DocumentSellerService(db).ResolveAsync(document, company, false, default));
        Assert.Contains("не соответствует", error.Message); Assert.Empty(capture.Commands); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySeller_ReadsOnlyExactSourceImageColumnsWithoutLiveAddresses(bool deleted)
    {
        var capture = new LegacySellerQueryCapture();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: capture);
        var fixture = await CompanyDeleteFixture(db, true);
        var company = await db.LegalEntities.SingleAsync(row => row.Id == fixture.Company);
        company.LogoDataUrl = CompanyImageTestData.TransparentPng; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        if (deleted) Assert.IsType<NoContentResult>(await CreateController(db, fixture.Actor).Delete(fixture.Company, default));
        db.ChangeTracker.Clear(); capture.Commands.Clear();
        var document = new Contract { SellerCompanyId = fixture.Company, SellerSnapshotJson = SellerLegalEntityComposer.ToSnapshotJson(new LegalEntity { Id = fixture.Company, ShortName = "Historical" }) };
        var staleDefault = new LegalEntity { Id = fixture.Company, LogoDataUrl = "data:image/png;base64,c3RhbGU=" };
        var seller = Assert.IsType<LegalEntity>(await new DocumentSellerService(db).ResolveAsync(document, staleDefault, false, default));
        Assert.Equal(CompanyImageTestData.TransparentPng, seller.LogoDataUrl);
        var sql = Assert.Single(capture.Commands);
        Assert.Contains("WHERE", sql); Assert.Contains("\"Id\" =", sql);
        Assert.Contains("\"LogoDataUrl\"", sql); Assert.Contains("\"SealDataUrl\"", sql); Assert.Contains("\"DirectorSignatureDataUrl\"", sql);
        Assert.DoesNotContain("AddressRecords", sql); Assert.DoesNotContain("\"ShortName\"", sql); Assert.DoesNotContain("\"BankAccountsJson\"", sql);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task LegacySeller_MissingSourceOrSelectionDoesNotBorrowDefaultImagesOrWriteASnapshot()
    {
        await using var db = await CreateContextAsync();
        var fixture = await CompanyDeleteFixture(db, true);
        var defaultCompany = new LegalEntity { Id = fixture.Other, LogoDataUrl = "data:image/png;base64,b3RoZXI=" };
        var snapshot = SellerLegalEntityComposer.ToSnapshotJson(new LegalEntity { Id = 123456, ShortName = "Missing source" });
        var document = new Contract { SellerCompanyId = 123456, SellerSnapshotJson = snapshot };
        var service = new DocumentSellerService(db);
        Assert.Null((await service.ResolveAsync(document, defaultCompany, false, default))!.LogoDataUrl);
        document.SellerCompanyId = null;
        Assert.Null((await service.ResolveAsync(document, null, false, default))!.LogoDataUrl);
        Assert.Equal(snapshot, document.SellerSnapshotJson); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task LegacySeller_FreshReadDenialPreventsDeletedImageLookupAndPreservesHistory()
    {
        var capture = new LegacySellerQueryCapture();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: capture);
        var fixture = await CompanyDeleteFixture(db, true);
        var invoice = await db.Payments.SingleAsync();
        invoice.SellerSnapshotJson = SellerLegalEntityComposer.ToSnapshotJson(new LegalEntity { Id = fixture.Company, ShortName = "Historical" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.IsType<NoContentResult>(await CreateController(db, fixture.Actor).Delete(fixture.Company, default)); db.ChangeTracker.Clear();
        var before = await CompanyDeleteProtectedSnapshot(db);
        var document = await db.Payments.AsNoTracking().SingleAsync(); capture.Commands.Clear();
        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new DocumentSellerService(db).ResolveAsync(document, null, true, default, _ => Task.FromResult(false)));
        Assert.DoesNotContain(capture.Commands, sql => sql.Contains("LogoDataUrl", StringComparison.Ordinal));
        Assert.Equal(before, await CompanyDeleteProtectedSnapshot(db)); Assert.Empty(db.ChangeTracker.Entries());
    }

    private sealed class LegacySellerQueryCapture : DbCommandInterceptor
    {
        internal List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static async Task AssertLegacySellerDeletionAsync(PaymentContext db, string kind, bool own, bool freeze = false)
    {
        var fixture = await CompanyDeleteFixture(db, own);
        var company = await db.LegalEntities.SingleAsync(row => row.Id == fixture.Company);
        company.LogoDataUrl = CompanyImageTestData.TransparentPng;
        company.SealDataUrl = "data:image/png;base64,aGlzdG9yaWNhbC1zZWFs";
        company.DirectorSignatureDataUrl = "data:image/png;base64,aGlzdG9yaWNhbC1zaWduYXR1cmU=";
        var other = await db.LegalEntities.SingleAsync(row => row.Id == fixture.Other);
        other.LogoDataUrl = "data:image/png;base64,b3RoZXI=";
        var legacy = await ReadLegacySellerDocument(db, kind);
        var json = JsonNode.Parse(SellerLegalEntityComposer.ToSnapshotJson(new LegalEntity
        {
            Id = fixture.Company,
            ShortName = "Historical seller text",
            Inn = "7700000121",
            Address = "Historical document address"
        }))!.AsObject();
        foreach (var key in new[] { "ImagesFrozen", "ImagePrintDefaultsFrozen", "ImagePrintDefaults", "LogoVersionId", "SealVersionId", "SignatureVersionId", "FrozenAtUtc", "FreezeReason", "BranchName" }) json.Remove(key);
        legacy.SellerSnapshotJson = json.ToJsonString();
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await CompanyDeleteProtectedSnapshot(db);
        var service = new DocumentSellerService(db);
        legacy = await ReadLegacySellerDocument(db, kind, true);
        var originalJson = legacy.SellerSnapshotJson;
        var expected = Assert.IsType<LegalEntity>(await service.ResolveAsync(legacy, null, false, default));
        Assert.Equal(CompanyImageTestData.TransparentPng, expected.LogoDataUrl);
        Assert.Equal(company.SealDataUrl, expected.SealDataUrl); Assert.Equal(company.DirectorSignatureDataUrl, expected.DirectorSignatureDataUrl);
        Assert.Null(await service.ReadImagePrintDefaultsAsync(legacy, default));
        Assert.IsType<NoContentResult>(await CreateController(db, fixture.Actor).Delete(fixture.Company, default));
        db.ChangeTracker.Clear(); // A new document request must not rely on deleted tracked navigations.
        Assert.IsType<NotFoundResult>((await CreateController(db, fixture.Actor).GetById(fixture.Company, default)).Result);
        var after = Assert.IsType<LegalEntity>(await service.ResolveAsync(legacy, other, freeze, default, _ => Task.FromResult(true)));
        Assert.Equal(expected.ShortName, after.ShortName); Assert.Equal(expected.Inn, after.Inn); Assert.Equal(expected.Address, after.Address);
        Assert.Equal(expected.LogoDataUrl, after.LogoDataUrl); Assert.Equal(expected.SealDataUrl, after.SealDataUrl); Assert.Equal(expected.DirectorSignatureDataUrl, after.DirectorSignatureDataUrl);
        Assert.Null(await service.ReadImagePrintDefaultsAsync(legacy, default));
        Assert.Equal(originalJson, legacy.SellerSnapshotJson);
        Assert.Equal(before, await CompanyDeleteProtectedSnapshot(db));
        if (freeze) Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        else Assert.Empty(db.ChangeTracker.Entries());
    }

    private static async Task<IDocumentSeller> ReadLegacySellerDocument(PaymentContext db, string kind, bool noTracking = false)
        => kind switch
        {
            "invoice" => await (noTracking ? db.Payments.AsNoTracking() : db.Payments).SingleAsync(),
            "act" => await (noTracking ? db.Acts.AsNoTracking() : db.Acts).SingleAsync(),
            "contract" => await (noTracking ? db.Contracts.AsNoTracking() : db.Contracts).SingleAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}
