using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MessengerDocumentDeliveryServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SnapshotCheckpointCleanupPreservesNativeReplacementBeforeSizeConfirmation(bool generated, bool changedBytes)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var bytes = generated ? "%PDF-1.7 owned snapshot replacement"u8.ToArray()
            : await File.ReadAllBytesAsync(fixture.SourceFullPath);
        var foreign = bytes.ToArray();
        if (changedBytes) foreign[^1] ^= 1;
        string? path = null, originalIdentity = null, foreignIdentity = null;
        var displaced = Path.Combine(fixture.Configuration["FileStorage:RootPath"]!, "owned-displaced-snapshot.bin");
        var query = new SnapshotCheckpointQueryAction(() =>
        {
            var root = Path.Combine(fixture.Configuration["FileStorage:RootPath"]!, "messenger-document-deliveries");
            path = Assert.Single(Directory.EnumerateFiles(root, "*.snapshot", SearchOption.AllDirectories));
            // The checkpoint still verifies authority after its planned commit.
            // Wait for its exclusive writer to close, without weakening sharing.
            try
            {
                using var file = File.OpenRead(path);
                originalIdentity = StorageStageFileIdentity.Observe(file.SafeFileHandle);
            }
            catch (IOException) { return false; }
            File.Move(path, displaced);
            File.WriteAllBytes(path, foreign);
            using (var file = File.OpenRead(path)) foreignIdentity = StorageStageFileIdentity.Observe(file.SafeFileHandle);
            Assert.NotNull(originalIdentity);
            Assert.NotNull(foreignIdentity);
            Assert.NotEqual(originalIdentity, foreignIdentity);
            return true;
        });
        var boundary = new SnapshotCheckpointCommitAction(() => query.Armed = true);
        await using var db = fixture.CreateIndependentContext(boundary, query);
        var runtime = fixture.CreateManagedRuntime(db);
        using var source = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => generated
            ? runtime.Service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId,
                "invoice.pdf", "application/pdf", source, bytes.Length, binding.Id, null, default)
            : runtime.Service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, default));
        Assert.True(query.Executed);
        Assert.True(query.BlockedReads > 0);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(path!));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(displaced));
        var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.Equal(bytes.LongLength, row.Bytes);
        Assert.Null(row.ProductContextJson);
        Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var receipt));
        Assert.Equal(originalIdentity, receipt!.FileIdentity);
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(0, runtime.Provider.PutCalls);
    }

    private sealed class SnapshotCheckpointCommitAction(Action arm) : DbTransactionInterceptor
    {
        private int _commits;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (++_commits == 4) arm(); // Reserve, activate, initial receipt, final receipt.
            return Task.CompletedTask;
        }
    }

    private sealed class SnapshotCheckpointQueryAction(Func<bool> action, Func<DbCommand, bool>? predicate = null) : DbCommandInterceptor
    {
        internal bool Armed, Executed;
        internal int BlockedReads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && (predicate?.Invoke(command) ?? true))
            {
                if (action())
                {
                    Armed = false;
                    Executed = true;
                }
                else BlockedReads++;
            }
            return ValueTask.FromResult(result);
        }
    }
}
