using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageRuntimeContextsReuseInternalServicesWithScopedFaultInterceptors(bool postgres)
    {
        await using var fixture = await WriterProcessFixture.CreateAsync(postgres, new PreparationReadObserver());
        await using var fresh = fixture.NewContext();
        await using var anotherFresh = fixture.NewContext();
        await using var faultContext = fixture.NewContext(new PreparationReadObserver());
        // Provider registration order is identical; scoped fault instances must not fragment the cache.
        Assert.Same(fresh.GetService<IMemoryCache>(), anotherFresh.GetService<IMemoryCache>());
        Assert.Same(fixture.Db.GetService<IMemoryCache>(), fresh.GetService<IMemoryCache>());
        Assert.Same(fixture.Db.GetService<IMemoryCache>(), faultContext.GetService<IMemoryCache>());
        Assert.Equal("false", await faultContext.SystemSettings.Where(row => row.Key == "notifications.email.enabled")
            .Select(row => row.Value).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryEndsDurablePreparationBeforeDelayedProducerCanIssueWriter(bool postgres)
    {
        var pause = new PreparationPause();
        await using var f = await WriterProcessFixture.CreateAsync(postgres, pause);
        Directory.CreateDirectory(Path.Combine(f.Root, "objects"));
        var path = Path.Combine(f.Root, "objects", "preparing.tmp");
        pause.Armed = true;
        var admission = f.Stages.ReserveAsync("prepare:delayed", path, 4, default);
        try
        {
            await pause.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var fresh = f.NewContext();
            var row = await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(row.StageWriterAdmissionPending);
            Assert.False(Directory.Exists(Path.Combine(f.Root, "objects", ".stage-writer-fences-v1")));
            var old = DateTime.UtcNow.AddHours(-1);
            await fresh.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAt, old));
            var recovery = f.CreateStages(fresh);
            var preview = await recovery.ReconcilePendingAsync(false, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal("eligibleMissingStage", Assert.Single(preview.Items).Status);
            Assert.Equal(1, preview.Eligible);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await fresh.StorageCapacityReservations.Select(item => item.Status).SingleAsync());
            Assert.Equal(1, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
            var applied = await recovery.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal("releasedMissingStage", Assert.Single(applied.Items).Status);
            Assert.Equal(1, applied.Settled);
            pause.ResumePreparation.TrySetResult();
            var result = await admission.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(result.Ticket);
            Assert.Equal("Stage preparation ended before writer activation; retry the upload.", result.Error);
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.Combine(f.Root, "objects", ".stage-writer-fences-v1")));
            Assert.Equal(StorageCapacityReservationStatuses.Released, await fresh.StorageCapacityReservations.Select(item => item.Status).SingleAsync());
            Assert.True(await fresh.StorageCapacityReservations.Select(item => item.StageWriterAdmissionPending).SingleAsync());
            Assert.Equal(2, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
            Assert.Equal(0, (await recovery.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), default)).Inspected);
            Assert.Empty(await fresh.StorageObjects.AsNoTracking().ToArrayAsync());
            Assert.Empty(await fresh.StorageProductBindings.AsNoTracking().ToArrayAsync());
        }
        finally { pause.ResumePreparation.TrySetResult(); var result = await admission.WaitAsync(TimeSpan.FromSeconds(15)); if (result.Ticket is not null) await result.Ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationRowLockPreventsRecoveryFromReleasingAWriter(bool postgres)
    {
        var pause = new PreparationPause();
        var activation = new PreparationActivationPause();
        await using var f = await WriterProcessFixture.CreateAsync(postgres, pause, activation);
        Directory.CreateDirectory(Path.Combine(f.Root, "objects"));
        pause.Armed = true;
        activation.Armed = true;
        var admission = f.Stages.ReserveAsync("prepare:activation-wins", Path.Combine(f.Root, "objects", "active.tmp"), 4, default);
        Task<StorageStageRecoveryReport>? recovering = null;
        StorageStageCapacityTicket? ticket = null;
        try
        {
            await pause.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var old = DateTime.UtcNow.AddHours(-1);
            await using (var age = f.NewContext())
                await age.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAt, old));
            pause.ResumePreparation.TrySetResult();
            await activation.Activating.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var observer = new PreparationReadObserver();
            recovering = Task.Run(async () =>
            {
                await using var db = f.NewContext(observer);
                using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                return await f.CreateStages(db).ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), bound.Token);
            });
            await observer.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            Assert.False(recovering.IsCompleted);
            activation.Resume.TrySetResult();
            var result = await admission.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(result.Error);
            ticket = Assert.IsType<StorageStageCapacityTicket>(result.Ticket);
            var report = await recovering.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, report.Settled);
            Assert.Equal(0, report.Eligible);
            Assert.Equal("writerNotProvenStopped", Assert.Single(report.Items).Status);
            Assert.True(File.Exists(MarkerPath(f.Root, ticket)));
            Assert.False(File.Exists(ticket.AbsolutePath));
            await using var verify = f.NewContext();
            var row = await verify.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.False(row.StageWriterAdmissionPending);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
            Assert.Equal(1, await verify.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
        }
        finally
        {
            pause.ResumePreparation.TrySetResult();
            activation.Resume.TrySetResult();
            var result = await admission.WaitAsync(TimeSpan.FromSeconds(15));
            if (result.Ticket is not null) await result.Ticket.CloseWriterAsync();
            if (recovering is not null) await recovering.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PreparingRecoveryRetainsExistingBytesAndLegacyMissingMarker(bool postgres, bool legacy)
    {
        var pause = new PreparationPause();
        await using var f = await WriterProcessFixture.CreateAsync(postgres, pause);
        Directory.CreateDirectory(Path.Combine(f.Root, "objects"));
        var path = Path.Combine(f.Root, "objects", "retained.tmp");
        pause.Armed = true;
        var admission = f.Stages.ReserveAsync("prepare:retained", path, 4, default);
        try
        {
            await pause.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = f.NewContext();
            var old = DateTime.UtcNow.AddHours(-1);
            await db.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.UpdatedAt, old)
                .SetProperty(item => item.StageWriterAdmissionPending, !legacy));
            if (!legacy) await File.WriteAllBytesAsync(path, [7, 8]);
            var before = JsonSerializer.Serialize(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
            var stages = f.CreateStages(db);
            var preview = await stages.ReconcilePendingAsync(false, 10, TimeSpan.FromMinutes(10), default);
            var applied = await stages.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(0, preview.Eligible); Assert.Equal(0, applied.Settled);
            Assert.Equal("writerNotProvenStopped", Assert.Single(applied.Items).Status);
            Assert.Equal(before, JsonSerializer.Serialize(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync()));
            Assert.Equal(1, await db.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
            if (!legacy) Assert.Equal(new byte[] { 7, 8 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            pause.ResumePreparation.TrySetResult();
            var result = await admission.WaitAsync(TimeSpan.FromSeconds(15));
            if (result.Ticket is not null) await result.Ticket.CloseWriterAsync();
        }
    }

    private sealed class PreparationPause : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        private int _commits;
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumePreparation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed) _commits++;
            return ValueTask.FromResult(result);
        }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Armed && _commits == 1)
            {
                Prepared.TrySetResult();
                await ResumePreparation.Task.WaitAsync(cancellationToken);
            }
        }
    }
    private sealed class PreparationReadObserver : DbCommandInterceptor
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("StorageCapacityReservations", StringComparison.Ordinal)) ReadStarted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class PreparationActivationPause : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Activating { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("StageWriterAdmissionPending", StringComparison.Ordinal))
            {
                Armed = false;
                Activating.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
