using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

// Verify immutable replay identity against canonical storage without changing lifecycle policy.
public sealed partial class ElectronicSignatureServiceTests
{
    [Theory]
    [InlineData(false, "identical")]
    [InlineData(true, "identical")]
    [InlineData(false, "archive")]
    [InlineData(true, "archive")]
    [InlineData(false, "mra")]
    [InlineData(true, "mra")]
    [InlineData(false, "party")]
    [InlineData(true, "party")]
    [InlineData(false, "signer-mra")]
    [InlineData(true, "signer-mra")]
    [InlineData(false, "power")]
    [InlineData(true, "power")]
    [InlineData(false, "tracked-identical")]
    [InlineData(true, "tracked-identical")]
    [InlineData(false, "tracked-conflict")]
    [InlineData(true, "tracked-conflict")]
    public async Task InitialPreparedReplayRejectsChangedIdentityWithoutReplacingEnvelope(bool managed, string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var runtime = fixture.CreateManagedRuntime();
        var service = managed ? runtime.Service : fixture.Service;
        var bytes = "%PDF-1.7 initial identity replay"u8.ToArray();
        var request = fixture.Request();
        var original = await service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", default);
        var originalSignerIds = original.Signers.OrderBy(item => item.Order).Select(item => item.Id).ToArray();
        Assert.Equal(managed ? 1 : 0, runtime.Provider.PutCalls);
        Assert.Equal(managed ? 1 : 0, await fixture.Db.StorageObjects.CountAsync());
        Assert.Equal(managed ? 1 : 0, await fixture.Db.StorageProductBindings.CountAsync());
        var puts = runtime.Provider.PutCalls;
        var originalPdfPaths = Directory.EnumerateFiles(fixture.Root, "*.pdf", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(managed ? 2 : 1, originalPdfPaths.Length);
        foreach (var path in originalPdfPaths) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        if (change.StartsWith("tracked-", StringComparison.Ordinal))
        {
            original.ArchiveRetentionYears = 75;
            fixture.Db.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Modified, fixture.Db.Entry(original).State);
        }
        var originalCapacityState = await fixture.Db.StorageCapacityReservations.AsNoTracking().OrderBy(row => row.IdempotencyKey)
            .Select(row => new { row.IdempotencyKey, row.Status }).ToArrayAsync();
        var changed = change switch
        {
            "identical" or "tracked-identical" => request,
            "archive" or "tracked-conflict" => request with { ArchiveRetentionYears = 75 },
            "mra" => request with { RequiresMachineReadablePowerOfAttorney = false },
            "party" => request with
            {
                Signers = request.Signers.Select((signer, index) =>
                index == 0 ? signer with { PartyType = "representative" } : signer).ToArray()
            },
            "signer-mra" => request with
            {
                Signers = request.Signers.Select((signer, index) =>
                index == 1 ? signer with { RequiresMachineReadablePowerOfAttorney = false } : signer).ToArray()
            },
            "power" => request with
            {
                Signers = request.Signers.Select((signer, index) =>
                index == 1 ? signer with { PowerOfAttorneyReference = "МЧД-2" } : signer).ToArray()
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        if (!change.StartsWith("tracked-", StringComparison.Ordinal)) fixture.Db.ChangeTracker.Clear();
        using var replayPdf = new MemoryStream(bytes);
        if (managed && change.StartsWith("tracked-", StringComparison.Ordinal))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(
                changed, replayPdf, "invoice.pdf", default));
            Assert.Contains("Недостаточно подтверждённого места", error.Message);
            Assert.Equal(0L, replayPdf.Position);
            Assert.Equal(originalCapacityState, await fixture.Db.StorageCapacityReservations.AsNoTracking().OrderBy(row => row.IdempotencyKey)
                .Select(row => new { row.IdempotencyKey, row.Status }).ToArrayAsync());
        }
        else
        {
            if (change is "identical" or "tracked-identical")
            {
                var replay = await service.PrepareAsync(changed, replayPdf, "invoice.pdf", default);
                Assert.Equal(original.Id, replay.Id);
                Assert.Equal(original.PublicId, replay.PublicId);
                Assert.Equal(request.ArchiveRetentionYears, replay.ArchiveRetentionYears);
                Assert.Equal(originalSignerIds, replay.Signers.OrderBy(item => item.Order).Select(item => item.Id));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(
                    changed, replayPdf, "invoice.pdf", default));
            }
            Assert.Equal((long)bytes.Length, replayPdf.Position);
        }
        if (change.StartsWith("tracked-", StringComparison.Ordinal))
        {
            Assert.Equal(75, original.ArchiveRetentionYears);
            Assert.Equal(EntityState.Modified, fixture.Db.Entry(original).State);
        }
        Assert.Equal(puts, runtime.Provider.PutCalls);
        fixture.Db.ChangeTracker.Clear();
        var saved = Assert.Single(await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking()
            .Include(item => item.Signers).ToArrayAsync());
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(request.ArchiveRetentionYears, saved.ArchiveRetentionYears);
        Assert.Equal(request.RequiresMachineReadablePowerOfAttorney, saved.RequiresMachineReadablePowerOfAttorney);
        Assert.Equal(originalSignerIds, saved.Signers.OrderBy(item => item.Order).Select(item => item.Id));
        var savedSigners = saved.Signers.OrderBy(item => item.Order).ToArray();
        var expectedSigners = request.Signers.ToArray();
        for (var index = 0; index < expectedSigners.Length; index++)
        {
            Assert.Equal(expectedSigners[index].PartyType, savedSigners[index].PartyType);
            Assert.Equal(expectedSigners[index].RequiresMachineReadablePowerOfAttorney,
                savedSigners[index].RequiresMachineReadablePowerOfAttorney);
            Assert.Equal(expectedSigners[index].PowerOfAttorneyReference, savedSigners[index].PowerOfAttorneyReference);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(saved.SnapshotPath));
        Assert.Equal(originalPdfPaths, Directory.EnumerateFiles(fixture.Root, "*.pdf", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal));
        foreach (var path in originalPdfPaths) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }
}