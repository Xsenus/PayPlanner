using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MessengerDocumentDeliveryServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedLegacySnapshotCleansOnlyProvenOriginalAndPreservesSourceException(bool large, bool cancel)
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var binding = await fixture.CreateBindingAsync();
        var bytes = large ? RandomNumberGenerator.GetBytes(8 * 1024 * 1024 + 4)
            : "%PDF-1.7 owned legacy failure"u8.ToArray();
        "%PDF-1.7"u8.CopyTo(bytes);
        var root = Path.Combine(fixture.Configuration["FileStorage:RootPath"]!, "messenger-document-deliveries");
        Directory.CreateDirectory(root);
        var foreignPath = Path.Combine(root, "unrelated.snapshot");
        var foreign = "%PDF-1.7 unrelated legacy snapshot"u8.ToArray();
        await File.WriteAllBytesAsync(foreignPath, foreign);
        using var cancellation = new CancellationTokenSource();
        var sourceError = cancel ? (Exception)new OperationCanceledException("Owned caller cancellation", cancellation.Token)
            : new IOException("Owned legacy snapshot EOF failure");
        using var source = new SnapshotEofFailureStream(bytes, sourceError, cancel ? cancellation.Cancel : null);
        var error = await Record.ExceptionAsync(() => fixture.Service.PrepareGeneratedDocumentAsync(fixture.UserId,
            "invoice", 77, 1, fixture.ClientId, "invoice.pdf", "application/pdf", source, bytes.Length,
            binding.Id, null, cancellation.Token));
        Assert.Same(sourceError, error);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(foreignPath));
        Assert.Equal(foreignPath, Assert.Single(Directory.EnumerateFiles(root, "*.snapshot", SearchOption.AllDirectories)));
        Assert.Empty(await fixture.Db.MessengerDocumentDeliveries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }
}
