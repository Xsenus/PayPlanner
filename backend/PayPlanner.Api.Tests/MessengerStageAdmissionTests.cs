using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MessengerDocumentDeliveryServiceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task StoragePolicyCannotChangeDuringSnapshotInput(bool enable, bool atEnd)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        runtime.Options.CurrentValue.Enabled = !enable;
        runtime.Options.CurrentValue.UploadCapacityChecksEnabled = !enable;
        void ChangePolicy()
        {
            runtime.Options.CurrentValue.Enabled = enable;
            runtime.Options.CurrentValue.UploadCapacityChecksEnabled = enable;
        }
        using var input = new ObservedSnapshotStream("%PDF-1.7 policy transition"u8.ToArray(), onRead: atEnd ? null : ChangePolicy, onEnd: atEnd ? ChangePolicy : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId, "invoice.pdf", "application/pdf", input, input.DeclaredLength, binding.Id, null, CancellationToken.None));
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        if (enable) Assert.Empty(await fixture.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        else Assert.Equal(StorageCapacityReservationStatuses.Released, (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
        AssertNoSnapshots(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageDeniesSnapshotBeforeWritingOrReadingGeneratedInput(bool generated)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        runtime.Options.CurrentValue.UploadMinimumFreeBytes = long.MaxValue;
        var sourceBytes = await File.ReadAllBytesAsync(fixture.SourceFullPath);
        using var input = new ObservedSnapshotStream("%PDF-1.7 denied"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => generated
            ? runtime.Service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId, "invoice.pdf", "application/pdf", input, input.DeclaredLength, binding.Id, null, CancellationToken.None)
            : runtime.Service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None));
        Assert.Equal(0, input.ReadCalls);
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.StorageCapacityReservations.ToArrayAsync());
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.ToArrayAsync());
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(fixture.SourceFullPath));
        AssertNoSnapshots(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageCompletesExactSnapshotBeforeAtomicPublication(bool generated)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        var pdf = "%PDF-1.7 admitted snapshot"u8.ToArray();
        var sourceBytes = await File.ReadAllBytesAsync(fixture.SourceFullPath);
        using var input = new ObservedSnapshotStream(pdf);
        var result = generated
            ? await runtime.Service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId, "invoice.pdf", "application/pdf", input, pdf.Length, binding.Id, null, CancellationToken.None)
            : await runtime.Service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        var expected = generated ? pdf : sourceBytes;
        var stage = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(row => row.IdempotencyKey.StartsWith("stage:"));
        Assert.Equal(StorageCapacityReservationStatuses.Released, stage.Status);
        Assert.Equal(expected.Length, stage.Bytes);
        Assert.NotNull(stage.ProductContextJson);
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(fixture.Configuration["FileStorage:RootPath"]!, "messenger-document-deliveries", result.Delivery.SnapshotPath)));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(fixture.SourceFullPath));
        Assert.Single(await fixture.Db.StorageProductBindings.ToArrayAsync());
        Assert.Equal(1, runtime.Provider.PutCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageRejectsDeclaredSizeMismatchWithoutPublishingOrLeakingDebt(bool largerThanActual)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        using var input = new ObservedSnapshotStream("%PDF-1.7 size mismatch"u8.ToArray());
        var declared = input.DeclaredLength + (largerThanActual ? 1 : -1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId, "invoice.pdf", "application/pdf", input, declared, binding.Id, null, CancellationToken.None));
        var stage = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Released, stage.Status);
        Assert.True(stage.Bytes <= declared);
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.ToArrayAsync());
        AssertNoSnapshots(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageReleasesPartialGeneratedInputAfterReadFailureOrCancellation(bool cancelled)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        using var input = new ObservedSnapshotStream("%PDF-1.7 partial snapshot"u8.ToArray(), cancelled ? "cancel" : "read-error");
        var error = await Record.ExceptionAsync(() => runtime.Service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId, "invoice.pdf", "application/pdf", input, input.DeclaredLength, binding.Id, null, CancellationToken.None));
        if (cancelled) Assert.IsType<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
        var stage = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Released, stage.Status);
        Assert.Null(stage.ProductContextJson);
        Assert.Equal(0, runtime.Provider.PutCalls);
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.ToArrayAsync());
        AssertNoSnapshots(fixture);
    }

    [Theory]
    [InlineData(false, 2, false)]
    [InlineData(false, 2, true)]
    [InlineData(false, 3, false)]
    [InlineData(false, 3, true)]
    [InlineData(true, 2, false)]
    [InlineData(true, 2, true)]
    [InlineData(true, 3, false)]
    [InlineData(true, 3, true)]
    public async Task StageCommitFaultCannotPublishAndExactRetryPreservesSource(bool generated, int boundary, bool afterCommit)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var sourceBytes = await File.ReadAllBytesAsync(fixture.SourceFullPath);
        var fault = new SnapshotStageCommitFault(boundary, afterCommit);
        var proof = new UnavailableStageReleaseProof(() => afterCommit && boundary == 3 && fault.AfterCommitFaultTriggered);
        await using (var failing = fixture.CreateIndependentContext(fault, proof))
        {
            var runtime = fixture.CreateManagedRuntime(failing);
            await Assert.ThrowsAsync<IOException>(() => PrepareAsync(runtime.Service));
            Assert.False(fault.Armed);
            Assert.Equal(afterCommit && boundary == 3 ? 1 : 0, proof.Interceptions);
            Assert.Equal(0, runtime.Provider.PutCalls);
        }
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(StorageCapacityReservationStatuses.Released, (await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(fixture.SourceFullPath));
        AssertNoSnapshots(fixture);
        var resumed = fixture.CreateManagedRuntime(fixture.Db);
        var result = await PrepareAsync(resumed.Service);
        Assert.Equal(1, resumed.Provider.PutCalls);
        Assert.Equal(result.Delivery.Id, (await fixture.Db.StorageProductBindings.AsNoTracking().SingleAsync()).SourceId);

        Task<PreparedMessengerDocumentDelivery> PrepareAsync(MessengerDocumentDeliveryService service)
        {
            byte[] pdf = "%PDF-1.7 recovery snapshot"u8.ToArray();
            return generated
                ? service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId, "invoice.pdf", "application/pdf", new MemoryStream(pdf), pdf.Length, binding.Id, null, CancellationToken.None)
                : service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, CancellationToken.None);
        }
    }

    private static void AssertNoSnapshots(Fixture fixture)
    {
        var root = Path.Combine(fixture.Configuration["FileStorage:RootPath"]!, "messenger-document-deliveries");
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    private sealed class SnapshotStageCommitFault(int boundary, bool afterCommit) : DbTransactionInterceptor
    {
        // Preserve the original completion/release boundaries after adding
        // the pre-write activation and two initial/final checkpoint commits.
        private readonly int _targetBoundary = boundary >= 2 ? boundary + 3 : boundary;
        public bool Armed { get; private set; } = true;
        public bool AfterCommitFaultTriggered { get; private set; }
        private int _commits;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && ++_commits == _targetBoundary && !afterCommit) { Armed = false; throw new IOException("Owned snapshot stage before-commit fault."); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed && _commits == _targetBoundary && afterCommit) { Armed = false; AfterCommitFaultTriggered = true; throw new IOException("Owned snapshot stage lost acknowledgement."); }
            return Task.CompletedTask;
        }
    }

    private sealed class ObservedSnapshotStream(byte[] bytes, string? fault = null, Action? onRead = null, Action? onEnd = null) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public int ReadCalls { get; private set; }
        public long DeclaredLength => bytes.Length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (ReadCalls == 1) onRead?.Invoke();
            if (ReadCalls > 1 && fault == "read-error") throw new IOException("Owned snapshot input failure.");
            if (ReadCalls > 1 && fault == "cancel") throw new OperationCanceledException(cancellationToken);
            var count = await _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 5)], cancellationToken);
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
