using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RootAuthorityMutationBeforeCreateNewNeverGrantsWriter(bool postgres, bool missing)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var (stages, _, _, root) = CreateSharedBudgetServices(f);
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync("root-mutation:" + Guid.NewGuid().ToString("N"),
            Path.Combine(root, "mutation.tmp"), 4, default)).Ticket);
        try
        {
            var otherRoot = Directory.CreateDirectory(Path.Combine(f.Root, "other-owned-root")).FullName;
            var changed = missing ? null : StorageStageRootIdentity.Observe(otherRoot);
            if (!missing) { Assert.NotNull(changed); Assert.NotEqual(ticket.RootIdentity, changed); }
            await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.StageRootIdentity, changed));
            async Task Open() { await using var writer = await stages.OpenCheckpointWriterAsync(ticket, default); }
            await Assert.ThrowsAsync<InvalidOperationException>(Open);
            Assert.False(File.Exists(ticket.AbsolutePath));
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(changed, row.StageRootIdentity);
            Assert.Null(row.StageFileReceiptJson);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
            Assert.Equal(ticket.WriterMarkerIdentity, row.StageWriterMarkerIdentity);
            Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RootRecoveryRetainsOriginalFilesWhenDirectoryWasReplacedOrProofMissing(bool postgres, bool missing, bool apply)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var (stages, _, _, root) = CreateSharedBudgetServices(f);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync("root-recovery:" + Guid.NewGuid().ToString("N"),
            Path.Combine(root, "original.tmp"), 4, default)).Ticket);
        try
        {
            await stages.WriteCheckpointedFileAsync(ticket, async (stream, token) => await stream.WriteAsync(bytes, token), default);
        }
        finally { await ticket.CloseWriterAsync(); }
        var marker = Path.Combine(root, ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");
        var markerBytes = await File.ReadAllBytesAsync(marker);
        var before = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.NotNull(before.StageRootIdentity);
        if (missing)
            await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.StageRootIdentity, (string?)null));
        else
        {
            var retained = Path.Combine(f.Root, "retained-objects");
            var movedFile = Path.Combine(retained, "original.tmp");
            var movedMarkers = Path.Combine(retained, ".stage-writer-fences-v1");
            var newMarkers = Path.GetDirectoryName(marker)!;
            var prefix = Path.GetFullPath(f.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var path in new[] { root, retained, movedFile, movedMarkers, newMarkers, ticket.AbsolutePath })
                Assert.StartsWith(prefix, Path.GetFullPath(path));
            Assert.False(Directory.Exists(retained));
            Directory.Move(root, retained);
            Directory.CreateDirectory(root);
            // Move the ORIGINAL file objects back. Only the root object differs;
            // neither file identity, bytes, locator nor capacity group is replaced.
            Directory.Move(movedMarkers, newMarkers);
            File.Move(movedFile, ticket.AbsolutePath);
            Assert.NotEqual(ticket.RootIdentity, StorageStageRootIdentity.Observe(root));
            Assert.Equal(ticket.RootIdentity, StorageStageRootIdentity.Observe(retained));
            Assert.Equal(ticket.GroupId, LocalStorageCapacityProbe.Observe(root, "owned-replacement", 0).GroupId);
        }
        using (var held = File.OpenRead(marker)) Assert.Equal(ticket.WriterMarkerIdentity, StorageStageFileIdentity.Observe(held.SafeFileHandle));
        using (var held = File.OpenRead(ticket.AbsolutePath))
        {
            Assert.True(StorageStageFileReceipt.TryParse(before.StageFileReceiptJson, out var proof));
            Assert.Equal(proof!.FileIdentity, StorageStageFileIdentity.Observe(held.SafeFileHandle));
        }
        var aged = DateTime.UtcNow.AddMinutes(-30);
        await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, aged));
        var report = await stages.ReconcilePendingAsync(apply, 10, TimeSpan.FromMinutes(10), default);
        Assert.Equal(1, report.Inspected);
        Assert.Equal(0, report.Eligible);
        Assert.Equal(0, report.Settled);
        Assert.Equal(1, report.Retained);
        var after = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Pending, after.Status);
        Assert.Equal(missing ? null : before.StageRootIdentity, after.StageRootIdentity);
        Assert.Equal(before.StageFileReceiptJson, after.StageFileReceiptJson);
        Assert.Equal(before.StageWriterMarkerIdentity, after.StageWriterMarkerIdentity);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ticket.AbsolutePath));
        Assert.Equal(markerBytes, await File.ReadAllBytesAsync(marker));
        Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
    }
}
