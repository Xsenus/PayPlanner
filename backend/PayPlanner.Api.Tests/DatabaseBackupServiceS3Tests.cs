using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using System.Text.Json;
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
        Assert.Contains(upload.TargetPath, scope.S3.UploadedPaths);
        Assert.Contains($"{upload.TargetPath}.manifest.json", scope.S3.UploadedPaths);
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
        Assert.Contains(upload.TargetPath, scope.YandexDisk.UploadedPaths);
        Assert.Contains($"{upload.TargetPath}.manifest.json", scope.YandexDisk.UploadedPaths);
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
        Assert.True(File.Exists($"{fullPath}.fileset"));

        var result = await service.DeleteBackupAsync(created.Backup.RelativePath, CancellationToken.None);

        Assert.Equal(1, result.DeletedCount);
        Assert.False(result.DeletedAll);
        Assert.False(File.Exists(fullPath));
        Assert.False(File.Exists($"{fullPath}.fileset"));
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

    [Fact]
    public async Task EnsureScheduledBackupsAsync_RetainsNewestLocalBackupEvenWhenExpired()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await service.CreateManualBackupAsync(CancellationToken.None);
        var fullPath = service.ResolveBackupPath(created.Backup.RelativePath);
        Assert.NotNull(fullPath);
        File.SetLastWriteTimeUtc(fullPath, DateTime.UtcNow.AddDays(-10));

        await service.UpdateSettingsAsync(
            automaticEnabled: false,
            scheduleLocal: "03:00",
            retentionDays: 1,
            backupFormat: "sqlite",
            cancellationToken: CancellationToken.None);

        var createdCount = await service.EnsureScheduledBackupsAsync(CancellationToken.None);

        Assert.Equal(0, createdCount);
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public async Task EnsureScheduledBackupsAsync_DeletesExpiredBackupWhenNewerBackupExists()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var first = await service.CreateManualBackupAsync(CancellationToken.None);
        var second = await service.CreateManualBackupAsync(CancellationToken.None);
        var firstPath = service.ResolveBackupPath(first.Backup.RelativePath);
        var secondPath = service.ResolveBackupPath(second.Backup.RelativePath);
        Assert.NotNull(firstPath);
        Assert.NotNull(secondPath);
        File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(secondPath, DateTime.UtcNow.AddDays(-9));

        await service.UpdateSettingsAsync(
            automaticEnabled: false,
            scheduleLocal: "03:00",
            retentionDays: 1,
            backupFormat: "sqlite",
            cancellationToken: CancellationToken.None);

        await service.EnsureScheduledBackupsAsync(CancellationToken.None);

        Assert.False(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task EnsureScheduledBackupsAsync_PreservesLatestVerifiedBackupWhenNewestIsCorrupt()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var oldest = await service.CreateManualBackupAsync(CancellationToken.None);
        var latestVerified = await service.CreateManualBackupAsync(CancellationToken.None);
        var newestCorrupt = await service.CreateManualBackupAsync(CancellationToken.None);
        var oldestPath = service.ResolveBackupPath(oldest.Backup.RelativePath)!;
        var verifiedPath = service.ResolveBackupPath(latestVerified.Backup.RelativePath)!;
        var corruptPath = service.ResolveBackupPath(newestCorrupt.Backup.RelativePath)!;
        await File.AppendAllTextAsync(corruptPath, "tampered");
        File.SetLastWriteTimeUtc(oldestPath, DateTime.UtcNow.AddDays(-11));
        File.SetLastWriteTimeUtc(verifiedPath, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(corruptPath, DateTime.UtcNow.AddDays(-9));
        await service.UpdateSettingsAsync(
            automaticEnabled: false, scheduleLocal: "03:00", retentionDays: 1,
            backupFormat: "sqlite", cancellationToken: CancellationToken.None);

        await service.EnsureScheduledBackupsAsync(CancellationToken.None);

        Assert.False(File.Exists(oldestPath));
        Assert.True(File.Exists(verifiedPath));
        Assert.True((await service.VerifyBackupAsync(latestVerified.Backup.RelativePath)).Verified);
        Assert.True(File.Exists(corruptPath));
    }

    [Fact]
    public async Task EnsureScheduledBackupsAsync_DoesNotDeleteExpiredFilesWithoutVerifiedBackup()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var first = await service.CreateManualBackupAsync(CancellationToken.None);
        var second = await service.CreateManualBackupAsync(CancellationToken.None);
        var firstPath = service.ResolveBackupPath(first.Backup.RelativePath)!;
        var secondPath = service.ResolveBackupPath(second.Backup.RelativePath)!;
        File.Delete($"{firstPath}.manifest.json");
        File.Delete($"{secondPath}.manifest.json");
        File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(secondPath, DateTime.UtcNow.AddDays(-9));
        await service.UpdateSettingsAsync(
            automaticEnabled: false, scheduleLocal: "03:00", retentionDays: 1,
            backupFormat: "sqlite", cancellationToken: CancellationToken.None);

        await service.EnsureScheduledBackupsAsync(CancellationToken.None);

        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task EnsureScheduledBackupsAsync_SkipsDeniedChildDirectoryAndCleansAccessibleBackups()
    {
        var reader = new FaultingBackupDirectoryReader();
        await using var scope = await CreateScopeAsync(reader);
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var oldest = await service.CreateManualBackupAsync(CancellationToken.None);
        var latest = await service.CreateManualBackupAsync(CancellationToken.None);
        var oldestPath = service.ResolveBackupPath(oldest.Backup.RelativePath)!;
        var latestPath = service.ResolveBackupPath(latest.Backup.RelativePath)!;
        File.SetLastWriteTimeUtc(oldestPath, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(latestPath, DateTime.UtcNow.AddDays(-9));
        var deniedDirectory = Path.Combine(scope.Root, "backups", "denied-child");
        Directory.CreateDirectory(deniedDirectory);
        var inaccessibleFile = Path.Combine(deniedDirectory, "unavailable.zip");
        await File.WriteAllTextAsync(inaccessibleFile, "not inspected or deleted");
        reader.DeniedFilesDirectory = deniedDirectory;
        reader.DeniedSubdirectoriesDirectory = deniedDirectory;
        await service.UpdateSettingsAsync(
            automaticEnabled: false, scheduleLocal: "03:00", retentionDays: 1,
            backupFormat: "zip", cancellationToken: CancellationToken.None);

        Assert.Equal(0, await service.EnsureScheduledBackupsAsync(CancellationToken.None));

        Assert.True(reader.DeniedFilesCount > 0);
        Assert.True(reader.DeniedSubdirectoriesCount > 0);
        Assert.False(File.Exists(oldestPath));
        Assert.True(File.Exists(latestPath));
        Assert.True((await service.VerifyBackupAsync(latest.Backup.RelativePath)).Verified);
        Assert.True(File.Exists(inaccessibleFile));
    }

    [Fact]
    public async Task EnsureScheduledBackupsAsync_DoesNotPruneWhenRootListingFails()
    {
        var reader = new FaultingBackupDirectoryReader();
        await using var scope = await CreateScopeAsync(reader);
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var oldest = await service.CreateManualBackupAsync(CancellationToken.None);
        var latest = await service.CreateManualBackupAsync(CancellationToken.None);
        var oldestPath = service.ResolveBackupPath(oldest.Backup.RelativePath)!;
        var latestPath = service.ResolveBackupPath(latest.Backup.RelativePath)!;
        File.SetLastWriteTimeUtc(oldestPath, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(latestPath, DateTime.UtcNow.AddDays(-9));
        reader.DeniedSubdirectoriesDirectory = Path.Combine(scope.Root, "backups");
        await service.UpdateSettingsAsync(
            automaticEnabled: false, scheduleLocal: "03:00", retentionDays: 1,
            backupFormat: "zip", cancellationToken: CancellationToken.None);

        Assert.Equal(0, await service.EnsureScheduledBackupsAsync(CancellationToken.None));

        Assert.True(reader.DeniedSubdirectoriesCount > 0);
        Assert.True(File.Exists(oldestPath));
        Assert.True(File.Exists(latestPath));
    }

    [Fact]
    public async Task EnsureScheduledBackupsAsync_ConcurrentRunsCreateOneVerifiedSlot()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(
            automaticEnabled: true, scheduleLocal: "00:00", retentionDays: 14,
            backupFormat: "sqlite", cancellationToken: CancellationToken.None);

        var counts = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => service.EnsureScheduledBackupsAsync(CancellationToken.None)));

        Assert.Equal(1, counts.Sum());
        var automatic = Assert.Single((await service.GetOverviewAsync()).Items, item => item.Trigger == "auto");
        Assert.True((await service.VerifyBackupAsync(automatic.RelativePath)).Verified);
    }

    [Fact]
    public async Task EnsureScheduledBackupsAsync_RetriesSlotWhenOnlyCopyIsCorrupt()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(
            automaticEnabled: true, scheduleLocal: "00:00", retentionDays: 14,
            backupFormat: "sqlite", cancellationToken: CancellationToken.None);
        Assert.Equal(1, await service.EnsureScheduledBackupsAsync(CancellationToken.None));
        var first = Assert.Single((await service.GetOverviewAsync()).Items, item => item.Trigger == "auto");
        var firstPath = service.ResolveBackupPath(first.RelativePath)!;
        await File.AppendAllTextAsync(firstPath, "tampered");

        Assert.Equal(1, await service.EnsureScheduledBackupsAsync(CancellationToken.None));
        var backups = (await service.GetOverviewAsync()).Items.Where(item => item.Trigger == "auto").ToList();
        Assert.Equal(2, backups.Count);
        var replacement = Assert.Single(backups, item => item.RelativePath != first.RelativePath);
        Assert.True((await service.VerifyBackupAsync(replacement.RelativePath)).Verified);
        Assert.False((await service.VerifyBackupAsync(first.RelativePath)).Verified);
    }

    [Fact]
    public async Task EnsureScheduledBackupsAsync_RechecksRequiredFilePartAfterCachedVerification()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(
            automaticEnabled: true, scheduleLocal: "00:00", retentionDays: 14,
            backupFormat: "sqlite", cancellationToken: CancellationToken.None);
        Assert.Equal(1, await service.EnsureScheduledBackupsAsync(CancellationToken.None));
        Assert.Equal(0, await service.EnsureScheduledBackupsAsync(CancellationToken.None));
        var first = Assert.Single((await service.GetOverviewAsync()).Items, item => item.Trigger == "auto");
        var firstPath = service.ResolveBackupPath(first.RelativePath)!;
        await File.AppendAllTextAsync($"{firstPath}.fileset", "tampered");

        Assert.Equal(1, await service.EnsureScheduledBackupsAsync(CancellationToken.None));
        var replacement = Assert.Single((await service.GetOverviewAsync()).Items,
            item => item.Trigger == "auto" && item.RelativePath != first.RelativePath);
        Assert.True((await service.VerifyBackupAsync(replacement.RelativePath)).Verified);
        Assert.False((await service.VerifyBackupAsync(first.RelativePath)).Verified);
    }

    [Fact]
    public async Task EnsureScheduledBackupsAsync_ReplacesIncompleteSlotEvenAfterCachedVerification()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(
            automaticEnabled: true, scheduleLocal: "00:00", retentionDays: 14,
            backupFormat: "sqlite", cancellationToken: CancellationToken.None);
        Assert.Equal(1, await service.EnsureScheduledBackupsAsync(CancellationToken.None));
        Assert.Equal(0, await service.EnsureScheduledBackupsAsync(CancellationToken.None));
        var first = Assert.Single((await service.GetOverviewAsync()).Items, item => item.Trigger == "auto");
        var manifestPath = $"{service.ResolveBackupPath(first.RelativePath)}.manifest.json";
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<DatabaseBackupManifest>(
            await File.ReadAllTextAsync(manifestPath), jsonOptions)!;
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest with { Status = "incomplete" }, jsonOptions));

        Assert.Equal(1, await service.EnsureScheduledBackupsAsync(CancellationToken.None));
        var replacement = Assert.Single((await service.GetOverviewAsync()).Items,
            item => item.Trigger == "auto" && item.RelativePath != first.RelativePath);
        Assert.True((await service.VerifyBackupAsync(replacement.RelativePath)).Verified);
    }

    [Fact]
    public async Task VerifyBackupAsync_ValidatesManifestAndDetectsTampering()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await service.CreateManualBackupAsync(CancellationToken.None);

        var verified = await service.VerifyBackupAsync(created.Backup.RelativePath, CancellationToken.None);

        Assert.True(verified.Verified);
        Assert.Equal("verified", verified.Status);
        Assert.NotNull(verified.BackupId);
        Assert.Equal(verified.ExpectedSha256, verified.ActualSha256);

        var fullPath = service.ResolveBackupPath(created.Backup.RelativePath);
        Assert.NotNull(fullPath);
        await File.AppendAllTextAsync(fullPath, "tampered");

        var mismatch = await service.VerifyBackupAsync(created.Backup.RelativePath, CancellationToken.None);

        Assert.False(mismatch.Verified);
        Assert.Equal("mismatch", mismatch.Status);
        Assert.NotEqual(mismatch.ExpectedSha256, mismatch.ActualSha256);
    }

    [Fact]
    public async Task VerifyBackupAsync_LabelsLegacyBackupWithoutManifest()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await service.CreateManualBackupAsync(CancellationToken.None);
        var fullPath = service.ResolveBackupPath(created.Backup.RelativePath);
        Assert.NotNull(fullPath);
        File.Delete($"{fullPath}.manifest.json");

        var result = await service.VerifyBackupAsync(created.Backup.RelativePath, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal("legacyUnverified", result.Status);
        Assert.Null(result.ExpectedSha256);
    }

    [Fact]
    public async Task VerifyBackupAsync_RejectsIncompleteManifestBeforeRestore()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await service.CreateManualBackupAsync(CancellationToken.None);
        var manifestPath = $"{service.ResolveBackupPath(created.Backup.RelativePath)}.manifest.json";
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<DatabaseBackupManifest>(
            await File.ReadAllTextAsync(manifestPath), jsonOptions)!;
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest with { Status = "incomplete" }, jsonOptions));

        var result = await service.VerifyBackupAsync(created.Backup.RelativePath);

        Assert.False(result.Verified);
        Assert.Equal("invalidManifest", result.Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadVerifiedManifestAsync(created.Backup.RelativePath));
    }

    [Fact]
    public async Task RestoreDrill_RestoresVerifiedSqliteBackupIntoDisposableDirectory()
    {
        await using var scope = await CreateScopeAsync();
        Directory.CreateDirectory(Path.Combine(scope.Root, "files", "task-files"));
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "files", "task-files", "evidence.txt"), "restored evidence");
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await backups.UpdateSettingsAsync(
            automaticEnabled: false,
            scheduleLocal: "03:00",
            retentionDays: 14,
            backupFormat: "zip",
            cancellationToken: CancellationToken.None);
        var created = await backups.CreateManualBackupAsync(CancellationToken.None);

        var report = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, executePostgreSql: false, confirmationToken: null, CancellationToken.None);

        Assert.Equal("restored", report.Status);
        Assert.Equal("ok", report.IntegrityStatus);
        Assert.True(report.TableCount > 0);
        Assert.Equal(0, report.ForeignKeyViolationCount);
        Assert.True(File.Exists(report.Target));
        Assert.True(File.Exists(report.ReportPath));
        Assert.StartsWith(Path.Combine(scope.Root, "restore-drills"), report.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("restored evidence", await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(report.Target)!, "files", "file-storage", "task-files", "evidence.txt")));
    }

    private static async Task<TestScope> CreateScopeAsync(IBackupDirectoryReader? directoryReader = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"payplanner-backup-s3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "payplanner.db");
        var backupDirectory = Path.Combine(root, "backups");
        var fileStorageDirectory = Path.Combine(root, "files");
        Directory.CreateDirectory(fileStorageDirectory);
        var connectionString = $"Data Source={dbPath}";
        var providerInfo = new DatabaseProviderInfo(DatabaseProviderKind.Sqlite, DatabaseProviderResolver.NormalizeSqliteConnection(connectionString));
        var s3 = new FakeS3StorageService();
        var yandexDisk = new FakeYandexDiskService();
        var services = new ServiceCollection();
        services.AddSingleton(providerInfo);
        if (directoryReader is not null) services.AddSingleton(directoryReader);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseBackup:Directory"] = backupDirectory,
                ["DatabaseBackup:IncludeFileStorage"] = "true",
                ["FileStorage:RootPath"] = fileStorageDirectory,
            })
            .Build());
        services.AddDbContextFactory<PaymentContext>(options => options.UseSqlite(providerInfo.ConnectionString));
        services.AddScoped<IS3StorageService>(_ => s3);
        services.AddScoped<IYandexDiskService>(_ => yandexDisk);
        services.AddSingleton(s3);
        services.AddSingleton(yandexDisk);
        services.AddSingleton<ILogger<DatabaseBackupService>>(NullLogger<DatabaseBackupService>.Instance);
        services.AddSingleton<ILogger<DatabaseRestoreDrillService>>(NullLogger<DatabaseRestoreDrillService>.Instance);
        services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(root));
        services.Configure<DatabaseRestoreDrillOptions>(options => options.RootDirectory = Path.Combine(root, "restore-drills"));
        services.AddSingleton<DatabaseBackupService>();
        services.AddSingleton<DatabaseRestoreDrillService>();
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
        public List<string> UploadedPaths { get; } = [];
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

            UploadedPaths.Add(path ?? string.Empty);
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
        public List<string> UploadedPaths { get; } = [];
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

            UploadedPaths.Add(path ?? string.Empty);
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

    private sealed class TestScope : IAsyncDisposable
    {
        public TestScope(string root, ServiceProvider provider, FakeS3StorageService s3, FakeYandexDiskService yandexDisk)
        {
            Root = root;
            Provider = provider;
            S3 = s3;
            YandexDisk = yandexDisk;
        }

        public string Root { get; }
        public ServiceProvider Provider { get; }
        public FakeS3StorageService S3 { get; }
        public FakeYandexDiskService YandexDisk { get; }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FaultingBackupDirectoryReader : IBackupDirectoryReader
    {
        private readonly PhysicalBackupDirectoryReader _physical = new();
        public string? DeniedFilesDirectory { get; set; }
        public string? DeniedSubdirectoriesDirectory { get; set; }
        public int DeniedFilesCount { get; private set; }
        public int DeniedSubdirectoriesCount { get; private set; }

        public IReadOnlyList<string> GetFiles(string directory, string pattern)
        {
            if (string.Equals(directory, DeniedFilesDirectory, StringComparison.OrdinalIgnoreCase))
            {
                DeniedFilesCount++;
                throw new UnauthorizedAccessException("Simulated inaccessible backup files.");
            }
            return _physical.GetFiles(directory, pattern);
        }

        public IReadOnlyList<string> GetDirectories(string directory)
        {
            if (string.Equals(directory, DeniedSubdirectoriesDirectory, StringComparison.OrdinalIgnoreCase))
            {
                DeniedSubdirectoriesCount++;
                throw new UnauthorizedAccessException("Simulated inaccessible backup subdirectories.");
            }
            return _physical.GetDirectories(directory);
        }

        public bool IsEmpty(string directory) => _physical.IsEmpty(directory);
    }

    private sealed class TestWebHostEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
