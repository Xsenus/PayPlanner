using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ProductStorageBindingServiceTests
{
    [Fact]
    public async Task PublicationRejectsTombstoneRecordWithUnchangedObjectFlag()
    {
        await using var fixture = await Fixture.CreateAsync();
        var target = await fixture.AddTargetAsync();
        Assert.False(target.IsDeleted);
        fixture.Db.StorageTombstones.Add(new StorageTombstone { StorageObjectId = target.Id, Generation = target.CurrentGeneration });
        await fixture.Db.SaveChangesAsync();
        await using var transaction = await fixture.Db.Database.BeginTransactionAsync();
        var result = await fixture.Service.PublishAsync(fixture.Request(target), CancellationToken.None);
        Assert.False(result.Published);
        Assert.Equal("invalidObject", result.Status);
        Assert.False(await fixture.Db.StorageProductBindings.AnyAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task PublicationRequiresTransactionAndRollsBackWithDomainChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var target = await fixture.AddTargetAsync();
        var request = fixture.Request(target);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PublishAsync(request, CancellationToken.None));
        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync())
        {
            Assert.True((await fixture.Service.PublishAsync(request, CancellationToken.None)).Published);
            fixture.User.PhotoUrl = "new.png";
            await fixture.Db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.StorageProductBindings.AnyAsync());
        Assert.Equal("old.png", (await fixture.Db.Users.SingleAsync()).PhotoUrl);
        Assert.True(await fixture.Db.StorageObjects.AnyAsync(o => o.Id == target.Id));
    }

    [Fact]
    public async Task RetryIsIdempotentAndStaleReplacementCannotOverwriteNewerFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddTargetAsync();
        var second = await fixture.AddTargetAsync(name: "second.png");
        await using var transaction = await fixture.Db.Database.BeginTransactionAsync();
        var published = await fixture.Service.PublishAsync(fixture.Request(first), CancellationToken.None);
        Assert.Equal("published", published.Status);
        Assert.Equal("alreadyPublished", (await fixture.Service.PublishAsync(fixture.Request(first), CancellationToken.None)).Status);
        var replacement = await fixture.Service.PublishAsync(fixture.Request(second, 1), CancellationToken.None);
        Assert.True(replacement.Published);
        Assert.Equal(2, replacement.Version);
        Assert.Equal("conflict", (await fixture.Service.PublishAsync(fixture.Request(first), CancellationToken.None)).Status);
        Assert.Equal("alreadyPublished", (await fixture.Service.PublishAsync(fixture.Request(second, 1), CancellationToken.None)).Status);
        await transaction.CommitAsync();
        Assert.Equal(second.Id, (await fixture.Db.StorageProductBindings.AsNoTracking().SingleAsync()).StorageObjectId);
        Assert.Equal(2, await fixture.Db.StorageObjects.CountAsync());
        Assert.Equal("photo.png", (await fixture.Db.StorageObjects.AsNoTracking().SingleAsync(o => o.Id == first.Id)).LogicalName);
    }

    [Theory]
    [InlineData("missingCopy", "notReady")]
    [InlineData("duplicateProvider", "notReady")]
    [InlineData("outsidePolicy", "notReady")]
    [InlineData("wrongReplicaHash", "notReady")]
    [InlineData("unverifiedCopy", "notReady")]
    [InlineData("unknownCommit", "notReady")]
    [InlineData("invalidHash", "notReady")]
    [InlineData("deleted", "invalidObject")]
    [InlineData("wrongOwner", "invalidObject")]
    [InlineData("wrongClass", "invalidObject")]
    [InlineData("wrongName", "invalidObject")]
    [InlineData("wrongType", "invalidObject")]
    public async Task UnreadyOrMismatchedObjectCannotBePublished(string scenario, string expectedStatus)
    {
        await using var fixture = await Fixture.CreateAsync();
        var target = await fixture.AddTargetAsync(scenario);
        await using var transaction = await fixture.Db.Database.BeginTransactionAsync();
        var result = await fixture.Service.PublishAsync(fixture.Request(target) with
        {
            LogicalName = "photo.png",
            ContentType = "image/*"
        }, CancellationToken.None);
        Assert.False(result.Published);
        Assert.Equal(expectedStatus, result.Status);
        Assert.False(await fixture.Db.StorageProductBindings.AnyAsync());
        await transaction.RollbackAsync();
    }

    private sealed class Fixture(SqliteConnection connection, PaymentContext db, User user) : IAsyncDisposable
    {
        public PaymentContext Db => db;
        public User User => user;
        public ProductStorageBindingService Service { get; } = new(db, new ReadyPolicy());
        public ProductStorageBindingRequest Request(StorageObject target, long version = 0)
            => new(LegacyStorageSources.ProfilePhotos, user.Id, target.Id, target.LogicalName, "image/*", version);

        public async Task<StorageObject> AddTargetAsync(string scenario = "ready", string name = "photo.png")
        {
            var entry = new StorageObject
            {
                DataClass = scenario == "wrongClass" ? "task-file" : "profile-photo",
                OwnerType = "profile-photo",
                OwnerId = scenario == "wrongOwner" ? "999" : user.Id.ToString(),
                LogicalName = scenario == "wrongName" ? "other.png" : name,
                ContentType = scenario == "wrongType" ? "text/plain" : "image/*",
                IsDeleted = scenario == "deleted",
                CurrentGeneration = 1,
                Version = 1
            };
            var hash = scenario == "invalidHash" ? new string('z', 64) : new string('a', 64);
            var generation = new StorageObjectGeneration { StorageObject = entry, Generation = 1, SizeBytes = 4, Sha256 = hash };
            generation.Replicas.Add(new() { ProviderKey = "local", Locator = "primary", Status = StorageReplicaStatuses.Available, SizeBytes = 4, Sha256 = hash, LastVerifiedAt = DateTime.UtcNow });
            generation.Replicas.Add(new()
            {
                ProviderKey = scenario == "duplicateProvider" ? "local" : scenario == "outsidePolicy" ? "outside" : "mirror",
                Locator = "mirror",
                Status = scenario == "missingCopy" ? StorageReplicaStatuses.Pending : StorageReplicaStatuses.Available,
                SizeBytes = 4,
                LastVerifiedAt = scenario == "unverifiedCopy" ? null : DateTime.UtcNow,
                Sha256 = scenario == "wrongReplicaHash" ? new string('b', 64) : hash
            });
            entry.Generations.Add(generation);
            if (scenario == "unknownCommit")
                generation.Operations.Add(new()
                {
                    StorageObject = entry,
                    OperationType = "commit",
                    Status = StorageOperationStatuses.Unknown,
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                    ProviderKey = "local"
                });
            db.StorageObjects.Add(entry);
            await db.SaveChangesAsync();
            return entry;
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var user = new User
            {
                Email = "binding@example.test",
                FullName = "Тест",
                PasswordHash = "hash",
                PhotoUrl = "old.png",
                Role = new Role { Name = "admin", Description = "Администратор" }
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return new(connection, db, user);
        }
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    }
    private sealed class ReadyPolicy : IStoragePolicyService
    {
        public StoragePolicyEvaluation Evaluate(string dataClass)
            => new(dataClass, "test", true, true, true, 2, ["local", "mirror"], ["local", "mirror"], null, false, [], []);
    }
}
