using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignatureServiceTests
{
    [Theory]
    [InlineData("intact")]
    [InlineData("binding-version")]
    [InlineData("deleted")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("checksum")]
    [InlineData("replica-pending")]
    [InlineData("commit-pending")]
    [InlineData("tombstone")]
    public async Task InitialManagedReplayRequiresCanonicalPublishedReceipt(string damage)
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime();
        var bytes = "%PDF-1.7 initial published receipt"u8.ToArray();
        var request = fixture.Request();
        var original = await runtime.Service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", default);
        fixture.Db.ChangeTracker.Clear();
        var binding = await fixture.Db.StorageProductBindings.SingleAsync();
        var storageObject = await fixture.Db.StorageObjects.SingleAsync();
        var generation = await fixture.Db.StorageObjectGenerations.SingleAsync();
        var replica = await fixture.Db.StorageReplicas.SingleAsync();
        var commit = await fixture.Db.StorageOperations.SingleAsync(row => row.OperationType == "commit");
        switch (damage)
        {
            case "intact": break;
            case "binding-version": binding.Version++; break;
            case "deleted": storageObject.IsDeleted = true; break;
            case "owner": storageObject.OwnerId = "foreign-owner"; break;
            case "generation": storageObject.CurrentGeneration++; break;
            case "checksum": generation.Sha256 = new string('a', 64); break;
            case "replica-pending": replica.Status = StorageReplicaStatuses.Pending; break;
            case "commit-pending": commit.Status = StorageOperationStatuses.Pending; break;
            case "tombstone":
                fixture.Db.StorageTombstones.Add(new StorageTombstone
                { StorageObjectId = storageObject.Id, Generation = generation.Generation, Reason = "Synthetic receipt test" }); break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var paths = Directory.EnumerateFiles(fixture.Root, "*.pdf", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, paths.Length);
        var puts = runtime.Provider.PutCalls;
        using var replayPdf = new MemoryStream(bytes);
        if (damage == "intact")
        {
            var replay = await runtime.Service.PrepareAsync(request, replayPdf, "invoice.pdf", default);
            Assert.Equal(original.Id, replay.Id);
            Assert.Equal(original.PublicId, replay.PublicId);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Service.PrepareAsync(
                request, replayPdf, "invoice.pdf", default));
        }
        Assert.Equal((long)bytes.Length, replayPdf.Position);
        Assert.Equal(puts, runtime.Provider.PutCalls);
        Assert.Equal(original.Id, (await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(paths, Directory.EnumerateFiles(fixture.Root, "*.pdf", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal));
        foreach (var path in paths) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }
}
