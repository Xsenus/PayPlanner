using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageReservationRefusesRootObjectReplacementDuringAdmission(bool postgres)
    {
        var swap = new RootReplacementAfterAdmissionCommit();
        await using var f = await CreateWriterFixtureAsync(postgres, swap);
        var (stages, _, _, root) = CreateSharedBudgetServices(f);
        var retainedRoot = Path.Combine(f.Root, "retained-objects");
        var sentinel = Path.Combine(root, "owned-before-root-swap.bin");
        var original = new byte[] { 7, 8, 9 };
        await File.WriteAllBytesAsync(sentinel, original);
        var group = LocalStorageCapacityProbe.Observe(root, "owned-root-before", 0).GroupId;
        Assert.False(string.IsNullOrWhiteSpace(group));
        var fixturePrefix = Path.GetFullPath(f.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(fixturePrefix, Path.GetFullPath(root));
        Assert.StartsWith(fixturePrefix, Path.GetFullPath(retainedRoot));
        Assert.False(Directory.Exists(retainedRoot));
        swap.Action = () =>
        {
            // First admission committed; no writer marker or stage descriptor
            // exists yet. Move only this fixture's own synthetic directory.
            Assert.False(Directory.Exists(Path.Combine(root, ".stage-writer-fences-v1")));
            Directory.Move(root, retainedRoot);
            Directory.CreateDirectory(root);
            Assert.Equal(group, LocalStorageCapacityProbe.Observe(root, "owned-root-after", 0).GroupId);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(retainedRoot, Path.GetFileName(sentinel))));
            Assert.False(File.Exists(sentinel));
        };
        StorageStageCapacityTicket? ticket = null;
        try
        {
            var admission = await stages.ReserveAsync("root-replacement:" + Guid.NewGuid().ToString("N"),
                Path.Combine(root, "owned-stage.tmp"), 4, default);
            ticket = admission.Ticket;
            Assert.Equal(1, swap.Calls);
            Assert.Null(ticket);
            Assert.NotNull(admission.Error);
            Assert.False(File.Exists(Path.Combine(root, "owned-stage.tmp")));
            Assert.Equal(StorageCapacityReservationStatuses.Pending,
                await f.Db.StorageCapacityReservations.AsNoTracking().Select(r => r.Status).SingleAsync());
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(retainedRoot, Path.GetFileName(sentinel))));
            Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
        }
        finally { swap.Action = null; if (ticket is not null) await ticket.CloseWriterAsync(); }
    }

    private sealed class RootReplacementAfterAdmissionCommit : DbTransactionInterceptor
    {
        internal Action? Action;
        internal int Calls;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref Action, null) is { } action) { Calls++; action(); }
            return Task.CompletedTask;
        }
    }
}
