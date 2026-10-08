using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
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
    public async Task FinancialQueueBackfillsAcceptedCommitAndPublishesExactlyOnce(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        var frozen = Assert.IsType<GeneratedFinancialFrozenContent>(await new GeneratedFinancialDocumentRegistry(f.Db).ReadFrozenAsync(receipt, default));
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status);
        Assert.Empty(await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument).ToArrayAsync());
        await f.CopyPendingAsync(); f.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        Assert.Equal(0, (await f.RunCourtQueueAsync()).Claimed);
        await using var observer = f.Observer();
        await AssertPublishedFinancialGraphAsync(observer, frozen.Domain);
        var document = await observer.ClientCaseDocuments.SingleAsync();
        Assert.Equal(document.Id, (await observer.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
        Assert.Single(await observer.StorageProductBindings.ToArrayAsync());
        Assert.Equal("Автор генерации", document.UploadedByName);
        Assert.Single(await observer.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        Assert.Single(await observer.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).ToArrayAsync());
        Assert.NotEmpty(await observer.CaseDocumentNotificationDispatches.ToArrayAsync());
        Assert.Equal("uploaded", (await f.PublishFinancial(receipt)).Status);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialQueueLateLeaseLossRollsBackAllRecordsAndFreshWorkerResumes(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        var clock = new AdvancingClock();
        var fence = new CourtRecordsSavedInterceptor(() => clock.Advance(TimeSpan.FromHours(2)));
        await using var f = await CreateFixtureAsync(postgres, 2, true, fence);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status);
        await f.CopyPendingAsync(); f.Db.ChangeTracker.Clear(); fence.Armed = true;
        var interrupted = await f.RunCourtQueueAsync(clock);
        Assert.Equal(1, interrupted.Claimed); Assert.Equal(0, interrupted.Succeeded); Assert.Equal(1, interrupted.Retried);
        Assert.True(fence.Triggered); f.Db.ChangeTracker.Clear();
        await AssertPendingFinancialQueueGraphAsync(f);
        var operation = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Equal(StorageOperationStatuses.Running, operation.Status); Assert.Equal(0, operation.AttemptCount);
        Assert.NotNull(operation.LeaseOwner); Assert.True(operation.LeaseExpiresAt <= clock.GetUtcNow().UtcDateTime);
        fence.Armed = false;
        Assert.Equal(1, (await f.RunCourtQueueAsync(clock)).Succeeded);
        Assert.Equal("uploaded", (await f.PublishFinancial(receipt)).Status);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialQueueDisabledAfterRecordsRollsBackAndPreservesOriginalAttempt(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        Fixture? owned = null;
        var fence = new CourtRecordsSavedInterceptor(() => owned!.SetManagedEnabled(false));
        await using var f = await CreateFixtureAsync(postgres, 2, true, fence); owned = f;
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status);
        await f.CopyPendingAsync(); f.Db.ChangeTracker.Clear(); fence.Armed = true;
        Assert.Equal(0, (await f.RunCourtQueueAsync()).Succeeded); Assert.True(fence.Triggered);
        f.Db.ChangeTracker.Clear(); await AssertPendingFinancialQueueGraphAsync(f);
        var operation = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Equal(StorageOperationStatuses.Pending, operation.Status); Assert.Equal(0, operation.AttemptCount);
        Assert.Contains("disabled", operation.LastError);
        fence.Armed = false; f.SetManagedEnabled(true);
        var clock = new AdvancingClock(); clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, (await f.RunCourtQueueAsync(clock)).Succeeded);
        Assert.Equal("uploaded", (await f.PublishFinancial(receipt)).Status);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialQueueActualIntentRetirementBlocksWorkerWithoutDiscardingCopies(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, true);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status); await f.CopyPendingAsync();
        Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db).RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        var reads = f.Local.ReadCount + f.Mirror.ReadCount;
        Assert.Equal(1, (await f.RunCourtQueueAsync()).DeadLettered);
        Assert.Equal(reads, f.Local.ReadCount + f.Mirror.ReadCount);
        await AssertPendingFinancialQueueGraphAsync(f);
        Assert.Equal(2, await f.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        Assert.Single(await f.Db.GeneratedFinancialDocumentContents.ToArrayAsync());
        Assert.Single(await f.Db.CaseDocumentUploadRetirements.ToArrayAsync());
        Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db).RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialQueuePublishedDocumentAloneCannotProveMissingFinancialWinner(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, true);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status); await f.CopyPendingAsync();
        Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        f.Db.ChangeTracker.Clear();
        switch (kind)
        {
            case GeneratedFinancialDocumentKind.Invoice: f.Db.Payments.Remove(await f.Db.Payments.SingleAsync()); break;
            case GeneratedFinancialDocumentKind.Act: f.Db.Acts.Remove(await f.Db.Acts.SingleAsync()); break;
            case GeneratedFinancialDocumentKind.Contract: f.Db.Contracts.Remove(await f.Db.Contracts.SingleAsync()); break;
        }
        await f.Db.SaveChangesAsync();
        var operation = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        operation.Status = StorageOperationStatuses.Pending; operation.NextAttemptAt = DateTime.UtcNow;
        operation.CompletedAt = null; await f.Db.SaveChangesAsync();
        var reads = f.Local.ReadCount + f.Mirror.ReadCount;
        Assert.Equal(1, (await f.RunCourtQueueAsync()).DeadLettered);
        Assert.Equal(reads, f.Local.ReadCount + f.Mirror.ReadCount);
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Equal("forbidden", (await f.PublishFinancial(receipt)).Status);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialQueueLostDomainCommitAckRechecksFinancialWinnerOnRetry(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        var fence = new FinancialCaptureCommitAckFailure();
        await using var f = await CreateFixtureAsync(postgres, 2, true, fence);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status); await f.CopyPendingAsync();
        fence.Armed = true;
        Assert.Equal(0, (await f.RunCourtQueueAsync()).Succeeded); Assert.True(fence.Triggered);
        fence.Armed = false; f.Db.ChangeTracker.Clear();
        await using var observer = f.Observer();
        var document = await observer.ClientCaseDocuments.SingleAsync();
        Assert.Equal(document.Id, (await observer.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
        var clock = new AdvancingClock(); clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, (await f.RunCourtQueueAsync(clock)).Succeeded);
        Assert.Equal(0, (await f.RunCourtQueueAsync(clock)).Claimed);
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 2 : 1, await FinancialRowsAsync(observer));
        Assert.Single(await observer.ClientCaseDocuments.ToArrayAsync()); Assert.Single(await observer.StorageProductBindings.ToArrayAsync());
        Assert.Single(await observer.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        Assert.Single(await observer.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).ToArrayAsync());
        Assert.Equal("uploaded", (await f.PublishFinancial(receipt)).Status);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    private static async Task AssertPendingFinancialQueueGraphAsync(Fixture f)
    {
        await using var observer = f.Observer();
        Assert.Equal(0, await FinancialRowsAsync(observer)); Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await observer.StorageProductBindings.ToArrayAsync());
        Assert.Empty(await observer.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" || a.ObjectType == "payment" || a.ObjectType == "act" || a.ObjectType == "contract").ToArrayAsync());
        Assert.Empty(await observer.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).ToArrayAsync());
        Assert.Empty(await observer.CaseDocumentNotificationDispatches.ToArrayAsync());
        Assert.Null((await observer.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
        Assert.Single(await observer.GeneratedFinancialNumberClaims.ToArrayAsync());
        Assert.Equal("approved", (await observer.ClientCaseWorkOrders.SingleAsync()).Status);
    }
}
