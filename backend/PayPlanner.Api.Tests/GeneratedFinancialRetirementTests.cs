using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialRetirementRequiresOwnedAttemptButNotPermissionToCreateAnotherInvoice(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var accepted = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(accepted.Receipt);
        var other = new User
        {
            Email = "financial-retirement-other@example.local",
            FullName = "Другой сотрудник",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            Role = new Role
            {
                Name = "financial-retirement-other",
                Permissions = new[] { RolePermissionCatalog.Cases, RolePermissionCatalog.Accounts }.Select(section => new RolePermission
                { Section = section, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.All }).ToList()
            }
        };
        f.Db.Users.Add(other); await f.Db.SaveChangesAsync();
        var service = new CaseDocumentUploadRetirementService(f.Db);
        Assert.Equal("unknown", await service.RetireAsync(f.Case.Id, other.Id, receipt.Proof.UploadKey, default));
        Assert.Empty(await f.Db.CaseDocumentUploadRetirements.ToArrayAsync());
        var permission = await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Accounts);
        permission.CanCreate = false; await f.Db.SaveChangesAsync();
        Assert.Equal("retired", await service.RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        Assert.Single(await f.Db.CaseDocumentUploadRetirements.ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FinancialRetirementRejectsRevokedCaseEditOrInactiveAuthor(bool postgres, bool inactive)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var accepted = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(accepted.Receipt);
        if (inactive) f.User.IsActive = false;
        else (await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Cases)).CanEdit = false;
        await f.Db.SaveChangesAsync();
        Assert.Equal("forbidden", await new CaseDocumentUploadRetirementService(f.Db)
            .RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        Assert.Empty(await f.Db.CaseDocumentUploadRetirements.ToArrayAsync());
        Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialRetirementStopsOwnedAcceptanceBeforeStorageAndPreservesEvidence(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var accepted = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, kind);
        Assert.Equal("recorded", accepted.Status);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(accepted.Receipt);
        var before = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        var bytes = (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes;
        Assert.Equal(receipt.Proof.UploadKey.ToString("N"), before.UploadKeyText);
        var service = new CaseDocumentUploadRetirementService(f.Db);
        Assert.Equal("retired", await service.RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        Assert.Equal("retired", await service.RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        var marker = Assert.Single(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        Assert.Null(marker.StorageObjectId); Assert.Equal(receipt.Proof.UploadKey, marker.UploadKey);
        Assert.Equal(f.User.Id, marker.ActorId); Assert.Equal(f.Case.Id, marker.CaseId);
        var after = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(before.FrozenDomainJson, after.FrozenDomainJson);
        Assert.Equal(before.FrozenDomainSha256, after.FrozenDomainSha256);
        Assert.Equal(bytes, (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes);
        Assert.Null(await new GeneratedFinancialDocumentRegistry(f.Db).ReadFrozenAsync(receipt, default));
        var replacement = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, kind);
        Assert.Equal("recorded", replacement.Status); Assert.NotEqual(receipt.Proof.UploadKey, replacement.Receipt!.Proof.UploadKey);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync());
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialRetirementRejectsUnknownCorruptAndHistoricalPublishedAcceptance(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var accepted = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(accepted.Receipt);
        var service = new CaseDocumentUploadRetirementService(f.Db);
        Assert.Equal("unknown", await service.RetireAsync(f.Case.Id, f.User.Id, Guid.NewGuid(), default));
        var header = await f.Db.GeneratedFinancialDocumentAttempts.SingleAsync();
        var originalSha = header.ContentSha256; header.ContentSha256 = new string('z', 64); await f.Db.SaveChangesAsync();
        Assert.Equal("unknown", await service.RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        header.ContentSha256 = originalSha; header.PublishedDocumentId = 999999; await f.Db.SaveChangesAsync();
        Assert.Equal("published", await service.RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        Assert.Empty(await f.Db.CaseDocumentUploadRetirements.ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialRetirementMigrationPreservesFrozenAcceptanceAndRefusesUnsafeDowngrade(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var accepted = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(accepted.Receipt);
        var before = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        var bytes = (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes;
        var migrations = (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray();
        var lifecycle = Array.FindIndex(migrations, m => m.Contains("AddGeneratedFinancialRegistryLifecycle", StringComparison.Ordinal));
        Assert.True(lifecycle > 0);
        var migrator = f.Db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[lifecycle - 1]);
        await migrator.MigrateAsync();
        f.Db.ChangeTracker.Clear();
        var upgraded = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(before.FrozenDomainJson, upgraded.FrozenDomainJson);
        Assert.Equal(before.Id, upgraded.Id); Assert.Equal(receipt.Proof.UploadKey.ToString("N"), upgraded.UploadKeyText);
        Assert.Equal(bytes, (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes);
        Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db)
            .RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(migrations[lifecycle - 1]));
        Assert.Contains(migrations[lifecycle], await f.Db.Database.GetAppliedMigrationsAsync());
        await migrator.MigrateAsync();
        Assert.Null((await f.Db.CaseDocumentUploadRetirements.AsNoTracking().SingleAsync()).StorageObjectId);
        Assert.Equal(before.FrozenDomainJson, (await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync()).FrozenDomainJson);
        Assert.Empty(await f.Db.Database.GetPendingMigrationsAsync());
        Assert.False(f.Db.Database.HasPendingModelChanges());
    }
}
