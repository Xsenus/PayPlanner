using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageObjectServiceTests
{
    [Fact]
    public async Task CapacityCheckedUploadReservesThenAtomicallyPublishesAndReplays()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local") { AvailableBytes = 100 };
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        byte[] bytes = [1, 2, 3];
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var request = new StorageObjectWriteRequest("document", "task", "42", "reserved.bin",
            "application/octet-stream", new MemoryStream(bytes), "capacity-upload", ExpectedSizeBytes: bytes.Length,
            ExpectedSha256: sha);
        var created = await service.WriteAsync(request, CancellationToken.None);
        Assert.Equal("available", created.Status);
        Assert.Single(local.Objects);
        var reservation = Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        Assert.Equal(StorageCapacityReservationStatuses.Released, reservation.Status);
        Assert.Equal(created.ObjectId, reservation.ObjectId);
        Assert.Equal(2, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
        var replay = await service.WriteAsync(request with { Content = new MemoryStream(bytes) }, CancellationToken.None);
        Assert.Equal(created.ObjectId, replay.ObjectId);
        Assert.Single(local.Objects);
        Assert.Equal(2, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(request with
        {
            Content = new MemoryStream([9, 9, 9]),
            ExpectedSha256 = new string('a', 64)
        }, CancellationToken.None));
    }

    [Fact]
    public async Task CapacityCheckedUploadRefusesUnknownBudgetBeforeWriting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local") { AvailableBytes = null };
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        byte[] bytes = [1, 2, 3];
        var failed = await service.WriteAsync(new("document", "task", "42", "reserved.bin",
            "application/octet-stream", new MemoryStream(bytes), "capacity-unknown",
            ExpectedSizeBytes: bytes.Length, ExpectedSha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            CancellationToken.None);
        Assert.Equal(Guid.Empty, failed.ObjectId);
        Assert.Empty(local.Objects);
        Assert.Empty(await db.StorageCapacityReservations.ToArrayAsync());
        Assert.Equal(0, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
    }

    [Fact]
    public async Task CapacityCheckedUploadNeverPublishesBytesDifferentFromReservation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local") { AvailableBytes = 100 };
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var failed = await service.WriteAsync(new("document", "task", "42", "reserved.bin",
            "application/octet-stream", new MemoryStream([1, 2, 3]), "capacity-wrong-bytes",
            ExpectedSizeBytes: 3, ExpectedSha256: new string('a', 64)), CancellationToken.None);
        Assert.Equal(Guid.Empty, failed.ObjectId);
        Assert.Empty(await db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Equal(StorageCapacityReservationStatuses.Pending,
            Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync()).Status);
        Assert.Equal(1, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
    }

    [Fact]
    public async Task CapacityCheckedUploadDoesNotReleaseChangedReservation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local") { AvailableBytes = 100 };
        local.AfterPut = async () =>
        {
            await db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.Bytes, r => r.Bytes + 1));
        };
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        byte[] bytes = [1, 2, 3];
        var failed = await service.WriteAsync(new("document", "task", "42", "reserved.bin",
            "application/octet-stream", new MemoryStream(bytes), "capacity-tampered",
            ExpectedSizeBytes: bytes.Length, ExpectedSha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            CancellationToken.None);
        Assert.Equal(Guid.Empty, failed.ObjectId);
        Assert.Empty(await db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Equal(StorageCapacityReservationStatuses.Pending,
            Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync()).Status);
    }

    [Fact]
    public async Task CapacityCheckedUploadRejectsVolumeChangeAfterLocalWrite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local") { AvailableBytes = 100 };
        local.AfterPut = () => { local.CapacityGroupId = "replacement-volume"; return Task.CompletedTask; };
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        byte[] bytes = [1, 2, 3];
        var failed = await service.WriteAsync(new("document", "task", "42", "reserved.bin",
            "application/octet-stream", new MemoryStream(bytes), "capacity-remount",
            ExpectedSizeBytes: bytes.Length, ExpectedSha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            CancellationToken.None);
        Assert.Equal(Guid.Empty, failed.ObjectId);
        Assert.Empty(await db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Equal(StorageCapacityReservationStatuses.Pending,
            Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync()).Status);
    }

    [Fact]
    public async Task InterruptedCapacityCheckedUploadRetainsReservationAndResumesSameBytes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local")
        {
            AvailableBytes = 100,
            AfterPut = () => throw new IOException("Synthetic interrupted upload")
        };
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        byte[] bytes = [1, 2, 3];
        var request = new StorageObjectWriteRequest("document", "task", "42", "reserved.bin",
            "application/octet-stream", new MemoryStream(bytes), "capacity-resume",
            ExpectedSizeBytes: bytes.Length, ExpectedSha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        await Assert.ThrowsAsync<IOException>(() => service.WriteAsync(request, CancellationToken.None));
        Assert.Equal(StorageCapacityReservationStatuses.Pending,
            Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync()).Status);
        Assert.Empty(await db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
        local.AfterPut = null;
        var resumed = await service.WriteAsync(request with { Content = new MemoryStream(bytes) }, CancellationToken.None);
        Assert.NotEqual(Guid.Empty, resumed.ObjectId);
        Assert.Single(local.Objects);
        Assert.Equal(StorageCapacityReservationStatuses.Released,
            Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync()).Status);
        Assert.Equal(2, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
    }

    [Fact]
    public async Task CapacityCheckedUploadTransfersRemoteDemandToDurableCopyQueue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local") { AvailableBytes = 100 };
        var mirror = new MemoryProvider("mirror") { AvailableBytes = 100 };
        var registry = new StorageProviderRegistry([local, mirror]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0,
            ProviderConnections = new(StringComparer.OrdinalIgnoreCase) { ["mirror"] = "owned-mirror" },
            DefaultPolicy = new()
            {
                RequiredCopies = 2,
                WriteProviders = ["local", "mirror"],
                ReadProviders = ["local", "mirror"]
            }
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        byte[] bytes = [1, 2, 3];
        var created = await service.WriteAsync(new("document", "task", "42", "reserved.bin",
            "application/octet-stream", new MemoryStream(bytes), "capacity-handoff",
            ExpectedSizeBytes: bytes.Length, ExpectedSha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            CancellationToken.None);
        Assert.Equal("pending", created.Status);
        Assert.Equal(3, await db.StorageCapacityReservations.CountAsync());
        Assert.Equal(3, await db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Released));
        var copy = Assert.Single(await db.StorageOperations.AsNoTracking()
            .Where(o => o.OperationType == StorageOperationTypes.Replicate).ToArrayAsync());
        Assert.Equal(StorageOperationStatuses.Pending, copy.Status);
        Assert.Equal("mirror", copy.ProviderKey);
        Assert.Equal(2, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
    }

    [Fact]
    public async Task CapacityCheckedUploadRefusesUnknownRemoteQuotaBeforeLocalWrite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local") { AvailableBytes = 100 };
        var mirror = new MemoryProvider("mirror") { AvailableBytes = null };
        var registry = new StorageProviderRegistry([local, mirror]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0,
            ProviderConnections = new(StringComparer.OrdinalIgnoreCase) { ["mirror"] = "owned-mirror" },
            DefaultPolicy = new()
            {
                RequiredCopies = 2,
                WriteProviders = ["local", "mirror"],
                ReadProviders = ["local", "mirror"]
            }
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        byte[] bytes = [1, 2, 3];
        var refused = await service.WriteAsync(new("document", "task", "42", "reserved.bin",
            "application/octet-stream", new MemoryStream(bytes), "capacity-unknown-mirror",
            ExpectedSizeBytes: bytes.Length, ExpectedSha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            CancellationToken.None);
        Assert.Equal(Guid.Empty, refused.ObjectId);
        Assert.Empty(local.Objects);
        Assert.Empty(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task CapacityCheckedUploadPublishesOnOwnedMigratedPostgreSql()
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_capacity_upload_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await create.ExecuteNonQueryAsync(); created = true; }
            settings.Database = name;
            var dbOptions = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options;
            await using var db = new PostgreSqlPaymentContext(dbOptions);
            await db.Database.MigrateAsync();
            var local = new MemoryProvider("local") { AvailableBytes = 100 };
            var registry = new StorageProviderRegistry([local]);
            var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                UploadMinimumFreeBytes = 0
            });
            var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
            byte[] bytes = [1, 2, 3];
            var createdObject = await service.WriteAsync(new("document", "task", "42", "owned.bin",
                "application/octet-stream", new MemoryStream(bytes), "capacity-pg",
                ExpectedSizeBytes: bytes.Length, ExpectedSha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
                CancellationToken.None);
            Assert.Equal("available", createdObject.Status);
            Assert.Single(await db.StorageObjects.AsNoTracking().ToArrayAsync());
            Assert.Equal(StorageCapacityReservationStatuses.Released,
                Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync()).Status);
            Assert.Equal(2, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
            await using var siblingDb = new PostgreSqlPaymentContext(dbOptions);
            var siblingService = new StorageObjectService(siblingDb, new StoragePolicyService(monitor, registry), registry, monitor);
            var observed = 0;
            var bothObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            local.BeforeObserve = async () =>
            {
                if (Interlocked.Increment(ref observed) == 2) bothObserved.TrySetResult();
                await bothObserved.Task.WaitAsync(TimeSpan.FromSeconds(20));
            };
            byte[] competingBytes = Enumerable.Repeat((byte)7, 60).ToArray();
            var competingSha = Convert.ToHexString(SHA256.HashData(competingBytes)).ToLowerInvariant();
            StorageObjectWriteRequest Competing(string key) => new("document", "task", key, key + ".bin",
                "application/octet-stream", new MemoryStream(competingBytes), key,
                ExpectedSizeBytes: competingBytes.Length, ExpectedSha256: competingSha);
            var outcomes = await Task.WhenAll(
                service.WriteAsync(Competing("capacity-race-left"), CancellationToken.None),
                siblingService.WriteAsync(Competing("capacity-race-right"), CancellationToken.None));
            Assert.Single(outcomes, outcome => outcome.ObjectId != Guid.Empty);
            Assert.Single(outcomes, outcome => outcome.ObjectId == Guid.Empty);
            Assert.Equal(2, await db.StorageObjects.AsNoTracking().CountAsync());
            Assert.Equal(4, await db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
        }
        finally
        {
            Assert.Matches("^pp_capacity_upload_[a-f0-9]{32}$", name);
            if (created)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task WritePassesKnownSizeToLocalProviderAndRejectsNegativeSizeBeforeWriting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local");
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new() { Enabled = true });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var invalid = new StorageObjectWriteRequest("document", "task", "42", "size.txt", "text/plain",
            new MemoryStream([1, 2, 3]), "size-invalid", ExpectedSizeBytes: -1);
        await Assert.ThrowsAsync<ArgumentException>(() => service.WriteAsync(invalid, CancellationToken.None));
        Assert.Equal(0, invalid.Content.Position);
        Assert.Empty(local.Objects);
        var saved = await service.WriteAsync(invalid with { IdempotencyKey = "size-valid", ExpectedSizeBytes = 3 }, CancellationToken.None);
        Assert.NotEqual(Guid.Empty, saved.ObjectId);
        Assert.Equal(3, local.LastExpectedSizeBytes);
        var mismatched = await service.WriteAsync(invalid with
        {
            IdempotencyKey = "size-mismatch",
            Content = new MemoryStream([1, 2]),
            ExpectedSizeBytes = 3
        }, CancellationToken.None);
        Assert.Equal(Guid.Empty, mismatched.ObjectId);
        Assert.Single(local.Objects);
        Assert.Single(await db.StorageObjects.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("io")]
    [InlineData("locatorRace")]
    [InlineData("statusRace")]
    public async Task ReadDoesNotReturnBytesChangedAfterMetadataVerification(string failure)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local");
        var mirror = new MemoryProvider("mirror");
        var registry = new StorageProviderRegistry([local, mirror]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            ProviderConnections = new(StringComparer.OrdinalIgnoreCase) { ["mirror"] = "owned-read-mirror" },
            DefaultPolicy = new()
            {
                Version = "read-stream-proof",
                RequiredCopies = 2,
                WriteProviders = ["local", "mirror"],
                ReadProviders = ["local", "mirror"]
            }
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var bytes = Encoding.UTF8.GetBytes("Проверенный документ до подмены");
        var created = await service.WriteAsync(new("document", "case", "42", "stream.txt", "text/plain",
            new MemoryStream(bytes), "read-stream-proof"), CancellationToken.None);
        Assert.Equal(1, (await new StorageOperationProcessor(db, registry, monitor, TimeProvider.System,
            NullLogger<StorageOperationProcessor>.Instance).ProcessBatchAsync("seed-read-mirror", CancellationToken.None)).Succeeded);
        local.ResetReadCounters();
        mirror.ResetReadCounters();
        var locator = Assert.Single(local.Objects).Key;
        local.BeforeRead = () =>
        {
            if (failure == "locatorRace") return db.StorageReplicas.Where(r => r.ProviderKey == "local")
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Locator, "changed-by-other-request"));
            if (failure == "statusRace") return db.StorageReplicas.Where(r => r.ProviderKey == "local")
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, StorageReplicaStatuses.Corrupt));
            if (failure == "io") throw new IOException("Owned synthetic read failure");
            local.Objects[locator] = failure switch
            {
                "short" => bytes[..^1],
                "long" => [.. bytes, 1],
                _ => bytes.Select(value => (byte)(value ^ 1)).ToArray(),
            };
            return Task.CompletedTask;
        };
        await using var read = await service.OpenReadAsync(created.ObjectId, CancellationToken.None);
        Assert.NotNull(read);
        using var actual = new MemoryStream();
        await read!.Content.CopyToAsync(actual);
        Assert.Equal(bytes, actual.ToArray());
        Assert.Equal("mirror", read.ProviderKey);
        Assert.Equal(0, local.HeadCalls + mirror.HeadCalls);
        Assert.Equal(1, local.ReadCalls);
        Assert.Equal(1, mirror.ReadCalls);
        Assert.False(mirror.LastRead!.CanRead);
        var staged = Assert.IsType<FileStream>(read.Content);
        Assert.True(staged.CanSeek);
        Assert.StartsWith("pp_verified_read_", Path.GetFileName(staged.Name));
        Assert.True(File.Exists(staged.Name));
        await read.DisposeAsync();
        Assert.False(File.Exists(staged.Name));
        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), $"pp_verified_read_{created.ObjectId:N}_*.tmp"));
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("checksum")]
    [InlineData("locator")]
    [InlineData("status")]
    [InlineData("tombstone")]
    public async Task ReadRejectsCatalogChangedWhileDownloading(string change)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local");
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            DefaultPolicy = new() { RequiredCopies = 1, WriteProviders = ["local"], ReadProviders = ["local"] }
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var created = await service.WriteAsync(new("document", "case", "42", "race.txt", "text/plain",
            new MemoryStream([1, 2, 3]), "owned-read-race"), CancellationToken.None);
        local.BeforeRead = async () =>
        {
            switch (change)
            {
                case "deleted": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true)); break;
                case "version": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.Version, o => o.Version + 1)); break;
                case "generation": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.CurrentGeneration, 2)); break;
                case "checksum": await db.StorageObjectGenerations.ExecuteUpdateAsync(s => s.SetProperty(g => g.Sha256, new string('a', 64))); break;
                case "locator": await db.StorageReplicas.ExecuteUpdateAsync(s => s.SetProperty(r => r.Locator, "changed-by-other-request")); break;
                case "status": await db.StorageReplicas.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, StorageReplicaStatuses.Corrupt)); break;
                case "tombstone": db.StorageTombstones.Add(new() { StorageObjectId = created.ObjectId }); await db.SaveChangesAsync(); break;
            }
        };
        Assert.Null(await service.OpenReadAsync(created.ObjectId, CancellationToken.None));
        Assert.False(local.LastRead!.CanRead);
        Assert.Equal(1, local.ReadCalls);
        Assert.Equal(change == "status" ? StorageReplicaStatuses.Corrupt : StorageReplicaStatuses.Available,
            (await db.StorageReplicas.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), $"pp_verified_read_{created.ObjectId:N}_*.tmp"));
    }

    [Fact]
    public async Task ReadCancellationClosesOwnedStreamAndStagingWithoutChangingReplicaState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local");
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            DefaultPolicy = new() { RequiredCopies = 1, WriteProviders = ["local"], ReadProviders = ["local"] }
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var created = await service.WriteAsync(new("document", "case", "42", "cancel.txt", "text/plain",
            new MemoryStream([1, 2, 3]), "owned-read-cancel"), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        local.BeforeRead = () => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.OpenReadAsync(created.ObjectId, cancellation.Token));
        Assert.False(local.LastRead!.CanRead);
        Assert.Equal(StorageReplicaStatuses.Available, (await db.StorageReplicas.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), $"pp_verified_read_{created.ObjectId:N}_*.tmp"));
    }

    [Fact]
    public async Task ReadStagingFailureDoesNotMarkHealthyReplicaSuspect()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local");
        var mirror = new MemoryProvider("mirror");
        var registry = new StorageProviderRegistry([local, mirror]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            ProviderConnections = new(StringComparer.OrdinalIgnoreCase) { ["mirror"] = "owned-stage-mirror" },
            DefaultPolicy = new() { RequiredCopies = 2, WriteProviders = ["local", "mirror"], ReadProviders = ["local", "mirror"] }
        });
        var storage = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var created = await storage.WriteAsync(new("document", "case", "42", "stage.txt", "text/plain",
            new MemoryStream([1, 2, 3]), "owned-read-stage-failure"), CancellationToken.None);
        Assert.Equal(1, (await new StorageOperationProcessor(db, registry, monitor, TimeProvider.System,
            NullLogger<StorageOperationProcessor>.Instance).ProcessBatchAsync("owned-stage-copy", CancellationToken.None)).Succeeded);
        local.ResetReadCounters(); mirror.ResetReadCounters();
        var reader = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor,
            verifiedReadSpoolFactory: _ => throw new IOException("Owned synthetic spool is full."));

        Assert.Null(await reader.OpenReadAsync(created.ObjectId, CancellationToken.None));
        Assert.Equal(1, local.ReadCalls);
        Assert.Equal(0, mirror.ReadCalls);
        Assert.False(local.LastRead!.CanRead);
        Assert.All(await db.StorageReplicas.AsNoTracking().ToArrayAsync(),
            replica => Assert.Equal(StorageReplicaStatuses.Available, replica.Status));
        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), $"pp_verified_read_{created.ObjectId:N}_*.tmp"));
    }

    [Theory]
    [InlineData(StorageOperationStatuses.Succeeded, false)]
    [InlineData(StorageOperationStatuses.Succeeded, true)]
    [InlineData(StorageOperationStatuses.Pending, false)]
    [InlineData(StorageOperationStatuses.Running, false)]
    [InlineData(StorageOperationStatuses.Failed, false)]
    [InlineData(StorageOperationStatuses.Unknown, false)]
    [InlineData(StorageOperationStatuses.DeadLetter, false)]
    public async Task ReconciliationRepairsNewLossWithoutReopeningPriorOperations(string priorStatus, bool competing)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var beforeSave = new ReconciliationSaveRace();
        var plainOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(beforeSave).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local"); var mirror = new MemoryProvider("mirror");
        var registry = new StorageProviderRegistry([local, mirror]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            ProviderConnections = new(StringComparer.OrdinalIgnoreCase) { ["mirror"] = "owned-repair-mirror" },
            DefaultPolicy = new()
            {
                Version = "repair-cycles",
                RequiredCopies = 2,
                WriteProviders = ["local", "mirror"],
                ReadProviders = ["local", "mirror"]
            }
        });
        var storage = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var bytes = Encoding.UTF8.GetBytes("Repeated recovery: Документ");
        await storage.WriteAsync(new("document", "case", "42", "cycles.txt", "text/plain", new MemoryStream(bytes), "repair-cycles"), CancellationToken.None);
        var processor = new StorageOperationProcessor(db, registry, monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
        Assert.Equal(1, (await processor.ProcessBatchAsync("seed-repair-copy", CancellationToken.None)).Succeeded);
        db.ChangeTracker.Clear(); mirror.Objects.Clear();
        var reconcile = new StorageReconciliationService(db, registry);
        Assert.Equal(1, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        var firstId = (await db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.Repair)).Id;
        Assert.Equal(0, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        if (priorStatus == StorageOperationStatuses.Succeeded)
        {
            Assert.Equal(1, (await processor.ProcessBatchAsync("first-repair", CancellationToken.None)).Succeeded);
            Assert.Equal(bytes, Assert.Single(mirror.Objects).Value);
            mirror.Objects.Clear();
        }
        else
            await db.StorageOperations.Where(o => o.Id == firstId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, priorStatus));
        db.ChangeTracker.Clear();
        var first = await db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == firstId);
        if (competing) db.Clients.Add(new Client { Name = "Keep unrelated tracked data" });
        if (competing) beforeSave.Callback = async () =>
        {
            await using var winnerDb = new PaymentContext(plainOptions);
            var won = await new StorageReconciliationService(winnerDb, registry).RunAsync(true, CancellationToken.None);
            Assert.Equal(1, won.RepairOperationsQueued);
        };
        var next = await reconcile.RunAsync(true, CancellationToken.None);
        Assert.Equal(priorStatus == StorageOperationStatuses.Succeeded && !competing ? 1 : 0, next.RepairOperationsQueued);
        Assert.Equal(competing ? 1 : 0, beforeSave.Calls);
        if (competing) Assert.Single(await db.Clients.AsNoTracking().Where(c => c.Name == "Keep unrelated tracked data").ToArrayAsync());
        Assert.Equal(0, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        var retained = await db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == firstId);
        Assert.Equal(first.Status, retained.Status);
        Assert.Equal(first.CompletedAt, retained.CompletedAt);
        Assert.Equal(first.AttemptCount, retained.AttemptCount);
        Assert.Equal(first.IdempotencyKey, retained.IdempotencyKey);
        Assert.Equal(first.UpdatedAt, retained.UpdatedAt);
        Assert.Equal(priorStatus == StorageOperationStatuses.Succeeded ? 2 : 1,
            await db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.Repair));
        if (priorStatus == StorageOperationStatuses.Succeeded)
        {
            Assert.Equal(1, (await processor.ProcessBatchAsync("second-repair", CancellationToken.None)).Succeeded);
            Assert.Equal(bytes, Assert.Single(mirror.Objects).Value);
            Assert.Equal(0, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
            Assert.Equal(2, await db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.Repair && o.Status == StorageOperationStatuses.Succeeded));
        }
    }

    private sealed class ReconciliationSaveRace : SaveChangesInterceptor
    {
        public Func<Task>? Callback { get; set; }
        public int Calls { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Callback is { } action) { Callback = null; Calls++; await action(); }
            return result;
        }
    }

    [Theory]
    [InlineData("unavailable", false)]
    [InlineData("unavailable", true)]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("conflict", false)]
    [InlineData("conflict", true)]
    [InlineData("noChecksum", false)]
    [InlineData("noChecksum", true)]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("corrupt", false)]
    [InlineData("corrupt", true)]
    [InlineData("sizeNoChecksum", false)]
    [InlineData("sizeNoChecksum", true)]
    public async Task ReconciliationRequiresEvidenceBeforeMarkingReplicaDamaged(string outcome, bool apply)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local");
        var mirror = new MemoryProvider("mirror");
        var registry = new StorageProviderRegistry([local, mirror]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            ProviderConnections = new(StringComparer.OrdinalIgnoreCase) { ["mirror"] = "owned-memory-mirror" },
            DefaultPolicy = new()
            {
                Version = "reconcile-evidence",
                RequiredCopies = 2,
                WriteProviders = ["local", "mirror"],
                ReadProviders = ["local", "mirror"]
            }
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var bytes = Encoding.UTF8.GetBytes("Verified recovery bytes: Документ");
        await service.WriteAsync(new("document", "case", "42", "evidence.txt", "text/plain", new MemoryStream(bytes),
            "reconcile-evidence"), CancellationToken.None);
        var processor = new StorageOperationProcessor(db, registry, monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
        Assert.Equal(1, (await processor.ProcessBatchAsync("owned-reconcile-seed", CancellationToken.None)).Succeeded);
        db.ChangeTracker.Clear();
        if (outcome == "unknown")
            await db.StorageReplicas.Where(r => r.ProviderKey == "mirror").ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, StorageReplicaStatuses.Corrupt).SetProperty(r => r.LastError, "Earlier confirmed damage"));
        var prior = await db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == "mirror");
        var operationCount = await db.StorageOperations.CountAsync();
        mirror.HeadOverride = outcome switch
        {
            "unavailable" => StorageOperationStatus.Unavailable,
            "unknown" => StorageOperationStatus.Unknown,
            "conflict" => StorageOperationStatus.Conflict,
            _ => null
        };
        mirror.OmitHeadChecksum = outcome is "noChecksum" or "sizeNoChecksum";
        if (outcome == "missing") mirror.Objects.Clear();
        if (outcome == "corrupt")
        {
            var damaged = bytes.ToArray(); damaged[0] ^= 1;
            mirror.Objects[prior.Locator] = damaged; // Same size, different checksum.
        }
        if (outcome == "sizeNoChecksum") mirror.Objects[prior.Locator] = [1];
        var verifiedDamage = outcome is "missing" or "corrupt" or "sizeNoChecksum";
        var report = await new StorageReconciliationService(db, registry).RunAsync(apply, CancellationToken.None);
        var issue = Assert.Single(report.Issues);
        var expectedKind = outcome == "missing" ? "missing" : verifiedDamage ? "corrupt" : "verificationUnavailable";
        Assert.Equal(expectedKind, issue.Kind);
        Assert.Equal(1, report.HealthyReplicas);
        Assert.Equal(outcome == "missing" ? 1 : 0, report.MissingReplicas);
        Assert.Equal(verifiedDamage && outcome != "missing" ? 1 : 0, report.CorruptReplicas);
        Assert.Equal(apply && verifiedDamage ? 1 : 0, report.RepairOperationsQueued);
        var after = await db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Id == prior.Id);
        Assert.Equal(apply && verifiedDamage ? expectedKind : prior.Status, after.Status);
        Assert.Equal(prior.LastVerifiedAt, after.LastVerifiedAt);
        Assert.Equal(operationCount + (apply && verifiedDamage ? 1 : 0), await db.StorageOperations.CountAsync());
        if (!verifiedDamage)
        {
            Assert.Equal(prior.LastError, after.LastError);
            Assert.Equal(prior.UpdatedAt, after.UpdatedAt);
            Assert.Equal(bytes, mirror.Objects[prior.Locator]);
            mirror.HeadOverride = null; mirror.OmitHeadChecksum = false;
            var recovered = await new StorageReconciliationService(db, registry).RunAsync(true, CancellationToken.None);
            Assert.Equal(2, recovered.HealthyReplicas);
            Assert.Empty(recovered.Issues);
            Assert.Equal(0, recovered.RepairOperationsQueued);
            Assert.Equal(operationCount, await db.StorageOperations.CountAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingFirstWriteReplaysOnlyMatchingWinnerAndPreservesUnrelatedTrackedChanges(bool differentOwner)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        await using var db = new PaymentContext(dbOptions);
        await using var winnerDb = new PaymentContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local");
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new() { Enabled = true });
        var policy = new StoragePolicyService(monitor, registry);
        var service = new StorageObjectService(db, policy, registry, monitor);
        var winnerService = new StorageObjectService(winnerDb, policy, registry, monitor);
        var unrelated = new Client { Name = "Не относится к загрузке" };
        db.Clients.Add(unrelated);
        Guid winnerId = Guid.Empty;
        // The other scoped context commits after this request's initial lookup,
        // but before it saves its graph: a real SQLite UNIQUE constraint failure.
        local.AfterPut = async () =>
        {
            local.AfterPut = null;
            var winner = await winnerService.WriteAsync(new("document", "task", differentOwner ? "43" : "42",
                "file.txt", "text/plain", new MemoryStream([1, 2, 3]), "competing-key"), CancellationToken.None);
            winnerId = winner.ObjectId;
        };
        var request = new StorageObjectWriteRequest("document", "task", "42", "file.txt", "text/plain",
            new MemoryStream([1, 2, 3]), "competing-key");
        if (differentOwner)
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(request, CancellationToken.None));
        else
        {
            var replay = await service.WriteAsync(request, CancellationToken.None);
            Assert.Equal(winnerId, replay.ObjectId);
        }
        Assert.Equal(EntityState.Added, db.Entry(unrelated).State);
        Assert.Single(local.Objects);
        Assert.Single(await db.StorageObjects.ToListAsync());
        await db.SaveChangesAsync();
        Assert.Single(await db.Clients.ToListAsync());
        Assert.Single(await db.StorageObjects.ToListAsync());
        Assert.Single(await db.StorageOperations.ToListAsync());
    }

    [Theory]
    [InlineData("class")]
    [InlineData("ownerType")]
    [InlineData("ownerId")]
    [InlineData("name")]
    [InlineData("contentType")]
    [InlineData("user")]
    [InlineData("deleted")]
    public async Task Retry_RejectsDifferentBindingOrDeletedObjectWithoutWriting(string change)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local");
        var registry = new StorageProviderRegistry([local]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new() { Enabled = true });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var request = new StorageObjectWriteRequest("document", "task", "42", "file.txt", "text/plain",
            new MemoryStream([1, 2, 3]), "shared-key");
        var created = await service.WriteAsync(request, CancellationToken.None);
        if (change == "deleted")
            Assert.True(await service.DeleteAsync(created.ObjectId, null, "test", CancellationToken.None));
        var retry = request with
        {
            DataClass = change == "class" ? "photo" : request.DataClass,
            OwnerType = change == "ownerType" ? "case" : request.OwnerType,
            OwnerId = change == "ownerId" ? "99" : request.OwnerId,
            LogicalName = change == "name" ? "other.txt" : request.LogicalName,
            ContentType = change == "contentType" ? "image/png" : request.ContentType,
            CreatedByUserId = change == "user" ? 77 : request.CreatedByUserId,
            Content = new MemoryStream([9, 9]),
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(retry, CancellationToken.None));
        Assert.DoesNotContain("shared-key", error.Message);
        Assert.Equal(0, retry.Content.Position);
        Assert.Single(await db.StorageObjects.ToListAsync());
        Assert.Single(await db.StorageObjectGenerations.ToListAsync());
        Assert.Equal(new byte[] { 1, 2, 3 }, local.Objects.Values.Single());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retry_StaysPendingUntilPolicyCopiesExist(bool localIsRequired)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var local = new MemoryProvider("local");
        var mirror = new MemoryProvider("mirror");
        var registry = new StorageProviderRegistry([local, mirror]);
        var settings = new StorageOrchestrationOptions
        {
            Enabled = true,
            ProviderConnections = new(StringComparer.OrdinalIgnoreCase) { ["mirror"] = "synthetic" },
            DefaultPolicy = new()
            {
                RequiredCopies = localIsRequired ? 2 : 1,
                WriteProviders = localIsRequired ? ["local", "mirror"] : ["mirror"],
                ReadProviders = ["local", "mirror"],
            },
        };
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(settings);
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var request = new StorageObjectWriteRequest("document", "task", "42", "file.txt", "text/plain",
            new MemoryStream([1, 2, 3]), "mirror-key");
        var created = await service.WriteAsync(request, CancellationToken.None);
        Assert.Equal("pending", created.Status);
        // Even an available staging copy outside the durability policy must not satisfy the acknowledgement.
        await db.StorageReplicas.Where(item => item.ProviderKey == "local")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, StorageReplicaStatuses.Available));
        var pending = await service.WriteAsync(request with { Content = new MemoryStream([9]) }, CancellationToken.None);
        Assert.Equal(created.ObjectId, pending.ObjectId);
        Assert.Equal("pending", pending.Status);
        Assert.Equal(localIsRequired ? 1 : 0, pending.AvailableCopies);
        var processor = new StorageOperationProcessor(db, registry, monitor, TimeProvider.System,
            NullLogger<StorageOperationProcessor>.Instance);
        Assert.Equal(1, (await processor.ProcessBatchAsync("synthetic-worker", CancellationToken.None)).Succeeded);
        var completed = await service.WriteAsync(request with { Content = new MemoryStream([9]) }, CancellationToken.None);
        Assert.Equal("available", completed.Status);
        Assert.Equal(settings.DefaultPolicy.RequiredCopies, completed.AvailableCopies);
        Assert.Equal(created.ObjectId, completed.ObjectId);
        Assert.Single(local.Objects);
        Assert.Single(mirror.Objects);
        await db.StorageOperations.Where(item => item.OperationType == "commit")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, StorageOperationStatuses.Unknown));
        Assert.Equal("pending", (await service.WriteAsync(request, CancellationToken.None)).Status);
        await db.StorageOperations.Where(item => item.OperationType == "commit")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, StorageOperationStatuses.Succeeded));
        await db.StorageReplicas.Where(item => item.ProviderKey == "mirror")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, StorageReplicaStatuses.Corrupt));
        Assert.Equal("pending", (await service.WriteAsync(request, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task LocalOnly_WriteReadRetryAndDelete_AreConsistent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        await using var db = new PaymentContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var provider = new MemoryProvider("local");
        var registry = new StorageProviderRegistry([provider]);
        var settings = new StorageOrchestrationOptions
        {
            Enabled = true,
            PolicyDryRunEnabled = true,
            WorkerBatchSize = 10,
            WorkerMaxAttempts = 3,
            WorkerLeaseSeconds = 60,
        };
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(settings);
        var policies = new StoragePolicyService(monitor, registry);
        var service = new StorageObjectService(db, policies, registry, monitor);
        var bytes = Encoding.UTF8.GetBytes("stable content");

        var created = await service.WriteAsync(new StorageObjectWriteRequest(
            "document", "task", "42", "Доказательство.txt", "text/plain",
            new MemoryStream(bytes), "request-42"), CancellationToken.None);

        Assert.Equal("available", created.Status);
        Assert.Equal(1, created.AvailableCopies);
        Assert.NotEqual(Guid.Empty, created.ObjectId);
        Assert.Single(await db.StorageObjects.ToListAsync());
        Assert.Single(await db.StorageObjectGenerations.ToListAsync());

        var retry = await service.WriteAsync(new StorageObjectWriteRequest(
            " document ", " task ", " 42 ", " Доказательство.txt ", " text/plain ",
            new MemoryStream(Encoding.UTF8.GetBytes("must not replace")), "request-42"), CancellationToken.None);
        Assert.Equal(created.ObjectId, retry.ObjectId);
        Assert.Single(await db.StorageObjects.ToListAsync());

        await using (var read = await service.OpenReadAsync(created.ObjectId, CancellationToken.None))
        {
            Assert.NotNull(read);
            using var memory = new MemoryStream();
            await read!.Content.CopyToAsync(memory);
            Assert.Equal(bytes, memory.ToArray());
        }

        var reconciliation = new StorageReconciliationService(db, registry);
        var healthyReport = await reconciliation.RunAsync(apply: false, CancellationToken.None);
        Assert.Equal(1, healthyReport.HealthyReplicas);
        var locator = provider.Objects.Keys.Single();
        provider.Objects[locator] = Encoding.UTF8.GetBytes("corrupted");
        var corruptReport = await reconciliation.RunAsync(apply: true, CancellationToken.None);
        Assert.Equal(1, corruptReport.CorruptReplicas);
        Assert.Equal(StorageReplicaStatuses.Corrupt, (await db.StorageReplicas.AsNoTracking().SingleAsync()).Status);

        Assert.True(await service.DeleteAsync(created.ObjectId, 7, "user request", CancellationToken.None));
        Assert.Null(await service.OpenReadAsync(created.ObjectId, CancellationToken.None));
        Assert.Single(await db.StorageTombstones.ToListAsync());

        var processor = new StorageOperationProcessor(
            db, registry, monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
        var processed = await processor.ProcessBatchAsync("delete-worker", CancellationToken.None);

        Assert.Equal(0, processed.Succeeded);
        Assert.Equal(1, processed.Retried);
        Assert.Single(provider.Objects);
        Assert.Equal(StorageReplicaStatuses.DeletePending, (await db.StorageReplicas.SingleAsync()).Status);
        var pendingDelete = await db.StorageOperations.SingleAsync(o => o.OperationType == StorageOperationTypes.Delete);
        Assert.Equal(StorageOperationStatuses.Failed, pendingDelete.Status);
        provider.Objects[locator] = bytes;
        pendingDelete.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var retried = await processor.ProcessBatchAsync("delete-worker-retry", CancellationToken.None);
        Assert.Equal(1, retried.Succeeded);
        Assert.Empty(provider.Objects);
        Assert.Equal(StorageReplicaStatuses.Deleted, (await db.StorageReplicas.SingleAsync()).Status);
    }

    [Fact]
    public async Task Write_DoesNotChangeLegacyStorageWhenRoutingIsDisabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        await using var db = new PaymentContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var provider = new MemoryProvider("local");
        var registry = new StorageProviderRegistry([provider]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new StorageOrchestrationOptions { Enabled = false });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);

        var result = await service.WriteAsync(new StorageObjectWriteRequest(
            "document", "task", "42", "file.txt", "text/plain", new MemoryStream([1, 2, 3]), "disabled-1"), CancellationToken.None);

        Assert.Equal("disabled", result.Status);
        Assert.Empty(provider.Objects);
        Assert.Empty(await db.StorageObjects.ToListAsync());
    }

    [Fact]
    public async Task Delete_WithLegalHold_CreatesTombstoneWithoutPhysicalDelete()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        await using var db = new PaymentContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var provider = new MemoryProvider("local");
        var registry = new StorageProviderRegistry([provider]);
        var settings = new StorageOrchestrationOptions
        {
            Enabled = true,
            DefaultPolicy = new StoragePolicyDefinition
            {
                Version = "hold-v1",
                RequiredCopies = 1,
                WriteProviders = ["local"],
                ReadProviders = ["local"],
                LegalHold = true,
            },
        };
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(settings);
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var created = await service.WriteAsync(new StorageObjectWriteRequest(
            "evidence", "case", "10", "evidence.bin", "application/octet-stream", new MemoryStream([1, 2, 3]), "hold-write"), CancellationToken.None);

        Assert.True(await service.DeleteAsync(created.ObjectId, 5, "retention", CancellationToken.None));
        var tombstone = await db.StorageTombstones.SingleAsync();
        Assert.True(tombstone.LegalHold);
        Assert.False(await db.StorageOperations.AnyAsync(item => item.OperationType == StorageOperationTypes.Delete));
        Assert.True(provider.Objects.Any());

        var report = await new StorageReconciliationService(db, registry).RunAsync(true, CancellationToken.None);
        Assert.Contains(report.Issues, item => item.Kind == "retained");
        Assert.Equal(0, report.DeleteOperationsQueued);
        Assert.True(provider.Objects.Any());
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class MemoryProvider(string key) : IStorageProvider, IStorageRepairDestinationProvider, IStorageCapacityProvider
    {
        public string Key => key;
        public long? AvailableBytes { get; set; } = 1_000_000;
        public string CapacityGroupId { get; set; } = "synthetic-local-volume";
        public Func<Task>? BeforeObserve { get; set; }
        public async Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId,
            long minimumFreeBytes, CancellationToken ct)
        {
            if (BeforeObserve is not null) await BeforeObserve();
            return new StorageCapacityResource(resourceKey, CapacityGroupId, AvailableBytes,
                minimumFreeBytes, DateTimeOffset.UtcNow);
        }
        public long? LastExpectedSizeBytes { get; private set; }
        public string CreateRepairLocator(string originalLocator, Guid generationId, string idempotencyKey)
            => $"owned-recovery/{generationId:N}/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant()}.bin";
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public Func<Task>? AfterPut { get; set; }
        public Func<Task>? AfterHead { get; set; }
        public Func<Task>? BeforeRead { get; set; }
        public int HeadCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public Stream? LastRead { get; private set; }
        public void ResetReadCounters() { HeadCalls = 0; ReadCalls = 0; LastRead = null; }
        public StorageOperationStatus? HeadOverride { get; set; }
        public bool OmitHeadChecksum { get; set; }
        public StorageProviderCapabilities Capabilities { get; } = new(true, true, true, true, false, false, false, false, false);

        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken)
        {
            LastExpectedSizeBytes = request.ExpectedSizeBytes;
            if (!request.Overwrite && Objects.ContainsKey(request.Locator))
                return new(StorageOperationStatus.Conflict, request.Locator, 0, null);
            using var memory = new MemoryStream();
            await request.Content.CopyToAsync(memory, cancellationToken);
            var bytes = memory.ToArray();
            if (request.ExpectedSizeBytes.HasValue && bytes.Length != request.ExpectedSizeBytes.Value)
                return new(StorageOperationStatus.Conflict, request.Locator, bytes.Length, null);
            Objects[request.Locator] = bytes;
            if (AfterPut is not null) await AfterPut();
            return new(StorageOperationStatus.Succeeded, request.Locator, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        public async Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken)
        {
            HeadCalls++;
            var metadata = HeadOverride.HasValue
                ? new StorageObjectMetadata(HeadOverride.Value, locator, 0, null, null, Error: "Synthetic metadata failure")
                : Objects.TryGetValue(locator, out var bytes)
                ? new StorageObjectMetadata(StorageOperationStatus.Succeeded, locator, bytes.Length,
                    OmitHeadChecksum ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), DateTime.UtcNow)
                : new StorageObjectMetadata(StorageOperationStatus.NotFound, locator, 0, null, null);
            if (AfterHead is not null) await AfterHead();
            return metadata;
        }

        public async Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken)
        {
            ReadCalls++;
            if (BeforeRead is not null) await BeforeRead();
            return Objects.TryGetValue(locator, out var bytes)
                ? new StorageReadResult(StorageOperationStatus.Succeeded, locator, LastRead = new MemoryStream(bytes, writable: false), bytes.Length)
                : new StorageReadResult(StorageOperationStatus.NotFound, locator, null, 0);
        }

        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken)
            => Task.FromResult(Objects.Remove(locator)
                ? new StorageDeleteResult(StorageOperationStatus.Succeeded, locator)
                : new StorageDeleteResult(StorageOperationStatus.NotFound, locator));
    }
}
