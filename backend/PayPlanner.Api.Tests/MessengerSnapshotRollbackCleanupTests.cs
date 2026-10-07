using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MessengerDocumentDeliveryServiceTests
{
    [Theory]
    [InlineData(false, false, "same-bytes")]
    [InlineData(false, false, "changed-bytes")]
    [InlineData(false, false, "root")]
    [InlineData(false, true, "same-bytes")]
    [InlineData(false, true, "changed-bytes")]
    [InlineData(false, true, "root")]
    [InlineData(true, false, "same-bytes")]
    [InlineData(true, false, "changed-bytes")]
    [InlineData(true, false, "root")]
    [InlineData(true, true, "same-bytes")]
    [InlineData(true, true, "changed-bytes")]
    [InlineData(true, true, "root")]
    public async Task ConfirmedDeliveryRollbackCleanupPreservesReplacedSnapshot(bool generated, bool capacityEnabled, string change)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true, separateStorageRoot: true);
        var binding = await fixture.CreateBindingAsync();
        var sourceBytes = await File.ReadAllBytesAsync(fixture.SourceFullPath);
        var bytes = generated ? "%PDF-1.7 owned rollback snapshot"u8.ToArray() : sourceBytes;
        var root = fixture.Configuration["FileStorage:RootPath"]!;
        var snapshotRoot = Path.Combine(root, "messenger-document-deliveries");
        string? path = null, displaced = null;
        byte[]? expected = null;
        var originalError = new IOException("Owned snapshot domain-save fault");
        var fault = new SnapshotRollbackSaveAction(() =>
        {
            path = Assert.Single(Directory.EnumerateFiles(snapshotRoot, "*.snapshot", SearchOption.AllDirectories));
            string? originalIdentity;
            using (var file = File.OpenRead(path)) originalIdentity = StorageStageFileIdentity.Observe(file.SafeFileHandle);
            Assert.NotNull(originalIdentity);
            if (change == "root")
            {
                var oldIdentity = StorageStageRootIdentity.Observe(root);
                Assert.NotNull(oldIdentity);
                var retainedRoot = root + ".owned-original";
                Assert.Equal(Path.GetDirectoryName(root), Path.GetDirectoryName(retainedRoot));
                Directory.Move(root, retainedRoot);
                Directory.CreateDirectory(root);
                foreach (var child in Directory.EnumerateFileSystemEntries(retainedRoot))
                {
                    var destination = Path.Combine(root, Path.GetFileName(child));
                    if (Directory.Exists(child)) Directory.Move(child, destination);
                    else File.Move(child, destination);
                }
                Assert.NotEqual(oldIdentity, StorageStageRootIdentity.Observe(root));
                using var file = File.OpenRead(path);
                Assert.Equal(originalIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
                expected = bytes;
            }
            else
            {
                displaced = Path.Combine(root, "owned-displaced-rollback-snapshot.bin");
                File.Move(path, displaced);
                expected = bytes.ToArray();
                if (change == "changed-bytes") expected[^1] ^= 1;
                File.WriteAllBytes(path, expected);
                using var file = File.OpenRead(path);
                Assert.NotEqual(originalIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
            }
            throw originalError;
        });
        await using var db = fixture.CreateIndependentContext(fault);
        var service = capacityEnabled ? fixture.CreateManagedRuntime(db).Service : fixture.CreateIndependentService(db);
        using var input = new MemoryStream(bytes);
        var error = await Record.ExceptionAsync(() => generated
            ? service.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId,
                "invoice.pdf", "application/pdf", input, bytes.Length, binding.Id, null, default)
            : service.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, default));
        Assert.Same(originalError, error);
        Assert.True(fault.Executed);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path!));
        if (displaced is not null) Assert.Equal(bytes, await File.ReadAllBytesAsync(displaced));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(fixture.SourceFullPath));
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        if (capacityEnabled)
        {
            var pending = await fixture.Db.StorageObjects.AsNoTracking().SingleAsync();
            Assert.StartsWith("upload:messenger:", pending.OwnerId, StringComparison.Ordinal);
        }
        else Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
    }

    private sealed class SnapshotRollbackSaveAction(Action action) : SaveChangesInterceptor
    {
        internal bool Executed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Executed && eventData.Context!.ChangeTracker.Entries<MessengerDocumentDelivery>().Any(entry => entry.State == EntityState.Added))
            {
                Executed = true;
                action();
            }
            return ValueTask.FromResult(result);
        }
    }
}
