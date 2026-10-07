using Microsoft.EntityFrameworkCore;
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
    public async Task FinancialActionAfterCancelledLocalPutRestoresOriginalAcceptanceWithoutNewCapture(bool postgres,
        GeneratedFinancialDocumentKind kind)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2, true);
        var controller = f.Controller(); var key = Guid.NewGuid();
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Local.BeforePutAsync = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        using var cancellation = new CancellationTokenSource();
        var first = InvokeFinancialActionAsync(controller, f.Case.Id, kind, ct: cancellation.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        f.Local.BeforePutAsync = null; f.Db.ChangeTracker.Clear();
        var original = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        var bytes = (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes;
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(f.Db));
        var resource = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        var reservationKey = $"write:case-document:{f.Case.Id}:{f.User.Id}:{key:N}:capacity";
        var objectId = await f.Db.StorageCapacityReservations.Where(r => r.ResourceKey == resource && r.IdempotencyKey == reservationKey)
            .Select(r => r.ObjectId).SingleAsync();
        var order = await f.Db.ClientCaseWorkOrders.Include(o => o.Items).SingleAsync();
        order.Items.Single().Quantity = 100; order.Title = "Changed after cancellation";
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        controller = f.Controller(); controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        Assert.Equal(503, Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(
            await InvokeFinancialActionAsync(controller, f.Case.Id, kind)).StatusCode);
        Assert.Equal(objectId, Assert.Single(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync()).Id);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        await f.CopyPendingAsync(); f.Db.ChangeTracker.Clear();
        var created = Assert.IsType<Microsoft.AspNetCore.Mvc.CreatedResult>(await InvokeFinancialActionAsync(controller, f.Case.Id, kind));
        var document = Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal(document.Id, FinancialActionDocumentId(created.Value!));
        var header = Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().ToArrayAsync());
        Assert.Equal(original.Id, header.Id); Assert.Equal(original.EntityId, header.EntityId);
        Assert.Equal(original.FrozenDomainJson, header.FrozenDomainJson);
        Assert.Equal(bytes, (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes);
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 2 : 1, await FinancialRowsAsync(f.Db));
        Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id,
            document.OriginalFileName, document.ContentType, default);
        Assert.Equal(ProductStorageReadStatus.Available, read.Status); Assert.NotNull(read.File);
        await using var file = read.File!;
        using var copy = new MemoryStream(); await file.Content.CopyToAsync(copy); Assert.Equal(bytes, copy.ToArray());
        var replay = Assert.IsType<Microsoft.AspNetCore.Mvc.CreatedResult>(await InvokeFinancialActionAsync(controller, f.Case.Id, kind));
        Assert.Equal(created.Location, replay.Location); Assert.Equal(document.Id, FinancialActionDocumentId(replay.Value!));
        Assert.Equal(2, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialRecoveryAfterCancelledLocalPutRestoresOriginalAcceptanceWithoutNewCapture(bool postgres,
        GeneratedFinancialDocumentKind kind)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2, true);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        var originalJson = (await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync()).FrozenDomainJson;
        var originalBytes = (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes;
        f.FinancialStorageOptions.WorkerEnabled = true;
        var clock = new AdvancingClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Local.BeforePutAsync = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        using var cancellation = new CancellationTokenSource();
        var first = f.FinancialRecovery(clock: clock).ProcessBatchAsync(cancellation.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        f.Local.BeforePutAsync = null; f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(f.Db));
        var resource = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        var key = $"write:case-document:{f.Case.Id}:{f.User.Id}:{receipt.Proof.UploadKey:N}:capacity";
        var reservedObjectId = await f.Db.StorageCapacityReservations.Where(r => r.ResourceKey == resource && r.IdempotencyKey == key)
            .Select(r => r.ObjectId).SingleAsync();
        var order = await f.Db.ClientCaseWorkOrders.Include(o => o.Items).SingleAsync();
        order.Title = "Changed after cancellation"; order.Items.First().ServiceName = "Replacement after cancellation";
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await f.FinancialRecovery(clock: clock).ProcessBatchAsync(default));
        var restored = Assert.Single(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Equal(reservedObjectId, restored.Id);
        Assert.Single(await f.Db.StorageOperations.Where(o => o.OperationType == "commit").ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        await f.CopyPendingAsync(); f.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        var document = Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 2 : 1, await FinancialRowsAsync(f.Db));
        var header = Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().ToArrayAsync());
        Assert.Equal(receipt.Proof.AttemptId, header.Id); Assert.Equal(receipt.Proof.EntityId, header.EntityId);
        Assert.Equal(originalJson, header.FrozenDomainJson); Assert.Equal(document.Id, header.PublishedDocumentId);
        Assert.Equal(originalBytes, (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes);
        Assert.Equal(2, header.RecoveryAttemptCount);
        Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id,
            document.OriginalFileName, document.ContentType, default);
        Assert.Equal(ProductStorageReadStatus.Available, read.Status); Assert.NotNull(read.File);
        await using var file = read.File!;
        using var copied = new MemoryStream(); await file.Content.CopyToAsync(copied);
        Assert.Equal(originalBytes, copied.ToArray());
        var puts = f.Local.PutCount + f.Mirror.PutCount;
        var replay = await f.PublishFinancial(receipt);
        Assert.Equal("uploaded", replay.Status); Assert.Equal(document.Id, replay.DocumentId);
        Assert.Equal(puts, f.Local.PutCount + f.Mirror.PutCount); Assert.Equal(2, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }
}
