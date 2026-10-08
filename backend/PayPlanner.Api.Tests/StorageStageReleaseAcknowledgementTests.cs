using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task StageReleaseLostAcknowledgementUsesFreshExactState(bool postgres, bool physical, bool rejectBeforeCommit)
    {
        var failure = new StageReleaseCommitFault(rejectBeforeCommit);
        await using var fixture = await CreateWriterFixtureAsync(postgres, failure);
        var directory = Path.Combine(fixture.Root, "objects", ".ack-stage");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "original.tmp");
        var admission = await fixture.Stages.ReserveAsync("release-ack:" + Guid.NewGuid().ToString("N"), path, 4, default);
        Assert.Null(admission.Error);
        var ticket = Assert.IsType<PayPlanner.Api.Services.Storage.StorageStageCapacityTicket>(admission.Ticket);
        var marker = Path.Combine(fixture.Root, "objects", ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");
        try
        {
            if (physical) await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            var epochBefore = await fixture.Db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync();
            failure.Armed = true;
            Task Release() => physical ? fixture.Stages.SettlePhysicalAsync(ticket, default) : fixture.Stages.ReleaseAsync(ticket, default);
            if (rejectBeforeCommit) await Assert.ThrowsAsync<IOException>(Release);
            else await Release();
            Assert.False(failure.Armed);
            Assert.Null(fixture.Db.Database.CurrentTransaction);
            Assert.Equal(rejectBeforeCommit ? StorageCapacityReservationStatuses.Pending : StorageCapacityReservationStatuses.Released,
                await fixture.StatusAsync(ticket.Key));
            Assert.Equal(epochBefore + (rejectBeforeCommit ? 0 : 1),
                await fixture.Db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync());
            Assert.Equal(rejectBeforeCommit, File.Exists(marker));
            Assert.Equal(physical, File.Exists(path));
            if (physical) Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(1, await fixture.Db.StorageCapacityReservations.CountAsync());
            Assert.Equal(0, await fixture.Db.StorageObjects.CountAsync());
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false, "binding")]
    [InlineData(true, false, "binding")]
    [InlineData(false, true, "binding")]
    [InlineData(true, true, "binding")]
    [InlineData(false, false, "unavailable")]
    [InlineData(true, false, "unavailable")]
    [InlineData(false, true, "unavailable")]
    [InlineData(true, true, "unavailable")]
    public async Task StageReleaseLostAcknowledgementRetainsMarkerWithoutExactProof(bool postgres, bool physical, string damage)
    {
        var failure = new StageReleaseCommitFault(false);
        var proof = new StageReleaseProofReadFault(failure, damage == "unavailable");
        await using var fixture = await CreateWriterFixtureAsync(postgres, failure, proof);
        var directory = Path.Combine(fixture.Root, "objects", ".ack-stage");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "original.tmp");
        var admission = await fixture.Stages.ReserveAsync("release-proof:" + Guid.NewGuid().ToString("N"), path, 4, default);
        Assert.Null(admission.Error);
        var ticket = Assert.IsType<PayPlanner.Api.Services.Storage.StorageStageCapacityTicket>(admission.Ticket);
        var marker = Path.Combine(fixture.Root, "objects", ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");
        try
        {
            if (physical) await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            var epochBefore = await fixture.Db.StorageCapacityEpochs.Select(row => row.Version).SingleAsync();
            if (damage == "binding")
            {
                failure.AfterCommit = async (eventData, ct) =>
                {
                    await using var command = eventData.Context!.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "UPDATE \"StorageCapacityReservations\" SET \"BindingHash\" = @binding WHERE \"IdempotencyKey\" = @key";
                    var binding = command.CreateParameter(); binding.ParameterName = "binding"; binding.Value = new string('f', 64);
                    var key = command.CreateParameter(); key.ParameterName = "key"; key.Value = ticket.Key;
                    command.Parameters.Add(binding); command.Parameters.Add(key);
                    Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
                };
            }
            failure.Armed = true;
            Task Release() => physical ? fixture.Stages.SettlePhysicalAsync(ticket, default) : fixture.Stages.ReleaseAsync(ticket, default);
            var error = await Assert.ThrowsAsync<IOException>(Release);
            Assert.Equal("Simulated lost acknowledgement after stage release commit.", error.Message);
            proof.Suppressed = true;
            Assert.Equal(damage == "unavailable" ? 1 : 0, proof.Interceptions);
            Assert.Null(fixture.Db.Database.CurrentTransaction);
            var row = await fixture.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
            Assert.Equal(damage == "binding" ? new string('f', 64) : ticket.BindingHash, row.BindingHash);
            Assert.Equal(epochBefore + 1, await fixture.Db.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
            Assert.True(File.Exists(marker));
            Assert.Equal(physical, File.Exists(path));
            if (physical) Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(0, await fixture.Db.StorageObjects.CountAsync());
        }
        finally { proof.Suppressed = true; await ticket.CloseWriterAsync(); }
    }

    private sealed class StageReleaseProofReadFault(StageReleaseCommitFault failure, bool enabled) : DbCommandInterceptor
    {
        public bool Suppressed { get; set; }
        public int Interceptions { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (enabled && !Suppressed && failure.AfterCommitFaultTriggered)
            {
                Interceptions++;
                throw new IOException("Simulated unavailable fresh release proof.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StageReleaseCommitFault(bool rejectBeforeCommit) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool AfterCommitFaultTriggered { get; private set; }
        public Func<TransactionEndEventData, CancellationToken, Task>? AfterCommit { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && rejectBeforeCommit)
            {
                Armed = false;
                throw new IOException("Simulated rejection before stage release commit.");
            }
            return ValueTask.FromResult(result);
        }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Armed && !rejectBeforeCommit)
            {
                Armed = false;
                if (AfterCommit is not null) await AfterCommit(eventData, cancellationToken);
                AfterCommitFaultTriggered = true;
                throw new IOException("Simulated lost acknowledgement after stage release commit.");
            }
        }
    }
}
