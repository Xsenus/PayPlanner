using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class DocumentSellerSnapshotPolicyTests : IClassFixture<PostgreSqlModelTemplate>
{
    private readonly PostgreSqlModelTemplate template;
    public DocumentSellerSnapshotPolicyTests(PostgreSqlModelTemplate template) => this.template = template;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageFreeze_DoesNotFlushOrDiscardForeignPendingEntries(bool postgres)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        var changed = new Client { Name = "Persisted before change" };
        var deleted = new Client { Name = "Persisted before delete" };
        db.Clients.AddRange(changed, deleted);
        await db.SaveChangesAsync();
        changed.Name = "Pending change";
        db.Clients.Remove(deleted);
        var added = new Client { Name = "Pending addition" };
        db.Clients.Add(added);
        var document = new Act { Number = "NEW-SNAPSHOT" };

        await new DocumentSellerService(db).ResolveAsync(document, company, freeze: true, default);

        Assert.Equal(EntityState.Added, db.Entry(added).State);
        Assert.Equal(EntityState.Modified, db.Entry(changed).State);
        Assert.Equal(EntityState.Deleted, db.Entry(deleted).State);
        Assert.Equal("Persisted before change", await db.Clients.AsNoTracking().Where(x => x.Id == changed.Id).Select(x => x.Name).SingleAsync());
        Assert.True(await db.Clients.AsNoTracking().AnyAsync(x => x.Id == deleted.Id));
        Assert.False(await db.Clients.AsNoTracking().AnyAsync(x => x.Name == "Pending addition"));
        Assert.Equal(3, await db.CompanyBrandImageVersions.CountAsync());
        Assert.DoesNotContain("data:image", document.SellerSnapshotJson);
        var metadata = Assert.IsType<SellerSnapshotMetadata>(SellerLegalEntityComposer.ReadMetadata(document.SellerSnapshotJson));
        Assert.False(metadata.LegacyImages);
        Assert.NotNull(metadata.FrozenAtUtc);
        Assert.Equal(DateTimeKind.Utc, metadata.FrozenAtUtc!.Value.Kind);
        Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, metadata.FreezeReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedSnapshot_RollsBackNewImageVersionsWithoutSavingPendingProfile(bool postgres)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        SellerSnapshotBoundaryTestData.MakeOversizedProfile(company);
        await db.SaveChangesAsync();
        company.Phone = "Pending phone change";
        var document = new Act { Number = "OVERSIZED-SNAPSHOT" };

        await Assert.ThrowsAsync<SellerSnapshotTooLargeException>(() =>
            new DocumentSellerService(db).ResolveAsync(document, company, freeze: true, default));

        Assert.Null(document.SellerSnapshotJson);
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
        Assert.Equal(EntityState.Modified, db.Entry(company).State);
        Assert.Null(await db.LegalEntities.AsNoTracking().Where(x => x.Id == company.Id).Select(x => x.Phone).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationPolicy_IsCapturedBeforeLaterGlobalSettingChanges(bool postgres)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        var service = new DocumentSellerService(db);
        var document = new Act { Number = "EARLIER-DRAFT" };
        await service.InitializeForCreationAsync(document, company, default);
        Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, document.SellerSnapshotPolicy);
        Assert.Null(document.SellerSnapshotJson);
        db.SystemSettings.Add(new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.OnCreate });
        await db.SaveChangesAsync();

        await service.InitializeForCreationAsync(document, company, default);
        Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, document.SellerSnapshotPolicy);
        Assert.Null(document.SellerSnapshotJson);
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
        await service.ResolveAsync(document, company, freeze: true, default);
        Assert.Equal(DocumentSellerSnapshotPolicy.FirstFile, SellerLegalEntityComposer.ReadMetadata(document.SellerSnapshotJson)!.FreezeReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnCreate_RequiresTransactionAndRollsBackDocumentWithImageVersions(bool postgres)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        db.SystemSettings.Add(new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.OnCreate });
        await db.SaveChangesAsync();
        var document = new Act { Number = "AT-CREATION", SellerCompanyId = company.Id };
        var service = new DocumentSellerService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeForCreationAsync(document, company, default));
        Assert.Null(document.SellerSnapshotJson);
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Acts.Add(document);
            await service.InitializeForCreationAsync(document, company, default);
            Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, SellerLegalEntityComposer.ReadMetadata(document.SellerSnapshotJson)!.FreezeReason);
            await db.SaveChangesAsync();
            Assert.Equal(3, await db.CompanyBrandImageVersions.CountAsync());
            await transaction.RollbackAsync();
        }

        Assert.False(await db.Acts.AsNoTracking().AnyAsync(x => x.Number == "AT-CREATION"));
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingLegacySnapshot_IsNotRewrittenOrGivenAnInventedDate(bool postgres)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        var json = SellerLegalEntityComposer.ToSnapshotJson(SellerLegalEntityComposer.Compose(company, null));
        var document = new Act { Number = "LEGACY", SellerCompanyId = company.Id, SellerSnapshotJson = json };
        db.SystemSettings.Add(new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.OnCreate });
        await db.SaveChangesAsync();

        await new DocumentSellerService(db).InitializeForCreationAsync(document, company, default);

        Assert.Equal(json, document.SellerSnapshotJson);
        Assert.Null(document.SellerSnapshotPolicy);
        var metadata = SellerLegalEntityComposer.ReadMetadata(document.SellerSnapshotJson)!;
        Assert.True(metadata.LegacyImages);
        Assert.Null(metadata.FrozenAtUtc);
        Assert.Null(metadata.FreezeReason);
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDocument_RereadsPersistedWinnerInsteadOfFreezingStaleSelection(bool postgres)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        var document = new Act { Number = "FIRST-WINNER", SellerCompanyId = company.Id };
        db.Acts.Add(document);
        await db.SaveChangesAsync();
        var stale = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == document.Id);
        var service = new DocumentSellerService(db);
        await service.ResolveAsync(document, company, true, default, _ => Task.FromResult(true));
        var winner = document.SellerSnapshotJson;
        company.ShortName = "Later company name";
        company.SealDataUrl = "data:image/png;base64,TEFURVI=";
        await db.SaveChangesAsync();
        stale.SellerCompanyId = null;
        stale.SellerSnapshotJson = null;

        var seller = await service.ResolveAsync(stale, company, true, default, _ => Task.FromResult(true));

        Assert.Equal("Своя", seller!.ShortName);
        Assert.Equal(winner, stale.SellerSnapshotJson);
        Assert.Equal(winner, await db.Acts.AsNoTracking().Where(act => act.Id == document.Id).Select(act => act.SellerSnapshotJson).SingleAsync());
        Assert.Equal(3, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceGroup_UsesOneWinnerAndRejectsSelectionAfterFreeze(bool postgres)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        var invoice = new Invoice { Number = "SHARED-SNAPSHOT", Date = new DateTime(2026, 10, 24) };
        var first = new Payment { Invoice = invoice, Account = invoice.Number, DocumentKind = DocumentKind.Invoice, Type = PaymentType.Income };
        var second = new Payment { Invoice = invoice, Account = invoice.Number, DocumentKind = DocumentKind.Invoice, Type = PaymentType.Income };
        db.Payments.AddRange(first, second);
        await db.SaveChangesAsync();
        var service = new DocumentSellerService(db);

        await service.ResolveAsync(first, company, true, default, _ => Task.FromResult(true));
        var snapshots = await db.Payments.AsNoTracking().Where(payment => payment.InvoiceId == invoice.Id)
            .Select(payment => payment.SellerSnapshotJson).ToArrayAsync();
        Assert.Equal(2, snapshots.Length);
        Assert.All(snapshots, snapshot => Assert.Equal(first.SellerSnapshotJson, snapshot));
        Assert.NotNull(first.SellerSnapshotJson);
        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() =>
            service.UpdateSelectionAsync(second, null, null, default, _ => Task.FromResult(true)));
        Assert.Equal(snapshots, await db.Payments.AsNoTracking().Where(payment => payment.InvoiceId == invoice.Id)
            .Select(payment => payment.SellerSnapshotJson).ToArrayAsync());
        Assert.Equal(3, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDocument_DirtyAdmissionPreservesForeignPendingEntries(bool postgres)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        var document = new Act { Number = "DIRTY-ADMISSION" };
        db.Acts.Add(document);
        await db.SaveChangesAsync();
        company.Phone = "Unsaved profile";
        var added = new Client { Name = "Unsaved client" };
        db.Clients.Add(added);

        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() =>
            new DocumentSellerService(db).ResolveAsync(document, company, true, default, _ => Task.FromResult(true)));

        Assert.Equal(EntityState.Added, db.Entry(added).State);
        Assert.Equal(EntityState.Modified, db.Entry(company).State);
        Assert.Equal("Unsaved profile", company.Phone);
        Assert.Null(document.SellerSnapshotJson);
        Assert.Null(await db.Acts.AsNoTracking().Where(act => act.Id == document.Id).Select(act => act.SellerSnapshotJson).SingleAsync());
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDocument_FreshVisibilityDenialDoesNotReadOrPersistCompanyImages(bool postgres)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        var document = new Act { Number = "VISIBILITY-CHANGED" };
        db.Acts.Add(document);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() =>
            new DocumentSellerService(db).ResolveAsync(document, company, true, default, _ => Task.FromResult(false)));

        Assert.Null(await db.Acts.AsNoTracking().Where(act => act.Id == document.Id).Select(act => act.SellerSnapshotJson).SingleAsync());
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingInvalidSnapshot_IsNeverReplacedByCurrentProfile(bool postgres)
    {
        var (db, lease) = await CreateAsync(postgres);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        const string invalid = "{old-incomplete-snapshot";
        var document = new Act { Number = "INVALID-LEGACY", SellerSnapshotJson = invalid };
        db.Acts.Add(document);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<SellerSelectionException>(() =>
            new DocumentSellerService(db).ResolveAsync(document, company, true, default, _ => Task.FromResult(true)));

        Assert.Equal(invalid, await db.Acts.AsNoTracking().Where(act => act.Id == document.Id).Select(act => act.SellerSnapshotJson).SingleAsync());
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostSaveFault_RollsBackSnapshotAndVersionsWithoutLeavingPendingWrites(bool postgres)
    {
        var fault = new SnapshotSaveFault();
        var (db, lease) = await CreateAsync(postgres, fault);
        await using var ownedLease = lease;
        await using var ownedDb = db;
        var company = await SeedAsync(db);
        var document = new Act { Number = "SNAPSHOT-ROLLBACK" };
        db.Acts.Add(document);
        await db.SaveChangesAsync();
        fault.Enabled = true;

        await Assert.ThrowsAsync<SyntheticSnapshotSaveException>(() =>
            new DocumentSellerService(db).ResolveAsync(document, company, true, default, _ => Task.FromResult(true)));

        Assert.True(fault.Triggered);
        Assert.Null(await db.Acts.AsNoTracking().Where(act => act.Id == document.Id).Select(act => act.SellerSnapshotJson).SingleAsync());
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private sealed class SyntheticSnapshotSaveException : Exception;
    private sealed class SnapshotSaveFault : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<Act>().Any(entry => !string.IsNullOrWhiteSpace(entry.Entity.SellerSnapshotJson)))
            {
                Triggered = true;
                throw new SyntheticSnapshotSaveException();
            }
            return ValueTask.FromResult(result);
        }
    }

    private async Task<(PaymentContext Db, IAsyncDisposable Lease)> CreateAsync(bool postgres, IInterceptor? interceptor = null)
    {
        if (postgres)
        {
            var lease = await template.CreateDatabaseAsync();
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return (new PostgreSqlPaymentContext(options.Options), lease);
        }
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var sqliteOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection);
        if (interceptor is not null) sqliteOptions.AddInterceptors(interceptor);
        var db = new PaymentContext(sqliteOptions.Options);
        await db.Database.EnsureCreatedAsync();
        return (db, connection);
    }

    private static async Task<LegalEntity> SeedAsync(PaymentContext db)
    {
        var company = new LegalEntity
        {
            ShortName = "Своя",
            Inn = "7707083893",
            ProfileStatus = CompanyProfileStatuses.Active,
            LogoDataUrl = "data:image/png;base64,TE9HTw==",
            SealDataUrl = "data:image/png;base64,U0VBTA==",
            DirectorSignatureDataUrl = "data:image/png;base64,U0lHTg==",
        };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        await db.SaveChangesAsync();
        return company;
    }
}
