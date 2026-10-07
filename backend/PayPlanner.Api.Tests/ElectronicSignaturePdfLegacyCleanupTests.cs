using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignatureServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyPdfSourceFailurePreservesExceptionAndDeletesOnlyVerifiedTemporaryFile(bool large, bool cancel)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
        await fixture.Db.SaveChangesAsync();
        var root = Path.Combine(fixture.Root, "storage");
        Directory.CreateDirectory(root);
        var unrelated = Path.Combine(root, "unrelated-owned-fixture.pdf");
        var unrelatedBytes = "%PDF-1.7 unrelated original"u8.ToArray();
        await File.WriteAllBytesAsync(unrelated, unrelatedBytes);
        var bytes = large ? RandomNumberGenerator.GetBytes(8 * 1024 * 1024) : "%PDF-1.7 failed input"u8.ToArray();
        "%PDF-1.7"u8.CopyTo(bytes);
        using var cancellation = new CancellationTokenSource();
        var sourceError = cancel ? (Exception)new OperationCanceledException("Owned legacy PDF cancellation.", cancellation.Token)
            : new IOException("Owned legacy PDF input failure.");
        using var input = new ObservedPdfStream(bytes, onEnd: () =>
        {
            Assert.Single(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
            if (cancel) cancellation.Cancel();
            throw sourceError;
        });
        var error = await Record.ExceptionAsync(() => fixture.Service.PrepareAsync(fixture.Request(), input, "invoice.pdf", cancellation.Token));
        Assert.Same(sourceError, error);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(unrelatedBytes, await File.ReadAllBytesAsync(unrelated));
        Assert.Empty(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }
}
