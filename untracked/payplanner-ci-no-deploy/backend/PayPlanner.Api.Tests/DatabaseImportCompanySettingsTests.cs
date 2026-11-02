using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseImportCompanySettingsTests(PostgreSqlModelTemplate modelTemplate) : IClassFixture<PostgreSqlModelTemplate>
{
    private static readonly string[] ProtectedKeys = ["legalEntities.mainCompanyId", "legalEntities.mainCompanyIds",
        "legalEntities.defaultMainCompanyId", DocumentSellerSnapshotPolicy.SettingKey];
    private const string OrdinaryUpdatedKey = "ui.synthetic.import.updated";
    private const string OrdinaryInsertedKey = "ui.synthetic.import.inserted";

    [Theory]
    [InlineData(false, true, DocumentSellerSnapshotPolicy.FirstFile, DocumentSellerSnapshotPolicy.OnCreate, false)]
    [InlineData(false, true, DocumentSellerSnapshotPolicy.OnCreate, DocumentSellerSnapshotPolicy.FirstFile, false)]
    [InlineData(false, false, DocumentSellerSnapshotPolicy.FirstFile, null, false)]
    [InlineData(false, false, DocumentSellerSnapshotPolicy.OnCreate, null, false)]
    [InlineData(true, true, DocumentSellerSnapshotPolicy.FirstFile, DocumentSellerSnapshotPolicy.OnCreate, false)]
    [InlineData(true, true, DocumentSellerSnapshotPolicy.OnCreate, DocumentSellerSnapshotPolicy.FirstFile, false)]
    [InlineData(true, false, DocumentSellerSnapshotPolicy.FirstFile, null, false)]
    [InlineData(true, false, DocumentSellerSnapshotPolicy.OnCreate, null, false)]
    [InlineData(false, true, DocumentSellerSnapshotPolicy.FirstFile, DocumentSellerSnapshotPolicy.OnCreate, true)]
    [InlineData(true, true, DocumentSellerSnapshotPolicy.FirstFile, DocumentSellerSnapshotPolicy.OnCreate, true)]
    public async Task SeparateSqliteMerge_PreservesTargetCompanyGlobalsAndHistoryWithoutAssigningImportedCompany(
        bool postgres, bool targetHasSettings, string sourcePolicy, string? targetPolicy, bool normalizeSourceKeys)
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-import-company-settings-{Guid.NewGuid():N}.sqlite");
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(postgres, modelTemplate);
        PaymentContext Open() => postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(fixture.Db.Database.GetConnectionString()).Options, new HttpContextAccessor())
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite((SqliteConnection)fixture.Db.Database.GetDbConnection()).Options, new HttpContextAccessor());
        try
        {
            var sourceCompanyId = await SeedSourceAsync(path, sourcePolicy, normalizeSourceKeys);
            var originalSource = await File.ReadAllBytesAsync(path);
            int targetCompanyId;
            await using (var setup = Open())
            {
                Assert.Empty(await setup.SystemSettings.Where(row => ProtectedKeys.Contains(row.Key)).ToArrayAsync());
                setup.LegalEntities.Add(new LegalEntity { ShortName = "Target padding", Inn = "1111111111" });
                await setup.SaveChangesAsync();
                var own = new LegalEntity { ShortName = "Target own company", Inn = "2222222222" };
                setup.LegalEntities.Add(own);
                await setup.SaveChangesAsync();
                targetCompanyId = own.Id;
                Assert.NotEqual(sourceCompanyId, targetCompanyId);
                if (targetHasSettings)
                {
                    var values = new[] { own.Id.ToString(), JsonSerializer.Serialize(new[] { own.Id }), own.Id.ToString(), targetPolicy! };
                    setup.SystemSettings.AddRange(ProtectedKeys.Select((key, index) => new SystemSetting
                    {
                        Key = key,
                        Value = values[index],
                        CreatedAt = new DateTime(2026, 1, 2),
                        UpdatedAt = new DateTime(2026, 2, 3)
                    }));
                }
                setup.SystemSettings.Add(new SystemSetting { Key = OrdinaryUpdatedKey, Value = "target ordinary value" });
                var snapshot = JsonSerializer.Serialize(new { Id = own.Id, ShortName = "Historical target seller", ImagesFrozen = true, FrozenAtUtc = (DateTime?)null });
                setup.Payments.Add(new Payment
                {
                    Account = "TARGET-HISTORY-PAYMENT",
                    Description = "Existing target history",
                    Amount = 123.45m,
                    PaidAmount = 23.45m,
                    Date = new DateTime(2026, 3, 4),
                    Type = PaymentType.Income,
                    DocumentKind = DocumentKind.Payment,
                    Status = PaymentStatus.Pending,
                    SellerCompanyId = own.Id,
                    SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.FirstFile,
                    SellerSnapshotJson = snapshot
                });
                setup.Acts.Add(new Act
                {
                    Number = "TARGET-HISTORY-ACT",
                    Date = new DateTime(2026, 3, 4),
                    Amount = 123.45m,
                    SellerCompanyId = own.Id,
                    SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.OnCreate,
                    SellerSnapshotJson = snapshot
                });
                setup.Contracts.Add(new Contract
                {
                    Number = "TARGET-HISTORY-CONTRACT",
                    Date = new DateTime(2026, 3, 4),
                    StartDate = new DateTime(2026, 3, 4),
                    Amount = 123.45m,
                    IsOpenEnded = true,
                    SellerCompanyId = own.Id,
                    SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.FirstFile,
                    SellerSnapshotJson = snapshot
                });
                await setup.SaveChangesAsync();
            }

            string protectedBefore;
            string historyBefore;
            await using (var before = Open())
            {
                protectedBefore = await ProtectedSettingsAsync(before);
                historyBefore = await HistoricalScalarsAsync(before);
            }
            var factory = new ImportFactory(Open);
            var provider = new DatabaseProviderInfo(postgres ? DatabaseProviderKind.PostgreSql : DatabaseProviderKind.Sqlite,
                fixture.Db.Database.GetConnectionString()!);
            var service = new DatabaseImportService(factory, provider, NullLogger<DatabaseImportService>.Instance);

            var result = await service.ImportSqliteAsync(path);

            Assert.True(result.Success);
            Assert.DoesNotContain(result.Tables, table => table.Errors != 0 || table.Conflicts != 0);
            var settings = Assert.Single(result.Tables, table => table.Table == "SystemSettings");
            Assert.Equal(6, settings.SourceRows);
            Assert.Equal(4, settings.Skipped);
            Assert.Equal(1, settings.Inserted);
            Assert.Equal(1, settings.Updated);
            await using var verify = Open();
            Assert.Equal(protectedBefore, await ProtectedSettingsAsync(verify));
            Assert.Equal(historyBefore, await HistoricalScalarsAsync(verify));
            var imported = await verify.LegalEntities.SingleAsync(company => company.ShortName == "Imported source own company");
            Assert.NotEqual(sourceCompanyId, imported.Id);
            Assert.NotEqual(targetCompanyId, imported.Id);
            var ownIds = await OwnCompanyRegistry.LoadIdsAsync(verify);
            Assert.DoesNotContain(imported.Id, ownIds);
            if (targetHasSettings)
            {
                Assert.Equal(new[] { targetCompanyId }, ownIds.OrderBy(id => id));
                Assert.Equal(targetCompanyId, (await OwnCompanyRegistry.LoadDefaultAsync(verify))?.Id);
            }
            else
            {
                Assert.Empty(ownIds);
                Assert.Null(await OwnCompanyRegistry.LoadDefaultAsync(verify));
                Assert.Empty(await verify.SystemSettings.Where(row => ProtectedKeys.Contains(row.Key)).ToArrayAsync());
            }
            Assert.Equal(targetPolicy ?? DocumentSellerSnapshotPolicy.FirstFile,
                await new DocumentSellerService(verify).ReadCreationPolicyAsync(CancellationToken.None));
            Assert.Equal("source ordinary value", (await verify.SystemSettings.SingleAsync(row => row.Key == OrdinaryUpdatedKey)).Value);
            Assert.Equal("new ordinary value", (await verify.SystemSettings.SingleAsync(row => row.Key == OrdinaryInsertedKey)).Value);
            Assert.Empty(await verify.CompanyBranches.ToArrayAsync());
            Assert.Empty(await verify.CompanyBrandImageVersions.ToArrayAsync());
            Assert.Equal(originalSource, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static async Task<int> SeedSourceAsync(string path, string policy, bool normalizeKeys)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var source = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await source.Database.EnsureCreatedAsync();
        var company = new LegalEntity { ShortName = "Imported source own company", Inn = "3333333333" };
        source.LegalEntities.Add(company);
        await source.SaveChangesAsync();
        var values = new[] { company.Id.ToString(), JsonSerializer.Serialize(new[] { company.Id }), company.Id.ToString(), policy };
        source.SystemSettings.AddRange(ProtectedKeys.Select((key, index) => new SystemSetting
        {
            Key = normalizeKeys ? $"\t{key.ToUpperInvariant()}\r\n" : key,
            Value = values[index]
        }));
        source.SystemSettings.AddRange(new SystemSetting { Key = OrdinaryUpdatedKey, Value = "source ordinary value" },
            new SystemSetting { Key = OrdinaryInsertedKey, Value = "new ordinary value" });
        await source.SaveChangesAsync();
        return company.Id;
    }

    private static async Task<string> ProtectedSettingsAsync(PaymentContext db)
        => JsonSerializer.Serialize((await db.SystemSettings.Where(row => ProtectedKeys.Contains(row.Key))
            .AsNoTracking().OrderBy(row => row.Key).ToArrayAsync()).Select(row => ScalarState(db, row)));

    private static async Task<string> HistoricalScalarsAsync(PaymentContext db)
        => JsonSerializer.Serialize(new[] { ScalarState(db, await db.Payments.IgnoreQueryFilters().SingleAsync()),
            ScalarState(db, await db.Acts.IgnoreQueryFilters().SingleAsync()),
            ScalarState(db, await db.Contracts.IgnoreQueryFilters().SingleAsync()) });

    private static string ScalarState(PaymentContext db, object entity)
        => JsonSerializer.Serialize(db.Entry(entity).Properties.OrderBy(property => property.Metadata.Name, StringComparer.Ordinal)
            .ToDictionary(property => property.Metadata.Name, property => property.CurrentValue, StringComparer.Ordinal));

    private sealed class ImportFactory(Func<PaymentContext> create) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext() => create();
        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
