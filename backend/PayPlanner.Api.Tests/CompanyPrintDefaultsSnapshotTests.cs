using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CompanyPrintDefaultsSnapshotTests
{
    public static IEnumerable<object[]> Masks() => Enumerable.Range(0, 8).Select(mask => new object[] { mask });
    internal static CompanyDocumentImagePrintDefaults Value(int mask) => new(
        new((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0), new(false, true, false), new(true, false, true));
    [Theory]
    [MemberData(nameof(Masks))]
    public void Codec_RoundTripsAllIndependentFlagsWithoutImagePayloads(int mask)
    {
        var value = Value(mask);
        var json = SellerLegalEntityComposer.ToSnapshotJson(new LegalEntity { Id = 7, ShortName = "Frozen" },
            imagePrintDefaults: value, imagePrintDefaultsFrozen: true);
        var metadata = SellerLegalEntityComposer.ReadMetadata(json)!;
        Assert.True(metadata.ImagePrintDefaultsFrozen);
        Assert.Equal(value, metadata.ImagePrintDefaults);
        Assert.DoesNotContain("data:image", json);
        Assert.True(json.Length < 16000);
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("act")]
    [InlineData("contract")]
    public void DetailContract_ExposesIndependentFrozenFlagsWithWebPropertyNames(string kind)
    {
        var metadata = SellerLegalEntityComposer.ReadMetadata(SellerLegalEntityComposer.ToSnapshotJson(
            new LegalEntity { Id = 7, ShortName = "Frozen" }, imagePrintDefaults: Value(5), imagePrintDefaultsFrozen: true));
        object dto = kind switch
        {
            "invoice" => new PayPlanner.Api.Models.Responses.InvoiceDto { SellerSnapshotInfo = metadata },
            "act" => new PayPlanner.Api.Models.Responses.ActDto { SellerSnapshotInfo = metadata },
            _ => new PayPlanner.Api.Models.Responses.ContractDto { SellerSnapshotInfo = metadata },
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var info = json.RootElement.GetProperty("sellerSnapshotInfo");
        Assert.True(info.GetProperty("imagePrintDefaultsFrozen").GetBoolean());
        var flags = info.GetProperty("imagePrintDefaults").GetProperty("invoice");
        Assert.True(flags.GetProperty("includeLogo").GetBoolean());
        Assert.False(flags.GetProperty("includeSeal").GetBoolean());
        Assert.True(flags.GetProperty("includeSignature").GetBoolean());
        Assert.False(info.GetProperty("imagePrintDefaults").TryGetProperty("isEmpty", out _));
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("act")]
    [InlineData("contract")]
    public void Codec_PreservesPerKindFallbackAndDistinguishesLegacyFromCapturedAbsence(string kind)
    {
        var flags = new PayPlanner.Api.Services.Documents.DocumentImagePrintOptions(false, true, false);
        var value = new CompanyDocumentImagePrintDefaults(kind == "invoice" ? flags : null,
            kind == "act" ? flags : null, kind == "contract" ? flags : null);
        var company = new LegalEntity { Id = 7, ShortName = "Frozen" };
        var json = SellerLegalEntityComposer.ToSnapshotJson(company, imagePrintDefaults: value, imagePrintDefaultsFrozen: true);
        Assert.Equal(value, SellerLegalEntityComposer.ReadMetadata(json)!.ImagePrintDefaults);
        var empty = SellerLegalEntityComposer.ReadMetadata(SellerLegalEntityComposer.ToSnapshotJson(company, imagePrintDefaultsFrozen: true))!;
        Assert.True(empty.ImagePrintDefaultsFrozen);
        Assert.Null(empty.ImagePrintDefaults);
        var legacy = JsonNode.Parse(json)!.AsObject();
        legacy.Remove("ImagePrintDefaultsFrozen"); legacy.Remove("ImagePrintDefaults");
        var old = SellerLegalEntityComposer.ReadMetadata(legacy.ToJsonString())!;
        Assert.False(old.ImagePrintDefaultsFrozen);
        Assert.Null(old.ImagePrintDefaults);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"invoice\":null,\"act\":null,\"contract\":null}")]
    [InlineData("{\"version\":1,\"invoice\":{\"includeLogo\":true},\"act\":null,\"contract\":null}")]
    public void Codec_RejectsDamagedCapturedDefaultsWithoutChangingSnapshot(string invalid)
    {
        var node = JsonNode.Parse(SellerLegalEntityComposer.ToSnapshotJson(new LegalEntity { ShortName = "Frozen" }, imagePrintDefaultsFrozen: true))!;
        node["ImagePrintDefaults"] = JsonNode.Parse(invalid);
        var json = node.ToJsonString();
        Assert.Throws<SellerSelectionException>(() => SellerLegalEntityComposer.ReadMetadata(json));
        Assert.Equal(json, node.ToJsonString());
    }

    [Theory]
    [InlineData("invoice", false)]
    [InlineData("act", false)]
    [InlineData("contract", false)]
    [InlineData("invoice", true)]
    [InlineData("act", true)]
    [InlineData("contract", true)]
    public async Task Capture_UsesChosenCompanyAndPolicyAndNeverRewritesIssuedDefaults(string kind, bool onCreate)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var company = await SeedAsync(db);
        var other = new LegalEntity { ShortName = "Other", ProfileStatus = CompanyProfileStatuses.Active };
        db.LegalEntities.Add(other); await db.SaveChangesAsync();
        (await db.SystemSettings.SingleAsync(row => row.Key == "legalEntities.mainCompanyIds")).Value = $"[{company.Id},{other.Id}]";
        var store = new CompanyDocumentImagePrintDefaultsService(db);
        await store.StageAsync(company.Id, Value(2), true);
        await store.StageAsync(other.Id, Value(7), true);
        await db.SaveChangesAsync();
        var document = Document(kind);
        document.SellerCompanyId = other.Id;
        document.SellerSnapshotPolicy = onCreate ? DocumentSellerSnapshotPolicy.OnCreate : DocumentSellerSnapshotPolicy.FirstFile;
        var service = new DocumentSellerService(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await service.InitializeForCreationAsync(document, company, default);
            if (!onCreate) Assert.Null(document.SellerSnapshotJson);
            await transaction.CommitAsync();
        }
        await store.StageAsync(other.Id, Value(1), true);
        await db.SaveChangesAsync();
        await service.ResolveAsync(document, company, true, default);
        var issued = document.SellerSnapshotJson;
        var metadata = SellerLegalEntityComposer.ReadMetadata(issued)!;
        Assert.True(metadata.ImagePrintDefaultsFrozen);
        Assert.Equal(Value(onCreate ? 7 : 1), metadata.ImagePrintDefaults);
        Assert.Equal(other.Id, document.SellerCompanyId);
        Assert.Equal(document.SellerSnapshotPolicy, metadata.FreezeReason);
        await store.StageAsync(other.Id, null, true); await db.SaveChangesAsync();
        await service.ResolveAsync(document, company, true, default);
        Assert.Equal(issued, document.SellerSnapshotJson);
        Assert.Equal(0, await db.ClientCases.CountAsync());
        Assert.Equal(0, await db.MoneyOperations.CountAsync());
    }

    [Fact]
    public async Task Preview_DoesNotReadOrCaptureDefaultsAndLegacyWinnerStaysByteIdentical()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var company = await SeedAsync(db);
        db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(company.Id), Value = "broken" });
        await db.SaveChangesAsync();
        var document = new Act { Number = "UNISSUED", SellerCompanyId = company.Id };
        await new DocumentSellerService(db).ResolveAsync(document, company, false, default);
        Assert.Null(document.SellerSnapshotJson);
        var legacy = SellerLegalEntityComposer.ToSnapshotJson(company);
        document.SellerSnapshotJson = legacy;
        await new DocumentSellerService(db).ResolveAsync(document, company, true, default);
        Assert.Equal(legacy, document.SellerSnapshotJson);
        Assert.False(SellerLegalEntityComposer.ReadMetadata(legacy)!.ImagePrintDefaultsFrozen);
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_RefusesDamagedDefaultsWithoutSnapshotOrImageVersionWrites(bool oversized)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var company = await SeedAsync(db);
        company.LogoDataUrl = "data:image/png;base64,TE9HTw==";
        var invalid = oversized ? new string('x', 20000) : "broken";
        db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(company.Id), Value = invalid });
        db.Acts.Add(new Act { Number = "INVALID", SellerCompanyId = company.Id });
        await db.SaveChangesAsync();
        var document = await db.Acts.SingleAsync();
        await Assert.ThrowsAsync<SellerSelectionException>(() => new DocumentSellerService(db).ResolveAsync(document, company, true, default, _ => Task.FromResult(true)));
        Assert.Null(document.SellerSnapshotJson);
        Assert.Null(await db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync());
        Assert.Equal(invalid, (await db.SystemSettings.AsNoTracking().SingleAsync(row => row.Key == CompanyDocumentImagePrintDefaultsService.Key(company.Id))).Value);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Capture_DeniedVisibilityDoesNotPersistDefaultsAndOuterRollbackRestoresEverything()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var company = await SeedAsync(db);
        var store = new CompanyDocumentImagePrintDefaultsService(db);
        await store.StageAsync(company.Id, Value(5), true);
        db.Acts.Add(new Act { Number = "ROLLBACK", SellerCompanyId = company.Id }); await db.SaveChangesAsync();
        var document = await db.Acts.AsNoTracking().SingleAsync();
        var service = new DocumentSellerService(db);
        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => service.ResolveAsync(document, company, true, default, _ => Task.FromResult(false)));
        Assert.Null(await db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await service.ResolveAsync(document, company, true, default, _ => Task.FromResult(true));
            Assert.Equal(Value(5), SellerLegalEntityComposer.ReadMetadata(document.SellerSnapshotJson)!.ImagePrintDefaults);
            await transaction.RollbackAsync();
        }
        Assert.Null(await db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync());
        Assert.Equal(Value(5), (await store.ReadAsync(company.Id)).Value);
    }

    [Fact]
    public async Task PersistedWinner_KeepsCapturedAbsenceAfterAProfileUpdate()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var company = await SeedAsync(db);
        var document = new Act { Number = "WINNER", SellerCompanyId = company.Id };
        db.Acts.Add(document); await db.SaveChangesAsync();
        var stale = await db.Acts.AsNoTracking().SingleAsync();
        var service = new DocumentSellerService(db);
        await service.ResolveAsync(document, company, true, default, _ => Task.FromResult(true));
        var winner = document.SellerSnapshotJson;
        Assert.True(SellerLegalEntityComposer.ReadMetadata(winner)!.ImagePrintDefaultsFrozen);
        Assert.Null(SellerLegalEntityComposer.ReadMetadata(winner)!.ImagePrintDefaults);
        var store = new CompanyDocumentImagePrintDefaultsService(db);
        await store.StageAsync(company.Id, Value(7), true); await db.SaveChangesAsync();
        await service.ResolveAsync(stale, company, true, default, _ => Task.FromResult(true));
        Assert.Equal(winner, stale.SellerSnapshotJson);
        Assert.Equal(winner, await db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Null(SellerLegalEntityComposer.ReadMetadata(stale.SellerSnapshotJson)!.ImagePrintDefaults);
    }

    [Fact]
    public async Task Capture_RespectsCancellationWithoutSavingPendingProfile()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var company = await SeedAsync(db);
        company.ShortName = "Unsaved";
        var document = new Act { Number = "CANCELLED", SellerCompanyId = company.Id };
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocumentSellerService(db).ResolveAsync(document, company, true, cancellation.Token));
        Assert.Null(document.SellerSnapshotJson);
        Assert.Equal("Own company", await db.LegalEntities.AsNoTracking().Select(row => row.ShortName).SingleAsync());
        Assert.Equal(EntityState.Modified, db.Entry(company).State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Capture_ActUsesPersistedFinancialSideInsteadOfStaleNavigation(bool expense, bool onCreate)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var company = await SeedAsync(db);
        var payment = new Payment { Account = "SIDE", Type = expense ? PaymentType.Expense : PaymentType.Income, Amount = 100m };
        db.Payments.Add(payment);
        db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(company.Id), Value = "broken" });
        await db.SaveChangesAsync();
        var act = new Act { Number = "SIDE", Amount = 100m, InvoiceId = payment.Id, SellerCompanyId = company.Id,
            SellerSnapshotPolicy = onCreate ? DocumentSellerSnapshotPolicy.OnCreate : DocumentSellerSnapshotPolicy.FirstFile };
        if (!onCreate) { db.Acts.Add(act); await db.SaveChangesAsync(); act = await db.Acts.AsNoTracking().SingleAsync(); }
        act.Invoice = new Payment { Id = payment.Id, Type = expense ? PaymentType.Income : PaymentType.Expense };
        var service = new DocumentSellerService(db);
        await using var transaction = onCreate ? await db.Database.BeginTransactionAsync() : null;
        Task Capture() => onCreate ? service.InitializeForCreationAsync(act, company, default)
            : service.ResolveAsync(act, company, true, default, _ => Task.FromResult(true));
        await Assert.ThrowsAsync<SellerSnapshotConflictException>(Capture);
        Assert.Null(act.SellerSnapshotJson);
        // The caller explicitly reloads the observed side; the service does not retry or mutate it.
        act.Invoice = new Payment { Id = payment.Id, Type = expense ? PaymentType.Expense : PaymentType.Income };
        if (expense)
        {
            await Capture();
            var metadata = SellerLegalEntityComposer.ReadMetadata(act.SellerSnapshotJson)!;
            Assert.True(metadata.ImagePrintDefaultsFrozen);
            Assert.Null(metadata.ImagePrintDefaults);
            Assert.Equal(company.ShortName, metadata.CompanyName);
            Assert.Equal(act.SellerSnapshotPolicy, metadata.FreezeReason);
            if (onCreate) { act.Invoice = null; db.Acts.Add(act); await db.SaveChangesAsync(); await transaction!.CommitAsync(); }
            Assert.Equal(act.SellerSnapshotJson, await db.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
        }
        else
        {
            await Assert.ThrowsAsync<SellerSelectionException>(Capture);
            Assert.Null(act.SellerSnapshotJson);
            if (onCreate) await transaction!.RollbackAsync();
            Assert.False(await db.Acts.AsNoTracking().AnyAsync(row => row.SellerSnapshotJson != null));
        }
        Assert.Equal(expense ? PaymentType.Expense : PaymentType.Income, await db.Payments.AsNoTracking().Select(row => row.Type).SingleAsync());
        Assert.Equal(100m, await db.Payments.AsNoTracking().Select(row => row.Amount).SingleAsync());
        Assert.Empty(await db.TaskJournalItems.ToArrayAsync());
        Assert.Empty(await db.MoneyOperations.ToArrayAsync());
    }

    private static IDocumentSeller Document(string kind) => kind switch
    {
        "invoice" => new Payment { Type = PaymentType.Income },
        "act" => new Act { Number = "CAPTURE" },
        _ => new Contract { Number = "CAPTURE" },
    };

    private static async Task<LegalEntity> SeedAsync(PaymentContext db)
    {
        var company = new LegalEntity { ShortName = "Own company", ProfileStatus = CompanyProfileStatuses.Active };
        db.LegalEntities.Add(company); await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        await db.SaveChangesAsync();
        return company;
    }
}
