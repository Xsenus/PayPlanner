using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice, true)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice, true)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act, true)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act, true)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract, true)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract, true)]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice, false)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice, false)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act, false)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act, false)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract, false)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract, false)]
    public async Task FinancialStatusRequiresAcceptedSourceAndNativeWinner(bool postgres, GeneratedFinancialDocumentKind kind, bool loseWinner)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, true);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status);
        var pending = Assert.Single((await f.FinancialUploadStates([receipt.Proof.UploadKey]))!);
        Assert.NotEqual("uploaded", pending.Status); Assert.Null(pending.DocumentId);
        Assert.Equal("financial", pending.Generation!.Kind); Assert.True(pending.Generation.SourceVerified);
        Assert.Equal(receipt.Proof.Kind, pending.Generation.FinancialKind);
        Assert.Equal(kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => "Счет",
            GeneratedFinancialDocumentKind.Act => "Акт",
            _ => "Договор"
        }, pending.Generation.DocumentType);
        Assert.Equal("Черновик", pending.Generation.Status);
        await f.CopyPendingAsync(); f.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        var reads = f.Local.ReadCount + f.Mirror.ReadCount;
        var ready = Assert.Single((await f.FinancialUploadStates([receipt.Proof.UploadKey]))!);
        Assert.Equal("uploaded", ready.Status); Assert.True(ready.Generation!.SourceVerified);
        Assert.Equal(receipt.Proof.Kind, ready.Generation.FinancialKind);
        Assert.Equal((await f.Db.ClientCaseDocuments.SingleAsync()).Id, ready.DocumentId);
        f.Db.ChangeTracker.Clear();
        if (loseWinner)
        {
            switch (kind)
            {
                case GeneratedFinancialDocumentKind.Invoice: f.Db.Payments.Remove(await f.Db.Payments.SingleAsync()); break;
                case GeneratedFinancialDocumentKind.Act: f.Db.Acts.Remove(await f.Db.Acts.SingleAsync()); break;
                case GeneratedFinancialDocumentKind.Contract: f.Db.Contracts.Remove(await f.Db.Contracts.SingleAsync()); break;
            }
        }
        else (await f.Db.GeneratedFinancialDocumentAttempts.SingleAsync()).FrozenDomainSha256 = new string('b', 64);
        await f.Db.SaveChangesAsync();
        var damaged = Assert.Single((await f.FinancialUploadStates([receipt.Proof.UploadKey]))!);
        Assert.Equal("failed", damaged.Status); Assert.Null(damaged.DocumentId);
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Equal(reads, f.Local.ReadCount + f.Mirror.ReadCount);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialStatusBatchUsesOneMetadataQueryWithoutFrozenGraphsOrProviderIO(bool postgres)
    {
        var trace = new FinancialStatusQueryTrace();
        await using var f = await CreateFixtureAsync(postgres, 2, true, trace);
        var keys = new List<Guid>();
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        foreach (var kind in new[] { GeneratedFinancialDocumentKind.Invoice, GeneratedFinancialDocumentKind.Act, GeneratedFinancialDocumentKind.Contract })
        {
            var captured = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, kind);
            Assert.Equal("recorded", captured.Status);
            var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(captured.Receipt); keys.Add(receipt.Proof.UploadKey);
            Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status);
        }
        keys.AddRange(Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()));
        var reads = f.Local.ReadCount + f.Mirror.ReadCount; var puts = f.Local.PutCount + f.Mirror.PutCount;
        var states = (await f.FinancialUploadStates(keys.ToArray()))!;
        Assert.Equal(33, states.Count); Assert.Equal(3, states.Count(s => s.Generation?.SourceVerified == true));
        Assert.Equal(30, states.Count(s => s.Status == "unknown"));
        Assert.Equal(1, trace.Queries); Assert.False(trace.LoadedLargeContent);
        Assert.Equal(reads, f.Local.ReadCount + f.Mirror.ReadCount); Assert.Equal(puts, f.Local.PutCount + f.Mirror.PutCount);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
    }

    private sealed class FinancialStatusQueryTrace : DbCommandInterceptor
    {
        public int Queries { get; private set; }
        public bool LoadedLargeContent { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("case-upload-status-financial-proofs", StringComparison.Ordinal))
            {
                Queries++;
                LoadedLargeContent |= command.CommandText.Contains("FrozenDomainJson", StringComparison.Ordinal)
                    || command.CommandText.Contains("GeneratedFinancialDocumentContents", StringComparison.Ordinal);
            }
            return ValueTask.FromResult(result);
        }
    }
}
