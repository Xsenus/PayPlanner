using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialCancellationBeforeLocalWritePreservesAcceptanceAndAllowsExplicitRetirement(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, true);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var capture = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(capture.Receipt);
        var json = (await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync()).FrozenDomainJson;
        var bytes = (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes;
        f.FinancialStorageOptions.WorkerEnabled = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Local.BeforePutAsync = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        using var cancellation = new CancellationTokenSource();
        var run = f.FinancialRecovery().ProcessBatchAsync(cancellation.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        f.Local.BeforePutAsync = null; f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(f.Db));
        Assert.Equal(json, (await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync()).FrozenDomainJson);
        Assert.Equal(bytes, (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes);
        Assert.False(await f.Db.StorageOperations.AnyAsync(o => o.OperationType == "commit"));
        var local = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        var reservations = await f.Db.StorageCapacityReservations.Where(r => r.ResourceKey == local)
            .Select(r => r.Status).ToArrayAsync();
        Console.WriteLine("Owned financial cancellation primary reservation states: " + string.Join(",", reservations));
        Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db)
            .RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        Assert.Equal("retired", Assert.Single((await f.FinancialUploadStates([receipt.Proof.UploadKey]))!).Status);
        var replacement = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        Assert.Equal("recorded", replacement.Status); Assert.NotEqual(receipt.Proof.UploadKey, replacement.Receipt!.Proof.UploadKey);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(0, f.Mirror.PutCount);
    }
}
