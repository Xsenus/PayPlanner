using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageOperationProcessorTests
{
    [Fact]
    public async Task ProcessBatch_ReplicatesAndRecordsVerifiedReplica()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operationId = await fixture.SeedReplicateAsync();

        var result = await fixture.Processor.ProcessBatchAsync("worker-a", CancellationToken.None);

        Assert.Equal(1, result.Succeeded);
        var operation = await fixture.Db.StorageOperations.SingleAsync(item => item.Id == operationId);
        Assert.Equal(StorageOperationStatuses.Succeeded, operation.Status);
        var replica = await fixture.Db.StorageReplicas.SingleAsync();
        Assert.Equal(StorageReplicaStatuses.Available, replica.Status);
        Assert.Equal(fixture.Sha256, replica.Sha256);
        Assert.Equal(1, fixture.Target.PutCalls);
    }

    [Fact]
    public async Task ProcessBatch_ReconcilesUnknownWriteWithoutWritingTwice()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Target.ReturnUnknownAfterPut = true;
        var operationId = await fixture.SeedReplicateAsync();

        var first = await fixture.Processor.ProcessBatchAsync("worker-a", CancellationToken.None);
        Assert.Equal(1, first.Unknown);

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Target.ReturnUnknownAfterPut = false;
        var second = await fixture.Processor.ProcessBatchAsync("worker-b", CancellationToken.None);

        Assert.Equal(1, second.Succeeded);
        Assert.Equal(1, fixture.Target.PutCalls);
        Assert.Equal(StorageOperationStatuses.Succeeded,
            (await fixture.Db.StorageOperations.SingleAsync(item => item.Id == operationId)).Status);
    }

    [Fact]
    public async Task ProcessBatch_ClaimsExpiredLeaseButNotLiveLease()
    {
        await using var fixture = await Fixture.CreateAsync();
        var expiredId = await fixture.SeedReplicateAsync(StorageOperationStatuses.Running, fixture.Clock.UtcNow.AddMinutes(-1));
        var liveId = await fixture.SeedReplicateAsync(StorageOperationStatuses.Running, fixture.Clock.UtcNow.AddMinutes(5), "live-lease");

        var result = await fixture.Processor.ProcessBatchAsync("worker-new", CancellationToken.None);

        Assert.Equal(1, result.Claimed);
        Assert.Equal(StorageOperationStatuses.Succeeded,
            (await fixture.Db.StorageOperations.SingleAsync(item => item.Id == expiredId)).Status);
        Assert.Equal(StorageOperationStatuses.Running,
            (await fixture.Db.StorageOperations.SingleAsync(item => item.Id == liveId)).Status);
    }

    [Fact]
    public async Task ProcessBatch_DoesNotResurrectDeletedObject()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operationId = await fixture.SeedReplicateAsync(isDeleted: true);

        var result = await fixture.Processor.ProcessBatchAsync("worker-a", CancellationToken.None);

        Assert.Equal(1, result.DeadLettered);
        Assert.Equal(0, fixture.Target.PutCalls);
        var operation = await fixture.Db.StorageOperations.SingleAsync(item => item.Id == operationId);
        Assert.Equal(StorageOperationStatuses.DeadLetter, operation.Status);
        Assert.Contains("tombstone", operation.LastError, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private int _sequence;

        private Fixture(SqliteConnection connection, PaymentContext db, FakeTimeProvider clock, FakeProvider source, FakeProvider target)
        {
            _connection = connection;
            Db = db;
            Clock = clock;
            Source = source;
            Target = target;
            var options = new StaticOptionsMonitor<StorageOrchestrationOptions>(new StorageOrchestrationOptions
            {
                WorkerBatchSize = 20,
                WorkerLeaseSeconds = 60,
                WorkerMaxAttempts = 3,
            });
            Processor = new StorageOperationProcessor(
                db,
                new StorageProviderRegistry([source, target]),
                options,
                clock,
                NullLogger<StorageOperationProcessor>.Instance);
        }

        public PaymentContext Db { get; }
        public FakeTimeProvider Clock { get; }
        public FakeProvider Source { get; }
        public FakeProvider Target { get; }
        public StorageOperationProcessor Processor { get; }
        public byte[] Content { get; } = Encoding.UTF8.GetBytes("durable storage payload");
        public string Sha256 => Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
            var db = new PaymentContext(options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db, new FakeTimeProvider(), new FakeProvider("source"), new FakeProvider("target"));
        }

        public async Task<Guid> SeedReplicateAsync(
            string status = StorageOperationStatuses.Pending,
            DateTime? leaseExpiresAt = null,
            string? leaseOwner = null,
            bool isDeleted = false)
        {
            var index = Interlocked.Increment(ref _sequence);
            var sourceLocator = $"source/object-{index}";
            var targetLocator = $"target/object-{index}";
            Source.Objects[sourceLocator] = Content;
            var storageObject = new StorageObject
            {
                DataClass = "test",
                OwnerType = "test",
                OwnerId = index.ToString(),
                LogicalName = $"object-{index}.txt",
                ContentType = "text/plain",
                CurrentGeneration = 1,
                IsDeleted = isDeleted,
            };
            var generation = new StorageObjectGeneration
            {
                StorageObject = storageObject,
                Generation = 1,
                SizeBytes = Content.Length,
                Sha256 = Sha256,
                PolicyVersion = "test-v1",
            };
            var operation = new StorageOperation
            {
                StorageObject = storageObject,
                StorageObjectGeneration = generation,
                OperationType = StorageOperationTypes.Replicate,
                Status = status,
                IdempotencyKey = $"replicate-{index}",
                ProviderKey = Target.Key,
                PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(targetLocator, Source.Key, sourceLocator)),
                LeaseOwner = leaseOwner,
                LeaseExpiresAt = leaseExpiresAt,
                CreatedAt = Clock.UtcNow,
                UpdatedAt = Clock.UtcNow,
            };
            Db.StorageOperations.Add(operation);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return operation.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        public DateTime UtcNow => _now.UtcDateTime;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeProvider(string key) : IStorageProvider
    {
        public string Key => key;
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public bool ReturnUnknownAfterPut { get; set; }
        public int PutCalls { get; private set; }
        public StorageProviderCapabilities Capabilities { get; } = new(true, true, true, true, false, false, false, false, false);

        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken)
        {
            PutCalls++;
            using var memory = new MemoryStream();
            await request.Content.CopyToAsync(memory, cancellationToken);
            var bytes = memory.ToArray();
            if (!request.Overwrite && Objects.ContainsKey(request.Locator))
                return new(StorageOperationStatus.Conflict, request.Locator, 0, null);
            Objects[request.Locator] = bytes;
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new(ReturnUnknownAfterPut ? StorageOperationStatus.Unknown : StorageOperationStatus.Succeeded,
                request.Locator, bytes.Length, hash, Error: ReturnUnknownAfterPut ? "response lost" : null);
        }

        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken)
        {
            if (!Objects.TryGetValue(locator, out var bytes))
                return Task.FromResult(new StorageObjectMetadata(StorageOperationStatus.NotFound, locator, 0, null, null));
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return Task.FromResult(new StorageObjectMetadata(StorageOperationStatus.Succeeded, locator, bytes.Length, hash, DateTime.UtcNow));
        }

        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken)
            => Objects.TryGetValue(locator, out var bytes)
                ? Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, locator, new MemoryStream(bytes, writable: false), bytes.Length))
                : Task.FromResult(new StorageReadResult(StorageOperationStatus.NotFound, locator, null, 0));

        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken)
            => Task.FromResult(Objects.Remove(locator)
                ? new StorageDeleteResult(StorageOperationStatus.Succeeded, locator)
                : new StorageDeleteResult(StorageOperationStatus.NotFound, locator));
    }
}
