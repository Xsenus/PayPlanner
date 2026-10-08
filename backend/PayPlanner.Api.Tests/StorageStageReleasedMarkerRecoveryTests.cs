using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReleasedWriterMarkerRecoversUnknownAcknowledgementWithoutChangingDebtOrBytes(bool postgres, bool physical)
    {
        var failure = new StageReleaseCommitFault(false);
        var proof = new StageReleaseProofReadFault(failure, true);
        await using var fixture = await CreateWriterFixtureAsync(postgres, failure, proof);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "objects"));
        var path = Path.Combine(fixture.Root, "objects", "original.tmp");
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await fixture.Stages.ReserveAsync("marker:released", path, 4, default)).Ticket);
        StorageStageCapacityTicket? pending = null;
        var marker = MarkerPath(fixture.Root, ticket);
        try
        {
            if (physical) await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            failure.Armed = true;
            await Assert.ThrowsAsync<IOException>(() => physical
                ? fixture.Stages.SettlePhysicalAsync(ticket, default) : fixture.Stages.ReleaseAsync(ticket, default));
            await ticket.CloseWriterAsync();
            proof.Suppressed = true;
            Assert.Equal(1, proof.Interceptions);
            Assert.True(File.Exists(marker));
            var old = DateTime.UtcNow.AddHours(-12);
            await fixture.Db.StorageCapacityReservations.Where(row => row.IdempotencyKey == ticket.Key)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReleasedAt, old).SetProperty(row => row.UpdatedAt, old));
            pending = Assert.IsType<StorageStageCapacityTicket>((await fixture.Stages.ReserveAsync("marker:pending",
                Path.Combine(fixture.Root, "objects", "pending.tmp"), 4, default)).Ticket);
            var before = JsonSerializer.Serialize(await fixture.Db.StorageCapacityReservations.AsNoTracking().OrderBy(row => row.IdempotencyKey).ToArrayAsync());
            var epoch = await fixture.Db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync();
            var preview = await fixture.Stages.ReconcileReleasedWriterMarkersAsync(false, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(1, preview.Inspected);
            Assert.Equal(1, preview.Eligible);
            Assert.Equal(0, preview.Removed);
            Assert.Equal("eligibleReleasedMarker", Assert.Single(preview.Items).Status);
            Assert.True(File.Exists(marker));
            var applied = await fixture.Stages.ReconcileReleasedWriterMarkersAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(1, applied.Removed);
            Assert.Equal("releasedMarkerRemoved", Assert.Single(applied.Items).Status);
            Assert.False(File.Exists(marker));
            Assert.True(File.Exists(MarkerPath(fixture.Root, pending)));
            var repeat = await fixture.Stages.ReconcileReleasedWriterMarkersAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(0, repeat.Eligible);
            Assert.Equal(0, repeat.Removed);
            Assert.Equal("markerAlreadyAbsent", Assert.Single(repeat.Items).Status);
            Assert.Equal(before, JsonSerializer.Serialize(await fixture.Db.StorageCapacityReservations.AsNoTracking().OrderBy(row => row.IdempotencyKey).ToArrayAsync()));
            Assert.Equal(epoch, await fixture.Db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync());
            Assert.Equal(physical, File.Exists(path));
            if (physical) Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
            Assert.Empty(await fixture.Db.StorageObjects.ToArrayAsync());
            Assert.Empty(await fixture.Db.StorageProductBindings.ToArrayAsync());
        }
        finally { proof.Suppressed = true; if (pending is not null) await pending.CloseWriterAsync(); await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, "active")]
    [InlineData(true, "active")]
    [InlineData(false, "tampered")]
    [InlineData(true, "tampered")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "foreignBytes")]
    [InlineData(true, "foreignBytes")]
    public async Task ReleasedMarkerCleanupNeverUsesAgeAsWriterOrByteOwnershipProof(bool postgres, string state)
    {
        await using var fixture = await CreateWriterFixtureAsync(postgres);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "objects"));
        var path = Path.Combine(fixture.Root, "objects", "foreign.tmp");
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await fixture.Stages.ReserveAsync("marker:" + state, path, 4, default)).Ticket);
        var marker = MarkerPath(fixture.Root, ticket);
        try
        {
            var old = DateTime.UtcNow.AddHours(-12);
            await fixture.Db.StorageCapacityReservations.ExecuteUpdateAsync(setters =>
                setters.SetProperty(row => row.Status, StorageCapacityReservationStatuses.Released)
                    .SetProperty(row => row.ReleasedAt, old).SetProperty(row => row.UpdatedAt, old));
            if (state != "active") await ticket.CloseWriterAsync();
            if (state == "tampered") await File.WriteAllTextAsync(marker, new string('f', 64));
            if (state == "missing") File.Delete(marker);
            if (state == "foreignBytes") await File.WriteAllBytesAsync(path, [9, 8]);
            var epoch = await fixture.Db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync();
            var applied = await fixture.Stages.ReconcileReleasedWriterMarkersAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(state == "foreignBytes" ? 1 : 0, applied.Removed);
            Assert.Equal(state switch { "foreignBytes" => "releasedMarkerRemoved", "missing" => "markerAlreadyAbsent", _ => "markerNotProvenStopped" },
                Assert.Single(applied.Items).Status);
            Assert.Equal(state is "active" or "tampered", File.Exists(marker));
            if (state == "tampered") Assert.Equal(new string('f', 64), await File.ReadAllTextAsync(marker));
            if (state == "foreignBytes") Assert.Equal(new byte[] { 9, 8 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(epoch, await fixture.Db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync());
            Assert.Equal(StorageCapacityReservationStatuses.Released, await fixture.StatusAsync(ticket.Key));
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReleasedMarkerRequiresFreshUnchangedReceiptAfterAcquiringFence(bool postgres, bool unavailable)
    {
        var fault = new MarkerFreshProofFault(unavailable);
        await using var fixture = await CreateWriterFixtureAsync(postgres, fault);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "objects"));
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await fixture.Stages.ReserveAsync("marker:fresh",
            Path.Combine(fixture.Root, "objects", "fresh.tmp"), 4, default)).Ticket);
        try
        {
            await ticket.CloseWriterAsync();
            var old = DateTime.UtcNow.AddHours(-12);
            await fixture.Db.StorageCapacityReservations.ExecuteUpdateAsync(setters =>
                setters.SetProperty(row => row.Status, StorageCapacityReservationStatuses.Released)
                    .SetProperty(row => row.ReleasedAt, old).SetProperty(row => row.UpdatedAt, old));
            fault.Armed = true;
            var result = await fixture.Stages.ReconcileReleasedWriterMarkersAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(1, fault.Interceptions);
            Assert.Equal(0, result.Eligible);
            Assert.Equal(0, result.Removed);
            Assert.Equal(unavailable ? "markerNotProvenStopped" : "changedAuthority", Assert.Single(result.Items).Status);
            Assert.True(File.Exists(MarkerPath(fixture.Root, ticket)));
        }
        finally { fault.Armed = false; await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedMarkerPagesAreBoundedAndDirtyUnitsAndInvalidCursorsAreRejected(bool postgres)
    {
        await using var fixture = await CreateWriterFixtureAsync(postgres);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "objects"));
        var tickets = new List<StorageStageCapacityTicket>();
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var ticket = Assert.IsType<StorageStageCapacityTicket>((await fixture.Stages.ReserveAsync("marker:page:" + index,
                    Path.Combine(fixture.Root, "objects", index + ".tmp"), 4, default)).Ticket);
                tickets.Add(ticket);
                await ticket.CloseWriterAsync();
            }
            var old = DateTime.UtcNow.AddHours(-12);
            await fixture.Db.StorageCapacityReservations.ExecuteUpdateAsync(setters =>
                setters.SetProperty(row => row.Status, StorageCapacityReservationStatuses.Released)
                    .SetProperty(row => row.ReleasedAt, old).SetProperty(row => row.UpdatedAt, old));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Stages.ReconcileReleasedWriterMarkersAsync(true, 0, TimeSpan.FromMinutes(10), default));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Stages.ReconcileReleasedWriterMarkersAsync(true, 1, TimeSpan.FromMinutes(9), default));
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Stages.ReconcileReleasedWriterMarkersAsync(true, 1, TimeSpan.FromMinutes(10), default, "stage:" + new string('a', 64)));
            var client = new Client { Name = "Unsaved marker unit" };
            fixture.Db.Clients.Add(client);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stages.ReconcileReleasedWriterMarkersAsync(true, 1, TimeSpan.FromMinutes(10), default));
            fixture.Db.Entry(client).State = EntityState.Detached;
            var keys = new List<string>();
            string? after = null;
            for (var index = 0; index < 3; index++)
            {
                var page = await fixture.Stages.ReconcileReleasedWriterMarkersAsync(true, 1, TimeSpan.FromMinutes(10), default, after);
                Assert.Equal(1, page.Inspected);
                Assert.Equal(1, page.Removed);
                Assert.Equal(index < 2, page.HasMore);
                keys.Add(Assert.Single(page.Items).Key);
                after = page.NextAfterKey;
            }
            Assert.Equal(tickets.Select(ticket => ticket.Key).OrderBy(key => key), keys);
            Assert.All(tickets, ticket => Assert.False(File.Exists(MarkerPath(fixture.Root, ticket))));
            Assert.Empty(await fixture.Db.Clients.ToArrayAsync());
        }
        finally { foreach (var ticket in tickets) await ticket.CloseWriterAsync(); }
    }

    private static string MarkerPath(string root, StorageStageCapacityTicket ticket)
        => Path.Combine(root, "objects", ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");

    private sealed class MarkerFreshProofFault(bool unavailable) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int Interceptions { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Armed || !command.CommandText.Contains("EXISTS", StringComparison.Ordinal)) return result;
            Armed = false;
            Interceptions++;
            if (unavailable) throw new IOException("Synthetic released marker fresh proof unavailable.");
            await using var mutation = command.Connection!.CreateCommand();
            mutation.CommandText = "UPDATE \"StorageCapacityReservations\" SET \"BindingHash\" = @binding";
            var binding = mutation.CreateParameter(); binding.ParameterName = "binding"; binding.Value = new string('f', 64);
            mutation.Parameters.Add(binding);
            Assert.Equal(1, await mutation.ExecuteNonQueryAsync(cancellationToken));
            return result;
        }
    }
}
