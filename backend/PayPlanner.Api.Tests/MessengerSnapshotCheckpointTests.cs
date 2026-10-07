using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MessengerDocumentDeliveryServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLargeGeneratedSnapshotRetainsWrittenPrefixAndFullPendingDebt(bool cancel)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        var bytes = RandomNumberGenerator.GetBytes(StorageStageFileReceipt.MaximumBlockBytes * 2 + 4);
        "%PDF-1.7"u8.CopyTo(bytes);
        var sourceError = cancel ? (Exception)new OperationCanceledException("Owned snapshot EOF cancellation")
            : new IOException("Owned snapshot EOF failure");
        using var input = new SnapshotEofFailureStream(bytes, sourceError);
        var error = await Record.ExceptionAsync(() => runtime.Service.PrepareGeneratedDocumentAsync(fixture.UserId,
            "invoice", 77, 1, fixture.ClientId, "invoice.pdf", "application/pdf", input, bytes.Length, binding.Id, null, default));
        Assert.Same(sourceError, error);
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.Equal(bytes.LongLength, row.Bytes);
        Assert.Null(row.ProductContextJson);
        Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var receipt));
        Assert.Equal(2, receipt!.Sequence);
        Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes * 2L, receipt.PlannedBytes);
        var root = Path.Combine(fixture.Configuration["FileStorage:RootPath"]!, "messenger-document-deliveries");
        var path = Assert.Single(Directory.EnumerateFiles(root, "*.snapshot", SearchOption.AllDirectories));
        Assert.Equal(bytes.AsSpan(0, (int)receipt.PlannedBytes).ToArray(), await File.ReadAllBytesAsync(path));
        using var file = File.OpenRead(path);
        Assert.Equal(receipt.FileIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(0, runtime.Provider.PutCalls);
    }

    [Fact]
    public async Task GeneratedSnapshotConfirmsOriginalEmptyFileBeforeFirstInputRead()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        StorageStageFileReceipt? initial = null;
        using var input = new ObservedSnapshotStream("%PDF-1.7 owned snapshot checkpoint"u8.ToArray(), onRead: () =>
        {
            var row = fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync().GetAwaiter().GetResult();
            Assert.False(row.StageWriterAdmissionPending);
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out initial));
            Assert.Equal(0, initial!.Sequence);
            Assert.Equal(0, initial.PlannedBytes);
            Assert.Equal(StorageStageFileReceipt.EmptySha256, initial.PlannedSha256);
        });
        await runtime.Service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId,
            "invoice.pdf", "application/pdf", input, input.DeclaredLength, binding.Id, null, default);
        Assert.NotNull(initial);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedSnapshotRetainsFinalOriginalFileReceipt(bool generated)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var runtime = fixture.CreateManagedRuntime(fixture.Db);
        var bytes = generated ? "%PDF-1.7 owned final snapshot"u8.ToArray()
            : await File.ReadAllBytesAsync(fixture.SourceFullPath);
        using var input = new MemoryStream(bytes);
        var result = generated
            ? await runtime.Service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId,
                "invoice.pdf", "application/pdf", input, bytes.Length, binding.Id, null, default)
            : await runtime.Service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, default);
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking()
            .SingleAsync(item => item.IdempotencyKey.StartsWith("stage:"));
        Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
        Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var receipt));
        Assert.Equal(1, receipt!.Sequence);
        Assert.Equal(bytes.LongLength, receipt.PlannedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), receipt.PlannedSha256);
        var path = Path.Combine(fixture.Configuration["FileStorage:RootPath"]!, "messenger-document-deliveries", result.Delivery.SnapshotPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        using var file = File.OpenRead(path);
        Assert.Equal(receipt.FileIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        Assert.Single(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, runtime.Provider.PutCalls);
    }

    private sealed class SnapshotEofFailureStream(byte[] bytes, Exception error, Action? beforeThrow = null) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                beforeThrow?.Invoke();
                throw error;
            }
            return count;
        }
    }
}
