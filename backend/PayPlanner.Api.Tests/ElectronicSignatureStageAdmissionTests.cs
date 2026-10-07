using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using System.Data.Common;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignatureServiceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SignatureStoragePolicyCannotChangeDuringInput(bool enable, bool atEnd)
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime();
        runtime.Options.CurrentValue.Enabled = !enable;
        runtime.Options.CurrentValue.UploadCapacityChecksEnabled = !enable;
        void ChangePolicy()
        {
            runtime.Options.CurrentValue.Enabled = enable;
            runtime.Options.CurrentValue.UploadCapacityChecksEnabled = enable;
        }
        using var input = new ObservedPdfStream("%PDF-1.7 signature policy change"u8.ToArray(), onRead: atEnd ? null : ChangePolicy, onEnd: atEnd ? ChangePolicy : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.PrepareAsync(fixture.Request(), input, "invoice.pdf", CancellationToken.None));
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        if (enable) Assert.Empty(await fixture.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        else Assert.Equal(StorageCapacityReservationStatuses.Released, (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "storage"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageKeepsExistingPdfLimitWithoutPublishingOversizedInput(bool seekable)
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime();
        var bytes = new byte[25 * 1024 * 1024 + 1];
        "%PDF-"u8.CopyTo(bytes);
        using var input = new ObservedPdfStream(bytes, seekable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.PrepareAsync(fixture.Request(), input, "invoice.pdf", CancellationToken.None));
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.ToArrayAsync());
        if (seekable)
        {
            Assert.Equal(0, input.ReadCalls);
            Assert.Empty(await fixture.Db.StorageCapacityReservations.ToArrayAsync());
        }
        else
        {
            var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(25L * 1024 * 1024, row.Bytes);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
            Assert.Null(row.ProductContextJson);
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var receipt));
            Assert.Equal(6, receipt!.Sequence);
            Assert.Equal(24L * 1024 * 1024, receipt.PlannedBytes);
            var retained = Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "storage"), "*.tmp", SearchOption.AllDirectories));
            Assert.Equal(bytes.AsSpan(0, (int)receipt.PlannedBytes).ToArray(), await File.ReadAllBytesAsync(retained));
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task StageCommitFaultLeavesRecoverableDebtAndNoPublishedEnvelope(int boundary, bool afterCommit)
    {
        // The initial receipt is committed before input; arming at EOF leaves
        // one final planned checkpoint before the physical-size boundaries.
        var fault = new StageCommitFault(boundary, afterCommit, checkpointCommits: 1);
        var proof = new UnavailableStageReleaseProof(() => afterCommit && boundary == 2 && fault.AfterCommitFaultTriggered);
        await using var fixture = await Fixture.CreateAsync(interceptors: [fault, proof]);
        var runtime = fixture.CreateManagedRuntime();
        var bytes = "%PDF-1.7 stage commit failure"u8.ToArray();
        using var input = new ObservedPdfStream(bytes, onEnd: () => fault.Armed = true);
        await Assert.ThrowsAsync<IOException>(() => runtime.Service.PrepareAsync(fixture.Request(), input, "invoice.pdf", CancellationToken.None));
        Assert.False(fault.Armed);
        Assert.Equal(afterCommit && boundary == 2 ? 1 : 0, proof.Interceptions);
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.ToArrayAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "storage"), "*", SearchOption.AllDirectories));
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        if (boundary == 1 && afterCommit)
        {
            Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
            Assert.Equal(bytes.Length, row.Bytes);
            Assert.NotNull(row.ProductContextJson);
        }
        var environment = new TestEnvironment(fixture.Root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ElectronicSignature:StorageRoot"] = Path.Combine(fixture.Root, "storage") }).Build();
        var stages = new StorageStageCapacityService(fixture.Db, new StorageProviderRegistry([new LegacyLocalStorageProvider(environment, configuration)]), runtime.Options, environment, configuration);
        var stale = DateTime.UtcNow.AddHours(-1);
        await fixture.Db.StorageCapacityReservations.Where(item => item.IdempotencyKey == row.IdempotencyKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAt, stale));
        await stages.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.Equal(StorageCapacityReservationStatuses.Released, (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
        var envelope = await runtime.Service.PrepareAsync(fixture.Request(), new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Assert.Equal(1, runtime.Provider.PutCalls);
        Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.ToArrayAsync());
    }

    private sealed class StageCommitFault(int boundary, bool afterCommit, bool countsActivation = false, int checkpointCommits = 0) : DbTransactionInterceptor
    {
        private readonly int _targetBoundary = (countsActivation && boundary >= 2 ? boundary + 1 : boundary) + checkpointCommits;
        public bool Armed { get; set; }
        public bool AfterCommitFaultTriggered { get; private set; }
        private int _commits;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && ++_commits == _targetBoundary && !afterCommit) { Armed = false; throw new IOException("Owned stage before-commit fault."); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed && _commits == _targetBoundary && afterCommit) { Armed = false; AfterCommitFaultTriggered = true; throw new IOException("Owned stage lost commit acknowledgement."); }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StageQuotaDenialPrecedesInputReadAndPhysicalWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime();
        runtime.Options.CurrentValue.UploadMinimumFreeBytes = long.MaxValue;
        using var input = new ObservedPdfStream("%PDF-1.7 denied"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.PrepareAsync(fixture.Request(), input, "invoice.pdf", CancellationToken.None));
        Assert.Equal(0, input.ReadCalls);
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.StorageCapacityReservations.ToArrayAsync());
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToArrayAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "storage"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageSealsActualSizeAndPreservesPublishedBytes(bool seekable)
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime();
        var bytes = "%PDF-1.7 admitted source"u8.ToArray();
        using var input = new ObservedPdfStream(bytes, seekable);
        var envelope = await runtime.Service.PrepareAsync(fixture.Request(), input, "invoice.pdf", CancellationToken.None);
        var stage = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(row => row.IdempotencyKey.StartsWith("stage:"));
        Assert.Equal(StorageCapacityReservationStatuses.Released, stage.Status);
        Assert.Equal(bytes.Length, stage.Bytes);
        Assert.NotNull(stage.ProductContextJson);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(envelope.SnapshotPath));
        Assert.Equal(bytes.Length, envelope.SizeBytes);
        Assert.Single(await fixture.Db.StorageProductBindings.ToArrayAsync());
        Assert.Equal(1, runtime.Provider.PutCalls);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "storage"), "*.tmp"));
    }

    [Theory]
    [InlineData("read-error")]
    [InlineData("cancel")]
    [InlineData("invalid-header")]
    public async Task FailedInputReleasesOnlyItsOwnStageAndPublishesNothing(string fault)
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime();
        using var input = new ObservedPdfStream(fault == "invalid-header" ? "no PDF"u8.ToArray() : "%PDF-1.7 incomplete"u8.ToArray(), fault: fault);
        var error = await Record.ExceptionAsync(() => runtime.Service.PrepareAsync(fixture.Request(), input, "invoice.pdf", CancellationToken.None));
        Assert.NotNull(error);
        if (fault == "cancel") Assert.IsType<OperationCanceledException>(error);
        if (fault == "read-error") Assert.IsType<IOException>(error);
        var stage = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Released, stage.Status);
        Assert.Null(stage.ProductContextJson);
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.ToArrayAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "storage"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ManagedCapacityPrerequisiteFailsBeforeInputRead()
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime();
        runtime.Options.CurrentValue.UploadCapacityChecksEnabled = false;
        using var input = new ObservedPdfStream("%PDF-1.7 prerequisite"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.PrepareAsync(fixture.Request(), input, "invoice.pdf", CancellationToken.None));
        Assert.Equal(0, input.ReadCalls);
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.StorageCapacityReservations.ToArrayAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "storage"), "*", SearchOption.AllDirectories));
    }

    private sealed class ObservedPdfStream(byte[] bytes, bool seekable = false, string? fault = null, Action? onEnd = null, Action? onRead = null) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? _inner.Length : throw new NotSupportedException();
        public override long Position { get => seekable ? _inner.Position : throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (ReadCalls == 1) onRead?.Invoke();
            if (ReadCalls > 1 && fault == "read-error") throw new IOException("Owned fixture input failure.");
            if (ReadCalls > 1 && fault == "cancel") throw new OperationCanceledException(cancellationToken);
            var count = await _inner.ReadAsync(buffer, cancellationToken);
            if (count == 0) onEnd?.Invoke();
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
