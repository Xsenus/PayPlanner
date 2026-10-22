using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Data.Common;
using Npgsql;
using Xunit.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class LegacyStorageBackfillTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LegacyReplicaMigrationPlansWithoutWritesAndResumesTwoVerifiedCopiesWithoutDuplicates()
        => await VerifyReplicaMigrationAsync();

    [Fact]
    public async Task LegacyMigrationEndIdBoundsSqlPagingAndReplicaQueue()
        => await VerifyEndIdScopeAsync();

    [Theory]
    [InlineData("insufficient")]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("pendingDebt")]
    [InlineData("uploadReservation")]
    public async Task CapacityPreflightRefusesNewQueueAndPreservesSource(string scenario)
        => await VerifyCapacityRefusalAsync(scenario);

    private static async Task VerifyCapacityRefusalAsync(string scenario, PaymentContext? database = null)
    {
        await using var fixture = new Fixture(database);
        var (path, bytes) = await SeedMigrationSourceAsync(fixture);
        await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: true), CancellationToken.None);
        if (scenario == "uploadReservation")
        {
            fixture.Db.StorageCapacityReservations.Add(new StorageCapacityReservation
            {
                IdempotencyKey = "owned-upload-reservation",
                ResourceKey = JsonSerializer.Serialize(new string?[] { "local", null }),
                GroupId = "synthetic-shared-volume",
                BindingHash = new string('a', 64),
                Bytes = 95,
                ObjectId = Guid.NewGuid(),
                LocalLocator = "owned/reserved.bin"
            });
            await fixture.Db.SaveChangesAsync();
            fixture.Db.ChangeTracker.Clear();
        }
        var settings = CapacitySettings();
        long? available = scenario == "unknown" ? null : scenario == "insufficient" ? 7 : 100;
        var observedAt = scenario == "stale" ? DateTimeOffset.UtcNow.AddMinutes(-2) : DateTimeOffset.UtcNow;
        StorageCapacityResource Observe(string key, long reserve) => new(key, "synthetic-shared-volume", available, reserve, observedAt);
        var (service, _) = CreateMigrationServices(fixture, configured: settings, capacity: Observe);
        var request = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid());
        var preview = await service.RunAsync(request, CancellationToken.None);
        if (scenario == "pendingDebt")
        {
            Assert.True(preview.Capacity!.CanSchedule);
            var first = await service.RunAsync(request with { Apply = true, ExpectedPolicyFingerprint = preview.PolicyFingerprint }, CancellationToken.None);
            Assert.Equal(4, first.Queued);
            available = 7;
        }
        var before = await fixture.Db.StorageOperations.CountAsync();
        var replicas = await fixture.Db.StorageReplicas.CountAsync();
        var refused = await service.RunAsync(request with { Apply = true, ExpectedPolicyFingerprint = preview.PolicyFingerprint }, CancellationToken.None);
        Assert.False(refused.Capacity!.CanSchedule); Assert.NotEmpty(refused.Errors); Assert.Equal(0, refused.Queued);
        Assert.Equal(before, await fixture.Db.StorageOperations.CountAsync());
        Assert.Equal(replicas, await fixture.Db.StorageReplicas.CountAsync());
        Assert.False(fixture.Db.ChangeTracker.HasChanges()); Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        if (scenario == "pendingDebt")
        {
            var group = Assert.Single(refused.Capacity.Groups, g => g.GroupId == "synthetic-shared-volume");
            Assert.Equal(8, group.PendingBytes); Assert.Equal(0, group.AdditionalBytes);
        }
        if (scenario == "uploadReservation")
            Assert.Equal(95, Assert.Single(refused.Capacity.Groups, g => g.GroupId == "synthetic-shared-volume").PendingBytes);
    }

    [Fact]
    public async Task NativeCapacityPreflightGroupsSharedVolumeAndDoesNotDuplicateRetryDebt()
        => await VerifyNativeCapacityAsync();

    private static async Task VerifyNativeCapacityAsync(PaymentContext? database = null)
    {
        await using var fixture = new Fixture(database);
        await SeedMigrationSourceAsync(fixture);
        await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: true), CancellationToken.None);
        var settings = CapacitySettings();
        var (service, _) = CreateMigrationServices(fixture, configured: settings);
        var request = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid());
        var preview = await service.RunAsync(request, CancellationToken.None);
        Assert.Empty(preview.Errors); Assert.True(preview.Capacity!.CanSchedule);
        var group = Assert.Single(preview.Capacity.Groups);
        Assert.Equal(16, group.AdditionalBytes); Assert.Equal(0, group.PendingBytes);
        var applied = await service.RunAsync(request with { Apply = true, ExpectedPolicyFingerprint = preview.PolicyFingerprint }, CancellationToken.None);
        Assert.Equal(4, applied.Queued);
        var retry = await service.RunAsync(request with { Apply = true, ExpectedPolicyFingerprint = preview.PolicyFingerprint }, CancellationToken.None);
        Assert.Empty(retry.Errors); Assert.Equal(0, retry.Queued);
        group = Assert.Single(retry.Capacity!.Groups);
        Assert.Equal(16, group.PendingBytes); Assert.Equal(0, group.AdditionalBytes);
        settings.MigrationMinimumFreeBytes++;
        var drift = await service.RunAsync(request with { Apply = true, ExpectedPolicyFingerprint = preview.PolicyFingerprint }, CancellationToken.None);
        Assert.NotEmpty(drift.Errors); Assert.Equal(0, drift.Queued);
        Assert.Equal(4, await fixture.Db.StorageOperations.CountAsync());
    }

    [Theory]
    [InlineData("native")]
    [InlineData("insufficient")]
    [InlineData("pendingDebt")]
    [InlineData("uploadReservation")]
    [InlineData("concurrent")]
    [InlineData("largeQueue")]
    [InlineData("overflow")]
    [InlineData("missingGeneration")]
    [InlineData("negativeSize")]
    [InlineData("terminalDebt")]
    public async Task CapacityPreflightUsesOwnedMigratedPostgreSql(string scenario)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_capacity_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync(); var created = false;
        try
        {
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await command.ExecuteNonQueryAsync(); created = true; }
            settings.Database = name;
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options;
            await using (var migrate = new PostgreSqlPaymentContext(options)) await migrate.Database.MigrateAsync();
            if (scenario == "concurrent") await VerifyConcurrentCapacityAsync(settings.ConnectionString);
            else
            {
                var database = new PostgreSqlPaymentContext(options);
                if (scenario == "native") await VerifyNativeCapacityAsync(database);
                else if (scenario is "largeQueue" or "overflow" or "missingGeneration" or "negativeSize" or "terminalDebt") await VerifyAggregatedDebtAsync(scenario, database);
                else await VerifyCapacityRefusalAsync(scenario, database);
            }
            output.WriteLine($"Owned migrated PostgreSQL capacity case executed: {scenario}.");
        }
        finally
        {
            if (created)
            {
                await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
            }
        }
    }

    [Theory]
    [InlineData("largeQueue")]
    [InlineData("overflow")]
    [InlineData("missingGeneration")]
    [InlineData("negativeSize")]
    [InlineData("terminalDebt")]
    public async Task CapacityDebtAggregationIsBoundedAndRejectsInvalidTotals(string scenario)
        => await VerifyAggregatedDebtAsync(scenario);

    private static async Task VerifyAggregatedDebtAsync(string scenario, PaymentContext? database = null)
    {
        await using var fixture = new Fixture(database);
        var (path, bytes) = await SeedMigrationSourceAsync(fixture);
        await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: true), CancellationToken.None);
        var generation = await fixture.Db.StorageObjectGenerations.SingleAsync();
        if (scenario is "overflow" or "negativeSize" or "terminalDebt")
        {
            generation = new StorageObjectGeneration
            {
                StorageObjectId = generation.StorageObjectId,
                Generation = 2,
                SizeBytes = scenario == "negativeSize" ? -1 : long.MaxValue,
                Sha256 = generation.Sha256
            };
            fixture.Db.StorageObjectGenerations.Add(generation); await fixture.Db.SaveChangesAsync();
        }
        var count = scenario == "largeQueue" ? 12000 : 2;
        for (var index = 0; index < count; index++)
            fixture.Db.StorageOperations.Add(new()
            {
                StorageObjectId = generation.StorageObjectId,
                StorageObjectGenerationId = scenario == "missingGeneration" ? null : generation.Id,
                OperationType = StorageOperationTypes.Replicate,
                ProviderKey = index % 2 == 0 ? "local" : "LOCAL",
                IdempotencyKey = "synthetic-debt:" + index,
                Status = scenario == "terminalDebt" ? index == 0 ? StorageOperationStatuses.Succeeded : StorageOperationStatuses.DeadLetter
                    : index % 3 == 0 ? StorageOperationStatuses.Unknown : index % 3 == 1 ? StorageOperationStatuses.Failed : StorageOperationStatuses.Pending
            });
        await fixture.Db.SaveChangesAsync();
        var (service, _) = CreateMigrationServices(fixture, configured: CapacitySettings(),
            capacity: (key, reserve) => new(key, "synthetic-shared-volume", 1_000_000, reserve, DateTimeOffset.UtcNow));
        var request = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid());
        var preview = await service.RunAsync(request, CancellationToken.None);
        var applied = await service.RunAsync(request with { Apply = true, ExpectedPolicyFingerprint = preview.PolicyFingerprint }, CancellationToken.None);
        if (scenario is "largeQueue" or "terminalDebt")
        {
            Assert.Empty(applied.Errors); Assert.Equal(4, applied.Queued);
            var group = Assert.Single(applied.Capacity!.Groups, g => g.GroupId == "synthetic-shared-volume");
            Assert.Equal(scenario == "largeQueue" ? 48000 : 0, group.PendingBytes); Assert.Equal(8, group.AdditionalBytes);
            Assert.Equal(2, applied.Capacity.Groups.Count);
        }
        else { Assert.NotEmpty(applied.Errors); Assert.Equal(0, applied.Queued); }
        Assert.Equal(count + applied.Queued, await fixture.Db.StorageOperations.CountAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    private static async Task VerifyConcurrentCapacityAsync(string connectionString)
    {
        var barrier = new CapacityReadBarrier();
        DbContextOptions<PostgreSqlPaymentContext> Options() => new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(connectionString).AddInterceptors(barrier).Options;
        await using var left = new Fixture(new PostgreSqlPaymentContext(Options()));
        await using var right = new Fixture(new PostgreSqlPaymentContext(Options()));
        async Task<long> SeedAsync(Fixture fixture)
        {
            await SeedMigrationSourceAsync(fixture);
            var id = await fixture.Db.TaskJournalFiles.MaxAsync(f => f.Id);
            await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, AfterId: id - 1, EndId: id, Apply: true), CancellationToken.None);
            return id;
        }
        var firstId = await SeedAsync(left); var secondId = await SeedAsync(right);
        StorageCapacityResource Observe(string key, long reserve) => new(key, "synthetic-shared-volume", 12, reserve, DateTimeOffset.UtcNow);
        var (first, _) = CreateMigrationServices(left, configured: CapacitySettings(), capacity: Observe);
        var (second, _) = CreateMigrationServices(right, configured: CapacitySettings(), capacity: Observe);
        var firstRequest = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid(), AfterId: firstId - 1, EndId: firstId);
        var secondRequest = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid(), AfterId: secondId - 1, EndId: secondId);
        var firstPreview = await first.RunAsync(firstRequest, CancellationToken.None);
        var secondPreview = await second.RunAsync(secondRequest, CancellationToken.None);
        Assert.Empty(firstPreview.Errors); Assert.Empty(secondPreview.Errors);
        firstRequest = firstRequest with { Apply = true, ExpectedPolicyFingerprint = firstPreview.PolicyFingerprint };
        secondRequest = secondRequest with { Apply = true, ExpectedPolicyFingerprint = secondPreview.PolicyFingerprint };
        barrier.Enabled = true;
        var outcomes = await Task.WhenAll(first.RunAsync(firstRequest, CancellationToken.None),
            second.RunAsync(secondRequest, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(40));
        barrier.Enabled = false;
        Assert.Equal(2, barrier.Arrivals);
        Assert.Equal(new[] { 0, 4 }, outcomes.Select(o => o.Queued).Order().ToArray());
        Assert.Contains(Assert.Single(outcomes, o => o.Queued == 0).Errors, error => error.Contains("Concurrent storage planning"));
        Assert.Equal(4, await left.Db.StorageOperations.CountAsync());
        Assert.Equal(4, await left.Db.StorageReplicas.CountAsync());
        Assert.False(left.Db.ChangeTracker.HasChanges()); Assert.False(right.Db.ChangeTracker.HasChanges());
        var retry = outcomes[0].Queued == 0 ? await first.RunAsync(firstRequest, CancellationToken.None)
            : await second.RunAsync(secondRequest, CancellationToken.None);
        Assert.Equal(0, retry.Queued); Assert.False(retry.Capacity!.CanSchedule);
        Assert.Equal(4, await left.Db.StorageOperations.CountAsync());
    }

    private sealed class CapacityReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public bool Enabled { get; set; }
        public int Arrivals => _arrivals;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("storage-capacity-pending-budget", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _both.TrySetResult();
                await _both.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private static StorageOrchestrationOptions CapacitySettings() => new()
    {
        Enabled = true,
        MigrationCapacityChecksEnabled = true,
        MigrationMinimumFreeBytes = 0,
        DefaultPolicy = new() { RequiredCopies = 2, WriteProviders = ["local", "second"], ReadProviders = ["local", "second"] }
    };

    [Fact]
    public async Task CapacityObservationCannotPublishAfterSettingsChange()
    {
        await using var fixture = new Fixture();
        await SeedMigrationSourceAsync(fixture);
        await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: true), CancellationToken.None);
        var settings = CapacitySettings(); var change = false;
        StorageCapacityResource Observe(string key, long reserve)
        {
            if (change) settings.MigrationMinimumFreeBytes++;
            return new(key, "synthetic-shared-volume", 1000, reserve, DateTimeOffset.UtcNow);
        }
        var (service, _) = CreateMigrationServices(fixture, configured: settings, capacity: Observe);
        var request = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid());
        var preview = await service.RunAsync(request, CancellationToken.None);
        Assert.Empty(preview.Errors); change = true;
        var refused = await service.RunAsync(request with { Apply = true, ExpectedPolicyFingerprint = preview.PolicyFingerprint }, CancellationToken.None);
        Assert.Contains(refused.Errors, error => error.Contains("settings changed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, refused.Queued); Assert.Equal(0, await fixture.Db.StorageOperations.CountAsync());
        Assert.Equal(1, await fixture.Db.StorageReplicas.CountAsync()); Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task CapacityObservationCannotPublishAfterAnotherBudgetChange()
    {
        await using var fixture = new Fixture();
        await SeedMigrationSourceAsync(fixture);
        await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: true), CancellationToken.None);
        var changeDuringObservation = false;
        var changed = false;
        StorageCapacityResource Observe(string key, long reserve)
        {
            if (changeDuringObservation && !changed)
            {
                changed = true;
                Assert.Equal(1, fixture.Db.StorageCapacityEpochs.Where(e => e.Id == 1)
                    .ExecuteUpdate(s => s.SetProperty(e => e.Version, e => e.Version + 1)));
            }
            return new(key, "synthetic-shared-volume", 1000, reserve, DateTimeOffset.UtcNow);
        }
        var (service, _) = CreateMigrationServices(fixture, configured: CapacitySettings(), capacity: Observe);
        var request = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid());
        var preview = await service.RunAsync(request, CancellationToken.None);
        Assert.Empty(preview.Errors);
        changeDuringObservation = true;
        var refused = await service.RunAsync(request with
        {
            Apply = true,
            ExpectedPolicyFingerprint = preview.PolicyFingerprint
        }, CancellationToken.None);
        Assert.Contains(refused.Errors, error => error.Contains("budget changed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, refused.Queued);
        Assert.Equal(0, await fixture.Db.StorageOperations.CountAsync());
        Assert.Equal(1, await fixture.Db.StorageReplicas.CountAsync());
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task LegacyReplicaMigrationUsesOwnedMigratedPostgreSqlWithRealLocalCopies()
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_legacy_plan_" + Guid.NewGuid().ToString("N");
        var adminSettings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(adminSettings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync(); var created = false;
        try
        {
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await command.ExecuteNonQueryAsync(); created = true; }
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = name };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options;
            await using (var migrate = new PostgreSqlPaymentContext(options)) await migrate.Database.MigrateAsync();
            await VerifyReplicaMigrationAsync(new PostgreSqlPaymentContext(options));
            await VerifyEndIdScopeAsync(new PostgreSqlPaymentContext(options));
            output.WriteLine("Owned PostgreSQL migrations, two real local copy/verification queues, stable resume and new-run re-verification executed.");
            output.WriteLine("All seven legacy source queries and bounded EndId paging/replica queue executed on owned PostgreSQL.");
        }
        finally
        {
            if (created)
            {
                await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
            }
        }
    }

    private static async Task VerifyEndIdScopeAsync(PaymentContext? database = null)
    {
        await using var fixture = new Fixture(database);
        foreach (var source in LegacyStorageSources.All)
        {
            var empty = await fixture.Service.RunAsync(new(source, EndId: 0), CancellationToken.None);
            Assert.Empty(empty.Items); Assert.False(empty.HasMore); Assert.Equal(0L, empty.EndId);
        }
        var (_, bytes) = await SeedMigrationSourceAsync(fixture);
        var first = await fixture.Db.TaskJournalFiles.OrderByDescending(f => f.Id).FirstAsync();
        var last = new TaskJournalFile
        {
            TaskJournalItemId = first.TaskJournalItemId,
            OriginalFileName = "end.bin",
            StoragePath = "end.bin",
            SizeBytes = 4,
            ContentType = "application/octet-stream",
            DocumentKey = Guid.NewGuid().ToString("N"),
            Version = 1
        };
        var excluded = new TaskJournalFile
        {
            TaskJournalItemId = first.TaskJournalItemId,
            OriginalFileName = "excluded.bin",
            StoragePath = "missing-outside-scope.bin",
            SizeBytes = 4,
            ContentType = "application/octet-stream",
            DocumentKey = Guid.NewGuid().ToString("N"),
            Version = 1
        };
        fixture.Db.TaskJournalFiles.AddRange(last, excluded); await fixture.Db.SaveChangesAsync();
        await File.WriteAllBytesAsync(Path.Combine(fixture.FileRoot, "task-files", last.StoragePath), bytes);
        var request = new LegacyStorageBackfillRequest(LegacyStorageSources.TaskFiles, first.Id - 1, BatchSize: 1, Apply: true, EndId: last.Id);
        var page = await fixture.Service.RunAsync(request, CancellationToken.None);
        Assert.Equal(first.Id, Assert.Single(page.Items).SourceId); Assert.True(page.HasMore);
        var final = await fixture.Service.RunAsync(request with { AfterId = first.Id }, CancellationToken.None);
        Assert.Equal(last.Id, Assert.Single(final.Items).SourceId); Assert.False(final.HasMore); Assert.Equal(0, final.Missing);
        var terminal = await fixture.Service.RunAsync(request with { AfterId = last.Id }, CancellationToken.None);
        Assert.Empty(terminal.Items); Assert.False(terminal.HasMore); Assert.Equal(last.Id, terminal.EndId);
        Assert.False(await fixture.Db.StorageObjects.AnyAsync(o => o.Id == LegacyStorageSources.GetObjectId(LegacyStorageSources.TaskFiles, excluded.Id)));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RunAsync(request with { AfterId = last.Id + 1 }, CancellationToken.None));
        var (service, _) = CreateMigrationServices(fixture);
        var operationCount = await fixture.Db.StorageOperations.CountAsync();
        var replicaRequest = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid(),
            AfterId: first.Id - 1, EndId: first.Id);
        var preview = await service.RunAsync(replicaRequest, CancellationToken.None);
        Assert.Equal(first.Id, Assert.Single(preview.SourceVerification.Items).SourceId);
        Assert.False(preview.SourceVerification.HasMore); Assert.Equal(first.Id, preview.SourceVerification.EndId);
        var applied = await service.RunAsync(replicaRequest with { Apply = true, ExpectedPolicyFingerprint = preview.PolicyFingerprint }, CancellationToken.None);
        Assert.Equal(4, applied.Queued); Assert.Equal(operationCount + 4, await fixture.Db.StorageOperations.CountAsync());
        Assert.All(applied.Items, item => Assert.Equal(first.Id, item.SourceId));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RunAsync(replicaRequest with { EndId = first.Id - 2 }, CancellationToken.None));
    }

    private static async Task VerifyReplicaMigrationAsync(PaymentContext? database = null)
    {
        await using var fixture = new Fixture(database);
        var (path, bytes) = await SeedMigrationSourceAsync(fixture);
        await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: true), CancellationToken.None);
        var (service, processor) = CreateMigrationServices(fixture);
        var request = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid());
        var preview = await service.RunAsync(request, CancellationToken.None);
        request = request with { ExpectedPolicyFingerprint = preview.PolicyFingerprint };
        Assert.False(preview.BatchVerified); Assert.Equal(0, preview.Queued);
        Assert.Equal(2, preview.Items.Count); Assert.All(preview.Items, i => Assert.Equal("planned", i.Status));
        Assert.Equal(1, await fixture.Db.StorageReplicas.CountAsync());
        Assert.Equal(0, await fixture.Db.StorageOperations.CountAsync());
        var accepted = await service.RunAsync(request with { Apply = true }, CancellationToken.None);
        Assert.Equal(4, accepted.Queued); Assert.False(accepted.BatchVerified);
        Assert.Equal(0, (await service.RunAsync(request with { Apply = true }, CancellationToken.None)).Queued);
        Assert.Equal(3, await fixture.Db.StorageReplicas.CountAsync());
        Assert.Equal(4, await fixture.Db.StorageOperations.CountAsync());
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await processor.ProcessBatchAsync("synthetic-legacy-migration", CancellationToken.None);
            var retryAt = DateTime.UtcNow.AddMinutes(-1);
            await fixture.Db.StorageOperations.Where(o => o.Status == StorageOperationStatuses.Failed)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, retryAt));
        }
        var completed = await service.RunAsync(request with { Apply = true }, CancellationToken.None);
        Assert.True(completed.BatchVerified); Assert.Equal(0, completed.Queued);
        Assert.All(completed.Items, i => Assert.Equal("verified", i.Status));
        Assert.Equal(4, await fixture.Db.StorageOperations.CountAsync(o => o.Status == StorageOperationStatuses.Succeeded));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        var generation = await fixture.Db.StorageObjectGenerations.SingleAsync();
        Assert.Equal("legacy-v1", generation.PolicyVersion); // Never mutates immutable generation policy.
        foreach (var copy in completed.Items)
        {
            var root = Path.Combine(fixture.Root, copy.ProviderKey == "local" ? "managed" : "second");
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, copy.Locator!)));
        }
        // Another run must prove its own copies, not reuse last run's verification receipt.
        var another = await service.RunAsync(request with { RunId = Guid.NewGuid(), Apply = true }, CancellationToken.None);
        Assert.False(another.BatchVerified); Assert.Equal(2, another.Queued);
        Assert.Equal(2, await fixture.Db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.Replicate));
        Assert.Equal(3, await fixture.Db.StorageReplicas.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyReplicaMigrationReadsHundredObjectsWithBoundedQueriesRatherThanPerFileLookups(bool capacityChecks)
    {
        var queries = 0;
        await using var fixture = new Fixture(onCommand: _ => queries++);
        var (path, bytes) = await SeedMigrationSourceAsync(fixture);
        var task = await fixture.Db.TaskJournalItems.SingleAsync();
        for (var index = 1; index < 100; index++)
        {
            var name = $"source-{index}.bin";
            await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, name), bytes);
            fixture.Db.TaskJournalFiles.Add(new()
            {
                TaskJournalItemId = task.Id,
                OriginalFileName = name,
                StoragePath = name,
                SizeBytes = bytes.Length,
                ContentType = "application/octet-stream",
                DocumentKey = Guid.NewGuid().ToString("N"),
                Version = 1
            });
        }
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(100, (await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, BatchSize: 100, Apply: true), CancellationToken.None)).Registered);
        var (service, _) = CreateMigrationServices(fixture, configured: capacityChecks ? CapacitySettings() : null);
        queries = 0;
        var preview = await service.RunAsync(new(LegacyStorageSources.TaskFiles, Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(100, preview.SourceVerification.Scanned);
        Assert.Equal(200, preview.Items.Count); Assert.Equal(0, preview.Queued);
        Assert.Equal(capacityChecks ? 12 : 6, queries);
        if (capacityChecks)
        {
            Assert.True(preview.Capacity!.CanSchedule);
            Assert.Equal(1600, Assert.Single(preview.Capacity.Groups).AdditionalBytes);
        }
    }

    [Fact]
    public async Task PendingUploadReservationSharesBudgetWithLegacyCopyAndReleaseRestoresAdmission()
    {
        await using var fixture = new Fixture();
        await SeedMigrationSourceAsync(fixture);
        await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: true), CancellationToken.None);
        var reservation = new StorageCapacityReservation
        {
            IdempotencyKey = "owned-upload",
            ResourceKey = JsonSerializer.Serialize(new string?[] { "local", null }),
            GroupId = "owned-shared",
            BindingHash = new string('a', 64),
            Bytes = 3,
            ObjectId = Guid.NewGuid(),
            LocalLocator = "owned/reserved.bin"
        };
        fixture.Db.StorageCapacityReservations.Add(reservation);
        await fixture.Db.SaveChangesAsync();
        var (service, processor) = CreateMigrationServices(fixture, configured: CapacitySettings(),
            capacity: (key, minimum) => new(key, "owned-shared", 10, minimum, DateTimeOffset.UtcNow));
        var refused = await service.RunAsync(new(LegacyStorageSources.TaskFiles, Guid.NewGuid()), CancellationToken.None);
        Assert.False(refused.Capacity!.CanSchedule);
        var shared = Assert.Single(refused.Capacity.Groups, g => g.GroupId == "owned-shared");
        Assert.Equal(3, shared.PendingBytes);
        Assert.Equal(8, shared.AdditionalBytes);
        Assert.Equal(0, await fixture.Db.StorageOperations.CountAsync());
        var (remounted, _) = CreateMigrationServices(fixture, configured: CapacitySettings(),
            capacity: (key, minimum) => new(key, "different-physical-volume", 100, minimum, DateTimeOffset.UtcNow));
        var changed = await remounted.RunAsync(new(LegacyStorageSources.TaskFiles, Guid.NewGuid()), CancellationToken.None);
        Assert.Null(changed.Capacity);
        Assert.NotEmpty(changed.Errors);

        reservation.Status = StorageCapacityReservationStatuses.Released;
        reservation.ReleasedAt = DateTime.UtcNow;
        await fixture.Db.SaveChangesAsync();
        var admitted = await service.RunAsync(new(LegacyStorageSources.TaskFiles, Guid.NewGuid()), CancellationToken.None);
        Assert.True(admitted.Capacity!.CanSchedule);
        Assert.Equal(0, Assert.Single(admitted.Capacity.Groups, g => g.GroupId == "owned-shared").PendingBytes);
        var before = await fixture.Db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync();
        var request = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid());
        var preview = await service.RunAsync(request, CancellationToken.None);
        var queued = await service.RunAsync(request with
        {
            Apply = true,
            ExpectedPolicyFingerprint = preview.PolicyFingerprint
        }, CancellationToken.None);
        Assert.Equal(4, queued.Queued);
        Assert.Equal(before + 1, await fixture.Db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
        await processor.ProcessBatchAsync("capacity-epoch-test", CancellationToken.None);
        Assert.Equal(2, await fixture.Db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.Replicate
            && o.Status == StorageOperationStatuses.Succeeded));
        Assert.Equal(before + 3, await fixture.Db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
    }

    [Fact]
    public async Task LegacyReplicaMigrationRequiresPreviewAndRejectsChangedPolicyWithoutQueueWrites()
    {
        await using var fixture = new Fixture();
        await SeedMigrationSourceAsync(fixture);
        await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: true), CancellationToken.None);
        var settings = new StorageOrchestrationOptions
        {
            Enabled = true,
            DefaultPolicy = new()
            {
                RequiredCopies = 2,
                WriteProviders = ["local", "second"],
                ReadProviders = ["local", "second"]
            }
        };
        var (service, _) = CreateMigrationServices(fixture, configured: settings);
        var request = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(() => service.RunAsync(request with { Apply = true }, CancellationToken.None));
        var preview = await service.RunAsync(request, CancellationToken.None);
        settings.DefaultPolicy.Version = "changed-policy";
        var stale = await service.RunAsync(request with { Apply = true, ExpectedPolicyFingerprint = preview.PolicyFingerprint }, CancellationToken.None);
        Assert.False(stale.BatchVerified); Assert.Equal(0, stale.Queued); Assert.NotEmpty(stale.Errors);
        Assert.Equal(0, await fixture.Db.StorageOperations.CountAsync());
        var fresh = await service.RunAsync(request, CancellationToken.None);
        Assert.NotEqual(preview.PolicyFingerprint, fresh.PolicyFingerprint);
        Assert.Equal(4, (await service.RunAsync(request with { Apply = true, ExpectedPolicyFingerprint = fresh.PolicyFingerprint }, CancellationToken.None)).Queued);
    }

    [Theory]
    [InlineData("unregistered")]
    [InlineData("changedSource")]
    [InlineData("disabled")]
    [InlineData("invalidPolicy")]
    [InlineData("readonlyWriter")]
    [InlineData("foreignLocator")]
    public async Task LegacyReplicaMigrationRefusesUnverifiedSourcesAndInvalidDestinations(string scenario)
    {
        await using var fixture = new Fixture();
        var (path, bytes) = await SeedMigrationSourceAsync(fixture);
        if (scenario != "unregistered") await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: true), CancellationToken.None);
        if (scenario == "changedSource") await File.WriteAllBytesAsync(path, [99, 98, 97, 96]);
        if (scenario == "foreignLocator")
        {
            var generation = await fixture.Db.StorageObjectGenerations.SingleAsync();
            fixture.Db.StorageReplicas.Add(new()
            {
                StorageObjectGenerationId = generation.Id,
                ProviderKey = "local",
                Locator = "foreign.bin",
                Status = StorageReplicaStatuses.Available,
                SizeBytes = bytes.Length,
                Sha256 = generation.Sha256
            });
            await fixture.Db.SaveChangesAsync();
        }
        var (service, _) = CreateMigrationServices(fixture, scenario);
        var request = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid());
        var preview = await service.RunAsync(request, CancellationToken.None);
        var result = await service.RunAsync(request with { Apply = true, ExpectedPolicyFingerprint = preview.PolicyFingerprint }, CancellationToken.None);
        Assert.False(result.BatchVerified);
        if (scenario == "foreignLocator")
        {
            Assert.Contains(result.Items, i => i.ProviderKey == "local" && i.Status == "conflict");
            Assert.False(await fixture.Db.StorageOperations.AnyAsync(o => o.ProviderKey == "local"));
        }
        else { Assert.Equal(0, result.Queued); Assert.Equal(0, await fixture.Db.StorageOperations.CountAsync()); }
        Assert.Equal(scenario == "changedSource" ? new byte[] { 99, 98, 97, 96 } : bytes, await File.ReadAllBytesAsync(path));
    }

    private static async Task<(string Path, byte[] Bytes)> SeedMigrationSourceAsync(Fixture fixture)
    {
        var task = new TaskJournalItem { Title = "Synthetic replica migration", DueAt = DateTime.UtcNow.AddDays(1) };
        fixture.Db.TaskJournalItems.Add(task);
        var file = new TaskJournalFile
        {
            TaskJournalItem = task,
            OriginalFileName = "source.bin",
            StoragePath = "source.bin",
            ContentType = "application/octet-stream",
            SizeBytes = 4,
            DocumentKey = Guid.NewGuid().ToString("N"),
            Version = 1
        };
        fixture.Db.TaskJournalFiles.Add(file);
        await fixture.Db.SaveChangesAsync();
        var root = Path.Combine(fixture.FileRoot, "task-files"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "source.bin"); byte[] bytes = [11, 22, 33, 44];
        await File.WriteAllBytesAsync(path, bytes);
        return (path, bytes);
    }

    private static (LegacyStorageReplicaMigrationService Service, StorageOperationProcessor Processor) CreateMigrationServices(Fixture fixture, string? scenario = null,
        StorageOrchestrationOptions? configured = null, Func<string, long, StorageCapacityResource>? capacity = null)
    {
        var environment = new TestEnvironment(fixture.Root);
        LocalStorageProvider Local(string suffix) => new(environment, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["StorageOrchestration:LocalRoot"] = Path.Combine(fixture.Root, suffix) }).Build());
        var registry = new StorageProviderRegistry([fixture.Provider, new NamedLocalProvider("local", Local("managed"), capacity), new NamedLocalProvider("second", Local("second"), capacity)]);
        var monitor = new MigrationOptions(configured ?? new()
        {
            Enabled = scenario != "disabled",
            WorkerBatchSize = 20,
            DefaultPolicy = new()
            {
                RequiredCopies = scenario == "invalidPolicy" ? 3 : 2,
                WriteProviders = scenario == "readonlyWriter" ? ["legacy-local", "local"] : ["local", "second"],
                ReadProviders = ["local", "second"]
            }
        });
        var service = new LegacyStorageReplicaMigrationService(fixture.Db, fixture.Service,
            new StoragePolicyService(monitor, registry), registry, monitor);
        var processor = new StorageOperationProcessor(fixture.Db, registry, monitor, TimeProvider.System,
            NullLogger<StorageOperationProcessor>.Instance);
        return (service, processor);
    }

    private sealed class MigrationOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }

    private sealed class NamedLocalProvider(string key, IStorageProvider inner,
        Func<string, long, StorageCapacityResource>? capacity = null) : IStorageProvider, IStorageCapacityProvider
    {
        public string Key => key;
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId, long minimumFreeBytes, CancellationToken ct)
            => capacity is not null ? Task.FromResult(capacity(resourceKey, minimumFreeBytes))
                : ((IStorageCapacityProvider)inner).ObserveCapacityAsync(resourceKey, connectionId, minimumFreeBytes, ct);
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct) => inner.PutAsync(request, ct);
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => inner.HeadAsync(locator, ct);
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => throw new InvalidOperationException("Migration must not delete sources or copies.");
    }
    public static IEnumerable<object[]> RegisteredDriftCases =>
        new[] { "bytes", "size", "owner", "ownerType", "name", "type", "class", "deleted", "tombstone", "generation",
            "locator", "generationSha", "replicaSha", "replicaSize", "replicaStatus", "duplicateLocator" }
            .SelectMany(change => new[] { new object[] { change, false }, new object[] { change, true } });

    [Theory]
    [MemberData(nameof(RegisteredDriftCases))]
    public async Task RegisteredSourceDriftIsReportedWithoutChangingCatalogOrSource(string change, bool apply)
    {
        await using var fixture = new Fixture();
        await AssertRegisteredDriftAsync(fixture, change, apply);
    }

    private static async Task AssertRegisteredDriftAsync(Fixture fixture, string change, bool apply)
    {
        var task = new TaskJournalItem { Title = "Источник", DueAt = DateTime.UtcNow.AddDays(1) };
        fixture.Db.TaskJournalItems.Add(task);
        await fixture.Db.SaveChangesAsync();
        var relative = Path.Combine(task.Id.ToString(), "source.bin");
        var path = Path.Combine(fixture.FileRoot, "task-files", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        fixture.Db.TaskJournalFiles.Add(new()
        {
            TaskJournalItemId = task.Id,
            DocumentKey = "source",
            Version = 1,
            OriginalFileName = "source.bin",
            StoredFileName = "source.bin",
            StoragePath = relative,
            ContentType = "application/octet-stream",
            SizeBytes = 4,
            UploadedByName = "Тест"
        });
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(1, (await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: true), CancellationToken.None)).Registered);
        var objectId = await fixture.Db.StorageObjects.Select(o => o.Id).SingleAsync();
        var generationId = await fixture.Db.StorageObjectGenerations.Select(g => g.Id).SingleAsync();
        switch (change)
        {
            case "bytes": await File.WriteAllBytesAsync(path, [9, 8, 7, 6]); break;
            case "size": await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]); break;
            case "owner": await fixture.Db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerId, "other")); break;
            case "ownerType": await fixture.Db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerType, "profile-photo")); break;
            case "name": await fixture.Db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.LogicalName, "other.bin")); break;
            case "type": await fixture.Db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.ContentType, "image/png")); break;
            case "class": await fixture.Db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.DataClass, "profile-photo")); break;
            case "deleted": await fixture.Db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true)); break;
            case "tombstone": fixture.Db.StorageTombstones.Add(new() { StorageObjectId = objectId }); await fixture.Db.SaveChangesAsync(); break;
            case "generation": await fixture.Db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.CurrentGeneration, 2)); break;
            case "locator": await fixture.Db.StorageReplicas.ExecuteUpdateAsync(s => s.SetProperty(r => r.Locator, "files/task-files/other.bin")); break;
            case "generationSha": await fixture.Db.StorageObjectGenerations.ExecuteUpdateAsync(s => s.SetProperty(g => g.Sha256, new string('f', 64))); break;
            case "replicaSha": await fixture.Db.StorageReplicas.ExecuteUpdateAsync(s => s.SetProperty(r => r.Sha256, new string('f', 64))); break;
            case "replicaSize": await fixture.Db.StorageReplicas.ExecuteUpdateAsync(s => s.SetProperty(r => r.SizeBytes, 5)); break;
            case "replicaStatus": await fixture.Db.StorageReplicas.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, StorageReplicaStatuses.Suspect)); break;
            case "duplicateLocator":
                fixture.Db.StorageReplicas.Add(new()
                {
                    StorageObjectGenerationId = generationId,
                    ProviderKey = "legacy-local",
                    Locator = "files/task-files/other.bin",
                    Status = StorageReplicaStatuses.Available,
                    SizeBytes = 4,
                    Sha256 = new string('a', 64)
                }); await fixture.Db.SaveChangesAsync(); break;
        }
        async Task<string> Snapshot() => JsonSerializer.Serialize(new
        {
            Objects = await fixture.Db.StorageObjects.AsNoTracking().OrderBy(o => o.Id).ToArrayAsync(),
            Generations = await fixture.Db.StorageObjectGenerations.AsNoTracking().OrderBy(g => g.Id).ToArrayAsync(),
            Replicas = await fixture.Db.StorageReplicas.AsNoTracking().OrderBy(r => r.Id).ToArrayAsync(),
            Tombstones = await fixture.Db.StorageTombstones.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync()
        });
        var before = await Snapshot();
        var beforeBytes = await File.ReadAllBytesAsync(path);
        var report = await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, Apply: apply), CancellationToken.None);
        Assert.Equal(before, await Snapshot());
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, report.Invalid);
        Assert.Equal(0, report.AlreadyRegistered);
        Assert.Equal(0, report.Registered);
        Assert.Equal("invalid", Assert.Single(report.Items).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlRegisteredSourceDriftUsesRealMigratedCatalog(bool apply)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_backfill_drift_" + Guid.NewGuid().ToString("N");
        var adminSettings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            {
                await create.ExecuteNonQueryAsync();
                created = true;
            }
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = name };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options;
            await using (var migrate = new PostgreSqlPaymentContext(options)) await migrate.Database.MigrateAsync();
            foreach (var scenario in RegisteredDriftCases.Where(row => (bool)row[1] == apply))
            {
                await using var fixture = new Fixture(new PostgreSqlPaymentContext(options));
                await AssertRegisteredDriftAsync(fixture, (string)scenario[0], apply);
                await fixture.Db.StorageObjects.ExecuteDeleteAsync();
                await fixture.Db.TaskJournalItems.IgnoreQueryFilters().ExecuteDeleteAsync();
            }
            output.WriteLine($"Owned PostgreSQL migrations and 16 source/catalog drift scenarios executed; apply={apply}.");
        }
        finally
        {
            if (created)
            {
                await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
            }
        }
    }

    [Fact]
    public async Task RepeatedBatchReadsCatalogOnceWithoutPerFileQueries()
    {
        var commandCount = 0;
        await using var fixture = new Fixture(onCommand: _ => commandCount++);
        var task = new TaskJournalItem { Title = "Пакет", DueAt = DateTime.UtcNow.AddDays(1) };
        fixture.Db.TaskJournalItems.Add(task);
        await fixture.Db.SaveChangesAsync();
        var directory = Path.Combine(fixture.FileRoot, "task-files", task.Id.ToString());
        Directory.CreateDirectory(directory);
        const int count = 125;
        for (var index = 0; index < count; index++)
        {
            var fileName = $"{index}.bin";
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), [1, 2, 3]);
            fixture.Db.TaskJournalFiles.Add(new()
            {
                TaskJournalItemId = task.Id,
                DocumentKey = index.ToString(),
                Version = 1,
                OriginalFileName = fileName,
                StoredFileName = fileName,
                StoragePath = Path.Combine(task.Id.ToString(), fileName),
                ContentType = "application/octet-stream",
                SizeBytes = 3,
                UploadedByName = "Тест"
            });
        }
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(count, (await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, BatchSize: count, Apply: true), CancellationToken.None)).Registered);
        foreach (var apply in new[] { false, true })
        {
            commandCount = 0;
            var report = await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, BatchSize: count, Apply: apply), CancellationToken.None);
            Assert.Equal(count, report.AlreadyRegistered);
            Assert.Equal(0, report.Invalid);
            Assert.Equal(2, commandCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfilePhotoBackfillBindsLocalBytesWithoutChangingPublicUrl(bool absoluteUrl)
    {
        await using var fixture = new Fixture();
        var user = new User
        {
            Email = "photo-backfill@example.test",
            PasswordHash = "test",
            FullName = "Фото",
            Role = new Role { Name = "employee", Description = "Тест" }
        };
        fixture.Db.Users.Add(user);
        await fixture.Db.SaveChangesAsync();
        var relativeUrl = $"/user-photos/{user.Id}/photo.png";
        user.PhotoUrl = absoluteUrl ? "https://crm.example.test" + relativeUrl : relativeUrl;
        await fixture.Db.SaveChangesAsync();
        var directory = Path.Combine(fixture.FileRoot, "user-photos", user.Id.ToString());
        Directory.CreateDirectory(directory);
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(Path.Combine(directory, "photo.png"), bytes);
        var report = await fixture.Service.RunAsync(new(LegacyStorageSources.ProfilePhotos, Apply: true), CancellationToken.None);
        Assert.Equal(1, report.Registered);
        var entry = await fixture.Db.StorageObjects.AsNoTracking().SingleAsync();
        Assert.Equal(LegacyStorageSources.GetObjectId(LegacyStorageSources.ProfilePhotos, user.Id), entry.Id);
        Assert.Equal("profile-photo", entry.DataClass);
        Assert.Equal(user.Id.ToString(), entry.OwnerId);
        Assert.Equal("photo.png", entry.LogicalName);
        Assert.Equal("image/*", entry.ContentType);
        var generation = await fixture.Db.StorageObjectGenerations.AsNoTracking().SingleAsync();
        Assert.Equal(bytes.Length, generation.SizeBytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), generation.Sha256);
        Assert.Equal(user.PhotoUrl, (await fixture.Db.Users.AsNoTracking().SingleAsync()).PhotoUrl);
        Assert.Equal(1, (await fixture.Service.RunAsync(new(LegacyStorageSources.ProfilePhotos, Apply: true), CancellationToken.None)).AlreadyRegistered);
    }

    [Theory]
    [InlineData(true, "intact")]
    [InlineData(false, "intact")]
    [InlineData(true, "tampered")]
    [InlineData(false, "tampered")]
    [InlineData(true, "emptyChecksum")]
    [InlineData(false, "emptyChecksum")]
    [InlineData(true, "changedAfterRegistration")]
    [InlineData(false, "changedAfterRegistration")]
    public async Task SignatureSources_RegisterOriginalsAndArtifactsOnlyWithRecordedChecksum(bool original, string state)
    {
        await using var fixture = new Fixture();
        var user = new User
        {
            Email = "signature-backfill@example.test",
            PasswordHash = "test",
            FullName = "Автор",
            Role = new Role { Name = "employee", Description = "Тест" }
        };
        fixture.Db.Users.Add(user);
        await fixture.Db.SaveChangesAsync();
        var folder = Path.Combine(fixture.FileRoot, "electronic-signatures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, original ? "original.pdf" : "signature.bin");
        byte[] bytes = [1, 2, 3, 4];
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(path, state == "tampered" ? [9, 8, 7, 6] : bytes);
        var envelope = new ElectronicSignatureEnvelope
        {
            DocumentType = "invoice",
            DocumentId = 1,
            OriginalFileName = "original.pdf",
            SnapshotPath = path,
            SizeBytes = bytes.Length,
            ChecksumSha256 = state == "emptyChecksum" ? "" : checksum,
            CreatedByUserId = user.Id,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
        };
        fixture.Db.ElectronicSignatureEnvelopes.Add(envelope);
        await fixture.Db.SaveChangesAsync();
        var artifact = new ElectronicSignatureArtifact
        {
            EnvelopeId = envelope.Id,
            FileName = "signature.bin",
            StoragePath = path,
            ArtifactType = "detached-signature",
            SizeBytes = bytes.Length,
            ChecksumSha256 = state == "emptyChecksum" ? "" : checksum,
        };
        fixture.Db.ElectronicSignatureArtifacts.Add(artifact);
        await fixture.Db.SaveChangesAsync();
        var source = original ? LegacyStorageSources.ElectronicSignatureOriginals : LegacyStorageSources.ElectronicSignatures;
        var sourceId = original ? envelope.Id : artifact.Id;
        var report = await fixture.Service.RunAsync(new(source, Apply: true), CancellationToken.None);
        if (state is "tampered" or "emptyChecksum")
        {
            Assert.Equal(1, report.Invalid);
            Assert.Equal(0, report.Registered);
            Assert.False(await fixture.Db.StorageObjects.AnyAsync());
        }
        else
        {
            Assert.Equal(1, report.Registered);
            var entry = await fixture.Db.StorageObjects.AsNoTracking().SingleAsync();
            Assert.Equal(LegacyStorageSources.GetObjectId(source, sourceId), entry.Id);
            Assert.Equal(original ? "esign-original" : "esign-artifact", entry.OwnerType);
            Assert.Equal(sourceId.ToString(), entry.OwnerId);
            Assert.Equal(checksum.ToLowerInvariant(), (await fixture.Db.StorageObjectGenerations.AsNoTracking().SingleAsync()).Sha256);
            if (state == "changedAfterRegistration") await File.WriteAllBytesAsync(path, [9, 8, 7, 6]);
            var repeated = await fixture.Service.RunAsync(new(source, Apply: true), CancellationToken.None);
            Assert.Equal(state == "changedAfterRegistration" ? 1 : 0, repeated.Invalid);
            Assert.Equal(state == "intact" ? 1 : 0, repeated.AlreadyRegistered);
            Assert.Single(await fixture.Db.StorageObjects.ToListAsync());
            Assert.Single(await fixture.Db.StorageReplicas.ToListAsync());
        }
        Assert.Equal(path, (await fixture.Db.ElectronicSignatureEnvelopes.AsNoTracking().SingleAsync()).SnapshotPath);
        Assert.Equal(path, (await fixture.Db.ElectronicSignatureArtifacts.AsNoTracking().SingleAsync()).StoragePath);
    }

    [Fact]
    public async Task TaskFiles_DryRunApplyResumeAndMissing_AreSafeAndIdempotent()
    {
        await using var fixture = new Fixture();
        var task = new TaskJournalItem { ObjectName = "Объект", Title = "Задача", DueAt = DateTime.UtcNow.AddDays(1) };
        fixture.Db.TaskJournalItems.Add(task);
        await fixture.Db.SaveChangesAsync();
        var existingPath = Path.Combine(task.Id.ToString(), "first.txt");
        var existingFullPath = Path.Combine(fixture.FileRoot, "task-files", existingPath);
        Directory.CreateDirectory(Path.GetDirectoryName(existingFullPath)!);
        await File.WriteAllTextAsync(existingFullPath, "данные");
        fixture.Db.TaskJournalFiles.AddRange(
            new TaskJournalFile
            {
                TaskJournalItemId = task.Id,
                DocumentKey = "one",
                Version = 1,
                OriginalFileName = "первый.txt",
                StoredFileName = "first.txt",
                StoragePath = existingPath,
                ContentType = "text/plain",
                SizeBytes = new FileInfo(existingFullPath).Length,
                UploadedByName = "Тест",
            },
            new TaskJournalFile
            {
                TaskJournalItemId = task.Id,
                DocumentKey = "two",
                Version = 1,
                OriginalFileName = "нет.txt",
                StoredFileName = "missing.txt",
                StoragePath = Path.Combine(task.Id.ToString(), "missing.txt"),
                ContentType = "text/plain",
                SizeBytes = 10,
                UploadedByName = "Тест",
            });
        await fixture.Db.SaveChangesAsync();

        var dryRun = await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, BatchSize: 1), CancellationToken.None);
        Assert.False(dryRun.Applied);
        Assert.True(dryRun.HasMore);
        Assert.Single(dryRun.Items);
        Assert.Equal("ready", dryRun.Items[0].Status);
        Assert.Empty(await fixture.Db.StorageObjects.ToListAsync());

        var applied = await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, BatchSize: 1, Apply: true), CancellationToken.None);
        Assert.Equal(1, applied.Registered);
        var storageObject = await fixture.Db.StorageObjects.Include(item => item.Generations).ThenInclude(item => item.Replicas).SingleAsync();
        Assert.Equal("legacy-local", storageObject.Generations.Single().Replicas.Single().ProviderKey);
        Assert.Equal(StorageReplicaStatuses.Available, storageObject.Generations.Single().Replicas.Single().Status);

        var retry = await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, BatchSize: 1, Apply: true), CancellationToken.None);
        Assert.Equal(1, retry.AlreadyRegistered);
        Assert.Single(await fixture.Db.StorageObjects.ToListAsync());

        var next = await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, applied.NextAfterId!.Value, 10, true), CancellationToken.None);
        Assert.Equal(1, next.Missing);
        Assert.Equal("missing", Assert.Single(next.Items).Status);
        Assert.Single(await fixture.Db.StorageObjects.ToListAsync());
    }

    [Fact]
    public async Task LegacyProvider_MapsOnlyConfiguredRootsAndNeverDeletes()
    {
        await using var fixture = new Fixture();
        var file = Path.Combine(fixture.FileRoot, "task-files", "1", "document.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [1, 2, 3]);

        Assert.True(fixture.Provider.TryCreateLocator(file, out var locator));
        var metadata = await fixture.Provider.HeadAsync(locator, CancellationToken.None);
        var delete = await fixture.Provider.DeleteAsync(locator, CancellationToken.None);

        Assert.Equal(StorageOperationStatus.Succeeded, metadata.Status);
        Assert.Equal(3, metadata.SizeBytes);
        Assert.Equal(StorageOperationStatus.Conflict, delete.Status);
        Assert.True(File.Exists(file));
        Assert.False(fixture.Provider.TryCreateLocator(Path.Combine(fixture.Root, "outside.bin"), out _));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Provider.HeadAsync("files/../outside.bin", CancellationToken.None));
    }

    [Fact]
    public async Task LegacyCapacityObservationUsesOnlyConfiguredAliasesAndSharedPhysicalGroup()
    {
        await using var fixture = new Fixture();
        var files = await fixture.Provider.ObserveCapacityAsync("files-budget", "files", 17, CancellationToken.None);
        var photos = await fixture.Provider.ObserveCapacityAsync("photos-budget", "profile-current", 17, CancellationToken.None);
        var unknown = await fixture.Provider.ObserveCapacityAsync("unknown-budget", "../files", 17, CancellationToken.None);

        Assert.True(files.AvailableBytes >= 0);
        Assert.Equal(files.GroupId, photos.GroupId);
        Assert.Equal(17, files.MinimumFreeBytes);
        Assert.Null(unknown.AvailableBytes);
        Assert.Empty(unknown.GroupId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection? _connection;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"payplanner-legacy-backfill-{Guid.NewGuid():N}");
        public string FileRoot { get; }
        public PaymentContext Db { get; }
        public LegacyLocalStorageProvider Provider { get; }
        public LegacyStorageCatalogBackfillService Service { get; }

        public Fixture(PaymentContext? database = null, Action<string>? onCommand = null)
        {
            Directory.CreateDirectory(Root);
            FileRoot = Path.Combine(Root, "files");
            Directory.CreateDirectory(FileRoot);
            var environment = new TestEnvironment(Root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = FileRoot,
                ["ProfilePhotos:StorageRoot"] = Path.Combine(FileRoot, "user-photos"),
            }).Build();
            if (database is null)
            {
                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(_connection);
                if (onCommand is not null) options.LogTo(onCommand, [RelationalEventId.CommandExecuted]);
                Db = new PaymentContext(options.Options);
                Db.Database.EnsureCreated();
            }
            else Db = database;
            Provider = new LegacyLocalStorageProvider(environment, configuration);
            Service = new LegacyStorageCatalogBackfillService(Db, Provider, environment, configuration);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
