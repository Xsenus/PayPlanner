using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageReconciliationPagingTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public async Task ProviderProbeExceptionDoesNotAbortScanOrOverwriteVerifiedReplica()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedAsync(fixture.Db, 3);
        var originalStatus = await fixture.Db.StorageReplicas.AsNoTracking()
            .Where(r => r.Locator == "copy/1").Select(r => r.Status).SingleAsync();
        var provider = new ProbeProvider
        {
            BeforeProbe = locator => locator == "copy/1"
                ? Task.FromException(new IOException("Temporary provider failure"))
                : Task.CompletedTask
        };
        var scan = new StorageReconciliationService(fixture.Db, new StorageProviderRegistry([provider]));

        var report = await scan.RunAsync(true, CancellationToken.None);

        Assert.Equal(3, report.ReplicasChecked);
        Assert.Equal(2, report.HealthyReplicas);
        Assert.Equal(0, report.MissingReplicas);
        Assert.Equal(0, report.CorruptReplicas);
        Assert.Equal(0, report.RepairOperationsQueued);
        Assert.Contains(report.Issues, issue => issue.Kind == "verificationUnavailable" && issue.Locator == "copy/1");
        Assert.Equal(originalStatus, await fixture.Db.StorageReplicas.AsNoTracking()
            .Where(r => r.Locator == "copy/1").Select(r => r.Status).SingleAsync());
        AssertClean(scan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeReplicaSetIsBoundedWithoutLosingCountersOrRepeatingProbes(bool apply)
    {
        await using var fixture = await Fixture.CreateAsync();
        var generation = await SeedAsync(fixture.Db, 1201);
        var provider = new ProbeProvider { Healthy = locator => locator == "copy/1200" };
        var scan = new StorageReconciliationService(fixture.Db, new StorageProviderRegistry([provider]));
        fixture.Commands.Reset();
        var report = await scan.RunAsync(apply, CancellationToken.None);
        Assert.Equal(1, report.ObjectsChecked);
        Assert.Equal(1201, report.ReplicasChecked);
        Assert.Equal(1, report.HealthyReplicas);
        Assert.Equal(1200, report.MissingReplicas);
        Assert.Equal(1200, report.TotalIssues);
        Assert.Equal(1000, report.Issues.Count);
        Assert.True(report.IssuesTruncated);
        Assert.Equal(1200, report.IssueCountsByKind["missing"]);
        Assert.Equal(1201, provider.Calls.Count);
        Assert.All(provider.Calls.Values, count => Assert.Equal(1, count));
        Assert.Equal(apply ? 1200 : 0, report.RepairOperationsQueued);
        Assert.Equal(apply ? 1200 : 0, await fixture.Db.StorageOperations.CountAsync());
        Assert.InRange(fixture.Commands.ReplicaUpdates, 0, 7);
        Assert.Equal(apply ? 6 : 0, fixture.Commands.HistoryReads);
        Assert.InRange(fixture.Commands.PeakOwnedTracked, 0, StorageReconciliationService.ReplicaPageSize);
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
        AssertClean(scan);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1200, document.RootElement.GetProperty("totalIssues").GetInt64());
        Assert.True(document.RootElement.GetProperty("issuesTruncated").GetBoolean());
        Assert.NotEqual(Guid.Empty, report.ScanId);
        if (apply)
        {
            var jobs = await fixture.Db.StorageOperations.AsNoTracking().ToArrayAsync();
            Assert.Equal(1200, jobs.Select(o => o.IdempotencyKey).Distinct().Count());
            Assert.All(jobs, job => Assert.Equal(generation.Id, job.StorageObjectGenerationId));
            fixture.Commands.Reset();
            Assert.Equal(0, (await scan.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
            Assert.Equal(6, fixture.Commands.HistoryReads);
            AssertClean(scan);
        }
    }

    [Fact]
    public async Task BindingChangedWithinBatchIsNotOverwrittenAndRequiresAnotherScan()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedAsync(fixture.Db, 201);
        var first = await fixture.Db.StorageReplicas.AsNoTracking().OrderBy(r => r.Id).FirstAsync();
        var provider = new ProbeProvider { Healthy = locator => locator == "copy/200" };
        provider.BeforeProbe = async locator =>
        {
            if (locator == "copy/50") await fixture.Db.StorageReplicas.Where(r => r.Id == first.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, StorageReplicaStatuses.Suspect).SetProperty(r => r.LastError, "Keep concurrent diagnosis"));
        };
        var scan = new StorageReconciliationService(fixture.Db, new StorageProviderRegistry([provider]));
        var report = await scan.RunAsync(true, CancellationToken.None);
        Assert.Equal(200, report.MissingReplicas);
        Assert.Equal(0, report.RepairOperationsQueued);
        Assert.Contains(report.Issues, row => row.Kind == "catalogChanged");
        var retained = await fixture.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Id == first.Id);
        Assert.Equal(StorageReplicaStatuses.Suspect, retained.Status);
        Assert.Equal("Keep concurrent diagnosis", retained.LastError);
        Assert.Equal(first.UpdatedAt, retained.UpdatedAt);
        Assert.Equal(199, await fixture.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Missing));
        provider.BeforeProbe = null;
        Assert.Equal(200, (await scan.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        AssertClean(scan);
    }

    [Fact]
    public async Task DeletionUsesBatchedExistingKeysAndPreservesUnrelatedTrackedChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var generation = await SeedAsync(fixture.Db, 401);
        await fixture.Db.StorageObjects.Where(o => o.Id == generation.StorageObjectId).ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true));
        var replicas = await fixture.Db.StorageReplicas.AsNoTracking().OrderBy(r => r.Id).ToArrayAsync();
        foreach (var replica in replicas.Where(r => r.Id % 2 != 0))
            fixture.Db.StorageOperations.Add(new StorageOperation
            {
                StorageObjectId = generation.StorageObjectId,
                StorageObjectGenerationId = generation.Id,
                OperationType = StorageOperationTypes.Delete,
                ProviderKey = "probe",
                IdempotencyKey = $"reconcile:delete:{generation.Id:N}:{replica.Id}",
                Status = StorageOperationStatuses.DeadLetter
            });
        await fixture.Db.SaveChangesAsync(); fixture.Db.ChangeTracker.Clear();
        var client = new Client { Name = "Unrelated pending client" };
        fixture.Db.Clients.Add(client);
        var provider = new ProbeProvider();
        var scan = new StorageReconciliationService(fixture.Db, new StorageProviderRegistry([provider]));
        fixture.Commands.Reset();
        var report = await scan.RunAsync(true, CancellationToken.None);
        Assert.Equal(200, report.DeleteOperationsQueued);
        Assert.Equal(3, fixture.Commands.DeleteKeyReads);
        Assert.Equal(401, await fixture.Db.StorageOperations.CountAsync());
        Assert.Equal(201, await fixture.Db.StorageOperations.CountAsync(o => o.Status == StorageOperationStatuses.DeadLetter));
        Assert.Equal(client.Name, (await fixture.Db.Clients.AsNoTracking().SingleAsync(c => c.Id == client.Id)).Name);
        Assert.Same(client, Assert.Single(fixture.Db.ChangeTracker.Entries<Client>()).Entity);
        Assert.Single(fixture.Db.ChangeTracker.Entries<ObjectAuditEvent>());
        Assert.DoesNotContain(fixture.Db.ChangeTracker.Entries(), e => e.Entity is StorageOperation or StorageReplica);
        Assert.Empty(provider.Calls);
        Assert.InRange(fixture.Commands.PeakOwnedTracked, 0, 200);
        fixture.Commands.Reset();
        Assert.Equal(0, (await scan.RunAsync(true, CancellationToken.None)).DeleteOperationsQueued);
        Assert.Equal(3, fixture.Commands.DeleteKeyReads);
        AssertClean(scan);
    }

    [Fact]
    public async Task HealthyMetadataIsSavedOncePerPageRatherThanOncePerReplica()
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedAsync(fixture.Db, 1201);
        var provider = new ProbeProvider();
        var scan = new StorageReconciliationService(fixture.Db, new StorageProviderRegistry([provider]));
        fixture.Commands.Reset();
        var report = await scan.RunAsync(true, CancellationToken.None);
        Assert.Equal(1201, report.HealthyReplicas);
        Assert.Equal(7, fixture.Commands.ReplicaUpdates);
        Assert.Empty(report.Issues);
        Assert.Equal(1201, await fixture.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available && r.LastVerifiedAt != null));
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
        AssertClean(scan);
    }

    [Fact]
    public async Task ManyObjectsAndGenerationsAreAllVisited()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 103; i++) await SeedAsync(fixture.Db, 1);
        var large = new StorageObject { CurrentGeneration = 205, LogicalName = "many-versions" };
        for (var i = 1; i <= 205; i++)
            large.Generations.Add(new StorageObjectGeneration
            {
                Generation = i,
                SizeBytes = 5,
                Sha256 = Hash,
                Replicas = [new StorageReplica { ProviderKey = "probe", Locator = $"version/{i}", SizeBytes = 5, Sha256 = Hash }]
            });
        fixture.Db.StorageObjects.Add(large);
        await fixture.Db.SaveChangesAsync(); fixture.Db.ChangeTracker.Clear();
        var scan = new StorageReconciliationService(fixture.Db, new StorageProviderRegistry([new ProbeProvider()]));
        var report = await scan.RunAsync(false, CancellationToken.None);
        Assert.Equal(104, report.ObjectsChecked);
        Assert.Equal(308, report.ReplicasChecked);
        Assert.Equal(308, report.HealthyReplicas);
        Assert.Empty(report.Issues);
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
        AssertClean(scan);
    }

    [Theory]
    [InlineData("locator")]
    [InlineData("status")]
    [InlineData("generation")]
    [InlineData("newReplica")]
    [InlineData("newObject")]
    public async Task ChangesDuringProbeDoNotPublishStaleRepairEvidence(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var generation = await SeedAsync(fixture.Db, 201);
        var original = await fixture.Db.StorageReplicas.AsNoTracking().OrderBy(r => r.Id).FirstAsync();
        var provider = new ProbeProvider { Healthy = locator => locator == "copy/200" };
        provider.BeforeProbe = async locator =>
        {
            if (locator != "copy/200") return;
            if (mutation == "locator") await fixture.Db.StorageReplicas.Where(r => r.Id == original.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Locator, "rebound"));
            if (mutation == "status") await fixture.Db.StorageReplicas.Where(r => r.Id == original.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, StorageReplicaStatuses.Suspect));
            if (mutation == "generation") await fixture.Db.StorageObjectGenerations.Where(g => g.Id == generation.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.SizeBytes, 6));
            if (mutation == "newReplica")
            {
                fixture.Db.StorageReplicas.Add(new StorageReplica { StorageObjectGenerationId = generation.Id, ProviderKey = "probe", Locator = "new", SizeBytes = 5, Sha256 = Hash });
                await fixture.Db.SaveChangesAsync(); fixture.Db.ChangeTracker.Clear();
            }
            if (mutation == "newObject") await SeedAsync(fixture.Db, 1);
        };
        var scan = new StorageReconciliationService(fixture.Db, new StorageProviderRegistry([provider]));
        var report = await scan.RunAsync(true, CancellationToken.None);
        Assert.Contains(report.Issues, issue => issue.Kind == "catalogChanged");
        Assert.Equal(1, report.ObjectsChecked);
        Assert.Equal(201, report.ReplicasChecked);
        var expected = mutation == "status" ? 199 : mutation == "newObject" ? 200 : 0;
        Assert.Equal(expected, report.RepairOperationsQueued);
        var current = await fixture.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Id == original.Id);
        if (mutation == "locator") Assert.Equal("rebound", current.Locator);
        if (mutation == "status") Assert.Equal(StorageReplicaStatuses.Suspect, current.Status);
        AssertClean(scan);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancellationAndProviderFailureDisposeOnlyOwnedSpools(bool cancel, bool afterDamagedBatch)
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedAsync(fixture.Db, afterDamagedBatch ? 201 : 3);
        using var cancellation = new CancellationTokenSource();
        var provider = new ProbeProvider
        {
            Healthy = _ => false,
            BeforeProbe = locator =>
        {
            if (afterDamagedBatch && locator != "copy/200") return Task.CompletedTask;
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw new IOException("Owned probe failure");
        }
        };
        var scan = new StorageReconciliationService(fixture.Db, new StorageProviderRegistry([provider]));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.RunAsync(true, cancellation.Token));
        else
        {
            var report = await scan.RunAsync(true, cancellation.Token);
            Assert.Contains(report.Issues, issue => issue.Kind == "verificationUnavailable");
        }
        AssertClean(scan);
        Assert.Empty(await fixture.Db.StorageOperations.ToArrayAsync());
    }

    [Theory]
    [InlineData("generationMissing")]
    [InlineData("replicaMissing")]
    [InlineData("catalogStateMismatch")]
    public async Task IncompleteOrConflictingCatalogIsNotReportedHealthy(string issue)
    {
        await using var fixture = await Fixture.CreateAsync();
        var generation = await SeedAsync(fixture.Db, issue == "catalogStateMismatch" ? 1 : 0);
        if (issue == "generationMissing") await fixture.Db.StorageObjectGenerations.Where(g => g.Id == generation.Id).ExecuteDeleteAsync();
        if (issue == "catalogStateMismatch")
        {
            fixture.Db.StorageTombstones.Add(new StorageTombstone { StorageObjectId = generation.StorageObjectId, Generation = 1 });
            await fixture.Db.SaveChangesAsync(); fixture.Db.ChangeTracker.Clear();
        }
        var provider = new ProbeProvider();
        var scan = new StorageReconciliationService(fixture.Db, new StorageProviderRegistry([provider]));
        var report = await scan.RunAsync(true, CancellationToken.None);
        Assert.Contains(report.Issues, row => row.Kind == issue);
        Assert.Equal(0, report.HealthyReplicas);
        Assert.Empty(provider.Calls);
        AssertClean(scan);
    }

    [Fact]
    public async Task PostgreSqlBatchesTranslateAndRespectCompletedAndBlockedHistory()
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        { Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase), "PostgreSQL configuration required in CI"); return; }
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        var name = "pp_reconcile_paging_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            settings.Database = name;
            var commands = new Commands();
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).AddInterceptors(commands).Options);
            await db.Database.MigrateAsync();
            var generation = await SeedAsync(db, 401);
            var targets = await db.StorageReplicas.AsNoTracking().OrderBy(r => r.Id).Take(400).ToArrayAsync();
            foreach (var target in targets)
                db.StorageOperations.Add(new StorageOperation
                {
                    StorageObjectId = generation.StorageObjectId,
                    StorageObjectGenerationId = generation.Id,
                    OperationType = StorageOperationTypes.Repair,
                    ProviderKey = "probe",
                    IdempotencyKey = $"reconcile:repair:{generation.Id:N}:{target.Id}",
                    Status = target.Id % 2 == 0 ? StorageOperationStatuses.DeadLetter : StorageOperationStatuses.Succeeded
                });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var scan = new StorageReconciliationService(db, new StorageProviderRegistry([new ProbeProvider { Healthy = locator => locator == "copy/400" }]));
            commands.Reset();
            var report = await scan.RunAsync(true, CancellationToken.None);
            Assert.Equal(200, report.RepairOperationsQueued);
            Assert.Equal(3, commands.ReplicaUpdates);
            Assert.Equal(2, commands.HistoryReads);
            Assert.Equal(200, await db.StorageOperations.CountAsync(o => o.Status == StorageOperationStatuses.Pending && o.IdempotencyKey.EndsWith(":1")));
            Assert.Equal(0, (await scan.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
            Assert.Empty(db.ChangeTracker.Entries());
            AssertClean(scan);
        }
        finally
        {
            Assert.Matches("^pp_reconcile_paging_[a-f0-9]{32}$", name);
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
        }
    }

    private static void AssertClean(StorageReconciliationService scan)
        => Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"pp_reconcile_{scan.LastScanId:N}_*.tmp"));

    private static async Task<StorageObjectGeneration> SeedAsync(PaymentContext db, int replicas)
    {
        var generation = new StorageObjectGeneration
        {
            Generation = 1,
            SizeBytes = 5,
            Sha256 = Hash,
            StorageObject = new StorageObject { CurrentGeneration = 1, LogicalName = "owned-paging" }
        };
        for (var i = 0; i < replicas; i++) generation.Replicas.Add(new StorageReplica { ProviderKey = "probe", Locator = $"copy/{i}", SizeBytes = 5, Sha256 = Hash });
        db.StorageObjectGenerations.Add(generation);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        return generation;
    }

    private sealed class Fixture(SqliteConnection connection, PaymentContext db, Commands commands) : IAsyncDisposable
    {
        public PaymentContext Db => db;
        public Commands Commands => commands;
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var commands = new Commands();
            var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(commands).Options);
            await db.Database.EnsureCreatedAsync();
            return new(connection, db, commands);
        }
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    }

    private sealed class Commands : DbCommandInterceptor
    {
        public int ReplicaUpdates { get; private set; }
        public int HistoryReads { get; private set; }
        public int DeleteKeyReads { get; private set; }
        public int PeakOwnedTracked { get; private set; }
        public void Reset() { ReplicaUpdates = 0; HistoryReads = 0; DeleteKeyReads = 0; PeakOwnedTracked = 0; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("\"StorageReplicas\"")) ReplicaUpdates++; return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("\"StorageOperations\"") && command.CommandText.Contains("GROUP BY")) HistoryReads++;
            if (command.CommandText.Contains("FROM \"StorageOperations\"") && command.CommandText.Contains("\"IdempotencyKey\"") && !command.CommandText.Contains("GROUP BY")) DeleteKeyReads++;
            if (data.Context is { } db) PeakOwnedTracked = Math.Max(PeakOwnedTracked, db.ChangeTracker.Entries().Count(e => e.Entity is StorageOperation or StorageReplica));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ProbeProvider : IStorageProvider
    {
        public string Key => "probe";
        public StorageProviderCapabilities Capabilities => new(true, false, true, false, false, false, false, false, false);
        public Func<string, bool> Healthy { get; init; } = _ => true;
        public Func<string, Task>? BeforeProbe { get; set; }
        public Dictionary<string, int> Calls { get; } = new();
        public async Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct)
        {
            Calls[locator] = Calls.GetValueOrDefault(locator) + 1;
            if (BeforeProbe is { } callback) await callback(locator);
            ct.ThrowIfCancellationRequested();
            return Healthy(locator) ? new(StorageOperationStatus.Succeeded, locator, 5, Hash, null) : new(StorageOperationStatus.NotFound, locator, 0, null, null);
        }
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => throw new NotSupportedException();
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => throw new NotSupportedException();
    }
}
