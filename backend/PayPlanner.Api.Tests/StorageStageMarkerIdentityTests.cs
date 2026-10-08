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
    public async Task ReplacedMarkerWithEqualProofDoesNotEstablishOriginalWriterQuiescence(bool postgres, bool apply)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var ticket = await ReserveCheckpointAsync(f, 4);
        await ticket.CloseWriterAsync();
        var marker = Path.Combine(f.Root, "objects", ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");
        var bytes = await File.ReadAllBytesAsync(marker);
        string original;
        await using (var file = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
            original = Assert.IsType<string>(StorageStageFileIdentity.Observe(file.SafeFileHandle));
        Assert.Equal(original, ticket.WriterMarkerIdentity);
        Assert.Equal(original, await f.Db.StorageCapacityReservations.Select(r => r.StageWriterMarkerIdentity).SingleAsync());
        var displaced = marker + ".displaced";
        File.Move(marker, displaced);
        await File.WriteAllBytesAsync(marker, bytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await using (var replacement = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.NotEqual(original, Assert.IsType<string>(StorageStageFileIdentity.Observe(replacement.SafeFileHandle)));
        var old = DateTime.UtcNow.AddMinutes(-30);
        await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, old));
        await using var fresh = f.NewContext();
        var recovery = f.CreateStages(fresh);
        var report = await recovery.ReconcilePendingAsync(apply, 10, TimeSpan.FromMinutes(10), default);
        Assert.Equal("writerNotProvenStopped", Assert.Single(report.Items).Status);
        Assert.Equal(0, report.Eligible);
        Assert.Equal(0, report.Settled);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(ticket.Key));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(marker));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(displaced));
        Assert.False(File.Exists(ticket.AbsolutePath));
        Assert.Equal(0, await fresh.StorageObjects.CountAsync());
    }
}
