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
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialRecoveryRestartsAcceptedSourceWithoutRenderingOrReallocating(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, true);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        var before = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        var bytes = (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes;
        var order = await f.Db.ClientCaseWorkOrders.SingleAsync(); order.Title = "Изменён после принятия";
        f.User.FullName = "Изменённое имя автора"; await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        f.FinancialStorageOptions.WorkerEnabled = true;
        await using (var restarted = f.Observer())
        {
            Assert.Equal(1, await f.FinancialRecovery(restarted).ProcessBatchAsync(default));
            Assert.Equal(0, await f.FinancialRecovery(restarted).ProcessBatchAsync(default));
        }
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        var accepted = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(before.Id, accepted.Id); Assert.Equal(before.EntityId, accepted.EntityId);
        Assert.Equal(before.InvoiceEntityId, accepted.InvoiceEntityId); Assert.Equal(before.FrozenDomainJson, accepted.FrozenDomainJson);
        Assert.Equal(1, accepted.RecoveryAttemptCount); Assert.Null(accepted.RecoveryLeaseToken);
        Assert.Equal("pending", accepted.RecoveryLastStatus);
        await f.CopyPendingAsync(); f.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 2 : 1, await FinancialRowsAsync(f.Db));
        var document = await f.Db.ClientCaseDocuments.SingleAsync(); Assert.Equal(before.ActorName, document.UploadedByName);
        var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id,
            document.OriginalFileName, document.ContentType, default);
        Assert.NotNull(read.File); await using var content = read.File;
        using var copy = new MemoryStream(); await content.Content.CopyToAsync(copy); Assert.Equal(bytes, copy.ToArray());
        Assert.Equal(document.Id, (await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync()).PublishedDocumentId);
        Assert.Equal(0, await f.FinancialRecovery().ProcessBatchAsync(default));
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, "disabled")]
    [InlineData(true, "disabled")]
    [InlineData(false, "retired")]
    [InlineData(true, "retired")]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    [InlineData(false, "corrupt")]
    [InlineData(true, "corrupt")]
    public async Task FinancialRecoveryRejectsDisabledRetiredRevokedAndDamagedSourcesBeforeProviderIO(bool postgres, string scenario)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, true);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var result = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        var receipt = result.Receipt!; f.FinancialStorageOptions.WorkerEnabled = true;
        if (scenario == "disabled") f.FinancialStorageOptions.Enabled = false;
        else if (scenario == "retired") Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db)
            .RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        else if (scenario == "permission")
        {
            (await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Accounts)).CanCreate = false;
            await f.Db.SaveChangesAsync();
        }
        else
        {
            (await f.Db.GeneratedFinancialDocumentContents.SingleAsync()).Bytes = [0]; await f.Db.SaveChangesAsync();
        }
        f.Db.ChangeTracker.Clear();
        Assert.Equal(scenario is "disabled" or "retired" ? 0 : 1, await f.FinancialRecovery().ProcessBatchAsync(default));
        Assert.Equal(0, await f.FinancialRecovery().ProcessBatchAsync(default));
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync()); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        Assert.Equal(0, f.Local.ReadCount); Assert.Equal(0, f.Mirror.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialRecoveryConcurrentWorkersClaimOneLeaseBeforeLocalWrite(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, true);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        Assert.Equal("recorded", (await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice)).Status);
        f.FinancialStorageOptions.WorkerEnabled = true; f.PrepareIdentityObservers();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Local.BeforePutAsync = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var first = f.IdentityObserver(); await using var second = f.IdentityObserver();
        var firstRun = f.FinancialRecovery(first).ProcessBatchAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal(0, await f.FinancialRecovery(second).ProcessBatchAsync(default));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await firstRun);
        await using var proof = f.IdentityObserver();
        var header = await proof.GeneratedFinancialDocumentAttempts.SingleAsync();
        Assert.Equal(1, header.RecoveryAttemptCount); Assert.Null(header.RecoveryLeaseToken);
        Assert.Single(await proof.StorageObjects.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(proof));
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialRecoveryExpiredLeaseCannotPublishEvenWithOneRequiredCopy(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 1, true);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        Assert.Equal("recorded", (await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice)).Status);
        f.FinancialStorageOptions.WorkerEnabled = true; f.FinancialStorageOptions.WorkerLeaseSeconds = 15;
        var clock = new AdvancingClock();
        f.Local.BeforePutAsync = _ => { clock.Advance(TimeSpan.FromMinutes(5)); return Task.CompletedTask; };
        Assert.Equal(1, await f.FinancialRecovery(clock: clock).ProcessBatchAsync(default));
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Single(await f.Db.StorageObjects.ToArrayAsync()); Assert.Equal(1, f.Local.PutCount);
        Assert.Null((await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync()).RecoveryLeaseToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialRecoveryMigrationPreservesAcceptanceAndRefusesDroppingActiveLease(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var receipt = (await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice)).Receipt!;
        var before = await f.Db.GeneratedFinancialDocumentAttempts.SingleAsync();
        var json = before.FrozenDomainJson; var bytes = (await f.Db.GeneratedFinancialDocumentContents.SingleAsync()).Bytes;
        var migrations = (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray();
        var recovery = Array.FindIndex(migrations, m => m.Contains("AddGeneratedFinancialAttemptRecovery", StringComparison.Ordinal));
        Assert.True(recovery > 0);
        before.RecoveryLeaseToken = Guid.NewGuid();
        before.RecoveryLeaseExpiresAt = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(10), DateTimeKind.Unspecified);
        await f.Db.SaveChangesAsync();
        var migrator = f.Db.GetService<IMigrator>();
        await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(migrations[recovery - 1]));
        Assert.Contains(migrations[recovery], await f.Db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(before.RecoveryLeaseToken, (await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync()).RecoveryLeaseToken);
        before.RecoveryLeaseToken = null; before.RecoveryLeaseExpiresAt = null; await f.Db.SaveChangesAsync();
        await migrator.MigrateAsync(migrations[recovery - 1]); await migrator.MigrateAsync(); f.Db.ChangeTracker.Clear();
        var after = await f.Db.GeneratedFinancialDocumentAttempts.SingleAsync();
        Assert.Equal(receipt.Proof.AttemptId, after.Id); Assert.Equal(json, after.FrozenDomainJson);
        Assert.Equal(bytes, (await f.Db.GeneratedFinancialDocumentContents.SingleAsync()).Bytes);
        Assert.Equal(0, after.RecoveryAttemptCount); Assert.Null(after.RecoveryLeaseToken);
        Assert.Empty(await f.Db.Database.GetPendingMigrationsAsync()); Assert.False(f.Db.Database.HasPendingModelChanges());
    }
}
