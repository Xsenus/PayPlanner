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
            cancellationToken: CancellationToken.None);

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
            cancellationToken: CancellationToken.None);

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
            cancellationToken: CancellationToken.None);

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

    [Fact]
    public async Task CreateManualBackupAsync_UploadsBackupToYandexDiskWhenEnabled()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(
            automaticEnabled: true,
            scheduleLocal: "03:00",
            retentionDays: 14,
            backupFormat: "sqlite",
            yandexDiskUploadEnabled: true,
            yandexDiskPrefixTemplate: "backups/database/{yyyy}/{MM}",
            cancellationToken: CancellationToken.None);

        var result = await service.CreateManualBackupAsync(CancellationToken.None);

        var upload = Assert.Single(result.YandexDiskUploadResults);
        Assert.Equal("success", upload.Status);
        Assert.EndsWith(result.Backup.FileName, upload.TargetPath, StringComparison.Ordinal);
        Assert.Contains($"/{DateTimeOffset.Now:yyyy}/{DateTimeOffset.Now:MM}/", $"/{upload.TargetPath}");
        Assert.Equal(result.Backup.RelativePath, upload.BackupRelativePath);
        Assert.Equal(result.Backup.SizeBytes, upload.SizeBytes);
        Assert.Equal(upload.TargetPath, scope.YandexDisk.UploadedPath);
        Assert.True(scope.YandexDisk.UploadedBytes > 0);

        var overview = await service.GetOverviewAsync(CancellationToken.None);
        var stored = Assert.Single(overview.YandexDiskUploadResults);
        Assert.Equal(upload.Id, stored.Id);
        Assert.Equal("success", stored.Status);
        Assert.True(overview.YandexDiskUpload.Enabled);
    }

    [Fact]
    public async Task RetryYandexDiskUploadAsync_ReuploadsFailedBackupAndUpdatesStoredResult()
    {
        await using var scope = await CreateScopeAsync();
        scope.YandexDisk.ThrowOnUpload = true;
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(
            automaticEnabled: true,
            scheduleLocal: "03:00",
            retentionDays: 14,
            backupFormat: "sqlite",
            yandexDiskUploadEnabled: true,
            yandexDiskPrefixTemplate: "backups/database/{yyyy}/{MM}",
            cancellationToken: CancellationToken.None);

        var failedResult = await service.CreateManualBackupAsync(CancellationToken.None);
        var failedUpload = Assert.Single(failedResult.YandexDiskUploadResults);
        Assert.Equal("failed", failedUpload.Status);

        scope.YandexDisk.ThrowOnUpload = false;

        var retry = await service.RetryYandexDiskUploadAsync(failedUpload.Id, CancellationToken.None);

        Assert.Equal(failedUpload.Id, retry.Id);
        Assert.Equal("success", retry.Status);
        Assert.Null(retry.Error);
        Assert.Equal(failedUpload.TargetPath, retry.TargetPath);
        Assert.Equal(failedResult.Backup.SizeBytes, retry.SizeBytes);
        Assert.True(scope.YandexDisk.UploadedBytes > 0);

        var overview = await service.GetOverviewAsync(CancellationToken.None);
        var stored = Assert.Single(overview.YandexDiskUploadResults);
        Assert.Equal(failedUpload.Id, stored.Id);
        Assert.Equal("success", stored.Status);
    }

    [Fact]
    public async Task DeleteBackupAsync_DeletesOnlyResolvedBackupAndReportsActualCount()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await service.CreateManualBackupAsync(CancellationToken.None);
        var fullPath = service.ResolveBackupPath(created.Backup.RelativePath);
        Assert.NotNull(fullPath);

        var result = await service.DeleteBackupAsync(created.Backup.RelativePath, CancellationToken.None);

        Assert.Equal(1, result.DeletedCount);
        Assert.False(result.DeletedAll);
        Assert.False(File.Exists(fullPath));
        Assert.Null(service.ResolveBackupPath(created.Backup.RelativePath));
    }

    [Fact]
    public async Task DeleteBackupAsync_RejectsTraversalAndMissingFiles()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();

        Assert.Null(service.ResolveBackupPath("../outside.dump"));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.DeleteBackupAsync("../outside.dump", CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.DeleteBackupAsync("missing.dump", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAllBackupsAsync_CountsOnlyFilesThatWereActuallyDeleted()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var first = await service.CreateManualBackupAsync(CancellationToken.None);
        var second = await service.CreateManualBackupAsync(CancellationToken.None);
        Assert.NotEqual(first.Backup.RelativePath, second.Backup.RelativePath);

        var result = await service.DeleteAllBackupsAsync(CancellationToken.None);

        Assert.True(result.DeletedAll);
        Assert.Equal(2, result.DeletedCount);
        Assert.Null(service.ResolveBackupPath(first.Backup.RelativePath));
        Assert.Null(service.ResolveBackupPath(second.Backup.RelativePath));
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
        var yandexDisk = new FakeYandexDiskService();
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
        services.AddScoped<IYandexDiskService>(_ => yandexDisk);
        services.AddSingleton(s3);
        services.AddSingleton(yandexDisk);
        services.AddSingleton<ILogger<DatabaseBackupService>>(NullLogger<DatabaseBackupService>.Instance);
        services.AddSingleton<DatabaseBackupService>();
        var serviceProvider = services.BuildServiceProvider();

        await using (var db = await serviceProvider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestScope(root, serviceProvider, s3, yandexDisk);
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

    private sealed class FakeYandexDiskService : IYandexDiskService
    {
        public bool ThrowOnUpload { get; set; }
        public string? UploadedPath { get; private set; }
        public long UploadedBytes { get; private set; }

        public Task<YandexDiskSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new YandexDiskSettingsDto { IsConfigured = true, RootPath = "disk:/PayPlanner" });

        public Task<YandexDiskSettingsDto> UpdateSettingsAsync(UpdateYandexDiskSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskConnectionTestDto> TestConnectionAsync(TestYandexDiskConnectionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskListResponseDto> ListAsync(string? path, int limit, int offset, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskOperationResultDto> CreateFolderAsync(string? path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskOperationResultDto> CopyAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskOperationResultDto> MoveAsync(string? fromPath, string? toPath, bool overwrite, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskOperationResultDto> DeleteAsync(string? path, bool permanently, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<YandexDiskOperationResultDto> UploadAsync(string? path, Stream content, string? contentType, bool overwrite, CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpload)
            {
                throw new YandexDiskOperationException(503, "Yandex Disk unavailable");
            }

            UploadedPath = path;
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken);
            UploadedBytes = memory.Length;
            return new YandexDiskOperationResultDto
            {
                Success = true,
                Path = path ?? string.Empty,
            };
        }

        public Task<YandexDiskDownload> DownloadAsync(string? path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskTextFileDto> GetTextFileAsync(string? path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskOperationResultDto> SaveTextFileAsync(UpdateYandexDiskTextFileRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskCrmSyncSettingsDto> GetCrmSyncSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskCrmSyncSettingsDto> UpdateCrmSyncSettingsAsync(UpdateYandexDiskCrmSyncSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskCrmSyncPreviewDto> PreviewCrmSyncAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskOperationResultDto> UploadCrmDocumentAsync(YandexDiskCrmDocumentUpload upload, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestScope(string root, ServiceProvider provider, FakeS3StorageService s3, FakeYandexDiskService yandexDisk) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public FakeS3StorageService S3 { get; } = s3;
        public FakeYandexDiskService YandexDisk { get; } = yandexDisk;

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
