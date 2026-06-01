using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupServiceS3Tests
{
    [Fact]
    public async Task CreateManualBackupAsync_UploadsBackupToConfiguredS3Connection()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(
            automaticEnabled: true,
            scheduleLocal: "03:00",
            retentionDays: 14,
            backupFormat: "sqlite",
            s3UploadEnabled: true,
            s3ConnectionIds: ["s3-main"],
            s3PrefixTemplate: "backups/database/{yyyy}/{MM}",
            CancellationToken.None);

        var result = await service.CreateManualBackupAsync(CancellationToken.None);

        var upload = Assert.Single(result.S3UploadResults);
        Assert.Equal("success", upload.Status);
        Assert.Equal("s3-main", upload.ConnectionId);
        Assert.EndsWith(result.Backup.FileName, upload.TargetPath, StringComparison.Ordinal);
        Assert.Contains($"/{DateTimeOffset.Now:yyyy}/{DateTimeOffset.Now:MM}/", $"/{upload.TargetPath}");
        Assert.Equal(result.Backup.RelativePath, upload.BackupRelativePath);
        Assert.Equal(result.Backup.SizeBytes, upload.SizeBytes);
        Assert.Equal(upload.TargetPath, scope.S3.UploadedPath);
        Assert.True(scope.S3.UploadedBytes > 0);

        var overview = await service.GetOverviewAsync(CancellationToken.None);
        var stored = Assert.Single(overview.S3UploadResults);
        Assert.Equal(upload.Id, stored.Id);
        Assert.Equal("success", stored.Status);
        Assert.True(overview.S3Upload.Enabled);
        Assert.Contains("s3-main", overview.S3Upload.ConnectionIds);
    }

    [Fact]
    public async Task CreateManualBackupAsync_RecordsFailedS3UploadWithoutBreakingLocalBackup()
    {
        await using var scope = await CreateScopeAsync();
        scope.S3.ThrowOnUpload = true;
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(
            automaticEnabled: true,
            scheduleLocal: "03:00",
            retentionDays: 14,
            backupFormat: "sqlite",
            s3UploadEnabled: true,
            s3ConnectionIds: ["s3-main"],
            s3PrefixTemplate: "backups/database/{yyyy}/{MM}",
            CancellationToken.None);

        var result = await service.CreateManualBackupAsync(CancellationToken.None);

        Assert.True(File.Exists(service.ResolveBackupPath(result.Backup.RelativePath)));
        var upload = Assert.Single(result.S3UploadResults);
        Assert.Equal("failed", upload.Status);
        Assert.Contains("S3 unavailable", upload.Error);
    }

    [Fact]
    public async Task RetryS3UploadAsync_ReuploadsFailedBackupAndUpdatesStoredResult()
    {
        await using var scope = await CreateScopeAsync();
        scope.S3.ThrowOnUpload = true;
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(
            automaticEnabled: true,
            scheduleLocal: "03:00",
            retentionDays: 14,
            backupFormat: "sqlite",
            s3UploadEnabled: true,
            s3ConnectionIds: ["s3-main"],
            s3PrefixTemplate: "backups/database/{yyyy}/{MM}",
            CancellationToken.None);

        var failedResult = await service.CreateManualBackupAsync(CancellationToken.None);
        var failedUpload = Assert.Single(failedResult.S3UploadResults);
        Assert.Equal("failed", failedUpload.Status);

        scope.S3.ThrowOnUpload = false;

        var retry = await service.RetryS3UploadAsync(failedUpload.Id, CancellationToken.None);

        Assert.Equal(failedUpload.Id, retry.Id);
        Assert.Equal("success", retry.Status);
        Assert.Null(retry.Error);
        Assert.Equal(failedUpload.TargetPath, retry.TargetPath);
        Assert.Equal(failedResult.Backup.SizeBytes, retry.SizeBytes);
        Assert.True(scope.S3.UploadedBytes > 0);

        var overview = await service.GetOverviewAsync(CancellationToken.None);
        var stored = Assert.Single(overview.S3UploadResults);
        Assert.Equal(failedUpload.Id, stored.Id);
        Assert.Equal("success", stored.Status);
    }

    private static async Task<TestScope> CreateScopeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"payplanner-backup-s3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "payplanner.db");
        var backupDirectory = Path.Combine(root, "backups");
        var connectionString = $"Data Source={dbPath}";
        var providerInfo = new DatabaseProviderInfo(DatabaseProviderKind.Sqlite, DatabaseProviderResolver.NormalizeSqliteConnection(connectionString));
        var s3 = new FakeS3StorageService();
        var services = new ServiceCollection();
        services.AddSingleton(providerInfo);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseBackup:Directory"] = backupDirectory,
            })
            .Build());
        services.AddDbContextFactory<PaymentContext>(options => options.UseSqlite(providerInfo.ConnectionString));
        services.AddScoped<IS3StorageService>(_ => s3);
        services.AddSingleton(s3);
        services.AddSingleton<ILogger<DatabaseBackupService>>(NullLogger<DatabaseBackupService>.Instance);
        services.AddSingleton<DatabaseBackupService>();
        var serviceProvider = services.BuildServiceProvider();

        await using (var db = await serviceProvider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestScope(root, serviceProvider, s3);
    }

    private sealed class FakeS3StorageService : IS3StorageService
    {
        public bool ThrowOnUpload { get; set; }
        public string? UploadedPath { get; private set; }
        public long UploadedBytes { get; private set; }

        public Task<S3StorageConnectionsOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new S3StorageConnectionsOverviewDto());

        public Task<S3StorageConnectionDto> UpsertConnectionAsync(UpsertS3StorageConnectionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteConnectionAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<S3StorageConnectionTestDto> TestConnectionAsync(string id, TestS3StorageConnectionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<S3StorageListResponseDto> ListObjectsAsync(string id, string? path, int limit, int offset, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<S3StorageOperationResultDto> CreateFolderAsync(string id, CreateS3StorageFolderRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<S3StorageOperationResultDto> UploadAsync(string id, string? path, Stream content, string? contentType, bool overwrite, CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpload)
            {
                throw new S3StorageOperationException(503, "S3 unavailable");
            }

            UploadedPath = path;
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken);
            UploadedBytes = memory.Length;
            return new S3StorageOperationResultDto
            {
                Success = true,
                Path = path ?? string.Empty,
                Key = path?.TrimStart('/') ?? string.Empty,
            };
        }

        public Task<S3StorageDownloadResult> DownloadAsync(string id, string? path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<S3StorageOperationResultDto> DeleteObjectAsync(string id, string? path, bool recursive, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<S3StorageOperationResultDto> CopyObjectAsync(string id, S3StorageObjectOperationRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<S3StorageOperationResultDto> MoveObjectAsync(string id, S3StorageObjectOperationRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<S3StorageTextFileDto> GetTextFileAsync(string id, string? path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<S3StorageOperationResultDto> SaveTextFileAsync(string id, UpdateS3StorageTextFileRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestScope(string root, ServiceProvider provider, FakeS3StorageService s3) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public FakeS3StorageService S3 { get; } = s3;

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
