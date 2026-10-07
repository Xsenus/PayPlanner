using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Fact]
    public async Task RecoverySettlesOnlyOldCompleteFilesAndLeavesPartialOrMissingReservations()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-stage-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var storageRoot = Path.Combine(root, "objects");
            var stageRoot = Path.Combine(storageRoot, ".task-uploads");
            Directory.CreateDirectory(stageRoot);
            var environment = new TestEnvironment(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [LocalStoragePaths.RootConfigurationKey] = storageRoot }).Build();
            var providers = new StorageProviderRegistry([new LocalStorageProvider(environment, configuration)]);
            var options = new StaticOptions(new StorageOrchestrationOptions
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = 0
            });
            var stages = new StorageStageCapacityService(db, providers, options, environment, configuration);
            async Task<StorageStageCapacityTicket> Add(string name, byte[]? bytes)
            {
                var path = Path.Combine(stageRoot, name + ".tmp");
                var (ticket, error) = await stages.ReserveAsync("recovery:" + name, path, 4, CancellationToken.None);
                Assert.Null(error);
                Assert.NotNull(ticket);
                if (bytes is not null) await File.WriteAllBytesAsync(path, bytes);
                await ticket!.CloseWriterAsync();
                // These fixtures explicitly represent pre-protocol reservations.
                // Missing legacy files must still keep their conservative debt.
                var legacyKey = "stage:" + ticket.Key["stage:writer-v1:".Length..];
                await db.StorageCapacityReservations.Where(row => row.IdempotencyKey == ticket.Key)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IdempotencyKey, legacyKey));
                return ticket with { Key = legacyKey };
            }
            var complete = await Add("complete", [1, 2, 3, 4]);
            var partial = await Add("partial", [1, 2]);
            var missing = await Add("missing", null);
            var recent = await Add("recent", [1, 2, 3, 4]);
            var old = DateTime.UtcNow.AddHours(-1);
            await db.StorageCapacityReservations.Where(r => r.IdempotencyKey != recent.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, old).SetProperty(r => r.UpdatedAt, old));

            var preview = await stages.ReconcilePendingAsync(false, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.Equal(3, preview.Inspected);
            Assert.Equal(1, preview.Eligible);
            Assert.Equal(0, preview.Settled);
            Assert.Equal(3, preview.Retained);
            Assert.Contains(preview.Items, item => item.Key == complete.Key && item.Status == "eligible");
            var partialPreview = Assert.Single(preview.Items, item => item.Key == partial.Key && item.Status == "incompleteFile");
            Assert.Equal(partial.RelativePath, partialPreview.RelativePath);
            Assert.Equal(4, partialPreview.ExpectedBytes);
            Assert.Equal(2, partialPreview.ObservedBytes);
            Assert.Equal(old, partialPreview.UpdatedAt);
            var missingPreview = Assert.Single(preview.Items, item => item.Key == missing.Key && item.Status == "unavailableFile");
            Assert.Equal(missing.RelativePath, missingPreview.RelativePath);
            Assert.Null(missingPreview.ObservedBytes);
            Assert.DoesNotContain(preview.Items, item => item.Key == recent.Key);
            var paged = new List<StorageStageRecoveryItem>();
            string? afterKey = null;
            do
            {
                var page = await stages.ReconcilePendingAsync(false, 1, TimeSpan.FromMinutes(10), CancellationToken.None, afterKey);
                paged.AddRange(page.Items);
                afterKey = page.NextAfterKey;
                Assert.Equal(page.HasMore, afterKey is not null);
            } while (afterKey is not null);
            Assert.Equal(preview.Items.Select(item => item.Key).OrderBy(key => key),
                paged.Select(item => item.Key).OrderBy(key => key));
            await Assert.ThrowsAsync<ArgumentException>(() => stages.ReconcilePendingAsync(false, 1,
                TimeSpan.FromMinutes(10), CancellationToken.None, "../unsafe"));
            var applied = await stages.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.Equal(1, applied.Settled);
            Assert.Equal(2, applied.Retained);
            Assert.Equal(StorageCapacityReservationStatuses.Released,
                (await db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.IdempotencyKey == complete.Key)).Status);
            Assert.Equal(3, await db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Pending));
            await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == missing.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LocalLocator, "../outside.tmp"));
            await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == partial.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.GroupId, "different-volume"));
            var unsafeRecovery = await stages.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
            var invalidPreview = Assert.Single(unsafeRecovery.Items, item => item.Key == missing.Key && item.Status == "invalidAuthority");
            Assert.Null(invalidPreview.RelativePath);
            Assert.Contains(unsafeRecovery.Items, item => item.Key == partial.Key && item.Status == "changedVolume");
            Assert.Equal(0, unsafeRecovery.Settled);
            await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == missing.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LocalLocator, missing.RelativePath));
            await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == partial.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.GroupId, partial.GroupId));
            foreach (var ticket in new[] { complete, partial, missing, recent })
            {
                if (File.Exists(ticket.AbsolutePath)) File.Delete(ticket.AbsolutePath);
                await stages.ReleaseAsync(ticket, CancellationToken.None);
            }
        }
        finally
        {
            Assert.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pp-stage-capacity-"), root,
                StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StageReservationPrecedesBytesAndRemainsUntilStageIsRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-stage-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var storageRoot = Path.Combine(root, "objects");
            var stageRoot = Path.Combine(storageRoot, ".task-uploads");
            Directory.CreateDirectory(stageRoot);
            var stage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".tmp");
            var environment = new TestEnvironment(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [LocalStoragePaths.RootConfigurationKey] = storageRoot }).Build();
            var local = new LocalStorageProvider(environment, configuration);
            var providers = new StorageProviderRegistry([local]);
            var options = new StaticOptions(new StorageOrchestrationOptions
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = 0
            });
            var stages = new StorageStageCapacityService(db, providers, options, environment, configuration);
            byte[] bytes = [1, 2, 3];
            var (ticket, error) = await stages.ReserveAsync("task:42:upload:1", stage, bytes.Length, CancellationToken.None);
            Assert.Null(error);
            Assert.NotNull(ticket);
            Assert.False(File.Exists(stage));
            Assert.Equal(1, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
            Assert.Equal(StorageCapacityReservationStatuses.Pending,
                Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync()).Status);
            await File.WriteAllBytesAsync(stage, bytes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => stages.ReleaseAsync(ticket!, CancellationToken.None));
            await stages.SettlePhysicalAsync(ticket!, CancellationToken.None);
            Assert.Equal(2, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
            var policy = new StoragePolicyService(options, providers);
            var objects = new StorageObjectService(db, policy, providers, options);
            await using var source = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read);
            var stored = await objects.WriteAsync(new("document", "task", "42", "staged.bin",
                "application/octet-stream", source, "stage-handoff", ExpectedSizeBytes: bytes.Length,
                ExpectedSha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), CancellationToken.None);
            Assert.Equal("available", stored.Status);
            Assert.Equal(4, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
            await source.DisposeAsync();
            File.Delete(stage);
            await stages.ReleaseAsync(ticket!, CancellationToken.None);
            Assert.Equal(4, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
            Assert.Equal(2, await db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Released));
            Assert.Equal(0, await db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Pending));
        }
        finally
        {
            Assert.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pp-stage-capacity-"), root,
                StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedStageUsesObservedPhysicalBytesWithoutChargingItsReservationTwice()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-stage-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var storageRoot = Path.Combine(root, "objects");
            var stageRoot = Path.Combine(storageRoot, ".task-uploads");
            Directory.CreateDirectory(stageRoot);
            var stage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".tmp");
            var environment = new TestEnvironment(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [LocalStoragePaths.RootConfigurationKey] = storageRoot }).Build();
            var providers = new StorageProviderRegistry([
                new PhysicalAwareLocalProvider(new LocalStorageProvider(environment, configuration), stage, 10)]);
            var options = new StaticOptions(new StorageOrchestrationOptions
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = 0
            });
            var stages = new StorageStageCapacityService(db, providers, options, environment, configuration);
            var (ticket, error) = await stages.ReserveAsync("task:42:physical", stage, 4, CancellationToken.None);
            Assert.Null(error);
            Assert.NotNull(ticket);
            await Assert.ThrowsAsync<FileNotFoundException>(() => stages.SettlePhysicalAsync(ticket!, CancellationToken.None));
            await File.WriteAllBytesAsync(stage, [1, 2, 3]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => stages.SettlePhysicalAsync(ticket!, CancellationToken.None));
            Assert.Equal(StorageCapacityReservationStatuses.Pending,
                Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync()).Status);
            await File.WriteAllBytesAsync(stage, [1, 2, 3, 4]);
            await stages.SettlePhysicalAsync(ticket!, CancellationToken.None);
            await using (var source = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var objects = new StorageObjectService(db, new StoragePolicyService(options, providers), providers, options);
                var stored = await objects.WriteAsync(new("document", "task", "42", "physical.bin",
                    "application/octet-stream", source, "physical-final", ExpectedSizeBytes: 4,
                    ExpectedSha256: Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant()), CancellationToken.None);
                Assert.Equal("available", stored.Status);
            }
            File.Delete(stage);
            await stages.ReleaseAsync(ticket!, CancellationToken.None);
            Assert.Equal(0, await db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Pending));
        }
        finally
        {
            Assert.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pp-stage-capacity-"), root,
                StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompatibilityCopySharesBudgetWithManagedStageAndRejectsUnlistedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-stage-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var environment = new TestEnvironment(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LocalStoragePaths.RootConfigurationKey] = Path.Combine(root, "objects"),
                ["FileStorage:RootPath"] = Path.Combine(root, "files")
            }).Build();
            var managed = Path.Combine(root, "objects", ".task-uploads");
            var legacy = Path.Combine(root, "files", "task-files");
            Directory.CreateDirectory(managed);
            Directory.CreateDirectory(legacy);
            var providers = new StorageProviderRegistry([
                new FixedCapacityLocalProvider(new LocalStorageProvider(environment, configuration), 7),
                new FixedCapacityLocalProvider(new LegacyLocalStorageProvider(environment, configuration), 7, "legacy-local")]);
            var options = new StaticOptions(new StorageOrchestrationOptions
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = 0
            });
            var stages = new StorageStageCapacityService(db, providers, options, environment, configuration);
            var managedPath = Path.Combine(managed, "stage.tmp");
            var (managedTicket, managedError) = await stages.ReserveAsync("shared:managed", managedPath, 4, CancellationToken.None);
            Assert.Null(managedError);
            Assert.NotNull(managedTicket);
            var (compatibilityTicket, compatibilityError) = await stages.ReserveCompatibilityAsync(
                "shared:legacy", Path.Combine(legacy, "compat.tmp"), 4, "files", CancellationToken.None);
            Assert.Null(compatibilityTicket);
            Assert.Contains("insufficient", compatibilityError, StringComparison.OrdinalIgnoreCase);
            var (outsideTicket, _) = await stages.ReserveCompatibilityAsync(
                "shared:outside", Path.Combine(root, "outside.tmp"), 1, "files", CancellationToken.None);
            Assert.Null(outsideTicket);
            File.Delete(managedPath);
            await stages.ReleaseAsync(managedTicket!, CancellationToken.None);
        }
        finally
        {
            Assert.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pp-stage-capacity-"), root,
                StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StageAndFinalCopyMustBothFitTheSamePhysicalBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-stage-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var storageRoot = Path.Combine(root, "objects");
            var stageRoot = Path.Combine(storageRoot, ".task-uploads");
            Directory.CreateDirectory(stageRoot);
            var stage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".tmp");
            var environment = new TestEnvironment(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [LocalStoragePaths.RootConfigurationKey] = storageRoot }).Build();
            var local = new FixedCapacityLocalProvider(new LocalStorageProvider(environment, configuration), 7);
            var providers = new StorageProviderRegistry([local]);
            var options = new StaticOptions(new StorageOrchestrationOptions
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = 0
            });
            var stages = new StorageStageCapacityService(db, providers, options, environment, configuration);
            byte[] bytes = [1, 2, 3, 4];
            var (ticket, error) = await stages.ReserveAsync("task:42:peak", stage, bytes.Length, CancellationToken.None);
            Assert.Null(error);
            Assert.NotNull(ticket);
            await File.WriteAllBytesAsync(stage, bytes);
            await using (var source = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var objects = new StorageObjectService(db, new StoragePolicyService(options, providers), providers, options);
                var refused = await objects.WriteAsync(new("document", "task", "42", "peak.bin",
                    "application/octet-stream", source, "peak-final", ExpectedSizeBytes: bytes.Length,
                    ExpectedSha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), CancellationToken.None);
                Assert.Equal(Guid.Empty, refused.ObjectId);
            }
            Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.StorageObjects.AsNoTracking().ToArrayAsync());
            File.Delete(stage);
            await stages.ReleaseAsync(ticket!, CancellationToken.None);
            Assert.Equal(0, await db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Pending));
        }
        finally
        {
            Assert.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pp-stage-capacity-"), root,
                StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownOrInsufficientStageCapacityNeverCreatesAReservation()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-stage-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var storageRoot = Path.Combine(root, "objects");
            var stageRoot = Path.Combine(storageRoot, ".photo-uploads");
            Directory.CreateDirectory(stageRoot);
            var environment = new TestEnvironment(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [LocalStoragePaths.RootConfigurationKey] = storageRoot }).Build();
            var local = new LocalStorageProvider(environment, configuration);
            var providers = new StorageProviderRegistry([local]);
            var options = new StaticOptions(new StorageOrchestrationOptions
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = long.MaxValue
            });
            var stages = new StorageStageCapacityService(db, providers, options, environment, configuration);
            var path = Path.Combine(stageRoot, "not-written.tmp");
            var (ticket, error) = await stages.ReserveAsync("photo:42", path, 3, CancellationToken.None);
            Assert.Null(ticket);
            Assert.NotNull(error);
            Assert.False(File.Exists(path));
            Assert.Empty(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
            Assert.Equal(0, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
            var (outside, _) = await stages.ReserveAsync("photo:42", Path.Combine(root, "foreign.tmp"), 3, CancellationToken.None);
            Assert.Null(outside);
        }
        finally
        {
            Assert.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pp-stage-capacity-"), root,
                StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StageReservationSettlesOnOwnedMigratedPostgreSqlAfterPhysicalWrite()
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_stage_capacity_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "pp-stage-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await command.ExecuteNonQueryAsync(); created = true; }
            settings.Database = name;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options);
            await db.Database.MigrateAsync();
            var storageRoot = Path.Combine(root, "objects");
            var stageRoot = Path.Combine(storageRoot, ".photo-uploads");
            Directory.CreateDirectory(stageRoot);
            var stage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".tmp");
            var environment = new TestEnvironment(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [LocalStoragePaths.RootConfigurationKey] = storageRoot }).Build();
            var providers = new StorageProviderRegistry([new LocalStorageProvider(environment, configuration)]);
            var options = new StaticOptions(new StorageOrchestrationOptions
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = 0
            });
            var stages = new StorageStageCapacityService(db, providers, options, environment, configuration);
            var (ticket, error) = await stages.ReserveAsync("photo:pg:stage", stage, 4, CancellationToken.None);
            Assert.Null(error);
            Assert.NotNull(ticket);
            await File.WriteAllBytesAsync(stage, [1, 2, 3, 4]);
            var secondStage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".tmp");
            var (secondTicket, secondError) = await stages.ReserveAsync("photo:pg:second", secondStage, 4, CancellationToken.None);
            Assert.Null(secondError);
            Assert.NotNull(secondTicket);
            await File.WriteAllBytesAsync(secondStage, [5, 6, 7, 8]);
            // Both manual writers have finished. Recovery must not bypass a
            // still-live writer lease merely because bytes look complete.
            await ticket!.CloseWriterAsync();
            await secondTicket!.CloseWriterAsync();
            Assert.Equal(2, await db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Pending));
            var old = DateTime.UtcNow.AddHours(-1);
            await db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, old));
            var healthController = new StorageAdminController(null!, providers,
                new StorageProviderHealthTracker(options, TimeProvider.System), null!, null!, null!, stages, db, null!);
            var healthResponse = Assert.IsType<OkObjectResult>((await healthController.GetHealth(CancellationToken.None)).Result);
            using (var health = JsonDocument.Parse(JsonSerializer.Serialize(healthResponse.Value)))
            {
                Assert.Equal(2, health.RootElement.GetProperty("pendingCapacityReservations").GetInt32());
                Assert.Equal(2, health.RootElement.GetProperty("pendingStageCapacityReservations").GetInt32());
            }
            var firstPage = await stages.ReconcilePendingAsync(false, 1, TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.True(firstPage.HasMore);
            Assert.NotNull(firstPage.NextAfterKey);
            var nextPage = await stages.ReconcilePendingAsync(false, 1, TimeSpan.FromMinutes(10),
                CancellationToken.None, firstPage.NextAfterKey);
            Assert.Single(nextPage.Items);
            Assert.NotEqual(Assert.Single(firstPage.Items).Key, Assert.Single(nextPage.Items).Key);
            var recovery = await stages.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.Equal(2, recovery.Settled);
            Assert.True(File.Exists(stage));
            Assert.True(File.Exists(secondStage));
            Assert.Equal(2, await db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Released));
            File.Delete(stage);
            File.Delete(secondStage);
            await stages.ReleaseAsync(ticket!, CancellationToken.None);
            await stages.ReleaseAsync(secondTicket!, CancellationToken.None);
            Assert.Equal(4, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
        }
        finally
        {
            Assert.Matches("^pp_stage_capacity_[a-f0-9]{32}$", name);
            if (created)
            {
                long connections = -1;
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                    count.Parameters.AddWithValue("name", name);
                    connections = Convert.ToInt64(await count.ExecuteScalarAsync());
                    if (connections == 0) break;
                    await Task.Delay(100);
                }
                Assert.Equal(0L, connections);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
            Assert.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pp-stage-capacity-"), root,
                StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public async Task ConfiguredSignatureCompatibilityRootRecoversOnlyCompletePhysicalStage(int bytes, bool eligible)
    {
        var root = Path.Combine(Path.GetTempPath(), "pp-stage-capacity-esign-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(root, "files");
        var signatures = Path.Combine(root, "configured-signatures");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(signatures);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
            await db.Database.MigrateAsync();
            var environment = new TestEnvironment(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PayPlanner.Api.Services.ApplicationFileStoragePaths.RootPathConfigurationKey] = files,
                [PayPlanner.Api.Services.ElectronicSignatureStoragePaths.StorageRootConfigurationKey] = signatures
            }).Build();
            var providers = new StorageProviderRegistry([new LegacyLocalStorageProvider(environment, configuration)]);
            var options = new StaticOptions(new StorageOrchestrationOptions
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = 0
            });
            var stages = new StorageStageCapacityService(db, providers, options, environment, configuration);
            var path = Path.Combine(signatures, "original.tmp");
            var (ticket, error) = await stages.ReserveCompatibilityAsync("esign:root:probe", path, 4, "esign-current", CancellationToken.None);
            Assert.Null(error);
            Assert.NotNull(ticket);
            Assert.Equal("original.tmp", ticket.RelativePath);
            Assert.Equal(path, ticket.AbsolutePath);
            var (outside, outsideError) = await stages.ReserveCompatibilityAsync("esign:outside", Path.Combine(files, "outside.tmp"), 4, "esign-current", CancellationToken.None);
            Assert.Null(outside);
            Assert.NotNull(outsideError);
            Assert.Single(await db.StorageCapacityReservations.ToArrayAsync());
            if (bytes > 0) await File.WriteAllBytesAsync(path, Enumerable.Range(1, bytes).Select(value => (byte)value).ToArray());
            ticket = await AsLegacyReservationAsync(db, ticket!);
            var old = DateTime.UtcNow.AddHours(-1);
            await db.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UpdatedAt, old));
            var preview = await stages.ReconcilePendingAsync(false, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.Equal(1, preview.Inspected);
            Assert.Equal(eligible ? 1 : 0, preview.Eligible);
            Assert.Equal(0, preview.Settled);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, (await db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
            var applied = await stages.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.Equal(eligible ? 1 : 0, applied.Settled);
            Assert.Equal(eligible ? 0 : 1, applied.Retained);
            Assert.Equal(eligible ? StorageCapacityReservationStatuses.Released : StorageCapacityReservationStatuses.Pending,
                (await db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
            Assert.Equal(bytes > 0, File.Exists(path));
            if (bytes > 0) Assert.Equal(Enumerable.Range(1, bytes).Select(value => (byte)value).ToArray(), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Assert.Matches("^pp-stage-capacity-esign-[a-f0-9]{32}$", Path.GetFileName(root));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    [Theory]
    [InlineData(4, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public async Task ConfiguredSignatureRootRecoveryOnOwnedMigratedPostgreSql(int bytes, bool eligible)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(raw), "This qualification requires an explicit local PostgreSQL endpoint.");
        var settings = new NpgsqlConnectionStringBuilder(raw!) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "::1", "localhost" });
        var name = "pp_stage_esign_root_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "pp-stage-capacity-esign-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(root, "files");
        var signatures = Path.Combine(root, "configured-signatures");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(signatures);
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await create.ExecuteNonQueryAsync(); created = true; }
            Console.WriteLine("Owned signature stage database: " + name);
            settings.Database = name;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options);
            await db.Database.MigrateAsync();
            var environment = new TestEnvironment(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PayPlanner.Api.Services.ApplicationFileStoragePaths.RootPathConfigurationKey] = files,
                [PayPlanner.Api.Services.ElectronicSignatureStoragePaths.StorageRootConfigurationKey] = signatures
            }).Build();
            var providers = new StorageProviderRegistry([new LegacyLocalStorageProvider(environment, configuration)]);
            var options = new StaticOptions(new StorageOrchestrationOptions
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = 0
            });
            var stages = new StorageStageCapacityService(db, providers, options, environment, configuration);
            var path = Path.Combine(signatures, "original.tmp");
            var (ticket, error) = await stages.ReserveCompatibilityAsync("esign:root:probe", path, 4, "esign-current", CancellationToken.None);
            Assert.Null(error);
            Assert.NotNull(ticket);
            Assert.Equal("original.tmp", ticket.RelativePath);
            Assert.Equal(path, ticket.AbsolutePath);
            var (outside, outsideError) = await stages.ReserveCompatibilityAsync("esign:outside", Path.Combine(files, "outside.tmp"), 4, "esign-current", CancellationToken.None);
            Assert.Null(outside);
            Assert.NotNull(outsideError);
            Assert.Single(await db.StorageCapacityReservations.ToArrayAsync());
            if (bytes > 0) await File.WriteAllBytesAsync(path, Enumerable.Range(1, bytes).Select(value => (byte)value).ToArray());
            ticket = await AsLegacyReservationAsync(db, ticket!);
            var old = DateTime.UtcNow.AddHours(-1);
            await db.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UpdatedAt, old));
            var preview = await stages.ReconcilePendingAsync(false, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.Equal(1, preview.Inspected);
            Assert.Equal(eligible ? 1 : 0, preview.Eligible);
            Assert.Equal(0, preview.Settled);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, (await db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
            var applied = await stages.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.Equal(eligible ? 1 : 0, applied.Settled);
            Assert.Equal(eligible ? 0 : 1, applied.Retained);
            Assert.Equal(eligible ? StorageCapacityReservationStatuses.Released : StorageCapacityReservationStatuses.Pending,
                (await db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
            Assert.Equal(bytes > 0, File.Exists(path));
            if (bytes > 0) Assert.Equal(Enumerable.Range(1, bytes).Select(value => (byte)value).ToArray(), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Assert.Matches("^pp_stage_esign_root_[a-f0-9]{32}$", name);
            if (created)
            {
                long connections = -1;
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                    count.Parameters.AddWithValue("name", name);
                    connections = Convert.ToInt64(await count.ExecuteScalarAsync());
                    if (connections == 0) break;
                    await Task.Delay(100);
                }
                Assert.Equal(0L, connections);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
            Assert.Matches("^pp-stage-capacity-esign-[a-f0-9]{32}$", Path.GetFileName(root));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }

    private sealed class FixedCapacityLocalProvider(IStorageProvider inner, long? available, string key = "local")
        : IStorageProvider, IStorageCapacityProvider
    {
        public string Key => key;
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public async Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId,
            long minimumFreeBytes, CancellationToken ct)
        {
            var physical = await ((IStorageCapacityProvider)inner).ObserveCapacityAsync(resourceKey, connectionId, minimumFreeBytes, ct);
            return new StorageCapacityResource(resourceKey, physical.GroupId, available, minimumFreeBytes, DateTimeOffset.UtcNow);
        }
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
            => inner.PutAsync(request, ct);
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct)
            => inner.HeadAsync(locator, ct);
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct)
            => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct)
            => inner.DeleteAsync(locator, ct);
    }

    private sealed class PhysicalAwareLocalProvider(IStorageProvider inner, string stagedPath, long baseline)
        : IStorageProvider, IStorageCapacityProvider
    {
        public string Key => "local";
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public async Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId,
            long minimumFreeBytes, CancellationToken ct)
        {
            var physical = await ((IStorageCapacityProvider)inner).ObserveCapacityAsync(resourceKey, connectionId, minimumFreeBytes, ct);
            return new StorageCapacityResource(resourceKey, physical.GroupId,
                baseline - (File.Exists(stagedPath) ? new FileInfo(stagedPath).Length : 0),
                minimumFreeBytes, DateTimeOffset.UtcNow);
        }
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
            => inner.PutAsync(request, ct);
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct)
            => inner.HeadAsync(locator, ct);
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct)
            => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct)
            => inner.DeleteAsync(locator, ct);
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
    private static async Task<StorageStageCapacityTicket> AsLegacyReservationAsync(PaymentContext db, StorageStageCapacityTicket ticket)
    {
        await ticket.CloseWriterAsync();
        var legacyKey = "stage:" + ticket.Key["stage:writer-v1:".Length..];
        await db.StorageCapacityReservations.Where(row => row.IdempotencyKey == ticket.Key)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IdempotencyKey, legacyKey));
        return ticket with { Key = legacyKey };
    }
}
