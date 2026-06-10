using System.Net;
using Amazon.S3;
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
        Assert.Equal(string.Empty, result.RootPrefix);
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

    [Fact]
    public async Task FileOperations_ListUploadCopyMoveDeleteWithinRootPrefix()
    {
        await using var scope = await CreateDbScopeAsync();
        var factory = new FakeS3StorageClientFactory();
        var service = new S3StorageService(scope.Db, factory);
        var connection = await service.UpsertConnectionAsync(new UpsertS3StorageConnectionRequest
        {
            Name = "HOSTKEY files",
            Provider = "hostkey",
            Bucket = "payplanner-files",
            RootPrefix = "crm",
            AccessKeyId = "s3uid_1234567890",
            SecretAccessKey = "secret-1234567890",
        }, CancellationToken.None);

        await service.CreateFolderAsync(connection.Id, new CreateS3StorageFolderRequest { Path = "/docs" }, CancellationToken.None);
        await using var upload = new MemoryStream("hello"u8.ToArray());
        await service.UploadAsync(connection.Id, "/docs/readme.txt", upload, "text/plain", overwrite: false, CancellationToken.None);

        var list = await service.ListObjectsAsync(connection.Id, "/docs", 100, 0, CancellationToken.None);
        Assert.Contains(list.Items, item => item.Path == "/docs/readme.txt" && item.Type == "file" && item.Key == "crm/docs/readme.txt");

        var text = await service.GetTextFileAsync(connection.Id, "/docs/readme.txt", CancellationToken.None);
        Assert.Equal("hello", text.Content);

        await service.CopyObjectAsync(connection.Id, new S3StorageObjectOperationRequest
        {
            FromPath = "/docs/readme.txt",
            ToPath = "/docs/readme-copy.txt",
        }, CancellationToken.None);
        await service.MoveObjectAsync(connection.Id, new S3StorageObjectOperationRequest
        {
            FromPath = "/docs/readme-copy.txt",
            ToPath = "/archive/readme-copy.txt",
            Overwrite = true,
        }, CancellationToken.None);

        Assert.True(await factory.Client.ExistsAsync("payplanner-files", "crm/archive/readme-copy.txt", CancellationToken.None));
        Assert.False(await factory.Client.ExistsAsync("payplanner-files", "crm/docs/readme-copy.txt", CancellationToken.None));

        await service.DeleteObjectAsync(connection.Id, "/archive", recursive: true, CancellationToken.None);
        Assert.False(await factory.Client.ExistsAsync("payplanner-files", "crm/archive/readme-copy.txt", CancellationToken.None));
    }

    [Fact]
    public async Task FileOperations_RejectsParentTraversalInObjectPath()
    {
        await using var scope = await CreateDbScopeAsync();
        var service = new S3StorageService(scope.Db, new FakeS3StorageClientFactory());
        var connection = await service.UpsertConnectionAsync(new UpsertS3StorageConnectionRequest
        {
            Name = "HOSTKEY files",
            Provider = "hostkey",
            Bucket = "payplanner-files",
            AccessKeyId = "s3uid_1234567890",
            SecretAccessKey = "secret-1234567890",
        }, CancellationToken.None);

        await using var upload = new MemoryStream("secret"u8.ToArray());
        var error = await Assert.ThrowsAsync<S3StorageOperationException>(() =>
            service.UploadAsync(connection.Id, "../secret.txt", upload, "text/plain", overwrite: false, CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("Путь S3 не должен содержать переходы", error.Message);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.Unauthorized, "Access Key")]
    [InlineData((int)HttpStatusCode.Forbidden, "отказ доступа")]
    [InlineData((int)HttpStatusCode.NotFound, "не найден")]
    [InlineData((int)HttpStatusCode.TooManyRequests, "S3 raw error")]
    [InlineData((int)HttpStatusCode.InternalServerError, "S3 raw error")]
    [InlineData((int)HttpStatusCode.ServiceUnavailable, "временно недоступно")]
    [InlineData(0, "S3 raw error")]
    public void AwsS3StorageClient_MapsAmazonS3ErrorsToOperationException(int statusCode, string expectedMessagePart)
    {
        var method = typeof(AwsS3StorageClient).GetMethod(
            "MapException",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var source = new AmazonS3Exception("S3 raw error")
        {
            StatusCode = (HttpStatusCode)statusCode,
        };

        var mapped = Assert.IsType<S3StorageOperationException>(method.Invoke(null, [source]));

        Assert.Equal(statusCode == 0 ? 502 : statusCode, mapped.StatusCode);
        Assert.Contains(expectedMessagePart, mapped.Message);
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
        private readonly Dictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);

        public List<string> Operations { get; } = [];

        public Task ListAsync(string bucket, string prefix, CancellationToken cancellationToken)
        {
            Operations.Add($"LIST {bucket} {prefix}");
            return Task.CompletedTask;
        }

        public Task<S3StorageClientListResult> ListObjectsAsync(string bucket, string prefix, string delimiter, CancellationToken cancellationToken)
        {
            Operations.Add($"LIST_OBJECTS {bucket} {prefix}");
            var bucketPrefix = $"{bucket}/";
            var keyPrefix = $"{bucket}/{prefix}";
            var prefixes = new SortedSet<string>(StringComparer.Ordinal);
            var objects = new List<S3StorageClientObject>();
            foreach (var pair in _objects.Where(item => item.Key.StartsWith(keyPrefix, StringComparison.Ordinal)))
            {
                var key = pair.Key[bucketPrefix.Length..];
                var remainder = key[prefix.Length..];
                var separatorIndex = remainder.IndexOf('/', StringComparison.Ordinal);
                if (separatorIndex >= 0 && separatorIndex < remainder.Length - 1)
                {
                    prefixes.Add(prefix + remainder[..(separatorIndex + 1)]);
                    continue;
                }

                objects.Add(new S3StorageClientObject(key, pair.Value.Content.Length, DateTime.UtcNow, pair.Value.ContentType));
            }

            return Task.FromResult(new S3StorageClientListResult(prefixes.ToList(), objects));
        }

        public Task<List<string>> ListKeysAsync(string bucket, string prefix, CancellationToken cancellationToken)
        {
            Operations.Add($"LIST_KEYS {bucket} {prefix}");
            var bucketPrefix = $"{bucket}/";
            var keyPrefix = $"{bucket}/{prefix}";
            return Task.FromResult(_objects.Keys
                .Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal))
                .Select(key => key[bucketPrefix.Length..])
                .ToList());
        }

        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken)
            => Task.FromResult(_objects.ContainsKey($"{bucket}/{key}"));

        public async Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken)
        {
            Operations.Add($"PUT_STREAM {bucket} {key}");
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken);
            _objects[$"{bucket}/{key}"] = new StoredObject(memory.ToArray(), contentType);
        }

        public Task PutTextAsync(string bucket, string key, string content, CancellationToken cancellationToken)
        {
            Operations.Add($"PUT {bucket} {key}");
            _objects[$"{bucket}/{key}"] = new StoredObject(System.Text.Encoding.UTF8.GetBytes(content), "text/plain; charset=utf-8");
            return Task.CompletedTask;
        }

        public Task<S3StorageDownloadedObject> GetAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            Operations.Add($"GET {bucket} {key}");
            var stored = _objects[$"{bucket}/{key}"];
            return Task.FromResult(new S3StorageDownloadedObject(
                new MemoryStream(stored.Content.ToArray()),
                stored.ContentType,
                Path.GetFileName(key)));
        }

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            Operations.Add($"DELETE {bucket} {key}");
            _objects.Remove($"{bucket}/{key}");
            return Task.CompletedTask;
        }

        public Task DeleteManyAsync(string bucket, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        {
            Operations.Add($"DELETE_MANY {bucket} {keys.Count}");
            foreach (var key in keys)
            {
                _objects.Remove($"{bucket}/{key}");
            }

            return Task.CompletedTask;
        }

        public Task CopyAsync(string bucket, string fromKey, string toKey, CancellationToken cancellationToken)
        {
            Operations.Add($"COPY {bucket} {fromKey} {toKey}");
            _objects[$"{bucket}/{toKey}"] = _objects[$"{bucket}/{fromKey}"];
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        private sealed record StoredObject(byte[] Content, string ContentType);
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
