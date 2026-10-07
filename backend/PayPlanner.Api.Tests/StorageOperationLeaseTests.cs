using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageOperationLeaseTests
{
    [Theory]
    [InlineData("expired")]
    [InlineData("owner")]
    [InlineData("intent")]
    [InlineData("terminal")]
    [InlineData("provider")]
    [InlineData("connection")]
    [InlineData("databaseFailure")]
    [InlineData("databaseTimeout")]
    public async Task RenewalCannotResurrectOrTakeOverLeaseAndCancelsIo(string scenario)
    {
        await using var f = await Fixture.CreateAsync();
        var operation = await f.SeedAsync("first", StorageOperationStatuses.Running);
        operation.LeaseOwner = "owned";
        operation.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(5);
        await f.Db.SaveChangesAsync();
        var snapshot = new StorageOperation
        {
            Id = operation.Id,
            OperationType = operation.OperationType,
            StorageObjectId = operation.StorageObjectId,
            StorageObjectGenerationId = operation.StorageObjectGenerationId,
            ProviderKey = operation.ProviderKey,
            LeaseOwner = operation.LeaseOwner,
            LeaseExpiresAt = operation.LeaseExpiresAt
        };
        if (scenario == "expired") operation.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        if (scenario == "owner") operation.LeaseOwner = "new-owner";
        if (scenario == "intent") operation.OperationType = StorageOperationTypes.Delete;
        if (scenario == "terminal") operation.Status = StorageOperationStatuses.Succeeded;
        if (scenario == "provider") operation.ProviderKey = "changed-provider";
        if (scenario == "connection") operation.ConnectionId = "changed-connection";
        await f.Db.SaveChangesAsync();
        if (scenario == "databaseFailure") f.Factory.Fail = true;
        if (scenario == "databaseTimeout") f.Factory.BeforeCreateAsync = ct => Task.Delay(TimeSpan.FromSeconds(10), ct);
        var expiry = operation.LeaseExpiresAt;
        await using var scope = new StorageOperationLeaseScope(f.Factory, snapshot, TimeProvider.System, TimeSpan.FromMilliseconds(900), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(10), scope.Token));
        Assert.True(scope.LeaseLost);
        await scope.StopAsync();
        f.Factory.Fail = false;
        f.Factory.BeforeCreateAsync = null;
        await using var fresh = f.Factory.CreateDbContext();
        var saved = await fresh.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id);
        Assert.Equal(expiry, saved.LeaseExpiresAt);
        Assert.Equal(operation.LeaseOwner, saved.LeaseOwner);
        Assert.Equal(operation.Status, saved.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostRenewalCancelsBlockedTransferWithoutStaleReceipt(bool databaseFailure)
    {
        await using var f = await Fixture.CreateAsync();
        var operation = await f.SeedAsync("first"); f.Db.ChangeTracker.Clear(); f.Target.BlockFirst = true;
        var processing = f.Worker(f.Db).ProcessBatchAsync("old-worker", CancellationToken.None);
        await f.Target.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        if (databaseFailure) f.Factory.Fail = true;
        else
        {
            await using var competing = f.Factory.CreateDbContext();
            await competing.StorageOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.LeaseOwner, "new-worker").SetProperty(o => o.LeaseExpiresAt, DateTime.UtcNow.AddMinutes(5)));
        }
        try
        {
            var result = await processing.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, result.Succeeded);
            Assert.Empty(f.Target.Objects);
            f.Db.Clients.Add(new Client { Name = "No stale result after timer cancellation" }); await f.Db.SaveChangesAsync();
            Assert.Equal(0, await f.Db.StorageReplicas.CountAsync());
            var saved = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id);
            Assert.Equal(StorageOperationStatuses.Running, saved.Status);
            if (!databaseFailure) Assert.Equal("new-worker", saved.LeaseOwner);
        }
        finally { f.Factory.Fail = false; f.Target.Release.TrySetResult(); }
    }

    [Fact]
    public async Task UserCancellationIsPropagatedAndDoesNotContinueRenewal()
    {
        await using var f = await Fixture.CreateAsync();
        var operation = await f.SeedAsync("first"); f.Db.ChangeTracker.Clear(); f.Target.BlockFirst = true;
        using var cancellation = new CancellationTokenSource();
        var processing = f.Worker(f.Db).ProcessBatchAsync("cancelled-worker", cancellation.Token);
        await f.Target.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Empty(f.Target.Objects);
        var before = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id);
        await Task.Delay(600);
        var after = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id);
        Assert.Equal(StorageOperationStatuses.Running, after.Status);
        Assert.Equal(before.LeaseExpiresAt, after.LeaseExpiresAt);
        Assert.Equal(0, await f.Db.StorageReplicas.CountAsync());
    }

    [Fact]
    public async Task DisposedRenewalDoesNotContinueWritingOrCancelCaller()
    {
        await using var f = await Fixture.CreateAsync();
        var operation = await f.SeedAsync("first", StorageOperationStatuses.Running);
        // Expiry and timer progression are explicit: a slow fixture save must not
        // consume this test's lease before it can exercise disposal.
        var clock = new ControlledLeaseClock(DateTimeOffset.UtcNow);
        operation.LeaseOwner = "owned"; operation.LeaseExpiresAt = clock.GetUtcNow().UtcDateTime.AddMilliseconds(900);
        await f.Db.SaveChangesAsync();
        var initial = operation.LeaseExpiresAt;
        var scope = new StorageOperationLeaseScope(f.Factory, operation, clock, TimeSpan.FromMilliseconds(900), CancellationToken.None);
        var disposed = false;
        try
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            await UntilAsync(async () =>
            {
                await using var fresh = f.Factory.CreateDbContext();
                return (await fresh.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id)).LeaseExpiresAt > initial;
            });
            Assert.False(scope.LeaseLost);
            Assert.Equal(1, f.Factory.RenewalContexts);
            var token = scope.Token;
            await scope.DisposeAsync();
            disposed = true;
            Assert.False(token.IsCancellationRequested);
            Assert.Equal(0, clock.ActiveTimers);
            await using var reader = f.Factory.CreateDbContext();
            var stopped = (await reader.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id)).LeaseExpiresAt;
            Assert.True(stopped > initial);
            clock.Advance(TimeSpan.FromMinutes(1));
            await using var after = f.Factory.CreateDbContext();
            Assert.Equal(stopped, (await after.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id)).LeaseExpiresAt);
            Assert.Equal(1, f.Factory.RenewalContexts);
            Assert.False(token.IsCancellationRequested);
        }
        finally { if (!disposed) await scope.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongTransferRenewsCurrentLeaseWithoutPreclaimingNextOrDuplicatePut(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres);
        var first = await f.SeedAsync("first"); var second = await f.SeedAsync("second");
        f.Db.ChangeTracker.Clear();
        f.Target.BlockFirst = true;
        var processing = f.Worker(f.Db).ProcessBatchAsync("same-worker-name", CancellationToken.None);
        await f.Target.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        DateTime originalExpiry;
        await using (var initial = f.Factory.CreateDbContext())
        {
            originalExpiry = (await initial.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == first.Id)).LeaseExpiresAt!.Value;
            var waiting = await initial.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == second.Id);
            Assert.Equal(StorageOperationStatuses.Pending, waiting.Status);
            Assert.Null(waiting.LeaseOwner);
        }
        try
        {
            await UntilAsync(() => Task.FromResult(DateTime.UtcNow > originalExpiry.AddMilliseconds(300)), TimeSpan.FromSeconds(25));
            await using var competing = f.Factory.CreateDbContext();
            var stillRunning = await competing.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == first.Id);
            Assert.True(stillRunning.LeaseExpiresAt > DateTime.UtcNow);
            Assert.Equal(1, (await f.Worker(competing).ProcessBatchAsync("same-worker-name", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(20))).Succeeded);
        }
        finally { f.Target.Release.TrySetResult(); }
        var result = await processing.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, result.Claimed); Assert.Equal(1, result.Succeeded);
        Assert.Equal(2, f.Target.PutCalls);
        await using var settled = f.Factory.CreateDbContext();
        Assert.Equal(2, await settled.StorageOperations.CountAsync(o => o.Status == StorageOperationStatuses.Succeeded));
        Assert.Equal(2, await settled.StorageReplicas.CountAsync(r => r.ProviderKey == "target" && r.Status == StorageReplicaStatuses.Available));
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    public async Task LostOwnerAfterProviderIoCannotPublishStaleResult(string outcome)
    {
        await using var f = await Fixture.CreateAsync();
        var operation = await f.SeedAsync("first"); f.Db.ChangeTracker.Clear();
        f.Target.AfterPut = async () =>
        {
            await using var competing = f.Factory.CreateDbContext();
            await competing.StorageOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.LeaseOwner, "new-owner").SetProperty(o => o.LeaseExpiresAt, DateTime.UtcNow.AddMinutes(5)));
        };
        f.Target.Outcome = outcome == "unknown" ? StorageOperationStatus.Unknown : outcome == "unavailable" ? StorageOperationStatus.Unavailable : StorageOperationStatus.Succeeded;
        var result = await f.Worker(f.Db).ProcessBatchAsync("old-owner", CancellationToken.None);
        Assert.Equal(0, result.Succeeded);
        f.Db.Clients.Add(new Client { Name = "Unrelated save after lost owner" }); await f.Db.SaveChangesAsync();
        var saved = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id);
        Assert.Equal("new-owner", saved.LeaseOwner); Assert.Equal(StorageOperationStatuses.Running, saved.Status);
        Assert.Equal(0, await f.Db.StorageReplicas.CountAsync(r => r.ProviderKey == "target"));
    }

    private sealed class ControlledLeaseClock(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object gate = new();
        private readonly HashSet<ControlledTimer> timers = [];
        private DateTimeOffset now = utcNow;
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public int ActiveTimers { get { lock (gate) return timers.Count(timer => timer.Due is not null); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
            ControlledTimer[] ready;
            lock (gate)
            {
                now += duration;
                ready = timers.Where(timer => timer.Due is not null && timer.Due <= now).ToArray();
                foreach (var timer in ready) timer.Due = timer.Period > TimeSpan.Zero ? now + timer.Period : null;
            }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class ControlledTimer(ControlledLeaseClock clock, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public DateTimeOffset? Due { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.gate)
                {
                    if (disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                    Period = period;
                    clock.timers.Add(this);
                    return true;
                }
            }
            public void Fire() => callback(state);
            public void Dispose()
            {
                lock (clock.gate) { disposed = true; Due = null; clock.timers.Remove(this); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (!await predicate()) await Task.Delay(50, deadline.Token);
    }

    private sealed class Factory(Func<PaymentContext> create) : IDbContextFactory<PaymentContext>
    {
        public bool Fail { get; set; }
        private int renewalContexts;
        public int RenewalContexts => Volatile.Read(ref renewalContexts);
        public Func<CancellationToken, Task>? BeforeCreateAsync { get; set; }
        public PaymentContext CreateDbContext() => Fail ? throw new IOException("Owned renewal database fault") : create();
        public async Task<PaymentContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref renewalContexts);
            if (BeforeCreateAsync is not null) await BeforeCreateAsync(ct);
            return CreateDbContext();
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly NpgsqlConnection? admin;
        private readonly string? databaseName;
        public PaymentContext Db { get; }
        public Factory Factory { get; }
        public Provider Source { get; } = new("source");
        public Provider Target { get; } = new("target");
        private Fixture(string root, Func<PaymentContext> create, NpgsqlConnection? admin = null, string? databaseName = null)
        { this.root = root; Db = create(); Factory = new(create); this.admin = admin; this.databaseName = databaseName; }
        public static async Task<Fixture> CreateAsync(bool postgres = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "pp_lease_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            if (postgres)
            {
                var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
                Assert.False(string.IsNullOrWhiteSpace(raw), "An owned PostgreSQL lease test requires PAYPLANNER_TEST_POSTGRES.");
                var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
                var admin = new NpgsqlConnection(settings.ConnectionString); await admin.OpenAsync();
                var name = "pp_lease_" + Guid.NewGuid().ToString("N");
                await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await command.ExecuteNonQueryAsync();
                settings.Database = name;
                var pgOptions = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options;
                var owned = new Fixture(root, () => new PostgreSqlPaymentContext(pgOptions), admin, name);
                try { await owned.Db.Database.MigrateAsync(); return owned; }
                catch { await owned.DisposeAsync(); throw; }
            }
            var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite($"Data Source={Path.Combine(root, "owned.db")};Pooling=False").Options;
            var f = new Fixture(root, () => new PaymentContext(options));
            await f.Db.Database.EnsureCreatedAsync(); return f;
        }
        public StorageOperationProcessor Worker(PaymentContext db)
        {
            // Match the ordinary runtime's automatic factory resolution, rather
            // than passing the renewal dependency explicitly to the constructor.
            using var services = new ServiceCollection().AddSingleton<IDbContextFactory<PaymentContext>>(Factory).BuildServiceProvider();
            return ActivatorUtilities.CreateInstance<StorageOperationProcessor>(services, db, new StorageProviderRegistry([Source, Target]),
                new SyntheticFixedOptions<StorageOrchestrationOptions>(new() { WorkerBatchSize = 20, WorkerLeaseSeconds = 15 }),
                TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
        }
        public async Task<StorageOperation> SeedAsync(string name, string status = StorageOperationStatuses.Pending)
        {
            byte[] bytes = [1, 2, 3]; Source.Objects[name] = bytes;
            var generation = new StorageObjectGeneration
            {
                Generation = 1,
                SizeBytes = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                StorageObject = new() { DataClass = "test", OwnerType = "test", OwnerId = name, LogicalName = name, CurrentGeneration = 1 }
            };
            var operation = new StorageOperation
            {
                StorageObject = generation.StorageObject,
                StorageObjectGeneration = generation,
                OperationType = StorageOperationTypes.Replicate,
                ProviderKey = "target",
                Status = status,
                IdempotencyKey = name,
                PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(name, "source", name)),
                CreatedAt = DateTime.UtcNow.AddSeconds(name == "first" ? -1 : 0)
            };
            Db.StorageOperations.Add(operation); await Db.SaveChangesAsync(); return operation;
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (admin is not null)
            {
                Assert.Matches("^pp_lease_[a-f0-9]{32}$", databaseName!);
                try
                {
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (true)
                    {
                        await using var active = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                        active.Parameters.AddWithValue("name", databaseName!);
                        if ((long)(await active.ExecuteScalarAsync())! == 0) break;
                        if (DateTime.UtcNow >= until) throw new InvalidOperationException("Owned lease database still has active connections; preserved.");
                        await Task.Delay(100);
                    }
                    await using var command = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
                    await command.ExecuteNonQueryAsync();
                }
                finally { await admin.DisposeAsync(); }
            }
            var resolvedRoot = Path.GetFullPath(root);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(resolvedRoot), ignoreCase: OperatingSystem.IsWindows());
            Assert.Matches("^pp_lease_[a-f0-9]{32}$", Path.GetFileName(resolvedRoot));
            Directory.Delete(resolvedRoot, true);
        }
    }

    private sealed class Provider(string key) : IStorageProvider
    {
        public string Key => key;
        public StorageProviderCapabilities Capabilities { get; } = new(true, true, true, true, false, false, false, false, false);
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
        public bool BlockFirst { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Task>? AfterPut { get; set; }
        public StorageOperationStatus Outcome { get; set; } = StorageOperationStatus.Succeeded;
        private int calls;
        public int PutCalls => calls;
        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            if (BlockFirst && request.Locator == "first") { Started.TrySetResult(); await Release.Task.WaitAsync(ct); }
            using var content = new MemoryStream(); await request.Content.CopyToAsync(content, ct); Objects[request.Locator] = content.ToArray();
            if (AfterPut is not null) await AfterPut();
            return new(Outcome, request.Locator, Objects[request.Locator].Length,
                Convert.ToHexString(SHA256.HashData(Objects[request.Locator])).ToLowerInvariant());
        }
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => Task.FromResult(Objects.TryGetValue(locator, out var bytes)
            ? new StorageObjectMetadata(StorageOperationStatus.Succeeded, locator, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), null)
            : new StorageObjectMetadata(StorageOperationStatus.NotFound, locator, 0, null, null));
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, locator, new MemoryStream(Objects[locator]), Objects[locator].Length));
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => Task.FromResult(new StorageDeleteResult(StorageOperationStatus.Succeeded, locator));
    }
}
