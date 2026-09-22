using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageObjectServiceTests
{
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
            "document", "task", "42", "Доказательство.txt", "text/plain",
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

        Assert.Equal(1, processed.Succeeded);
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
                Version = "hold-v1", RequiredCopies = 1, WriteProviders = ["local"], ReadProviders = ["local"], LegalHold = true,
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

    private sealed class MemoryProvider(string key) : IStorageProvider
    {
        public string Key => key;
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public StorageProviderCapabilities Capabilities { get; } = new(true, true, true, true, false, false, false, false, false);

        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken)
        {
            if (!request.Overwrite && Objects.ContainsKey(request.Locator))
                return new(StorageOperationStatus.Conflict, request.Locator, 0, null);
            using var memory = new MemoryStream();
            await request.Content.CopyToAsync(memory, cancellationToken);
            var bytes = memory.ToArray();
            Objects[request.Locator] = bytes;
            return new(StorageOperationStatus.Succeeded, request.Locator, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken)
            => Task.FromResult(Objects.TryGetValue(locator, out var bytes)
                ? new StorageObjectMetadata(StorageOperationStatus.Succeeded, locator, bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), DateTime.UtcNow)
                : new StorageObjectMetadata(StorageOperationStatus.NotFound, locator, 0, null, null));

        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken)
            => Task.FromResult(Objects.TryGetValue(locator, out var bytes)
                ? new StorageReadResult(StorageOperationStatus.Succeeded, locator, new MemoryStream(bytes, writable: false), bytes.Length)
                : new StorageReadResult(StorageOperationStatus.NotFound, locator, null, 0));

        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken)
            => Task.FromResult(Objects.Remove(locator)
                ? new StorageDeleteResult(StorageOperationStatus.Succeeded, locator)
                : new StorageDeleteResult(StorageOperationStatus.NotFound, locator));
    }
}
