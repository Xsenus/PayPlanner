using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Documents;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CompanyDocumentImagePrintDefaultsTests
{
    public static IEnumerable<object[]> Masks() => Enumerable.Range(0, 8).Select(mask => new object[] { mask });
    internal static CompanyDocumentImagePrintDefaults Value(int mask) => new(
        new((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0), new(false, true, false), new(true, false, true));

    [Theory]
    [MemberData(nameof(Masks))]
    public async Task Store_RoundTripsIndependentDefaultsAndKeepsOtherCompanyIsolated(int mask)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var service = new CompanyDocumentImagePrintDefaultsService(db);
        await service.StageAsync(7, Value(mask), true);
        await service.StageAsync(8, Value(mask ^ 7), true);
        Assert.Empty(await db.SystemSettings.AsNoTracking().ToListAsync());
        await db.SaveChangesAsync();
        Assert.Equal(Value(mask), (await service.ReadAsync(7)).Value);
        Assert.Equal(Value(mask ^ 7), (await service.ReadAsync(8)).Value);
        Assert.False((await service.ReadAsync(7)).Invalid);
    }

    [Fact]
    public async Task LegacyOmissionAndChangedFalse_DoNotOverwriteOrFlushPendingChanges()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var service = new CompanyDocumentImagePrintDefaultsService(db);
        await service.StageAsync(7, Value(3), true); await db.SaveChangesAsync();
        var before = await db.SystemSettings.AsNoTracking().SingleAsync();
        var pending = new LegalEntity { ShortName = "Pending unrelated profile" }; db.Add(pending);
        await service.StageAsync(7, null, null);
        await service.StageAsync(7, Value(6), null);
        await service.StageAsync(7, Value(6), false);
        var after = await db.SystemSettings.AsNoTracking().SingleAsync();
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Empty(await db.LegalEntities.AsNoTracking().ToListAsync());
        Assert.Equal(EntityState.Added, db.Entry(pending).State);
    }

    [Fact]
    public async Task Stage_CoalescesWritesAndSupportsResetThenSetBeforeSave()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var service = new CompanyDocumentImagePrintDefaultsService(db);
        await service.StageAsync(7, Value(1), true); await service.StageAsync(7, Value(2), true); await db.SaveChangesAsync();
        Assert.Single(await db.SystemSettings.ToListAsync());
        Assert.Equal(Value(2), (await service.ReadAsync(7)).Value);
        await service.StageAsync(7, Value(3), true);
        await service.StageAsync(7, null, true);
        await service.StageAsync(7, Value(3), true); await db.SaveChangesAsync();
        Assert.Equal(Value(3), (await service.ReadAsync(7)).Value);
        await service.StageAsync(7, new(null, null, null), true); await db.SaveChangesAsync();
        Assert.Null((await service.ReadAsync(7)).Value);
        Assert.False((await service.ReadAsync(7)).Invalid);
    }

    [Fact]
    public async Task EqualValue_DoesNotWriteAndMalformedValueCanBeRepairedOrReset()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(7), Value = "broken" });
        await db.SaveChangesAsync(); var service = new CompanyDocumentImagePrintDefaultsService(db);
        Assert.True((await service.ReadAsync(7)).Invalid);
        await service.StageAsync(7, Value(0), true); await db.SaveChangesAsync();
        Assert.False((await service.ReadAsync(7)).Invalid);
        await service.StageAsync(7, Value(0), true);
        Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        await service.StageAsync(7, null, true); await db.SaveChangesAsync();
        Assert.Empty(await db.SystemSettings.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"invoice\":null,\"act\":null,\"contract\":null}")]
    [InlineData("{\"version\":\"1\",\"invoice\":null,\"act\":null,\"contract\":null}")]
    [InlineData("{\"version\":1,\"invoice\":{},\"act\":null,\"contract\":null}")]
    [InlineData("{\"version\":1,\"invoice\":{\"includeLogo\":false,\"includeSeal\":\"false\",\"includeSignature\":false},\"act\":null,\"contract\":null}")]
    [InlineData("{\"version\":1,\"invoice\":null,\"act\":null}")]
    public void InvalidStorage_IsNotSilentlyTreatedAsLegacyDefaults(string json)
    {
        var result = CompanyDocumentImagePrintDefaultsService.Parse(json);
        Assert.True(result.Invalid); Assert.Null(result.Value);
    }

    [Fact]
    public void Codec_PreservesNullKindsAndEnforcesBoundedPayload()
    {
        var value = new CompanyDocumentImagePrintDefaults(new(false, false, false), null, null);
        var json = CompanyDocumentImagePrintDefaultsService.Serialize(value);
        Assert.Equal(value, CompanyDocumentImagePrintDefaultsService.Parse(json).Value);
        Assert.False(CompanyDocumentImagePrintDefaultsService.Parse(json.PadRight(CompanyDocumentImagePrintDefaultsService.MaximumLength)).Invalid);
        Assert.True(CompanyDocumentImagePrintDefaultsService.Parse(json.PadRight(CompanyDocumentImagePrintDefaultsService.MaximumLength + 1)).Invalid);
        Assert.False(CompanyDocumentImagePrintDefaultsService.Parse(null).Invalid);
        Assert.DoesNotContain("IsEmpty", JsonSerializer.Serialize(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompanyDocumentImagePrintDefaultsService.Key(0));
    }

    [Fact]
    public async Task Stage_RespectsCancellationAndDoesNotIntroduceMappedColumns()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var service = new CompanyDocumentImagePrintDefaultsService(db);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StageAsync(7, Value(1), true, cancellation.Token));
        Assert.Null(db.Model.FindEntityType(typeof(LegalEntity))!.FindProperty("DocumentImagePrintDefaults"));
        Assert.Empty(await db.SystemSettings.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("legalEntities.documentImagePrintDefaults.7")]
    [InlineData("  LEGALENTITIES.DOCUMENTIMAGEPRINTDEFAULTS.7  ")]
    public async Task Import_PreservesTargetCompanyDefaultsAndSkipsSourceLocalKeys(string sourceKey)
    {
        await using var source = await SqliteTestContextFactory.CreateAsync();
        await using var target = await SqliteTestContextFactory.CreateAsync();
        source.SystemSettings.Add(new SystemSetting { Key = sourceKey, Value = "source-local profile" });
        source.SystemSettings.Add(new SystemSetting { Key = "ordinary", Value = "ordinary source value" });
        target.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(7), Value = "target profile" });
        await source.SaveChangesAsync(); await target.SaveChangesAsync();
        var table = new DatabaseImportTableResultDto { Table = "SystemSettings" };
        var service = new DatabaseImportService(null!, new(DatabaseProviderKind.Sqlite, "Data Source=:memory:"), NullLogger<DatabaseImportService>.Instance);
        var method = typeof(DatabaseImportService).GetMethod("ImportEntitiesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(typeof(SystemSetting));
        static string Key(SystemSetting setting) => setting.Key.Trim().ToUpperInvariant();
        object[] arguments = [source, target, table,
            (Func<PaymentContext, IQueryable<SystemSetting>>)(context => context.SystemSettings.AsNoTracking()),
            (Func<PaymentContext, IQueryable<SystemSetting>>)(context => context.SystemSettings),
            (Func<SystemSetting, int>)(row => row.Id), (Func<SystemSetting, int>)(row => row.Id),
            (Func<SystemSetting, string>)Key, (Func<SystemSetting, string>)Key,
            (Action<SystemSetting, SystemSetting>)((from, to) => { to.Key = from.Key; to.Value = from.Value; }),
            new Dictionary<int, int>(), CancellationToken.None, null!, CompanyDocumentImagePrintDefaultsService.KeyPrefix.ToUpperInvariant()];
        await Assert.IsAssignableFrom<Task>(method.Invoke(service, arguments));
        Assert.Equal(1, table.Skipped); Assert.Equal(1, table.Inserted);
        Assert.Equal("target profile", (await target.SystemSettings.AsNoTracking().SingleAsync(row => row.Key == CompanyDocumentImagePrintDefaultsService.Key(7))).Value);
        Assert.Equal("ordinary source value", (await target.SystemSettings.AsNoTracking().SingleAsync(row => row.Key == "ordinary")).Value);
        Assert.Equal(2, await target.SystemSettings.CountAsync());
    }
}
