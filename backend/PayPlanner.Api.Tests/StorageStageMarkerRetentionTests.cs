using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MarkerMaintenanceRetainsUnknownOrReplacedIdentity(bool postgres, bool replace)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var ticket = await ReserveCheckpointAsync(f, 4);
        await ticket.CloseWriterAsync();
        var marker = Path.Combine(f.Root, "objects", ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");
        var bytes = await File.ReadAllBytesAsync(marker);
        var displaced = marker + ".displaced";
        if (replace)
        {
            File.Move(marker, displaced);
            await File.WriteAllBytesAsync(marker, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        else await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.StageWriterMarkerIdentity, (string?)null));
        var old = DateTime.UtcNow.AddMinutes(-30);
        await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, StorageCapacityReservationStatuses.Released)
            .SetProperty(r => r.ReleasedAt, old).SetProperty(r => r.UpdatedAt, old));
        var before = JsonSerializer.Serialize(await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync());
        await using var fresh = f.NewContext();
        var stages = f.CreateStages(fresh);
        foreach (var apply in new[] { false, true })
        {
            var report = await stages.ReconcileReleasedWriterMarkersAsync(apply, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal("markerNotProvenStopped", Assert.Single(report.Items).Status);
            Assert.Equal(0, report.Eligible);
            Assert.Equal(0, report.Removed);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(marker));
            if (replace) Assert.Equal(bytes, await File.ReadAllBytesAsync(displaced));
            Assert.Equal(before, JsonSerializer.Serialize(await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync()));
        }
        Assert.False(File.Exists(ticket.AbsolutePath));
        Assert.Equal(0, await fresh.StorageObjects.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingMarkerWithoutDurableNativeIdentityRetainsDebt(bool postgres)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var ticket = await ReserveCheckpointAsync(f, 4);
        await ticket.CloseWriterAsync();
        var old = DateTime.UtcNow.AddMinutes(-30);
        await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.StageWriterMarkerIdentity, (string?)null)
            .SetProperty(r => r.UpdatedAt, old));
        var before = JsonSerializer.Serialize(await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync());
        await using var fresh = f.NewContext();
        var stages = f.CreateStages(fresh);
        foreach (var apply in new[] { false, true })
        {
            var report = await stages.ReconcilePendingAsync(apply, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal("writerNotProvenStopped", Assert.Single(report.Items).Status);
            Assert.Equal(0, report.Eligible);
            Assert.Equal(0, report.Settled);
            Assert.Equal(before, JsonSerializer.Serialize(await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync()));
        }
    }
}
