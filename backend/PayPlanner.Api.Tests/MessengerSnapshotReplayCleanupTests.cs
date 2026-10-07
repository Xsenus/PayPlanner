using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MessengerDocumentDeliveryServiceTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task SnapshotExactReplayCleanupPreservesNativeReplacement(bool generated, bool capacityEnabled, bool changedBytes)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var bytes = generated ? "%PDF-1.7 owned replay snapshot"u8.ToArray()
            : await File.ReadAllBytesAsync(fixture.SourceFullPath);
        var seedService = capacityEnabled ? fixture.CreateManagedRuntime(fixture.Db).Service : fixture.Service;
        var seed = await PrepareAsync(seedService);
        var storageRoot = fixture.Configuration["FileStorage:RootPath"]!;
        var snapshotRoot = Path.Combine(storageRoot, "messenger-document-deliveries");
        var seedPath = Path.Combine(snapshotRoot, seed.Delivery.SnapshotPath);
        var foreign = bytes.ToArray();
        if (changedBytes) foreign[^1] ^= 1;
        string? replacedPath = null;
        var displaced = Path.Combine(storageRoot, "owned-displaced-replay-snapshot.bin");
        var query = new SnapshotCheckpointQueryAction(() =>
        {
            replacedPath = Assert.Single(Directory.EnumerateFiles(snapshotRoot, "*.snapshot", SearchOption.AllDirectories),
                path => !string.Equals(path, seedPath, StringComparison.OrdinalIgnoreCase));
            File.Move(replacedPath, displaced);
            File.WriteAllBytes(replacedPath, foreign);
            return true;
        }, command => command.CommandText.Contains("MessengerDocumentDeliveries", StringComparison.Ordinal)
            && command.CommandText.Contains("IdempotencyKey", StringComparison.Ordinal))
        { Armed = true };
        await using var db = fixture.CreateIndependentContext(query);
        var service = capacityEnabled ? fixture.CreateManagedRuntime(db).Service : fixture.CreateIndependentService(db);
        var replay = await PrepareAsync(service);
        Assert.True(query.Executed);
        Assert.Equal(seed.Delivery.Id, replay.Delivery.Id);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(seedPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(displaced));
        Assert.Equal(foreign, await File.ReadAllBytesAsync(replacedPath!));
        Assert.Single(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());

        async Task<PreparedMessengerDocumentDelivery> PrepareAsync(MessengerDocumentDeliveryService producer)
        {
            using var input = new MemoryStream(bytes);
            return generated
                ? await producer.PrepareGeneratedDocumentAsync(fixture.UserId, "invoice", 77, 1, fixture.ClientId,
                    "invoice.pdf", "application/pdf", input, bytes.Length, binding.Id, null, default)
                : await producer.PrepareCaseDocumentAsync(fixture.UserId, fixture.DocumentId, binding.Id, null, default);
        }
    }
}
