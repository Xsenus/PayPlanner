using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupServiceS3Tests
{
    [Fact]
    public async Task FileArchiveIncludesDefaultAndDualReadLegacyRootsWithoutDuplicates()
    {
        await using var scope = await CreateScopeAsync();
        var configuration = scope.Provider.GetRequiredService<IConfiguration>();
        configuration["FileStorage:RootPath"] = "";
        var expected = new Dictionary<string, string>
        {
            ["file-storage/task-files/evidence.txt"] = Path.Combine(scope.Root, "App_Data", "task-files", "evidence.txt"),
            ["file-storage/user-photos/photo.png"] = Path.Combine(scope.Root, "App_Data", "user-photos", "photo.png"),
            ["file-storage/electronic-signatures/sign.pdf"] = Path.Combine(scope.Root, "App_Data", "electronic-signatures", "sign.pdf"),
            ["file-storage/objects/object.bin"] = Path.Combine(scope.Root, "App_Data", "objects", "object.bin"),
            ["profile-photos-legacy/old.png"] = Path.Combine(scope.Root, "wwwroot", "user-photos", "old.png"),
            ["electronic-signatures-legacy/old.pdf"] = Path.Combine(scope.Root, "storage", "electronic-signatures", "old.pdf"),
        };
        foreach (var item in expected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.Value)!);
            await File.WriteAllTextAsync(item.Value, item.Key);
        }

        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var result = await backups.CreateManualBackupAsync();
        using var archive = System.IO.Compression.ZipFile.OpenRead($"{backups.ResolveBackupPath(result.Backup.RelativePath)}.fileset");
        Assert.Equal(expected.Keys.Order(), archive.Entries.Select(entry => entry.FullName).Order());
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            Assert.Equal(entry.FullName, await reader.ReadToEndAsync());
        }
        Assert.True((await backups.VerifyBackupAsync(result.Backup.RelativePath)).Verified);
        var manifest = await backups.ReadVerifiedManifestAsync(result.Backup.RelativePath);
        var mappings = Assert.Single(manifest.Parts!, part => part.Kind == "file-storage").FileRoots!;
        Assert.Equal(6, mappings.Count);
        Assert.Equal("file-storage/user-photos", Assert.Single(mappings, item => item.Name == "profile-photos").ArchivePrefix);
        Assert.Equal("file-storage/electronic-signatures", Assert.Single(mappings, item => item.Name == "electronic-signatures").ArchivePrefix);
        Assert.Equal("file-storage/objects", Assert.Single(mappings, item => item.Name == "local-objects").ArchivePrefix);
        Assert.Equal("profile-photos-legacy", Assert.Single(mappings, item => item.Name == "profile-photos-legacy").ArchivePrefix);
    }

    [Fact]
    public async Task FileArchiveResolvesRelativeAndSeparateManagedRootsAgainstContentRoot()
    {
        await using var scope = await CreateScopeAsync();
        var configuration = scope.Provider.GetRequiredService<IConfiguration>();
        var roots = new[]
        {
            (Key: "FileStorage:RootPath", Folder: "relative-files", Prefix: "file-storage"),
            (Key: "ProfilePhotos:StorageRoot", Folder: "relative-photos", Prefix: "profile-photos"),
            (Key: "ElectronicSignature:StorageRoot", Folder: "relative-signatures", Prefix: "electronic-signatures"),
            (Key: "StorageOrchestration:LocalRoot", Folder: "relative-objects", Prefix: "local-objects"),
        };
        foreach (var root in roots)
        {
            configuration[root.Key] = root.Folder;
            Directory.CreateDirectory(Path.Combine(scope.Root, root.Folder));
            await File.WriteAllTextAsync(Path.Combine(scope.Root, root.Folder, "content.txt"), root.Prefix);
        }

        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var result = await backups.CreateManualBackupAsync();
        using var archive = System.IO.Compression.ZipFile.OpenRead($"{backups.ResolveBackupPath(result.Backup.RelativePath)}.fileset");
        Assert.Equal(roots.Select(root => $"{root.Prefix}/content.txt").Order(), archive.Entries.Select(entry => entry.FullName).Order());
        foreach (var root in roots)
        {
            using var reader = new StreamReader(archive.GetEntry($"{root.Prefix}/content.txt")!.Open());
            Assert.Equal(root.Prefix, await reader.ReadToEndAsync());
        }
        var manifest = await backups.ReadVerifiedManifestAsync(result.Backup.RelativePath);
        var mappings = Assert.Single(manifest.Parts!, part => part.Kind == "file-storage").FileRoots!;
        Assert.Equal(4, mappings.Count);
        Assert.All(mappings, mapping => Assert.Equal(mapping.Name, mapping.ArchivePrefix));
    }

    [Theory]
    [InlineData("ProfilePhotos:StorageRoot")]
    [InlineData("ElectronicSignature:StorageRoot")]
    [InlineData("StorageOrchestration:LocalRoot")]
    public async Task FileArchiveRejectsMissingExplicitSecondaryRoot(string key)
    {
        await using var scope = await CreateScopeAsync();
        scope.Provider.GetRequiredService<IConfiguration>()[key] = "missing-root";
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => backups.CreateManualBackupAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.manifest.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FileArchiveDoesNotRequireUnusedDefaultOrLegacyDirectories()
    {
        await using var scope = await CreateScopeAsync();
        scope.Provider.GetRequiredService<IConfiguration>()["FileStorage:RootPath"] = "";
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var result = await backups.CreateManualBackupAsync();
        Assert.True((await backups.VerifyBackupAsync(result.Backup.RelativePath)).Verified);
        Assert.False(File.Exists($"{backups.ResolveBackupPath(result.Backup.RelativePath)}.fileset"));
    }

    [Fact]
    public async Task FileArchiveRejectsDefaultPathThatIsAFile()
    {
        await using var scope = await CreateScopeAsync();
        scope.Provider.GetRequiredService<IConfiguration>()["FileStorage:RootPath"] = "";
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "App_Data"), "not a directory");
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await Assert.ThrowsAsync<IOException>(() => backups.CreateManualBackupAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.manifest.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FileArchiveDisabledDoesNotAccessMissingConfiguredRoots()
    {
        await using var scope = await CreateScopeAsync();
        var configuration = scope.Provider.GetRequiredService<IConfiguration>();
        configuration["DatabaseBackup:IncludeFileStorage"] = "false";
        configuration["FileStorage:RootPath"] = "missing-root";
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var result = await backups.CreateManualBackupAsync();
        Assert.True((await backups.VerifyBackupAsync(result.Backup.RelativePath)).Verified);
        Assert.False(File.Exists($"{backups.ResolveBackupPath(result.Backup.RelativePath)}.fileset"));
    }

    [Fact]
    public async Task FileArchiveDeduplicatesExplicitNestedRootsAndExcludesBackupDirectory()
    {
        await using var scope = await CreateScopeAsync();
        var configuration = scope.Provider.GetRequiredService<IConfiguration>();
        configuration["DatabaseBackup:Directory"] = Path.Combine(scope.Root, "files", "backups");
        foreach (var key in new[] { "ProfilePhotos:StorageRoot", "ElectronicSignature:StorageRoot", "StorageOrchestration:LocalRoot" })
            configuration[key] = Path.Combine(scope.Root, "files");
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "files", "only.txt"), "one");
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var result = await backups.CreateManualBackupAsync();
        using var archive = System.IO.Compression.ZipFile.OpenRead($"{backups.ResolveBackupPath(result.Backup.RelativePath)}.fileset");
        Assert.Equal("file-storage/only.txt", Assert.Single(archive.Entries).FullName);
        var manifest = await backups.ReadVerifiedManifestAsync(result.Backup.RelativePath);
        var mappings = Assert.Single(manifest.Parts!, part => part.Kind == "file-storage").FileRoots!;
        Assert.Equal(4, mappings.Count);
        Assert.All(mappings, mapping => Assert.Equal("file-storage", mapping.ArchivePrefix));
    }

    [Fact]
    public async Task FileArchiveHistoricalManifestWithoutMappingsStillVerifiesAndRestores()
    {
        await using var scope = await CreateScopeAsync();
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "files", "evidence.txt"), "legacy bytes");
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var backup = await backups.CreateManualBackupAsync();
        var path = $"{backups.ResolveBackupPath(backup.Backup.RelativePath)}.manifest.json";
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        foreach (var part in json["parts"]!.AsArray()) part!.AsObject().Remove("fileRoots");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        Assert.True((await backups.VerifyBackupAsync(backup.Backup.RelativePath)).Verified);
        var drill = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(backup.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.Equal("restored", drill.Status);
        Assert.Equal("legacy bytes", await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(drill.ReportPath)!,
            "files", "file-storage", "evidence.txt")));
    }

    [Fact]
    public async Task FileArchiveDoesNotReadExcludedBackupDirectory()
    {
        var reader = new FaultingBackupDirectoryReader();
        await using var scope = await CreateScopeAsync(reader);
        var backupDirectory = Path.Combine(scope.Root, "files", "backups");
        scope.Provider.GetRequiredService<IConfiguration>()["DatabaseBackup:Directory"] = backupDirectory;
        reader.DeniedFilesDirectory = backupDirectory;
        reader.DeniedSubdirectoriesDirectory = backupDirectory;
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "files", "evidence.txt"), "working document");
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var result = await backups.CreateManualBackupAsync();
        using var archive = System.IO.Compression.ZipFile.OpenRead($"{backups.ResolveBackupPath(result.Backup.RelativePath)}.fileset");
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("file-storage/evidence.txt", entry.FullName);
        using var content = new StreamReader(entry.Open());
        Assert.Equal("working document", await content.ReadToEndAsync());
        // Cleanup legitimately reads the backup directory. A denial here must not spoil the file archive.
        Assert.True((await backups.VerifyBackupAsync(result.Backup.RelativePath)).Verified);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("unknown/photos")]
    [InlineData("file-storage//photos")]
    [InlineData("file-storage\\photos")]
    public async Task VerifyRejectsUnsafeFileRootMappingsBeforeAcceptingTheSet(string prefix)
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var backup = await backups.CreateManualBackupAsync();
        var path = $"{backups.ResolveBackupPath(backup.Backup.RelativePath)}.manifest.json";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<DatabaseBackupManifest>(await File.ReadAllTextAsync(path), options)!;
        var parts = manifest.Parts!.Select(part => part.Kind == "file-storage"
            ? part with { FileRoots = [new("file-storage", prefix)] } : part).ToArray();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest with { Parts = parts }, options));
        var verification = await backups.VerifyBackupAsync(backup.Backup.RelativePath);
        Assert.False(verification.Verified);
        Assert.Equal("invalidManifest", verification.Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.ReadVerifiedManifestAsync(backup.Backup.RelativePath));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null-item")]
    [InlineData("wrong-kind")]
    public async Task VerifyRejectsMalformedFileRootDeclarations(string corruption)
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var backup = await backups.CreateManualBackupAsync();
        var path = $"{backups.ResolveBackupPath(backup.Backup.RelativePath)}.manifest.json";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<DatabaseBackupManifest>(await File.ReadAllTextAsync(path), options)!;
        var parts = manifest.Parts!.Select(part => part.Kind != "file-storage" ? part : corruption switch
        {
            "empty" => part with { FileRoots = [] },
            "null-item" => part with { FileRoots = [null!] },
            "wrong-kind" => part with { Kind = "unknown" },
            _ => throw new InvalidOperationException("Unknown fixture corruption."),
        }).ToArray();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest with { Parts = parts }, options));
        var verification = await backups.VerifyBackupAsync(backup.Backup.RelativePath);
        Assert.False(verification.Verified);
        Assert.Equal("invalidManifest", verification.Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.ReadVerifiedManifestAsync(backup.Backup.RelativePath));
    }

    [Fact]
    public async Task FileArchiveRejectsWholeVolumeBeforeEnumeratingUnrelatedData()
    {
        await using var scope = await CreateScopeAsync();
        scope.Provider.GetRequiredService<IConfiguration>()["FileStorage:RootPath"] = Path.GetPathRoot(scope.Root)!;
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await Assert.ThrowsAsync<IOException>(() => backups.CreateManualBackupAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.manifest.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BackupAttemptDatabaseDumpFailureKeepsHistoryWithoutAReadySetOrSensitivePath()
    {
        await using var scope = await CreateScopeAsync();
        var unavailablePath = Path.Combine(scope.Root, "not-a-database.sqlite");
        var unavailableSource = new DatabaseProviderInfo(DatabaseProviderKind.Sqlite,
            new SqliteConnectionStringBuilder { DataSource = unavailablePath }.ConnectionString);
        var service = ActivatorUtilities.CreateInstance<DatabaseBackupService>(scope.Provider, unavailableSource);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateManualBackupAsync());
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        var attempt = await db.DatabaseBackupAttempts.SingleAsync();
        Assert.Equal(DatabaseBackupAttemptStatuses.Failed, attempt.Status);
        Assert.Equal("database", attempt.Stage);
        Assert.Null(attempt.BackupSetId);
        Assert.NotNull(attempt.CompletedAt);
        Assert.NotNull(attempt.Error);
        Assert.DoesNotContain(unavailablePath, attempt.Error, StringComparison.Ordinal);
        Assert.Empty(await db.DatabaseBackupSets.ToListAsync());
        Assert.False(File.Exists(unavailablePath));
    }

    [Fact]
    public async Task BackupAttemptIsRecordedBeforeUploadAndLinkedOnlyToVerifiedSet()
    {
        await using var scope = await CreateScopeAsync();
        await scope.Provider.GetRequiredService<DatabaseBackupService>().UpdateSettingsAsync(true, "03:00", 14, "sqlite",
            s3UploadEnabled: true, s3ConnectionIds: ["main"]);
        scope.S3.BeforeUploadAsync = async () =>
        {
            await using var during = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
            var running = await during.DatabaseBackupAttempts.SingleAsync();
            Assert.Equal(DatabaseBackupAttemptStatuses.Running, running.Status);
            Assert.Equal("uploading", running.Stage);
            Assert.NotNull(running.BackupSetId);
            Assert.Null(running.CompletedAt);
        };
        var result = await scope.Provider.GetRequiredService<DatabaseBackupService>().CreateManualBackupAsync();
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        var attempt = await db.DatabaseBackupAttempts.SingleAsync();
        Assert.Equal(DatabaseBackupAttemptStatuses.Succeeded, attempt.Status);
        Assert.Equal("completed", attempt.Stage);
        Assert.Equal("manual", attempt.Trigger);
        Assert.Null(attempt.ScheduleSlot);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Equal(result.Backup.RelativePath.Replace('\\', '/'), (await db.DatabaseBackupSets.SingleAsync()).BackupRelativePath);
        Assert.Equal((await db.DatabaseBackupSets.SingleAsync()).Id, attempt.BackupSetId);
    }

    [Fact]
    public async Task BackupAttemptCloudFailureKeepsVerifiedLocalSetAndDegradedAttempt()
    {
        await using var scope = await CreateScopeAsync();
        await scope.Provider.GetRequiredService<DatabaseBackupService>().UpdateSettingsAsync(true, "03:00", 14, "sqlite",
            s3UploadEnabled: true, s3ConnectionIds: ["main"]);
        scope.S3.ThrowOnUpload = true;
        await scope.Provider.GetRequiredService<DatabaseBackupService>().CreateManualBackupAsync();
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        var attempt = await db.DatabaseBackupAttempts.SingleAsync();
        Assert.Equal(DatabaseBackupAttemptStatuses.Degraded, attempt.Status);
        Assert.NotNull(attempt.BackupSetId);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Null(attempt.Error);
    }

    [Fact]
    public async Task BackupAttemptCancellationKeepsStageAndUsesIndependentRecordingToken()
    {
        await using var scope = await CreateScopeAsync();
        await scope.Provider.GetRequiredService<DatabaseBackupService>().UpdateSettingsAsync(true, "03:00", 14, "sqlite",
            s3UploadEnabled: true, s3ConnectionIds: ["main"]);
        using var cancellation = new CancellationTokenSource();
        scope.S3.BeforeUploadAsync = () => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Provider.GetRequiredService<DatabaseBackupService>().CreateManualBackupAsync(cancellation.Token));
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        var attempt = await db.DatabaseBackupAttempts.SingleAsync();
        Assert.Equal(DatabaseBackupAttemptStatuses.Cancelled, attempt.Status);
        Assert.Equal("uploading", attempt.Stage);
        Assert.NotNull(attempt.BackupSetId);
        Assert.NotNull(attempt.CompletedAt);
    }

    [Fact]
    public async Task BackupAttemptNextLeaseMarksOnlySameRootStaleRunsInterrupted()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.CreateManualBackupAsync();
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var previous = await db.DatabaseBackupAttempts.SingleAsync();
            db.DatabaseBackupAttempts.Add(new DatabaseBackupAttempt
            {
                Id = "stale",
                RootIdentity = previous.RootIdentity,
                Trigger = "auto",
                ScheduleSlot = "unfinished",
                Stage = "database",
                StartedAt = DateTime.UtcNow.AddMinutes(-20),
            });
            db.DatabaseBackupAttempts.Add(new DatabaseBackupAttempt
            {
                Id = "other-root",
                RootIdentity = new string('0', 64),
                Trigger = "manual",
            });
            await db.SaveChangesAsync();
        }
        await service.CreateManualBackupAsync();
        await using var after = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        var stale = await after.DatabaseBackupAttempts.SingleAsync(item => item.Id == "stale");
        Assert.Equal(DatabaseBackupAttemptStatuses.Interrupted, stale.Status);
        Assert.Equal("database", stale.Stage);
        Assert.NotNull(stale.CompletedAt);
        Assert.Null(stale.BackupSetId);
        Assert.Equal(DatabaseBackupAttemptStatuses.Running,
            (await after.DatabaseBackupAttempts.SingleAsync(item => item.Id == "other-root")).Status);
        Assert.Equal(2, await after.DatabaseBackupAttempts.CountAsync(item => item.Status == DatabaseBackupAttemptStatuses.Succeeded));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("zip")]
    public async Task RestoreDrillIsolatesSqliteEmailWithoutChangingSourceOrBackup(string format)
    {
        await using var scope = await CreateScopeAsync();
        await using (var source = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            foreach (var key in new[] { "enabled", "smtpHost", "smtpUsername", "smtpPassword", "fromEmail" })
                source.SystemSettings.Add(new SystemSetting { Key = $"notifications.email.{key}", Value = key == "enabled" ? "true" : "test-only.invalid" });
            foreach (var (channel, status) in new[] { ("email", "pending"), ("email", "failed"), ("email", "sent"), ("telegram", "failed") })
                source.NotificationDeliveries.Add(new NotificationDelivery { EventKey = $"test:{channel}:{status}", Channel = channel, Status = status });
            source.NotificationSenders.Add(new NotificationSender { Channel = "email", Name = "Test email", ConfigJson = "{\"smtpHost\":\"test-only.invalid\"}" });
            source.NotificationSenders.Add(new NotificationSender { Channel = "telegram", Name = "Test non-email", ConfigJson = "{\"test\":true}" });
            await source.SaveChangesAsync();
        }
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await backups.UpdateSettingsAsync(false, "03:00", 14, format);
        var created = await backups.CreateManualBackupAsync();
        var sourcePath = Path.Combine(scope.Root, "payplanner.db");
        var backupPath = backups.ResolveBackupPath(created.Backup.RelativePath)!;
        async Task<byte[]> Hash(string path)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
            return await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        }
        var sourceHash = await Hash(sourcePath);
        var backupHash = await Hash(backupPath);
        var report = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>().RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.Equal("restored", report.Status);
        await using (var restored = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = report.Target, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ConnectionString))
        {
            await restored.OpenAsync();
            await using var command = restored.CreateCommand();
            command.CommandText = "SELECT Value FROM SystemSettings WHERE Key = 'notifications.email.enabled'";
            Assert.Equal("false", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM SystemSettings WHERE Key LIKE 'notifications.email.smtp%' OR Key = 'notifications.email.fromEmail'";
            Assert.Equal(4L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM SystemSettings WHERE (Key LIKE 'notifications.email.smtp%' OR Key = 'notifications.email.fromEmail') AND Value <> ''";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT Status FROM NotificationDeliveries ORDER BY Id";
            await using (var reader = await command.ExecuteReaderAsync())
            {
                var statuses = new List<string>();
                while (await reader.ReadAsync()) statuses.Add(reader.GetString(0));
                Assert.Equal(["skipped", "skipped", "sent", "failed"], statuses);
            }
            command.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT ConfigJson FROM NotificationSenders WHERE Channel = 'email' AND IsEnabled = 0";
            Assert.Equal("{}", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT ConfigJson FROM NotificationSenders WHERE Channel = 'telegram' AND IsEnabled = 1";
            Assert.Equal("{\"test\":true}", await command.ExecuteScalarAsync());
            await DatabaseRestoreDrillService.SanitizeRestoredSqliteAsync(restored, CancellationToken.None);
            command.CommandText = "SELECT Value FROM SystemSettings WHERE Key = 'notifications.email.enabled'";
            Assert.Equal("false", await command.ExecuteScalarAsync());
        }
        Assert.Equal(sourceHash, await Hash(sourcePath));
        Assert.Equal(backupHash, await Hash(backupPath));
        await using var original = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal("true", (await original.SystemSettings.SingleAsync(item => item.Key == "notifications.email.enabled")).Value);
        Assert.Equal(2, await original.NotificationDeliveries.CountAsync(item => item.Channel == "email" && (item.Status == "pending" || item.Status == "failed")));
        Assert.True((await original.NotificationSenders.SingleAsync(item => item.Channel == "email")).IsEnabled);
        Assert.Contains("test-only.invalid", (await original.NotificationSenders.SingleAsync(item => item.Channel == "email")).ConfigJson);
        Assert.True((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
    }

    [Fact]
    public async Task RestoreDrillWaitsForDirectoryLeaseAndCancellationCreatesNoReport()
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var drill = scope.Provider.GetRequiredService<DatabaseRestoreDrillService>();
        await using (var lease = await BackupDirectoryLease.AcquireAsync(Path.Combine(scope.Root, "backups"), TimeSpan.FromSeconds(2), CancellationToken.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drill.RunAsync(created.Backup.RelativePath, false, null, cancellation.Token));
            Assert.False(Directory.Exists(Path.Combine(scope.Root, "restore-drills")));
            Assert.True(File.Exists(backups.ResolveBackupPath(created.Backup.RelativePath)));
        }
        var report = await drill.RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.Equal("restored", report.Status);
        Assert.True((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
    }

    [Theory]
    [InlineData(false, "delete")]
    [InlineData(true, "delete")]
    [InlineData(false, "cleanup")]
    [InlineData(true, "cleanup")]
    [InlineData(false, "create")]
    [InlineData(true, "create")]
    [InlineData(false, "retryS3")]
    [InlineData(true, "retryS3")]
    [InlineData(false, "retryYandex")]
    [InlineData(true, "retryYandex")]
    public async Task RestoreDrillHoldsLeaseAgainstConcurrentOperationsBeforeReadingSource(bool separateInstance, string action)
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        if (action == "retryS3")
        {
            scope.S3.ThrowOnUpload = true;
            await backups.UpdateSettingsAsync(false, "03:00", 14, "sqlite", s3UploadEnabled: true, s3ConnectionIds: ["s3-main"]);
        }
        if (action == "retryYandex")
        {
            scope.YandexDisk.ThrowOnUpload = true;
            await backups.UpdateSettingsAsync(false, "03:00", 14, "sqlite", yandexDiskUploadEnabled: true);
        }
        var created = await backups.CreateManualBackupAsync();
        var other = separateInstance ? ActivatorUtilities.CreateInstance<DatabaseBackupService>(scope.Provider) : backups;
        var monitor = new CallbackDrillOptions(Path.Combine(scope.Root, "restore-drills"), () =>
        {
            Assert.Throws<IOException>(() =>
            {
                using var lease = new FileStream(Path.Combine(scope.Root, "backups", ".database-backup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Func<Task> operation = action switch
            {
                "delete" => () => other.DeleteBackupAsync(created.Backup.RelativePath, cancellation.Token),
                "cleanup" => () => other.EnsureScheduledBackupsAsync(cancellation.Token),
                "create" => () => other.CreateManualBackupAsync(cancellation.Token),
                "retryS3" => () => other.RetryS3UploadAsync(Assert.Single(created.S3UploadResults).Id, cancellation.Token),
                "retryYandex" => () => other.RetryYandexDiskUploadAsync(Assert.Single(created.YandexDiskUploadResults).Id, cancellation.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
            Assert.ThrowsAny<OperationCanceledException>(() => Task.Run(operation).GetAwaiter().GetResult());
        });
        var drill = ActivatorUtilities.CreateInstance<DatabaseRestoreDrillService>(scope.Provider, monitor);
        var report = await drill.RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.Equal("restored", report.Status);
        Assert.True(monitor.ReadCount > 0);
        Assert.True(File.Exists(backups.ResolveBackupPath(created.Backup.RelativePath)));
        Assert.Equal(1, (await backups.DeleteBackupAsync(created.Backup.RelativePath)).DeletedCount);
    }

    private sealed class CallbackDrillOptions(string root, Action onRead) : IOptionsMonitor<DatabaseRestoreDrillOptions>
    {
        public int ReadCount { get; private set; }
        public DatabaseRestoreDrillOptions CurrentValue
        {
            get { ReadCount++; onRead(); return new DatabaseRestoreDrillOptions { RootDirectory = root }; }
        }
        public DatabaseRestoreDrillOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<DatabaseRestoreDrillOptions, string?> listener) => null;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RetryRecoversAbandonedPendingAttemptOnlyForIntactLocalSet(bool yandex, bool corrupt)
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite", s3UploadEnabled: !yandex,
            s3ConnectionIds: yandex ? [] : ["s3-main"], yandexDiskUploadEnabled: yandex);
        scope.S3.ThrowOnUpload = true;
        scope.YandexDisk.ThrowOnUpload = true;
        var failed = await service.CreateManualBackupAsync();
        var id = yandex ? Assert.Single(failed.YandexDiskUploadResults).Id : Assert.Single(failed.S3UploadResults).Id;
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var key = yandex ? "database-backup.yandex-disk.results" : "database-backup.s3.results";
            var setting = await db.SystemSettings.SingleAsync(item => item.Key == key);
            var results = System.Text.Json.Nodes.JsonNode.Parse(setting.Value!)!.AsArray();
            Assert.Single(results);
            results[0]!["status"] = "pending";
            setting.Value = results.ToJsonString();
            (await db.DatabaseBackupCopies.SingleAsync(item => item.ProviderKey != "local")).Status = DatabaseBackupCopyStatuses.Pending;
            (await db.DatabaseBackupSets.SingleAsync()).Status = DatabaseBackupSetStatuses.Replicating;
            await db.SaveChangesAsync();
        }
        scope.S3.ThrowOnUpload = false;
        scope.YandexDisk.ThrowOnUpload = false;
        async Task<string> Retry() => yandex ? (await service.RetryYandexDiskUploadAsync(id)).Status
            : (await service.RetryS3UploadAsync(id)).Status;
        if (corrupt)
        {
            await File.AppendAllTextAsync($"{service.ResolveBackupPath(failed.Backup.RelativePath)}.fileset", "corrupt");
            await Assert.ThrowsAsync<InvalidDataException>(() => Retry());
            Assert.Empty(scope.S3.UploadedPaths);
            Assert.Empty(scope.YandexDisk.UploadedPaths);
            await using var rejectedDb = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
            var rejectedCopy = await rejectedDb.DatabaseBackupCopies.SingleAsync(item => item.ProviderKey != "local");
            Assert.Equal(1, rejectedCopy.AttemptCount);
            Assert.Null(rejectedCopy.LastVerifiedAt);
            return;
        }
        Assert.Equal("success", await Retry());
        var uploadCount = yandex ? scope.YandexDisk.UploadedPaths.Count : scope.S3.UploadedPaths.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => Retry());
        Assert.Equal(uploadCount, yandex ? scope.YandexDisk.UploadedPaths.Count : scope.S3.UploadedPaths.Count);
        await using var finalDb = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        var copy = await finalDb.DatabaseBackupCopies.SingleAsync(item => item.ProviderKey != "local");
        Assert.Equal(2, copy.AttemptCount);
        Assert.Equal(DatabaseBackupCopyStatuses.Verified, copy.Status);
        Assert.NotNull(copy.LastVerifiedAt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetryHoldsDirectoryLeaseAndCancelledQueuedOperationsDoNotChangeTheActiveAttempt(bool yandex, bool separateInstance)
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite", s3UploadEnabled: !yandex,
            s3ConnectionIds: yandex ? [] : ["s3-main"], yandexDiskUploadEnabled: yandex);
        scope.S3.ThrowOnUpload = true;
        scope.YandexDisk.ThrowOnUpload = true;
        var failed = await service.CreateManualBackupAsync();
        var id = yandex ? Assert.Single(failed.YandexDiskUploadResults).Id : Assert.Single(failed.S3UploadResults).Id;
        scope.S3.ThrowOnUpload = false;
        scope.YandexDisk.ThrowOnUpload = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task PauseUpload()
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        scope.S3.BeforeUploadAsync = PauseUpload;
        scope.YandexDisk.BeforeUploadAsync = PauseUpload;
        async Task<string> Retry(DatabaseBackupService instance, CancellationToken token) => yandex
            ? (await instance.RetryYandexDiskUploadAsync(id, token)).Status
            : (await instance.RetryS3UploadAsync(id, token)).Status;
        var active = Retry(service, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Throws<IOException>(() =>
            {
                using var lease = new FileStream(Path.Combine(scope.Root, "backups", ".database-backup.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            });
            var other = separateInstance ? ActivatorUtilities.CreateInstance<DatabaseBackupService>(scope.Provider) : service;
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Retry(other, cancellation.Token));
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other.DeleteBackupAsync(failed.Backup.RelativePath, cancellation.Token));
            Assert.True(File.Exists(service.ResolveBackupPath(failed.Backup.RelativePath)));
            await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
            var copy = await db.DatabaseBackupCopies.SingleAsync(item => item.ProviderKey != "local");
            Assert.Equal(DatabaseBackupCopyStatuses.Pending, copy.Status);
            Assert.Equal(2, copy.AttemptCount);
        }
        finally
        {
            release.TrySetResult();
            await active;
        }
        Assert.Equal("success", await active);
        await using var finalDb = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal(DatabaseBackupSetStatuses.VerifiedConfigured, (await finalDb.DatabaseBackupSets.SingleAsync()).Status);
        Assert.Equal(2, (await finalDb.DatabaseBackupCopies.SingleAsync(item => item.ProviderKey != "local")).AttemptCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledManualAndRetryUploadsRemainRetryableWithoutClaimingVerification(bool yandex)
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite", s3UploadEnabled: !yandex,
            s3ConnectionIds: yandex ? [] : ["s3-main"], yandexDiskUploadEnabled: yandex);
        using var initialCancellation = new CancellationTokenSource();
        scope.S3.OnUpload = initialCancellation.Cancel;
        scope.YandexDisk.OnUpload = initialCancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateManualBackupAsync(initialCancellation.Token));
        var overview = await service.GetOverviewAsync();
        var id = yandex ? Assert.Single(overview.YandexDiskUploadResults).Id : Assert.Single(overview.S3UploadResults).Id;
        Assert.Equal("failed", yandex ? overview.YandexDiskUploadResults[0].Status : overview.S3UploadResults[0].Status);
        Assert.Contains("отменена", yandex ? overview.YandexDiskUploadResults[0].Error : overview.S3UploadResults[0].Error);
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var set = await db.DatabaseBackupSets.Include(item => item.Copies).SingleAsync();
            Assert.Equal(DatabaseBackupSetStatuses.Degraded, set.Status);
            Assert.Equal(DatabaseBackupCopyStatuses.Verified, set.Copies.Single(item => item.ProviderKey == "local").Status);
            var cloud = set.Copies.Single(item => item.ProviderKey != "local");
            Assert.Equal(DatabaseBackupCopyStatuses.Failed, cloud.Status);
            Assert.Null(cloud.LastVerifiedAt);
            Assert.Equal(1, cloud.AttemptCount);
        }
        using var retryCancellation = new CancellationTokenSource();
        scope.S3.OnUpload = retryCancellation.Cancel;
        scope.YandexDisk.OnUpload = retryCancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (yandex) await service.RetryYandexDiskUploadAsync(id, retryCancellation.Token);
            else await service.RetryS3UploadAsync(id, retryCancellation.Token);
        });
        scope.S3.OnUpload = null;
        scope.YandexDisk.OnUpload = null;
        if (yandex) Assert.Equal("success", (await service.RetryYandexDiskUploadAsync(id)).Status);
        else Assert.Equal("success", (await service.RetryS3UploadAsync(id)).Status);
        await using var finalDb = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal(DatabaseBackupSetStatuses.VerifiedConfigured, (await finalDb.DatabaseBackupSets.SingleAsync()).Status);
        var finalCopy = await finalDb.DatabaseBackupCopies.SingleAsync(item => item.ProviderKey != "local");
        Assert.Equal(3, finalCopy.AttemptCount);
        Assert.Equal(DatabaseBackupCopyStatuses.Verified, finalCopy.Status);
        Assert.NotNull(finalCopy.LastVerifiedAt);
    }

    [Fact]
    public async Task GetCatalogAsync_ReevaluatesConfiguredDestinationsWithoutOverwritingHistory()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.CreateManualBackupAsync();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite", s3UploadEnabled: true, s3ConnectionIds: ["new-destination"]);
        Assert.Equal(DatabaseBackupSetStatuses.AwaitingCopies, Assert.Single((await service.GetCatalogAsync()).Items).Status);
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var set = await db.DatabaseBackupSets.Include(item => item.Copies).SingleAsync();
            Assert.Equal(DatabaseBackupSetStatuses.LocalVerified, set.Status);
            Assert.Equal(DatabaseBackupCopyStatuses.Verified, Assert.Single(set.Copies).Status);
        }
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite", s3UploadEnabled: true, s3ConnectionIds: []);
        Assert.Equal(DatabaseBackupSetStatuses.Degraded, Assert.Single((await service.GetCatalogAsync()).Items).Status);
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite", s3UploadEnabled: false);
        Assert.Equal(DatabaseBackupSetStatuses.LocalVerified, Assert.Single((await service.GetCatalogAsync()).Items).Status);
    }

    [Fact]
    public async Task ImportCatalogAsync_PreviewsThenImportsLegacySetWithoutClaimingCloudVerification()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(false, "03:00", 14, "sqlite", s3UploadEnabled: true,
            s3ConnectionIds: ["s3-main"], cancellationToken: CancellationToken.None);
        var backup = await service.CreateManualBackupAsync();
        await ClearBackupCatalogAsync(scope);
        var uploadCount = scope.S3.UploadedPaths.Count;
        var path = service.ResolveBackupPath(backup.Backup.RelativePath)!;
        var originalBytes = await File.ReadAllBytesAsync(path);

        var preview = await service.ImportCatalogAsync([backup.Backup.RelativePath]);
        Assert.True(preview.DryRun);
        Assert.Equal("ready", Assert.Single(preview.Items).Status);
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
            Assert.Empty(await db.DatabaseBackupSets.ToListAsync());

        var applied = await service.ImportCatalogAsync([backup.Backup.RelativePath, backup.Backup.RelativePath], false);
        Assert.Equal(new[] { "imported", "alreadyRegistered" }, applied.Items.Select(item => item.Status));
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var set = await db.DatabaseBackupSets.Include(item => item.Copies).SingleAsync();
            var copy = Assert.Single(set.Copies);
            Assert.Equal("local", copy.ProviderKey);
            Assert.Equal(DatabaseBackupCopyStatuses.Verified, copy.Status);
            Assert.Equal(backup.Backup.RelativePath, set.BackupRelativePath);
        }
        Assert.Equal(uploadCount, scope.S3.UploadedPaths.Count);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ImportCatalogAsync_ReportsInvalidSetsAndContinuesWithValidFiles()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var valid = await service.CreateManualBackupAsync();
        var missingPart = await service.CreateManualBackupAsync();
        var legacy = await service.CreateManualBackupAsync();
        await ClearBackupCatalogAsync(scope);
        File.Delete($"{service.ResolveBackupPath(missingPart.Backup.RelativePath)}.fileset");
        File.Delete($"{service.ResolveBackupPath(legacy.Backup.RelativePath)}.manifest.json");

        var result = await service.ImportCatalogAsync([
            "../outside.zip", valid.Backup.RelativePath, missingPart.Backup.RelativePath,
            legacy.Backup.RelativePath, "missing.zip"], false);
        Assert.Equal(new[] { "failed", "imported", "failed", "failed", "failed" }, result.Items.Select(item => item.Status));
        Assert.All(result.Items.Where(item => item.Status == "failed"), item => Assert.False(string.IsNullOrWhiteSpace(item.Error)));
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal(valid.Backup.RelativePath, (await db.DatabaseBackupSets.SingleAsync()).BackupRelativePath);
    }

    [Fact]
    public async Task ImportCatalogAsync_RechecksAfterPreviewAndRejectsManifestPathMismatch()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var backup = await service.CreateManualBackupAsync();
        await ClearBackupCatalogAsync(scope);
        Assert.Equal("ready", Assert.Single((await service.ImportCatalogAsync([backup.Backup.RelativePath])).Items).Status);
        var manifestPath = $"{service.ResolveBackupPath(backup.Backup.RelativePath)}.manifest.json";
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["backupRelativePath"] = "different/file.zip";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        Assert.Equal("failed", Assert.Single((await service.ImportCatalogAsync([backup.Backup.RelativePath], false)).Items).Status);
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Empty(await db.DatabaseBackupSets.ToListAsync());
    }

    [Fact]
    public async Task ImportCatalogAsync_ValidatesBatchLimitAndCancellation()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportCatalogAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportCatalogAsync(Enumerable.Repeat("file.zip", 21).ToArray()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportCatalogAsync(["file.zip"], false, cancellation.Token));
    }

    private static async Task ClearBackupCatalogAsync(TestScope scope)
    {
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        db.DatabaseBackupSets.RemoveRange(await db.DatabaseBackupSets.Include(item => item.Copies).ToListAsync());
        await db.SaveChangesAsync();
    }

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

        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var set = await db.DatabaseBackupSets.Include(item => item.Copies).SingleAsync();
            Assert.Equal(result.Backup.RelativePath, set.BackupRelativePath);
            Assert.Equal(DatabaseBackupSetStatuses.VerifiedConfigured, set.Status);
            Assert.Equal(2, set.Copies.Count);
            Assert.Contains(set.Copies, item => item.ProviderKey == "local" && item.Status == DatabaseBackupCopyStatuses.Verified);
            Assert.Contains(set.Copies, item => item.ProviderKey == "s3" && item.Status == DatabaseBackupCopyStatuses.Verified
                && item.ConnectionId == "s3-main" && item.AttemptCount == 1 && item.LastVerifiedAt.HasValue);
        }

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
        scope.S3.UploadErrorMessage = "S3 unavailable " + new string('x', 3000);
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

        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        var copy = await db.DatabaseBackupCopies.SingleAsync(item => item.ProviderKey == "s3");
        Assert.Equal(DatabaseBackupCopyStatuses.Failed, copy.Status);
        Assert.Equal(1, copy.AttemptCount);
        Assert.Null(copy.LastVerifiedAt);
        Assert.Equal(2000, copy.LastError!.Length);
        Assert.Equal(DatabaseBackupSetStatuses.Degraded, (await db.DatabaseBackupSets.SingleAsync()).Status);
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

        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var copy = await db.DatabaseBackupCopies.SingleAsync(item => item.ProviderKey == "s3");
            Assert.Equal(DatabaseBackupCopyStatuses.Verified, copy.Status);
            Assert.Equal(2, copy.AttemptCount);
            Assert.Equal(DatabaseBackupSetStatuses.VerifiedConfigured, (await db.DatabaseBackupSets.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task RetryS3UploadAsync_RejectsChangedManifestForCatalogedBackupIdentity()
    {
        await using var scope = await CreateScopeAsync();
        scope.S3.ThrowOnUpload = true;
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite",
            s3UploadEnabled: true, s3ConnectionIds: ["s3-main"], cancellationToken: CancellationToken.None);
        var created = await service.CreateManualBackupAsync();
        var failedUpload = Assert.Single(created.S3UploadResults);
        var manifestPath = $"{service.ResolveBackupPath(created.Backup.RelativePath)}.manifest.json";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<DatabaseBackupManifest>(await File.ReadAllTextAsync(manifestPath), options)!;
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest with { ApplicationVersion = "changed" }, options));
        scope.S3.ThrowOnUpload = false;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.RetryS3UploadAsync(failedUpload.Id));

        Assert.Contains("изменилось", error.Message);
        Assert.Empty(scope.S3.UploadedPaths);
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal(1, (await db.DatabaseBackupCopies.SingleAsync(item => item.ProviderKey == "s3")).AttemptCount);
    }

    [Fact]
    public async Task RetryS3UploadAsync_RejectsCorruptLocalFilePartBeforeSending()
    {
        await using var scope = await CreateScopeAsync();
        scope.S3.ThrowOnUpload = true;
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite",
            s3UploadEnabled: true, s3ConnectionIds: ["s3-main"], cancellationToken: CancellationToken.None);
        var created = await service.CreateManualBackupAsync();
        var failedUpload = Assert.Single(created.S3UploadResults);
        Assert.Equal("failed", failedUpload.Status);
        await File.AppendAllTextAsync($"{service.ResolveBackupPath(created.Backup.RelativePath)}.fileset", "corrupt");
        scope.S3.ThrowOnUpload = false;

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RetryS3UploadAsync(failedUpload.Id));

        Assert.Empty(scope.S3.UploadedPaths);
        Assert.Equal("failed", Assert.Single((await service.GetOverviewAsync()).S3UploadResults).Status);
    }

    [Fact]
    public async Task CreateManualBackupAsync_DoesNotMarkS3CopySuccessfulWhenFilePartIsRejected()
    {
        await using var scope = await CreateScopeAsync();
        scope.S3.FailResultPathSuffix = ".fileset";
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite",
            s3UploadEnabled: true, s3ConnectionIds: ["s3-main"], cancellationToken: CancellationToken.None);

        var created = await service.CreateManualBackupAsync();

        var result = Assert.Single(created.S3UploadResults);
        Assert.Equal("failed", result.Status);
        Assert.DoesNotContain(scope.S3.UploadedPaths, path => path.EndsWith(".manifest.json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".fileset")]
    [InlineData(".manifest.json")]
    public async Task CreateManualBackupAsync_DoesNotMarkCorruptS3CopyAsVerified(string corruptSuffix)
    {
        await using var scope = await CreateScopeAsync();
        scope.S3.CorruptReadPathSuffix = corruptSuffix;
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "zip",
            s3UploadEnabled: true, s3ConnectionIds: ["s3-main"], cancellationToken: CancellationToken.None);

        var created = await service.CreateManualBackupAsync();

        var failed = Assert.Single(created.S3UploadResults);
        Assert.Equal("failed", failed.Status);
        Assert.Contains("Контрольная сумма", failed.Error);
        if (corruptSuffix != ".manifest.json")
            Assert.DoesNotContain(scope.S3.UploadedPaths, path => path.EndsWith(".manifest.json", StringComparison.Ordinal));

        scope.S3.CorruptReadPathSuffix = null;
        var retry = await service.RetryS3UploadAsync(failed.Id);
        Assert.Equal("success", retry.Status);
    }

    [Fact]
    public async Task CreateManualBackupAsync_RejectsTruncatedS3Readback()
    {
        await using var scope = await CreateScopeAsync();
        scope.S3.TruncateReadPathSuffix = ".fileset";
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "zip",
            s3UploadEnabled: true, s3ConnectionIds: ["s3-main"], cancellationToken: CancellationToken.None);

        var created = await service.CreateManualBackupAsync();

        var failed = Assert.Single(created.S3UploadResults);
        Assert.Equal("failed", failed.Status);
        Assert.Contains("Размер облачной части", failed.Error);
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

        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var copy = await db.DatabaseBackupCopies.SingleAsync(item => item.ProviderKey == "yandexDisk");
            Assert.Equal(DatabaseBackupCopyStatuses.Verified, copy.Status);
            Assert.Equal(1, copy.AttemptCount);
            Assert.True(copy.LastVerifiedAt.HasValue);
        }

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
    public async Task RetryYandexDiskUploadAsync_RejectsCorruptLocalFilePartBeforeSending()
    {
        await using var scope = await CreateScopeAsync();
        scope.YandexDisk.ThrowOnUpload = true;
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite",
            yandexDiskUploadEnabled: true, cancellationToken: CancellationToken.None);
        var created = await service.CreateManualBackupAsync();
        var failedUpload = Assert.Single(created.YandexDiskUploadResults);
        Assert.Equal("failed", failedUpload.Status);
        await File.AppendAllTextAsync($"{service.ResolveBackupPath(created.Backup.RelativePath)}.fileset", "corrupt");
        scope.YandexDisk.ThrowOnUpload = false;

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RetryYandexDiskUploadAsync(failedUpload.Id));

        Assert.Empty(scope.YandexDisk.UploadedPaths);
        Assert.Equal("failed", Assert.Single((await service.GetOverviewAsync()).YandexDiskUploadResults).Status);
    }

    [Fact]
    public async Task CreateManualBackupAsync_DoesNotMarkYandexCopySuccessfulWhenManifestIsRejected()
    {
        await using var scope = await CreateScopeAsync();
        scope.YandexDisk.FailResultPathSuffix = ".manifest.json";
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite",
            yandexDiskUploadEnabled: true, cancellationToken: CancellationToken.None);

        var created = await service.CreateManualBackupAsync();

        Assert.Equal("failed", Assert.Single(created.YandexDiskUploadResults).Status);
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".fileset")]
    [InlineData(".manifest.json")]
    public async Task CreateManualBackupAsync_DoesNotMarkCorruptYandexCopyAsVerified(string corruptSuffix)
    {
        await using var scope = await CreateScopeAsync();
        scope.YandexDisk.CorruptReadPathSuffix = corruptSuffix;
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "zip",
            yandexDiskUploadEnabled: true, cancellationToken: CancellationToken.None);

        var created = await service.CreateManualBackupAsync();

        var failed = Assert.Single(created.YandexDiskUploadResults);
        Assert.Equal("failed", failed.Status);
        Assert.Contains("Контрольная сумма", failed.Error);
        if (corruptSuffix != ".manifest.json")
            Assert.DoesNotContain(scope.YandexDisk.UploadedPaths, path => path.EndsWith(".manifest.json", StringComparison.Ordinal));

        scope.YandexDisk.CorruptReadPathSuffix = null;
        var retry = await service.RetryYandexDiskUploadAsync(failed.Id);
        Assert.Equal("success", retry.Status);
    }

    [Fact]
    public async Task CreateManualBackupAsync_RejectsTruncatedYandexReadback()
    {
        await using var scope = await CreateScopeAsync();
        scope.YandexDisk.TruncateReadPathSuffix = ".fileset";
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "zip",
            yandexDiskUploadEnabled: true, cancellationToken: CancellationToken.None);

        var created = await service.CreateManualBackupAsync();

        var failed = Assert.Single(created.YandexDiskUploadResults);
        Assert.Equal("failed", failed.Status);
        Assert.Contains("Размер облачной части", failed.Error);
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

        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        var set = await db.DatabaseBackupSets.Include(item => item.Copies).SingleAsync();
        Assert.Equal(DatabaseBackupSetStatuses.NoLocalCopy, set.Status);
        Assert.Equal(DatabaseBackupCopyStatuses.Deleted, Assert.Single(set.Copies).Status);
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

        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal(2, await db.DatabaseBackupSets.CountAsync(item => item.Status == DatabaseBackupSetStatuses.NoLocalCopy));
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

        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal(DatabaseBackupCopyStatuses.Deleted,
            await db.DatabaseBackupCopies.Where(item => item.ProviderKey == "local"
                && item.Locator == first.Backup.RelativePath).Select(item => item.Status).SingleAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Retention_PreservesReplicatedSetUntilNewConfiguredCopiesSucceed(bool s3, bool yandex)
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(false, "03:00", 1, "sqlite",
            s3UploadEnabled: s3, s3ConnectionIds: ["s3-main", "s3-second"],
            yandexDiskUploadEnabled: yandex, cancellationToken: CancellationToken.None);
        var previous = await service.CreateManualBackupAsync();
        var previousPath = service.ResolveBackupPath(previous.Backup.RelativePath)!;
        File.SetLastWriteTimeUtc(previousPath, DateTime.UtcNow.AddDays(-10));
        // With both destinations enabled, only Yandex fails: S3 success alone is insufficient.
        scope.S3.ThrowOnUpload = s3 && !yandex;
        scope.YandexDisk.ThrowOnUpload = yandex;

        await service.CreateManualBackupAsync();
        await service.EnsureScheduledBackupsAsync();

        Assert.True(File.Exists(previousPath));
        Assert.True((await service.VerifyBackupAsync(previous.Backup.RelativePath)).Verified);
        scope.S3.ThrowOnUpload = false;
        scope.YandexDisk.ThrowOnUpload = false;
        await service.CreateManualBackupAsync();
        Assert.False(File.Exists(previousPath));
    }

    [Fact]
    public async Task Retention_SkipsCleanupWhenNewDestinationHasNoVerifiedCopy()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var previous = await service.CreateManualBackupAsync();
        var previousPath = service.ResolveBackupPath(previous.Backup.RelativePath)!;
        File.SetLastWriteTimeUtc(previousPath, DateTime.UtcNow.AddDays(-10));
        await service.UpdateSettingsAsync(false, "03:00", 1, "sqlite",
            s3UploadEnabled: true, s3ConnectionIds: ["s3-main"], cancellationToken: CancellationToken.None);
        scope.S3.ThrowOnUpload = true;
        await service.CreateManualBackupAsync();
        await service.EnsureScheduledBackupsAsync();
        Assert.True(File.Exists(previousPath));
    }

    [Fact]
    public async Task Retention_DoesNotTrustCloudHistoryForChangedManifest()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(false, "03:00", 1, "sqlite",
            s3UploadEnabled: true, s3ConnectionIds: ["s3-main"], cancellationToken: CancellationToken.None);
        var previous = await service.CreateManualBackupAsync();
        var previousPath = service.ResolveBackupPath(previous.Backup.RelativePath)!;
        var replicated = await service.CreateManualBackupAsync();
        var replicatedPath = service.ResolveBackupPath(replicated.Backup.RelativePath)!;
        File.SetLastWriteTimeUtc(previousPath, DateTime.UtcNow.AddDays(-11));
        File.SetLastWriteTimeUtc(replicatedPath, DateTime.UtcNow.AddDays(-10));
        // Valid JSON still verifies its parts, but is no longer the remotely verified manifest.
        await File.AppendAllTextAsync($"{replicatedPath}.manifest.json", " ");
        scope.S3.ThrowOnUpload = true;
        await service.CreateManualBackupAsync();
        Assert.True(File.Exists(previousPath));
        Assert.False(File.Exists(replicatedPath));
    }

    [Fact]
    public async Task Retention_RequiresEveryConfiguredS3Connection()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(false, "03:00", 1, "sqlite",
            s3UploadEnabled: true, s3ConnectionIds: ["s3-main", "s3-second"], cancellationToken: CancellationToken.None);
        var previous = await service.CreateManualBackupAsync();
        var previousPath = service.ResolveBackupPath(previous.Backup.RelativePath)!;
        File.SetLastWriteTimeUtc(previousPath, DateTime.UtcNow.AddDays(-10));
        scope.S3.FailedConnectionId = "s3-second";
        var next = await service.CreateManualBackupAsync();
        Assert.Contains(next.S3UploadResults, item => item.ConnectionId == "s3-main" && item.Status == "success");
        Assert.Contains(next.S3UploadResults, item => item.ConnectionId == "s3-second" && item.Status != "success");
        await service.EnsureScheduledBackupsAsync();
        Assert.True(File.Exists(previousPath));
        scope.S3.FailedConnectionId = null;
        await service.CreateManualBackupAsync();
        Assert.False(File.Exists(previousPath));
    }

    [Fact]
    public async Task Retention_SkipsCleanupForEnabledS3WithoutConnections()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var previous = await service.CreateManualBackupAsync();
        var previousPath = service.ResolveBackupPath(previous.Backup.RelativePath)!;
        File.SetLastWriteTimeUtc(previousPath, DateTime.UtcNow.AddDays(-10));
        await service.UpdateSettingsAsync(false, "03:00", 1, "sqlite",
            s3UploadEnabled: true, s3ConnectionIds: [], cancellationToken: CancellationToken.None);
        await service.CreateManualBackupAsync();
        await service.EnsureScheduledBackupsAsync();
        Assert.True(File.Exists(previousPath));
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
        Assert.Equal(0, await service.EnsureScheduledBackupsAsync());
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        var attempt = await db.DatabaseBackupAttempts.SingleAsync();
        Assert.Equal("auto", attempt.Trigger);
        Assert.NotNull(attempt.ScheduleSlot);
        Assert.Equal(DatabaseBackupAttemptStatuses.Succeeded, attempt.Status);
    }

    [Fact]
    public async Task BackupDirectoryLease_WaitsForReleaseAndHonorsCancellation()
    {
        await using var scope = await CreateScopeAsync();
        var backupRoot = Path.Combine(scope.Root, "backups");
        var first = await BackupDirectoryLease.AcquireAsync(
            backupRoot, TimeSpan.FromSeconds(2), CancellationToken.None);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BackupDirectoryLease.AcquireAsync(backupRoot, TimeSpan.FromSeconds(2), cancellation.Token));
            await Assert.ThrowsAsync<IOException>(() =>
                BackupDirectoryLease.AcquireAsync(backupRoot, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }
        finally
        {
            await first.DisposeAsync();
        }

        await using var next = await BackupDirectoryLease.AcquireAsync(
            backupRoot, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(next.CanRead);
    }

    [Fact]
    public async Task EnsureScheduledBackupsAsync_TwoServiceInstancesShareOneSlot()
    {
        await using var scope = await CreateScopeAsync();
        var first = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var second = ActivatorUtilities.CreateInstance<DatabaseBackupService>(scope.Provider);
        await first.UpdateSettingsAsync(
            automaticEnabled: true, scheduleLocal: "00:00", retentionDays: 14,
            backupFormat: "sqlite", cancellationToken: CancellationToken.None);
        var backupRoot = Path.Combine(scope.Root, "backups");
        var externalLock = await BackupDirectoryLease.AcquireAsync(
            backupRoot, TimeSpan.FromSeconds(2), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<int[]> pending;
        try
        {
            pending = Task.WhenAll(
                first.EnsureScheduledBackupsAsync(timeout.Token),
                second.EnsureScheduledBackupsAsync(timeout.Token));
            await Task.Delay(250, timeout.Token);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            await externalLock.DisposeAsync();
        }

        var counts = await pending;
        Assert.Equal(1, counts.Sum());
        var automatic = Assert.Single((await first.GetOverviewAsync()).Items, item => item.Trigger == "auto");
        Assert.True((await first.VerifyBackupAsync(automatic.RelativePath)).Verified);
    }

    [Fact]
    public async Task DeleteBackupAsync_WaitsForSharedDirectoryLock()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await service.CreateManualBackupAsync(CancellationToken.None);
        var fullPath = service.ResolveBackupPath(created.Backup.RelativePath)!;
        var backupRoot = Path.Combine(scope.Root, "backups");
        var externalLock = await BackupDirectoryLease.AcquireAsync(
            backupRoot, TimeSpan.FromSeconds(2), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<DatabaseBackupDeleteResult> pending;
        try
        {
            pending = service.DeleteBackupAsync(created.Backup.RelativePath, timeout.Token);
            await Task.Delay(250, timeout.Token);
            Assert.False(pending.IsCompleted);
            Assert.True(File.Exists(fullPath));
        }
        finally
        {
            await externalLock.DisposeAsync();
        }

        Assert.Equal(1, (await pending).DeletedCount);
        Assert.False(File.Exists(fullPath));
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

    [Fact]
    public async Task CreateManualBackupAsync_RejectsMissingConfiguredFileRootWithoutLeavingIncompleteBackup()
    {
        await using var scope = await CreateScopeAsync();
        Directory.Move(Path.Combine(scope.Root, "files"), Path.Combine(scope.Root, "files-unavailable"));
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.CreateManualBackupAsync());

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.manifest.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.zip", SearchOption.AllDirectories));
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        var attempt = await db.DatabaseBackupAttempts.SingleAsync();
        Assert.Equal(DatabaseBackupAttemptStatuses.Failed, attempt.Status);
        Assert.Equal("files", attempt.Stage);
        Assert.Null(attempt.BackupSetId);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Empty(await db.DatabaseBackupSets.ToListAsync());
    }

    [Fact]
    public async Task CreateManualBackupAsync_RejectsInaccessibleFileSubdirectoryWithoutLeavingIncompleteBackup()
    {
        var directoryReader = new FaultingBackupDirectoryReader();
        await using var scope = await CreateScopeAsync(directoryReader);
        var inaccessibleDirectory = Path.Combine(scope.Root, "files", "task-files");
        Directory.CreateDirectory(inaccessibleDirectory);
        await File.WriteAllTextAsync(Path.Combine(inaccessibleDirectory, "evidence.txt"), "must not be skipped");
        directoryReader.DeniedFilesDirectory = inaccessibleDirectory;
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();

        await Assert.ThrowsAsync<IOException>(() => service.CreateManualBackupAsync());

        Assert.Equal(1, directoryReader.DeniedFilesCount);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.manifest.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.zip", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.fileset", SearchOption.AllDirectories));
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
        public Action? OnUpload { get; set; }
        public Func<Task>? BeforeUploadAsync { get; set; }
        public bool ThrowOnUpload { get; set; }
        public string? FailedConnectionId { get; set; }
        public string UploadErrorMessage { get; set; } = "S3 unavailable";
        public string? FailResultPathSuffix { get; set; }
        public string? CorruptReadPathSuffix { get; set; }
        public string? TruncateReadPathSuffix { get; set; }
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
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
            if (BeforeUploadAsync is not null) await BeforeUploadAsync();
            OnUpload?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnUpload || id == FailedConnectionId)
            {
                throw new S3StorageOperationException(503, UploadErrorMessage);
            }

            UploadedPaths.Add(path ?? string.Empty);
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken);
            UploadedBytes = memory.Length;
            _objects[path ?? string.Empty] = memory.ToArray();
            return new S3StorageOperationResultDto
            {
                Success = FailResultPathSuffix is null || !(path?.EndsWith(FailResultPathSuffix, StringComparison.Ordinal) ?? false),
                Path = path ?? string.Empty,
                Key = path?.TrimStart('/') ?? string.Empty,
            };
        }

        public Task<S3StorageDownloadResult> DownloadAsync(string id, string? path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<S3StorageStreamDownloadResult> OpenReadAsync(string id, string? path, CancellationToken cancellationToken = default)
        {
            if (!_objects.TryGetValue(path ?? string.Empty, out var stored)) throw new FileNotFoundException();
            var bytes = stored.ToArray();
            if (bytes.Length > 0 && TruncateReadPathSuffix is not null
                && (path?.EndsWith(TruncateReadPathSuffix, StringComparison.Ordinal) ?? false)) bytes = bytes[..^1];
            if (bytes.Length > 0 && CorruptReadPathSuffix is not null
                && (path?.EndsWith(CorruptReadPathSuffix, StringComparison.Ordinal) ?? false)) bytes[0] ^= 0xff;
            var stream = new MemoryStream(bytes, writable: false);
            return Task.FromResult(new S3StorageStreamDownloadResult(stream, "application/octet-stream",
                Path.GetFileName(path ?? string.Empty), path ?? string.Empty, path ?? string.Empty, bytes.Length, stream));
        }

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
        public Action? OnUpload { get; set; }
        public Func<Task>? BeforeUploadAsync { get; set; }
        public bool ThrowOnUpload { get; set; }
        public string? FailResultPathSuffix { get; set; }
        public string? CorruptReadPathSuffix { get; set; }
        public string? TruncateReadPathSuffix { get; set; }
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
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
            if (BeforeUploadAsync is not null) await BeforeUploadAsync();
            OnUpload?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnUpload)
            {
                throw new YandexDiskOperationException(503, "Yandex Disk unavailable");
            }

            UploadedPaths.Add(path ?? string.Empty);
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken);
            UploadedBytes = memory.Length;
            _objects[path ?? string.Empty] = memory.ToArray();
            return new YandexDiskOperationResultDto
            {
                Success = FailResultPathSuffix is null || !(path?.EndsWith(FailResultPathSuffix, StringComparison.Ordinal) ?? false),
                Path = path ?? string.Empty,
            };
        }

        public Task<YandexDiskDownload> DownloadAsync(string? path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<YandexDiskStreamDownload> OpenReadAsync(string? path, CancellationToken cancellationToken = default)
        {
            if (!_objects.TryGetValue(path ?? string.Empty, out var stored)) throw new FileNotFoundException();
            var bytes = stored.ToArray();
            if (bytes.Length > 0 && TruncateReadPathSuffix is not null
                && (path?.EndsWith(TruncateReadPathSuffix, StringComparison.Ordinal) ?? false)) bytes = bytes[..^1];
            if (bytes.Length > 0 && CorruptReadPathSuffix is not null
                && (path?.EndsWith(CorruptReadPathSuffix, StringComparison.Ordinal) ?? false)) bytes[0] ^= 0xff;
            var stream = new MemoryStream(bytes, writable: false);
            return Task.FromResult(new YandexDiskStreamDownload(Path.GetFileName(path ?? string.Empty), "application/octet-stream",
                stream, bytes.Length, stream));
        }

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
