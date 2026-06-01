using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class S3StorageServiceTests
{
    [Fact]
    public async Task UpsertConnectionAsync_AppliesHostkeyDefaultsAndMasksSecrets()
    {
        await using var scope = await CreateDbScopeAsync();
        var service = new S3StorageService(scope.Db, new FakeS3StorageClientFactory());

        var result = await service.UpsertConnectionAsync(new UpsertS3StorageConnectionRequest
        {
            Name = "HOSTKEY backups",
            Provider = "hostkey",
            Bucket = "payplanner-backups",
            AccessKeyId = "s3uid_1234567890",
            SecretAccessKey = "secret-1234567890",
        }, CancellationToken.None);

        Assert.Equal("HOSTKEY backups", result.Name);
        Assert.Equal("hostkey", result.Provider);
        Assert.Equal(S3StorageService.HostkeyEndpoint, result.Endpoint);
        Assert.Equal(S3StorageService.HostkeyRegion, result.Region);
        Assert.Equal("payplanner-backups", result.Bucket);
        Assert.Equal("payplanner", result.RootPrefix);
        Assert.True(result.IsConfigured);
        Assert.Equal("s3ui...7890", result.AccessKeyPreview);
        Assert.Equal("secr...7890", result.SecretKeyPreview);

        var overview = await service.GetOverviewAsync(CancellationToken.None);
        Assert.Single(overview.Connections);
        Assert.Contains(overview.ProviderPresets, item => item.Key == "hostkey"
            && item.Endpoint == S3StorageService.HostkeyEndpoint
            && item.Region == S3StorageService.HostkeyRegion);
    }

    [Fact]
    public async Task UpsertConnectionAsync_PreservesSecretWhenRequestOmitsSecret()
    {
        await using var scope = await CreateDbScopeAsync();
        var service = new S3StorageService(scope.Db, new FakeS3StorageClientFactory());
        var created = await service.UpsertConnectionAsync(new UpsertS3StorageConnectionRequest
        {
            Name = "Primary",
            Provider = "hostkey",
            Bucket = "payplanner-backups",
            AccessKeyId = "s3uid_1234567890",
            SecretAccessKey = "secret-1234567890",
        }, CancellationToken.None);

        var updated = await service.UpsertConnectionAsync(new UpsertS3StorageConnectionRequest
        {
            Id = created.Id,
            Name = "Primary renamed",
            Provider = "hostkey",
            Bucket = "payplanner-backups-2",
            AccessKeyId = "s3uid_1234567890",
        }, CancellationToken.None);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Primary renamed", updated.Name);
        Assert.Equal("payplanner-backups-2", updated.Bucket);
        Assert.Equal("secr...7890", updated.SecretKeyPreview);
    }

    [Fact]
    public async Task TestConnectionAsync_ListsUploadsReadsAndDeletesMarker()
    {
        await using var scope = await CreateDbScopeAsync();
        var factory = new FakeS3StorageClientFactory();
        var service = new S3StorageService(scope.Db, factory);
        var connection = await service.UpsertConnectionAsync(new UpsertS3StorageConnectionRequest
        {
            Name = "HOSTKEY backups",
            Provider = "hostkey",
            Bucket = "payplanner-backups",
            RootPrefix = "backups/database",
            AccessKeyId = "s3uid_1234567890",
            SecretAccessKey = "secret-1234567890",
        }, CancellationToken.None);

        var result = await service.TestConnectionAsync(connection.Id, new TestS3StorageConnectionRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("payplanner-backups", result.Bucket);
        Assert.Equal(4, result.Steps.Count);
        Assert.All(result.Steps, step => Assert.True(step.Success));
        Assert.StartsWith("backups/database/_payplanner-test/payplanner-s3-test-", result.TestKey, StringComparison.Ordinal);
        Assert.Equal(S3StorageService.HostkeyEndpoint, factory.LastSettings?.Endpoint);
        Assert.Equal(S3StorageService.HostkeyRegion, factory.LastSettings?.Region);
        Assert.Contains(factory.Client.Operations, item => item == "LIST payplanner-backups backups/database/_payplanner-test");
        Assert.Contains(factory.Client.Operations, item => item.StartsWith("PUT payplanner-backups backups/database/_payplanner-test/", StringComparison.Ordinal));
        Assert.Contains(factory.Client.Operations, item => item.StartsWith("GET payplanner-backups backups/database/_payplanner-test/", StringComparison.Ordinal));
        Assert.Contains(factory.Client.Operations, item => item.StartsWith("DELETE payplanner-backups backups/database/_payplanner-test/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertConnectionAsync_RejectsParentTraversalInRootPrefix()
    {
        await using var scope = await CreateDbScopeAsync();
        var service = new S3StorageService(scope.Db, new FakeS3StorageClientFactory());

        var error = await Assert.ThrowsAsync<S3StorageOperationException>(() =>
            service.UpsertConnectionAsync(new UpsertS3StorageConnectionRequest
            {
                Name = "Invalid",
                Provider = "hostkey",
                Bucket = "payplanner-backups",
                RootPrefix = "../private",
                AccessKeyId = "s3uid_1234567890",
                SecretAccessKey = "secret-1234567890",
            }, CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("Путь S3 не должен содержать переходы", error.Message);
    }

    private static async Task<TestDbScope> CreateDbScopeAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options, useObjectAuditModel: false);
        await db.Database.EnsureCreatedAsync();

        return new TestDbScope(connection, db);
    }

    private sealed class FakeS3StorageClientFactory : IS3StorageClientFactory
    {
        public FakeS3StorageClient Client { get; } = new();
        public S3StorageConnectionSettings? LastSettings { get; private set; }

        public IS3StorageClient Create(S3StorageConnectionSettings settings)
        {
            LastSettings = settings;
            return Client;
        }
    }

    private sealed class FakeS3StorageClient : IS3StorageClient
    {
        private readonly Dictionary<string, string> _objects = new(StringComparer.Ordinal);

        public List<string> Operations { get; } = [];

        public Task ListAsync(string bucket, string prefix, CancellationToken cancellationToken)
        {
            Operations.Add($"LIST {bucket} {prefix}");
            return Task.CompletedTask;
        }

        public Task PutTextAsync(string bucket, string key, string content, CancellationToken cancellationToken)
        {
            Operations.Add($"PUT {bucket} {key}");
            _objects[$"{bucket}/{key}"] = content;
            return Task.CompletedTask;
        }

        public Task<string> GetTextAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            Operations.Add($"GET {bucket} {key}");
            return Task.FromResult(_objects[$"{bucket}/{key}"]);
        }

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            Operations.Add($"DELETE {bucket} {key}");
            _objects.Remove($"{bucket}/{key}");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class TestDbScope(SqliteConnection connection, PaymentContext db) : IAsyncDisposable
    {
        public PaymentContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
