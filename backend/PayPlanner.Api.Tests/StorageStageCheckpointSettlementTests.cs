using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CheckpointSettlementFreshTransactionRejectsChangedAdmission(bool postgres, bool confirm)
    {
        var transition = new CheckpointAdmissionTransition();
        await using var f = await CreateWriterFixtureAsync(postgres, transition);
        var ticket = await ReserveCheckpointAsync(f);
        var bytes = new byte[] { 1, 2, 3, 4 };
        try
        {
            await f.Stages.WriteCheckpointedFileAsync(ticket, async (stream, ct) => await stream.WriteAsync(bytes, ct), default);
            transition.Key = ticket.Key;
            if (confirm) await Assert.ThrowsAsync<InvalidOperationException>(() => f.Stages.ConfirmPhysicalSizeAsync(ticket, bytes.Length, default));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => f.Stages.SettlePhysicalAsync(ticket, default));
            Assert.Null(transition.Key);
            Assert.Null(f.Db.Database.CurrentTransaction);
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(row.StageWriterAdmissionPending);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
            Assert.Null(row.ProductContextJson);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(ticket.AbsolutePath));
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    private sealed class CheckpointAdmissionTransition : DbTransactionInterceptor
    {
        public string? Key;
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData data, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            var key = Key;
            Key = null;
            if (key is not null)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE \"StorageCapacityReservations\" SET \"StageWriterAdmissionPending\" = @pending WHERE \"IdempotencyKey\" = @key";
                var pending = command.CreateParameter(); pending.ParameterName = "pending"; pending.Value = true;
                var target = command.CreateParameter(); target.ParameterName = "key"; target.Value = key;
                command.Parameters.Add(pending); command.Parameters.Add(target);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointSettlementRejectsUnactivatedReservation(bool postgres)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var ticket = await ReserveCheckpointAsync(f);
        var bytes = new byte[] { 1, 2, 3, 4 };
        try
        {
            await f.Stages.WriteCheckpointedFileAsync(ticket, async (stream, ct) => await stream.WriteAsync(bytes, ct), default);
            Assert.Equal(1, await f.Db.StorageCapacityReservations.Where(r => r.IdempotencyKey == ticket.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.StageWriterAdmissionPending, true)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Stages.SettlePhysicalAsync(ticket, default));
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(row.StageWriterAdmissionPending);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(ticket.AbsolutePath));
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false, "settle")]
    [InlineData(false, false, "confirm")]
    [InlineData(false, false, "recovery")]
    [InlineData(false, true, "settle")]
    [InlineData(false, true, "confirm")]
    [InlineData(false, true, "recovery")]
    [InlineData(true, false, "settle")]
    [InlineData(true, false, "confirm")]
    [InlineData(true, false, "recovery")]
    [InlineData(true, true, "settle")]
    [InlineData(true, true, "confirm")]
    [InlineData(true, true, "recovery")]
    [InlineData(false, false, "dry-run")]
    [InlineData(false, true, "dry-run")]
    [InlineData(true, false, "dry-run")]
    [InlineData(true, true, "dry-run")]
    public async Task CheckpointSettlementRetainsDebtForReplacedOrChangedFile(bool postgres, bool replacement, string operation)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var ticket = await ReserveCheckpointAsync(f);
        var bytes = new byte[] { 1, 2, 3, 4 };
        try
        {
            await f.Stages.WriteCheckpointedFileAsync(ticket, async (stream, ct) => await stream.WriteAsync(bytes, ct), default);
            await ticket.CloseWriterAsync();
            if (replacement)
            {
                File.Move(ticket.AbsolutePath, ticket.AbsolutePath + ".owned-original");
                await File.WriteAllBytesAsync(ticket.AbsolutePath, bytes);
            }
            else await File.WriteAllBytesAsync(ticket.AbsolutePath, new byte[] { 4, 3, 2, 1 });
            if (operation is "recovery" or "dry-run")
            {
                var agedAt = DateTime.UtcNow.AddMinutes(-30);
                await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, agedAt));
                var report = await f.Stages.ReconcilePendingAsync(operation == "recovery", 10, TimeSpan.FromMinutes(10), default);
                Assert.Equal(0, report.Settled);
                Assert.Equal(0, report.Eligible);
                Assert.Equal(1, report.Retained);
            }
            else if (operation == "confirm") await Assert.ThrowsAsync<InvalidOperationException>(() => f.Stages.ConfirmPhysicalSizeAsync(ticket, bytes.Length, default));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => f.Stages.SettlePhysicalAsync(ticket, default));
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
            Assert.Equal(4, row.Bytes);
            Assert.Equal(replacement ? bytes : new byte[] { 4, 3, 2, 1 }, await File.ReadAllBytesAsync(ticket.AbsolutePath));
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, "before")]
    [InlineData(true, "before")]
    [InlineData(false, "after")]
    [InlineData(true, "after")]
    [InlineData(false, "changed")]
    [InlineData(true, "changed")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "unavailable")]
    [InlineData(false, "inactive")]
    [InlineData(true, "inactive")]
    public async Task CheckpointSettlementLostAcknowledgementRequiresSameReceipt(bool postgres, string outcome)
    {
        var failure = new StageReleaseCommitFault(outcome == "before");
        var reads = new StageReleaseProofReadFault(failure, outcome == "unavailable");
        await using var f = await CreateWriterFixtureAsync(postgres, failure, reads);
        var ticket = await ReserveCheckpointAsync(f);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var marker = Path.Combine(f.Root, "objects", ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");
        try
        {
            await f.Stages.WriteCheckpointedFileAsync(ticket, async (stream, ct) => await stream.WriteAsync(bytes, ct), default);
            var proof = await f.Db.StorageCapacityReservations.Select(r => r.StageFileReceiptJson).SingleAsync();
            if (outcome is "changed" or "inactive")
            {
                failure.AfterCommit = async (data, ct) =>
                {
                    await using var command = data.Context!.Database.GetDbConnection().CreateCommand();
                    command.CommandText = outcome == "changed"
                        ? "UPDATE \"StorageCapacityReservations\" SET \"StageFileReceiptJson\" = NULL WHERE \"IdempotencyKey\" = @key"
                        : "UPDATE \"StorageCapacityReservations\" SET \"StageWriterAdmissionPending\" = TRUE WHERE \"IdempotencyKey\" = @key";
                    var key = command.CreateParameter(); key.ParameterName = "key"; key.Value = ticket.Key;
                    command.Parameters.Add(key);
                    Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
                };
            }
            failure.Armed = true;
            if (outcome == "after") await f.Stages.SettlePhysicalAsync(ticket, default);
            else
            {
                var error = await Assert.ThrowsAsync<IOException>(() => f.Stages.SettlePhysicalAsync(ticket, default));
                Assert.Equal(outcome == "before" ? "Simulated rejection before stage release commit."
                    : "Simulated lost acknowledgement after stage release commit.", error.Message);
            }
            reads.Suppressed = true;
            Assert.Null(f.Db.Database.CurrentTransaction);
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(outcome == "before" ? StorageCapacityReservationStatuses.Pending : StorageCapacityReservationStatuses.Released, row.Status);
            Assert.Equal(outcome == "changed" ? null : proof, row.StageFileReceiptJson);
            Assert.Equal(outcome == "inactive", row.StageWriterAdmissionPending);
            Assert.Equal(outcome != "after", File.Exists(marker));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(ticket.AbsolutePath));
            Assert.Equal(outcome == "unavailable" ? 1 : 0, reads.Interceptions);
        }
        finally { reads.Suppressed = true; await ticket.CloseWriterAsync(); }
    }
}
