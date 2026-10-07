using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupServiceS3Tests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("foreign")]
    public async Task ManagedBackupRejectsMissingOrMismatchedCloudConnectionBeforeRead(string? connectionId)
    {
        await using var scope = await CreateScopeAsync();
        byte[] bytes = [1, 2, 3, 4];
        await scope.S3.UploadAsync("conn", "guard.bin", new MemoryStream(bytes), "application/octet-stream", false);
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var generation = new StorageObjectGeneration
            {
                Generation = 1,
                SizeBytes = bytes.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                StorageObject = new() { CurrentGeneration = 1, DataClass = "document", OwnerType = "test", OwnerId = "owned", LogicalName = "guard.bin" }
            };
            generation.Replicas.Add(new()
            {
                ProviderKey = "s3",
                ConnectionId = connectionId,
                Locator = "conn/guard.bin",
                Status = StorageReplicaStatuses.Available,
                SizeBytes = bytes.Length,
                Sha256 = generation.Sha256
            });
            db.StorageObjectGenerations.Add(generation); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Provider.GetRequiredService<DatabaseBackupService>().CreateManualBackupAsync());
        Assert.Equal(0, scope.S3.ReadCalls);
        await using var after = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal(StorageReplicaStatuses.Available, (await after.StorageReplicas.SingleAsync()).Status);
        Assert.DoesNotContain(await after.DatabaseBackupCopies.ToListAsync(), c => c.Status == DatabaseBackupCopyStatuses.Verified);
    }

    [Fact]
    public async Task RestoredFullModelQualifiesProtectedSettingsWithCopiedKeysWithoutSourceRing()
    {
        await using var scope = await CreateScopeAsync();
        var sourceProtector = scope.Provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("PayPlanner.S3Storage.Secrets.v1");
        var password = Guid.NewGuid().ToString("N");
        var sourceConfiguration = scope.Provider.GetRequiredService<IConfiguration>();
        var sourceEnvironment = scope.Provider.GetRequiredService<IWebHostEnvironment>();
        var photoPath = Path.Combine(UserPhotoStoragePaths.ResolveRoot(sourceEnvironment, sourceConfiguration), "http.png");
        var documentPath = Path.Combine(ApplicationFileStoragePaths.ResolveRoot(sourceEnvironment, sourceConfiguration), "case-documents", "http-case.txt");
        var photoBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=");
        var documentBytes = "Восстановленный документ через настоящий HTTP — Ω"u8.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(photoPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(documentPath)!);
        await File.WriteAllBytesAsync(photoPath, photoBytes);
        await File.WriteAllBytesAsync(documentPath, documentBytes);
        int documentId;
        int caseId;
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = "integrations.s3.connections",
                Value = JsonSerializer.Serialize(new[] { new { secretAccessKey = "protected:v1:" + sourceProtector.Protect("synthetic-source-secret") } })
            });
            db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
            var user = new User
            {
                Email = "restored-http@example.test",
                FullName = "Synthetic restore operator",
                IsApproved = true,
                PhotoUrl = UserPhotoStoragePaths.RequestPath + "/http.png",
                Role = new Role { Name = "admin", Permissions = [new RolePermission { Section = "cases", CanView = true, ViewScope = "all" }] },
            };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
            var clientCase = new ClientCase { Client = new Client { Name = "Synthetic HTTP case" } };
            var document = new ClientCaseDocument
            {
                ClientCase = clientCase,
                DocumentKey = "http-fixture",
                OriginalFileName = "http-case.txt",
                ContentType = "text/plain",
                StoragePath = "http-case.txt",
                SizeBytes = documentBytes.Length,
            };
            db.AddRange(user, document);
            await db.SaveChangesAsync();
            documentId = document.Id;
            caseId = clientCase.Id;
        }
        scope.Provider.GetRequiredService<IOptionsMonitor<DatabaseRestoreDrillOptions>>().CurrentValue.PrepareLocalRuntime = true;
        var created = await scope.Provider.GetRequiredService<DatabaseBackupService>().CreateManualBackupAsync();
        var report = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.True(report.Status == "restored", JsonSerializer.Serialize(report));
        File.Delete(photoPath);
        File.Delete(documentPath);
        var artifact = Path.Combine(Path.GetDirectoryName(report.ReportPath)!, "restored-storage-paths.json");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));
        var keys = Path.Combine(Path.GetDirectoryName(artifact)!, "recovery-keys");
        Directory.CreateDirectory(keys);
        var sourceKeys = Path.Combine(scope.Root, "fixture-keys");
        foreach (var file in Directory.GetFiles(sourceKeys, "key-*.xml")) File.Copy(file, Path.Combine(keys, Path.GetFileName(file)));
        Directory.Move(sourceKeys, Path.Combine(scope.Root, "detached-source-keys"));
        Assert.False(Directory.Exists(sourceKeys));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RestoredRuntimeIsolation.EnabledKey] = "true",
            [RestoredRuntimeDatabaseIdentity.ConfigurationKey] = json.RootElement.GetProperty("runtimeInstanceId").GetString(),
            [RestoredRuntimeDataProtection.ArtifactPathKey] = artifact,
            [RestoredRuntimeDataProtection.KeysPathKey] = keys,
        }).Build();
        var recovery = RestoredRuntimeDataProtection.ReadConfiguration(configuration)!;
        var services = new ServiceCollection().AddLogging();
        services.AddDataProtection().SetApplicationName(recovery.Expected.ApplicationDiscriminator!)
            .PersistKeysToFileSystem(new DirectoryInfo(keys)).DisableAutomaticKeyGeneration();
        services.AddSingleton<DataProtectionRecoveryInventory>();
        using var restoredProvider = services.BuildServiceProvider();
        var databaseHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(report.Target));
        Assert.Equal(1, await RestoredRuntimeDataProtection.VerifyAsync(restoredProvider, configuration,
            new DatabaseProviderInfo(DatabaseProviderKind.Sqlite, $"Data Source={report.Target};Pooling=False"), recovery, CancellationToken.None));
        Assert.Equal(databaseHash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(report.Target)));
        await using (var isolated = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite($"Data Source={report.Target};Pooling=False").Options))
        {
            Assert.Equal("false", (await isolated.SystemSettings.SingleAsync(row => row.Key == "notifications.email.enabled")).Value);
            Assert.Empty(await isolated.NotificationSenders.Where(row => row.Channel == "email" && row.IsEnabled).ToListAsync());
        }
        await using var backend = await RestoredBackendProcess.StartAsync(artifact, report.Target, keys);
        using var identity = await backend.Client.GetAsync(RestoredRuntimeRequestGuard.IdentityPath);
        identity.EnsureSuccessStatusCode();
        using var health = await backend.Client.GetAsync("/api/health");
        health.EnsureSuccessStatusCode();
        using var login = await backend.Client.PostAsJsonAsync("/api/auth/login", new { email = "restored-http@example.test", password });
        login.EnsureSuccessStatusCode();
        var session = await login.Content.ReadFromJsonAsync<PayPlanner.Api.Models.Responses.LoginResponse>();
        Assert.NotNull(session);
        Assert.NotEmpty(session!.Token);
        backend.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.Token);
        Assert.Equal(photoBytes, await backend.Client.GetByteArrayAsync(session.User.PhotoUrl));
        Assert.Equal(documentBytes, await backend.Client.GetByteArrayAsync($"/api/case-documents/{documentId}/download"));
        // Browser dependencies are installed in the dedicated mandatory CI integration step.
        if (Environment.GetEnvironmentVariable("PAYPLANNER_TEST_RESTORED_BROWSER") == "true")
            await backend.CheckBrowserAsync(json.RootElement.GetProperty("runtimeInstanceId").GetString()!, "restored-http@example.test", password, caseId, "http-case.txt", documentBytes);
        backend.AssertNoServerErrors();
    }

    [Fact]
    public async Task CreatedBackupPersistsPublicKeyInventoryAndRejectsMalformedMetadata()
    {
        await using var scope = await CreateScopeAsync();
        var protector = scope.Provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("synthetic-backup-secret");
        var ciphertext = protector.Protect("synthetic-only-secret");
        var inventory = scope.Provider.GetRequiredService<DataProtectionRecoveryInventory>().Capture();
        Assert.NotEmpty(inventory.KeyIds);
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var path = backups.ResolveBackupPath(created.Backup.RelativePath) + ".manifest.json";
        var json = await File.ReadAllTextAsync(path);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<DatabaseBackupManifest>(json, jsonOptions)!;
        var recorded = Assert.IsType<DataProtectionRecoveryMetadata>(manifest.DataProtectionRecovery);
        Assert.Equal(inventory.ApplicationDiscriminator, recorded.ApplicationDiscriminator);
        Assert.Equal(inventory.KeyIds, recorded.KeyIds);
        Assert.False(recorded.ContainsKeyMaterial);
        Assert.False(recorded.DecryptionVerified);
        Assert.DoesNotContain("synthetic-only-secret", json);
        Assert.DoesNotContain(ciphertext, json);
        Assert.DoesNotContain("masterKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.True((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest with
        {
            DataProtectionRecovery = recorded with { KeyIds = [Guid.Empty] }
        }, jsonOptions));
        var malformed = await backups.VerifyBackupAsync(created.Backup.RelativePath);
        Assert.False(malformed.Verified);
        Assert.Equal("invalidManifest", malformed.Status);

        // Existing backups remain verifiable but cannot claim key-recovery compatibility.
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest with { DataProtectionRecovery = null }, jsonOptions));
        Assert.True((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
        Assert.Throws<InvalidDataException>(() => DataProtectionRecoveryInventory.RequireCompatible(null, inventory));
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "native-file")]
    [InlineData(false, "managed-file")]
    [InlineData(false, "settings")]
    public async Task PreparedNativeRuntimeReadsSignaturesPhotosAndCaseDocumentsWithoutOriginals(bool legacyPhoto, string failure)
    {
        await using var scope = await CreateScopeAsync();
        scope.Provider.GetRequiredService<IOptionsMonitor<DatabaseRestoreDrillOptions>>().CurrentValue.PrepareLocalRuntime = true;
        var sourceConfig = scope.Provider.GetRequiredService<IConfiguration>();
        var sourceEnvironment = scope.Provider.GetRequiredService<IWebHostEnvironment>();
        var signaturePath = Path.Combine(ElectronicSignatureStoragePaths.ResolveRoot(sourceEnvironment, sourceConfig), "original.pdf");
        var artifactPath = Path.Combine(ElectronicSignatureStoragePaths.ResolveLegacyRoot(sourceEnvironment), "artifact.pdf");
        var photoPath = Path.Combine(legacyPhoto ? UserPhotoStoragePaths.ResolveLegacyRoot(sourceEnvironment)
            : UserPhotoStoragePaths.ResolveRoot(sourceEnvironment, sourceConfig), "photo.png");
        var casePath = Path.Combine(ApplicationFileStoragePaths.ResolveRoot(sourceEnvironment, sourceConfig), "case-documents", "case.pdf");
        var paths = new[] { signaturePath, artifactPath, photoPath, casePath };
        var payload = "перенесённые байты документа"u8.ToArray();
        foreach (var path in paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, payload);
        }
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        Guid publicId;
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var user = new User
            {
                Email = "runtime@example.test",
                PasswordHash = "test",
                Role = new Role { Name = "runtime" },
                PhotoUrl = "https://old-host.example.test/user-photos/photo.png"
            };
            var clientCase = new ClientCase { Client = new Client { Name = "Runtime" } };
            db.AddRange(user, clientCase);
            await db.SaveChangesAsync();
            var envelope = new ElectronicSignatureEnvelope
            {
                CreatedByUserId = user.Id,
                SnapshotPath = signaturePath,
                ChecksumSha256 = sha,
                SizeBytes = payload.Length,
            };
            envelope.Artifacts.Add(new ElectronicSignatureArtifact
            {
                StoragePath = artifactPath,
                ChecksumSha256 = sha,
                SizeBytes = payload.Length,
                FileName = "artifact.pdf",
            });
            db.AddRange(envelope, new ClientCaseDocument
            {
                ClientCase = clientCase,
                DocumentKey = "runtime",
                StoragePath = "case.pdf",
                SizeBytes = payload.Length,
            });
            var managed = new StorageObject { CurrentGeneration = 1, DataClass = "document" };
            var generation = new StorageObjectGeneration { Generation = 1, SizeBytes = payload.Length, Sha256 = sha };
            generation.Replicas.Add(new StorageReplica
            {
                ProviderKey = "legacy-local",
                Locator = "files/case-documents/case.pdf",
                Status = StorageReplicaStatuses.Available,
            });
            managed.Generations.Add(generation);
            db.StorageObjects.Add(managed);
            await db.SaveChangesAsync();
            publicId = envelope.PublicId;
        }
        var created = await scope.Provider.GetRequiredService<DatabaseBackupService>().CreateManualBackupAsync();
        foreach (var path in paths) File.Delete(path);
        var drillRoot = Path.Combine(scope.Root, "native-runtime-drills");
        var options = new PreparingRuntimeOptions(drillRoot, () =>
        {
            if (failure == "none") return;
            var target = Assert.Single(Directory.EnumerateDirectories(drillRoot));
            if (failure == "settings")
            {
                File.WriteAllText(Path.Combine(target, "restored-storage-paths.json"), "keep existing artifact");
                return;
            }
            var path = failure == "managed-file"
                ? Assert.Single(Directory.EnumerateFiles(Path.Combine(target, "files", "managed-generations")))
                : Assert.Single(Directory.EnumerateFiles(Path.Combine(target, "files"), "original.pdf", SearchOption.AllDirectories));
            File.WriteAllText(path, "changed after verification");
        });
        var drill = new DatabaseRestoreDrillService(scope.Provider.GetRequiredService<DatabaseBackupService>(),
            scope.Provider.GetRequiredService<DatabaseProviderInfo>(), sourceEnvironment, options, NullLogger<DatabaseRestoreDrillService>.Instance);
        var report = await drill.RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.True(options.ReadCount == 2, $"Expected preparation option read; actual {options.ReadCount}. Restore report: {JsonSerializer.Serialize(report)}");
        if (failure != "none")
        {
            Assert.Equal("failed", report.Status);
            if (failure == "settings")
                Assert.Equal("keep existing artifact", await File.ReadAllTextAsync(Path.Combine(report.Target, "restored-storage-paths.json")));
            else Assert.False(File.Exists(Path.Combine(report.Target, "restored-storage-paths.json")));
            await using var failedDb = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite($"Data Source={Path.Combine(report.Target, "restored.sqlite")};Pooling=False").Options);
            Assert.Equal(signaturePath, (await failedDb.ElectronicSignatureEnvelopes.SingleAsync()).SnapshotPath);
            Assert.Equal(artifactPath, (await failedDb.ElectronicSignatureArtifacts.SingleAsync()).StoragePath);
            Assert.Equal("https://old-host.example.test/user-photos/photo.png", (await failedDb.Users.SingleAsync()).PhotoUrl);
            Assert.Equal("legacy-local", (await failedDb.StorageReplicas.SingleAsync()).ProviderKey);
            return;
        }
        Assert.True(report.Status == "restored", JsonSerializer.Serialize(report));
        var restoredRoot = Path.GetDirectoryName(report.ReportPath)!;
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(restoredRoot, "restored-storage-paths.json")));
        Assert.False(settings.RootElement.GetProperty("completeRuntimeConfiguration").GetBoolean());
        Assert.False(settings.RootElement.GetProperty("dataProtectionRecovery").GetProperty("containsKeyMaterial").GetBoolean());
        Assert.False(settings.RootElement.GetProperty("dataProtectionRecovery").GetProperty("decryptionVerified").GetBoolean());
        var restoredConfig = new ConfigurationBuilder().AddInMemoryCollection(settings.RootElement.GetProperty("storageSettings")
            .EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString())).Build();
        await using var restored = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite($"Data Source={report.Target};Pooling=False").Options);
        var environment = new TestWebHostEnvironment(restoredRoot);
        var signatures = new ElectronicSignatureService(restored, restoredConfig, new EphemeralDataProtectionProvider(), environment);
        var package = await signatures.CreateEvidencePackageAsync(publicId, CancellationToken.None);
        Assert.NotNull(package);
        using (var archive = ZipFile.OpenRead(package!.FullPath))
        {
            foreach (var name in new[] { "original/document.pdf", "artifacts/001-artifact.pdf" })
            {
                using var input = archive.GetEntry(name)!.Open();
                using var bytes = new MemoryStream();
                await input.CopyToAsync(bytes);
                Assert.Equal(payload, bytes.ToArray());
            }
        }
        var photo = (await restored.Users.SingleAsync()).PhotoUrl;
        Assert.StartsWith(UserPhotoStoragePaths.RequestPath + "/", photo);
        Assert.Equal(payload, await File.ReadAllBytesAsync(UserPhotoStoragePaths.ResolvePhotoPath(
            UserPhotoStoragePaths.ResolveRoot(environment, restoredConfig), photo)!));
        var caseStorage = new LocalCaseDocumentStorage(environment, restoredConfig);
        Assert.Equal(payload, await File.ReadAllBytesAsync(caseStorage.ResolvePath((await restored.ClientCaseDocuments.SingleAsync()).StoragePath)));
        await using var original = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal(signaturePath, (await original.ElectronicSignatureEnvelopes.SingleAsync()).SnapshotPath);
        Assert.Equal(artifactPath, (await original.ElectronicSignatureArtifacts.SingleAsync()).StoragePath);
        Assert.Equal("https://old-host.example.test/user-photos/photo.png", (await original.Users.SingleAsync()).PhotoUrl);
    }

    private sealed class PreparingRuntimeOptions(string root, Action beforePrepare) : IOptionsMonitor<DatabaseRestoreDrillOptions>
    {
        public int ReadCount { get; private set; }
        public DatabaseRestoreDrillOptions CurrentValue
        {
            get
            {
                if (++ReadCount == 2) beforePrepare();
                return new DatabaseRestoreDrillOptions { RootDirectory = root, PrepareLocalRuntime = true };
            }
        }
        public DatabaseRestoreDrillOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<DatabaseRestoreDrillOptions, string?> listener) => null;
    }

    [Theory]
    [InlineData("legacy-local", false)]
    [InlineData("s3", false)]
    [InlineData("legacy-local", true)]
    [InlineData("s3", true)]
    public async Task RestoredGenerationReadPreservesDeletionWithoutOriginalProvider(string provider, bool deleted)
    {
        await using var scope = await CreateScopeAsync();
        scope.Provider.GetRequiredService<IOptionsMonitor<DatabaseRestoreDrillOptions>>().CurrentValue.PrepareLocalRuntime = true;
        var payload = System.Text.Encoding.UTF8.GetBytes("живой документ на другом сервере");
        var sourcePath = Path.Combine(scope.Root, "files", "live-version.bin");
        if (provider == "legacy-local") await File.WriteAllBytesAsync(sourcePath, payload);
        else
        {
            using var content = new MemoryStream(payload);
            await scope.S3.UploadAsync("conn", "live-version.bin", content, "application/octet-stream", false);
        }
        var objectId = Guid.NewGuid();
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var item = new StorageObject
            {
                Id = objectId,
                DataClass = "document",
                LogicalName = "Документ.txt",
                ContentType = "text/plain",
                CurrentGeneration = 1,
                IsDeleted = deleted,
            };
            var generation = new StorageObjectGeneration
            {
                Generation = 1,
                SizeBytes = payload.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
            };
            generation.Replicas.Add(new StorageReplica
            {
                ProviderKey = provider,
                ConnectionId = provider == "s3" ? "conn" : null,
                Locator = provider == "s3" ? "conn/live-version.bin" : "files/live-version.bin",
                Status = "available",
            });
            item.Generations.Add(generation);
            if (deleted) item.Tombstones.Add(new StorageTombstone { Generation = 1, LegalHold = true });
            db.StorageObjects.Add(item);
            db.StorageOperations.Add(new StorageOperation
            {
                StorageObject = item,
                StorageObjectGeneration = generation,
                ProviderKey = provider,
                OperationType = StorageOperationTypes.Replicate,
                Status = StorageOperationStatuses.Pending,
                IdempotencyKey = "isolated-runtime-" + objectId.ToString("N"),
            });
            await db.SaveChangesAsync();
        }
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        if (provider == "legacy-local") File.Delete(sourcePath);
        else scope.S3.CorruptReadPathSuffix = "live-version.bin";
        var report = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.Equal("restored", report.Status);
        var restoredRoot = Path.GetDirectoryName(report.Target)!;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = Path.Combine(restoredRoot, "files"),
        }).Build();
        var local = new LocalStorageProvider(new TestWebHostEnvironment(restoredRoot), configuration);
        var registry = new StorageProviderRegistry([local]);
        var monitor = new RestoreStorageOptions(new StorageOrchestrationOptions());
        await using var restoredDb = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite($"Data Source={report.Target};Pooling=False").Options);
        var service = new StorageObjectService(restoredDb, new StoragePolicyService(monitor, registry), registry, monitor);
        await using var read = await service.OpenReadAsync(objectId, CancellationToken.None);
        if (deleted) Assert.Null(read);
        else
        {
            Assert.NotNull(read);
            using var bytes = new MemoryStream();
            await read!.Content.CopyToAsync(bytes);
            Assert.Equal(payload, bytes.ToArray());
            Assert.Equal("local", read.ProviderKey);
        }
        Assert.Equal(deleted, (await restoredDb.StorageObjects.SingleAsync()).IsDeleted);
        Assert.Equal(deleted, await restoredDb.StorageTombstones.AnyAsync(item => item.LegalHold));
        Assert.Equal(StorageOperationStatuses.DeadLetter, (await restoredDb.StorageOperations.SingleAsync()).Status);
        await using var source = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal(provider, (await source.StorageReplicas.SingleAsync()).ProviderKey);
        Assert.Equal(StorageOperationStatuses.Pending, (await source.StorageOperations.SingleAsync()).Status);
    }

    private sealed class RestoreStorageOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CloudUploadDoesNotPublishManifestWhenOpenedSourceChanges(bool yandex, bool changeManifest)
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite", s3UploadEnabled: !yandex,
            s3ConnectionIds: yandex ? [] : ["s3-main"], yandexDiskUploadEnabled: yandex);
        var attempted = false;
        void ChangeSource()
        {
            if (attempted) return;
            attempted = true;
            var description = Assert.Single(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"),
                "*.manifest.json", SearchOption.AllDirectories));
            // Windows rejects this write while the selected descriptor is open.
            // On Unix it succeeds; the descriptor checksum must reject the change.
            var path = changeManifest ? description : description[..^".manifest.json".Length];
            if (changeManifest)
            {
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                var manifest = JsonSerializer.Deserialize<DatabaseBackupManifest>(File.ReadAllText(path), options)!;
                File.WriteAllText(path, JsonSerializer.Serialize(manifest with
                {
                    ApplicationVersion = "valid but unselected description during upload",
                }, options));
            }
            else File.WriteAllText(path, "changed during cloud upload");
        }
        scope.S3.OnUpload = ChangeSource;
        scope.YandexDisk.OnUpload = ChangeSource;

        var created = await service.CreateManualBackupAsync();

        Assert.True(attempted);
        Assert.Equal("failed", yandex ? Assert.Single(created.YandexDiskUploadResults).Status
            : Assert.Single(created.S3UploadResults).Status);
        var paths = yandex ? scope.YandexDisk.UploadedPaths : scope.S3.UploadedPaths;
        Assert.DoesNotContain(paths, path => path.EndsWith(".manifest.json", StringComparison.Ordinal));
        await using var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.DoesNotContain(await db.DatabaseBackupCopies.ToListAsync(),
            copy => copy.ProviderKey != "local" && copy.Status == DatabaseBackupCopyStatuses.Verified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloudUploadCannotVerifyCopyAfterCatalogManifestChanges(bool yandex)
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await service.UpdateSettingsAsync(true, "03:00", 14, "sqlite", s3UploadEnabled: !yandex,
            s3ConnectionIds: yandex ? [] : ["s3-main"], yandexDiskUploadEnabled: yandex);
        var changed = false;
        void ChangeCatalog()
        {
            if (changed) return;
            changed = true;
            using var db = scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContext();
            db.DatabaseBackupSets.ExecuteUpdate(setters => setters.SetProperty(item => item.ManifestSha256,
                new string('0', 64)));
        }
        scope.S3.OnUpload = ChangeCatalog;
        scope.YandexDisk.OnUpload = ChangeCatalog;

        var created = await service.CreateManualBackupAsync();

        Assert.True(changed);
        Assert.Equal("failed", yandex ? Assert.Single(created.YandexDiskUploadResults).Status
            : Assert.Single(created.S3UploadResults).Status);
        var stored = await service.GetOverviewAsync();
        Assert.Equal("failed", yandex ? Assert.Single(stored.YandexDiskUploadResults).Status
            : Assert.Single(stored.S3UploadResults).Status);
        await using var verify = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Contains(await verify.DatabaseBackupCopies.ToListAsync(),
            copy => copy.ProviderKey == "local" && copy.Status == DatabaseBackupCopyStatuses.Verified);
        Assert.DoesNotContain(await verify.DatabaseBackupCopies.ToListAsync(),
            copy => copy.ProviderKey != "local" && copy.Status == DatabaseBackupCopyStatuses.Verified);
    }

    [Theory]
    [InlineData("legacy-local")]
    [InlineData("s3")]
    public async Task ManagedGenerationIsBackedUpAndRestoredWithoutOriginalReplica(string provider)
    {
        await using var scope = await CreateScopeAsync();
        var payload = System.Text.Encoding.UTF8.GetBytes("удержанная версия документа");
        var sourcePath = Path.Combine(scope.Root, "files", "version.bin");
        if (provider == "legacy-local") await File.WriteAllBytesAsync(sourcePath, payload);
        else
        {
            using var content = new MemoryStream(payload);
            await scope.S3.UploadAsync("conn", "version.bin", content, "application/octet-stream", false);
        }
        var generationId = Guid.NewGuid();
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            var item = new StorageObject { IsDeleted = true, CurrentGeneration = 1, LogicalName = "document" };
            var generation = new StorageObjectGeneration
            {
                Id = generationId,
                Generation = 1,
                SizeBytes = payload.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))
            };
            generation.Replicas.Add(new StorageReplica { ProviderKey = "legacy-local", Locator = "files/missing.bin", Status = "available" });
            generation.Replicas.Add(new StorageReplica
            {
                ProviderKey = provider,
                ConnectionId = provider == "s3" ? "conn" : null,
                Locator = provider == "s3" ? "conn/version.bin" : "files/version.bin",
                Status = "available"
            });
            item.Generations.Add(generation);
            item.Tombstones.Add(new StorageTombstone { Generation = 1, LegalHold = true });
            db.StorageObjects.Add(item);
            await db.SaveChangesAsync();
        }
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        Assert.Equal(5, manifest.FormatVersion);
        var mapPart = Assert.Single(manifest.Parts!, part => part.Kind == "managed-file-bindings");
        await using (var mapping = File.OpenRead(backups.ResolveBackupPath(mapPart.RelativePath)!))
        {
            var bindings = new List<DatabaseBackupManagedBinding>();
            await foreach (var binding in DatabaseBackupManagedBindings.ReadAsync(mapping,
                manifest.Parts!.Single(part => part.Kind == "file-references").Sha256, CancellationToken.None)) bindings.Add(binding);
            var saved = Assert.Single(bindings);
            Assert.Equal(provider, saved.SelectedReplica.ProviderKey);
            Assert.True(saved.SelectedReplica.LegalHold);
            Assert.True(saved.SelectedReplica.ObjectIsDeleted);
        }
        if (provider == "legacy-local") File.Delete(sourcePath);
        else scope.S3.CorruptReadPathSuffix = "version.bin";
        var report = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.Equal("restored", report.Status);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(report.Target)!,
            "files", "managed-generations", $"{generationId:D}.bin")));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("optional")]
    [InlineData("duplicate")]
    public async Task ManagedMappingIsMandatoryForNewFormat(string corruption)
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var map = manifest.Parts!.Single(part => part.Kind == "managed-file-bindings");
        var parts = manifest.Parts!.Where(part => part != map).ToList();
        if (corruption == "optional") parts.Add(map with { Required = false });
        if (corruption == "duplicate") { parts.Add(map); parts.Add(map); }
        await File.WriteAllTextAsync(backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json",
            JsonSerializer.Serialize(manifest with { Parts = parts }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.False((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedSelectionKeepsCheckedManifestWhenDescriptionIsReplaced(bool deleteDescription)
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var expected = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var core = typeof(DatabaseBackupService).GetMethod("VerifyBackupCoreAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var operation = (Task)core.Invoke(backups, [created.Backup.RelativePath, CancellationToken.None])!;
        await operation;
        var selection = operation.GetType().GetProperty("Result")!.GetValue(operation)!;
        var verification = (DatabaseBackupVerificationResult)selection.GetType().GetProperty("Verification")!.GetValue(selection)!;
        Assert.True(verification.Verified);

        var manifestPath = backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json";
        Assert.True(File.Exists(manifestPath));
        var checkedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(manifestPath))).ToLowerInvariant();
        if (deleteDescription) File.Delete(manifestPath);
        else await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(expected with
        {
            BackupId = "unverified replacement",
            RecoveryKeyReference = "unverified key",
            Parts = []
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var selected = (DatabaseBackupManifest)selection.GetType().GetProperty("Manifest")!.GetValue(selection)!;
        Assert.Equal(expected.BackupId, selected.BackupId);
        Assert.Equal(expected.RecoveryKeyReference, selected.RecoveryKeyReference);
        Assert.Equal(JsonSerializer.Serialize(expected.Parts), JsonSerializer.Serialize(selected.Parts));
        Assert.Equal(checkedHash, selection.GetType().GetProperty("ManifestSha256")!.GetValue(selection));
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.ReadVerifiedManifestAsync(created.Backup.RelativePath));
    }

    [Fact]
    public async Task FailedVerificationNeverExposesSelectedManifest()
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        await File.AppendAllTextAsync(backups.ResolveBackupPath(created.Backup.RelativePath)!, "corruption");
        var core = typeof(DatabaseBackupService).GetMethod("VerifyBackupCoreAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var operation = (Task)core.Invoke(backups, [created.Backup.RelativePath, CancellationToken.None])!;
        await operation;
        var selection = operation.GetType().GetProperty("Result")!.GetValue(operation)!;
        var verification = (DatabaseBackupVerificationResult)selection.GetType().GetProperty("Verification")!.GetValue(selection)!;
        Assert.False(verification.Verified);
        Assert.Null(selection.GetType().GetProperty("Manifest")!.GetValue(selection));
        Assert.Null(selection.GetType().GetProperty("ManifestSha256")!.GetValue(selection));
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.ReadVerifiedManifestAsync(created.Backup.RelativePath));
    }

    [Fact]
    public async Task CatalogStoresChecksumOfCheckedDescriptionNotLaterReplacement()
    {
        await using var scope = await CreateScopeAsync();
        var factory = scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var path = backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json";
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.DatabaseBackupSets.RemoveRange(await db.DatabaseBackupSets.ToListAsync());
            await db.SaveChangesAsync();
        }
        var changed = false;
        var replacing = new ReplacingContextFactory(factory, () =>
        {
            if (changed) return;
            changed = true;
            File.WriteAllText(path, JsonSerializer.Serialize(manifest with { ApplicationVersion = "later unselected description" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        });
        var service = new DatabaseBackupService(replacing, scope.Provider.GetRequiredService<IConfiguration>(),
            scope.Provider.GetRequiredService<DatabaseProviderInfo>(), NullLogger<DatabaseBackupService>.Instance,
            scope.Provider.GetRequiredService<IServiceScopeFactory>());
        var imported = await service.ImportCatalogAsync([created.Backup.RelativePath], false);
        Assert.Equal("imported", Assert.Single(imported.Items).Status);
        Assert.True(changed);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(expectedHash, (await db.DatabaseBackupSets.SingleAsync()).ManifestSha256);
        Assert.NotEqual(expectedHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant());
        Assert.Equal("failed", Assert.Single((await service.ImportCatalogAsync([created.Backup.RelativePath], false)).Items).Status);
    }

    private sealed class ReplacingContextFactory(IDbContextFactory<PaymentContext> inner, Action onCreate) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext() { onCreate(); return inner.CreateDbContext(); }
        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        { onCreate(); return inner.CreateDbContextAsync(cancellationToken); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloudRetryRejectsDescriptionReplacedAfterCatalogVerification(bool yandex)
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await backups.UpdateSettingsAsync(true, "03:00", 14, "sqlite", s3UploadEnabled: !yandex,
            s3ConnectionIds: yandex ? [] : ["s3-main"], yandexDiskUploadEnabled: yandex);
        scope.S3.ThrowOnUpload = true;
        scope.YandexDisk.ThrowOnUpload = true;
        var created = await backups.CreateManualBackupAsync();
        var id = yandex ? Assert.Single(created.YandexDiskUploadResults).Id : Assert.Single(created.S3UploadResults).Id;
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var path = backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json";
        var factory = scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>();
        string originalSha;
        await using (var db = await factory.CreateDbContextAsync())
            originalSha = (await db.DatabaseBackupSets.SingleAsync()).ManifestSha256;
        var calls = 0;
        var replacing = new ReplacingContextFactory(factory, () =>
        {
            // First context loads retry history; second registers the already checked description.
            if (++calls != 2) return;
            File.WriteAllText(path, JsonSerializer.Serialize(manifest with { ApplicationVersion = "different valid description" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        });
        var service = new DatabaseBackupService(replacing, scope.Provider.GetRequiredService<IConfiguration>(),
            scope.Provider.GetRequiredService<DatabaseProviderInfo>(), NullLogger<DatabaseBackupService>.Instance,
            scope.Provider.GetRequiredService<IServiceScopeFactory>());
        scope.S3.ThrowOnUpload = false;
        scope.YandexDisk.ThrowOnUpload = false;
        var status = yandex ? (await service.RetryYandexDiskUploadAsync(id)).Status : (await service.RetryS3UploadAsync(id)).Status;

        Assert.Equal("failed", status);
        Assert.True(calls >= 2);
        var paths = yandex ? scope.YandexDisk.UploadedPaths : scope.S3.UploadedPaths;
        Assert.Empty(paths);
        await using var restoredCatalog = await factory.CreateDbContextAsync();
        Assert.Equal(originalSha, (await restoredCatalog.DatabaseBackupSets.SingleAsync()).ManifestSha256);
        Assert.DoesNotContain(await restoredCatalog.DatabaseBackupCopies.ToListAsync(),
            copy => copy.ProviderKey != "local" && copy.Status == DatabaseBackupCopyStatuses.Verified);
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("identity")]
    [InlineData("hash")]
    public async Task NativeBindingMismatchCannotBeHiddenByFreshArtifactChecksum(string corruption)
    {
        await using var scope = await CreateScopeAsync();
        var photoDirectory = Path.Combine(scope.Root, "files", "user-photos");
        Directory.CreateDirectory(photoDirectory);
        await File.WriteAllTextAsync(Path.Combine(photoDirectory, "photo.png"), "photo payload");
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            db.Users.Add(new User
            {
                Email = "binding-check@local.test",
                PasswordHash = "test",
                Role = new Role { Name = "binding-check" },
                PhotoUrl = "/user-photos/photo.png"
            });
            await db.SaveChangesAsync();
        }
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "native-file-bindings");
        var path = backups.ResolveBackupPath(part.RelativePath)!;
        var lines = (await File.ReadAllTextAsync(path)).Split('\n');
        var binding = System.Text.Json.Nodes.JsonNode.Parse(lines[1])!;
        if (corruption == "entry") binding["archiveEntryPath"] = "file-storage/user-photos/missing.png";
        else if (corruption == "identity") binding["reference"]!["sourceId"] = 999999;
        else binding["actualSha256"] = new string('a', 64);
        lines[1] = binding.ToJsonString();
        await File.WriteAllTextAsync(path, string.Join('\n', lines));
        var changed = part with
        {
            SizeBytes = new FileInfo(path).Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)))
        };
        await File.WriteAllTextAsync(backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json",
            JsonSerializer.Serialize(manifest with { Parts = manifest.Parts!.Select(item => item == part ? changed : item).ToArray() },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var verification = await backups.VerifyBackupAsync(created.Backup.RelativePath);
        Assert.False(verification.Verified);
        Assert.Equal("mismatch", verification.Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None));
    }

    [Fact]
    public async Task NativeBindingRestoresPhotoAfterSourceStorageConfigurationChanges()
    {
        await using var scope = await CreateScopeAsync();
        var originalPhoto = Path.Combine(scope.Root, "files", "user-photos", "photo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(originalPhoto)!);
        await File.WriteAllTextAsync(originalPhoto, "portable photo bytes");
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            db.Users.Add(new User
            {
                Email = "portable-photo@local.test",
                PasswordHash = "test",
                Role = new Role { Name = "portable" },
                PhotoUrl = "/user-photos/photo.png"
            });
            await db.SaveChangesAsync();
        }
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        Assert.Equal(5, manifest.FormatVersion);
        Assert.True(Assert.Single(manifest.Parts!, part => part.Kind == "native-file-bindings").Required);
        File.Delete(originalPhoto);
        scope.Provider.GetRequiredService<IConfiguration>()[ApplicationFileStoragePaths.RootPathConfigurationKey]
            = Path.Combine(scope.Root, "empty-new-storage");
        var report = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.Equal("restored", report.Status);
        Assert.Equal("portable photo bytes", await File.ReadAllTextAsync(Path.Combine(
            Path.GetDirectoryName(report.Target)!, "files", "file-storage", "user-photos", "photo.png")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("optional")]
    [InlineData("duplicate")]
    public async Task NativeBindingArtifactIsMandatoryAndUnique(string corruption)
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "native-file-bindings");
        if (corruption == "missing") File.Delete(backups.ResolveBackupPath(part.RelativePath)!);
        else
        {
            var parts = corruption == "duplicate" ? manifest.Parts!.Append(part).ToArray()
                : manifest.Parts!.Select(item => item == part ? part with { Required = false } : item).ToArray();
            await File.WriteAllTextAsync(backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json",
                JsonSerializer.Serialize(manifest with { Parts = parts }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        Assert.False((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None));
    }

    [Fact]
    public async Task RefusedArchivePublicationDoesNotDeleteForeignDestination()
    {
        var reader = new FaultingBackupDirectoryReader();
        await using var scope = await CreateScopeAsync(reader);
        string? foreignPath = null;
        reader.OnFilesRead = directory =>
        {
            if (foreignPath is not null || directory != Path.Combine(scope.Root, "files")) return;
            var dump = Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*", SearchOption.AllDirectories)
                .Single(path => path.EndsWith(".sqlite", StringComparison.Ordinal) || path.EndsWith(".zip", StringComparison.Ordinal));
            foreignPath = dump + ".fileset";
            File.WriteAllText(foreignPath, "foreign destination must survive");
        };
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await Assert.ThrowsAsync<IOException>(() => backups.CreateManualBackupAsync());
        Assert.NotNull(foreignPath);
        Assert.Equal("foreign destination must survive", await File.ReadAllTextAsync(foreignPath!));
        Assert.Empty((await backups.GetOverviewAsync()).Items);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.references", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePhotoMustBePresentBeforeBackupIsPublished(bool present)
    {
        await using var scope = await CreateScopeAsync();
        var photoDirectory = Path.Combine(scope.Root, "files", "user-photos");
        Directory.CreateDirectory(photoDirectory);
        if (present) await File.WriteAllTextAsync(Path.Combine(photoDirectory, "photo.png"), "photo evidence");
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            db.Users.Add(new User
            {
                Email = "photo-coverage@local.test",
                PasswordHash = "test",
                Role = new Role { Name = "coverage" },
                PhotoUrl = "/user-photos/photo.png"
            });
            await db.SaveChangesAsync();
        }
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        if (present)
        {
            var created = await backups.CreateManualBackupAsync();
            Assert.True((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => backups.CreateManualBackupAsync());
            Assert.Contains("отсутствует", error.Message, StringComparison.Ordinal);
            Assert.Empty((await backups.GetOverviewAsync()).Items);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.references", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.fileset", SearchOption.AllDirectories));
        }
        await using var original = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal("/user-photos/photo.png", (await original.Users.SingleAsync()).PhotoUrl);
    }

    [Fact]
    public async Task NativePhotoRemovedAfterDatabaseSnapshotRejectsNewSetAndKeepsEarlierBackup()
    {
        var reader = new FaultingBackupDirectoryReader();
        await using var scope = await CreateScopeAsync(reader);
        var photoDirectory = Path.Combine(scope.Root, "files", "user-photos");
        Directory.CreateDirectory(photoDirectory);
        var photoPath = Path.Combine(photoDirectory, "photo.png");
        await File.WriteAllTextAsync(photoPath, "photo evidence");
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            db.Users.Add(new User
            {
                Email = "photo-concurrent@local.test",
                PasswordHash = "test",
                Role = new Role { Name = "photo-concurrent" },
                PhotoUrl = "/user-photos/photo.png"
            });
            await db.SaveChangesAsync();
        }
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var earlier = await backups.CreateManualBackupAsync();
        Assert.True((await backups.VerifyBackupAsync(earlier.Backup.RelativePath)).Verified);
        var removedDuringArchive = false;
        reader.OnFilesRead = directory =>
        {
            if (removedDuringArchive || !string.Equals(directory, photoDirectory, StringComparison.OrdinalIgnoreCase)) return;
            File.Delete(photoPath);
            removedDuringArchive = true;
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => backups.CreateManualBackupAsync());

        Assert.True(removedDuringArchive);
        Assert.True((await backups.VerifyBackupAsync(earlier.Backup.RelativePath)).Verified);
        Assert.Single((await backups.GetOverviewAsync()).Items);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.references", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NativePhotoOpenForMutationRejectsNewSetAndKeepsEarlierBackup()
    {
        await using var scope = await CreateScopeAsync();
        var photoDirectory = Path.Combine(scope.Root, "files", "user-photos");
        Directory.CreateDirectory(photoDirectory);
        var photoPath = Path.Combine(photoDirectory, "photo.png");
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(photoPath, bytes);
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            db.Users.Add(new User
            {
                Email = "photo-mutation@local.test",
                PasswordHash = "test",
                Role = new Role { Name = "photo-mutation" },
                PhotoUrl = "/user-photos/photo.png"
            });
            await db.SaveChangesAsync();
        }
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var earlier = await backups.CreateManualBackupAsync();
        // Windows enforces write/delete sharing independently. On Unix, an
        // exclusive .NET handle exercises the supported sharing conflict;
        // native Unix writers still require separate qualification.
        var writerSharing = OperatingSystem.IsWindows() ? FileShare.ReadWrite | FileShare.Delete : FileShare.None;
        await using (var writer = new FileStream(photoPath, FileMode.Open, FileAccess.ReadWrite, writerSharing))
            await Assert.ThrowsAsync<IOException>(() => backups.CreateManualBackupAsync());

        Assert.Equal(bytes, await File.ReadAllBytesAsync(photoPath));
        Assert.True((await backups.VerifyBackupAsync(earlier.Backup.RelativePath)).Verified);
        Assert.Single((await backups.GetOverviewAsync()).Items);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.fileset", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.Root, "backups"), "*.tmp", SearchOption.AllDirectories));
        var retry = await backups.CreateManualBackupAsync();
        Assert.True((await backups.VerifyBackupAsync(retry.Backup.RelativePath)).Verified);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("version")]
    public async Task RestoreRejectsInventoryFactsThatDifferFromDatabaseEvenWithFreshChecksums(string corruption)
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync())
        {
            db.Users.Add(new User
            {
                Email = "restore-compare@local.test",
                PasswordHash = "test",
                Role = new Role { Name = "compare" },
                PhotoUrl = "https://external.example.test/original.png"
            });
            await db.SaveChangesAsync();
        }
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "file-references");
        var path = backups.ResolveBackupPath(part.RelativePath)!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var files = node["storedFiles"]!.AsArray();
        Assert.Single(files);
        switch (corruption)
        {
            case "path": files[0]!["storedPath"] = "https://external.example.test/changed.png"; break;
            case "version": files[0]!["version"] = 99; break;
            case "missing": files.Clear(); node["storedFileCount"] = 0; break;
            default:
                var extra = files[0]!.DeepClone();
                extra["sourceId"] = 999999;
                files.Add(extra);
                node["storedFileCount"] = 2;
                break;
        }
        await File.WriteAllTextAsync(path, node.ToJsonString());
        var changedPart = part with
        {
            SizeBytes = new FileInfo(path).Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)))
        };
        await File.WriteAllTextAsync(backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json",
            // Historical v3 has an inventory but no binding artifact. Keep exercising
            // the restored-database boundary independently of the new v4 preflight.
            JsonSerializer.Serialize(manifest with
            {
                FormatVersion = 3,
                Parts = manifest.Parts!
                .Where(item => item.Kind is not ("native-file-bindings" or "managed-file-bindings")).Select(item => item == part ? changedPart : item).ToArray()
            },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
        var report = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.Equal("failed", report.Status);
        Assert.Contains("не соответствует", report.Error!, StringComparison.Ordinal);
        await using var original = await scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>().CreateDbContextAsync();
        Assert.Equal("https://external.example.test/original.png", (await original.Users.SingleAsync()).PhotoUrl);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("count")]
    [InlineData("version")]
    public async Task SnapshotInventorySchemaCannotBeBypassedWithUpdatedPartChecksum(string corruption)
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "file-references");
        var path = backups.ResolveBackupPath(part.RelativePath)!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        if (corruption == "missing") node.AsObject().Remove("storedFiles");
        else if (corruption == "count") node["storedFileCount"] = 1;
        else node["formatVersion"] = 2;
        await File.WriteAllTextAsync(path, node.ToJsonString());
        var changedPart = part with
        {
            SizeBytes = new FileInfo(path).Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)))
        };
        await File.WriteAllTextAsync(backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json",
            JsonSerializer.Serialize(manifest with { Parts = manifest.Parts!.Select(item => item == part ? changedPart : item).ToArray() },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var verification = await backups.VerifyBackupAsync(created.Backup.RelativePath);
        Assert.False(verification.Verified);
        Assert.Equal("mismatch", verification.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HistoricalFileArchivesWithoutEntryHashesStillRestore(int version)
    {
        await using var scope = await CreateScopeAsync();
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "files", "legacy.txt"), "legacy");
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "file-storage");
        var path = backups.ResolveBackupPath(part.RelativePath)!;
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
            foreach (var entry in archive.Entries) entry.Comment = string.Empty;
        var historicalPart = part with
        {
            FileEntryHashFormat = null,
            SizeBytes = new FileInfo(path).Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant()
        };
        var historical = manifest with
        {
            FormatVersion = version,
            Parts = manifest.Parts!.Where(item => item.Kind is not ("native-file-bindings" or "managed-file-bindings") && (version != 1 || item.Kind != "file-references"))
                .Select(item => item == part ? historicalPart : item).ToArray()
        };
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(historical,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        foreach (var item in node["parts"]!.AsArray()) item!.AsObject().Remove("fileEntryHashFormat");
        await File.WriteAllTextAsync(backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json", node.ToJsonString());
        Assert.True((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
        var report = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.Equal("restored", report.Status);
        Assert.Equal("legacy", await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(report.Target)!, "files", "file-storage", "legacy.txt")));
    }

    [Fact]
    public async Task ArchiveEntriesDeclareExactBinaryHashAndRestoreBytes()
    {
        await using var scope = await CreateScopeAsync();
        var bytes = Enumerable.Range(0, 131073).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(scope.Root, "files", "binary.bin"), bytes);
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "file-storage");
        Assert.Equal(DatabaseBackupArchiveHash.Format, part.FileEntryHashFormat);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(backups.ResolveBackupPath(part.RelativePath)!))
        {
            var entry = Assert.Single(archive.Entries);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                DatabaseBackupArchiveHash.FromComment(entry.Comment));
        }
        var report = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.Equal("restored", report.Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(report.Target)!, "files", "file-storage", "binary.bin")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("payplanner-sha256-v1:short")]
    [InlineData("payplanner-sha256-v1:0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task FileChecksumMetadataCannotBeHiddenByRefreshingOuterChecksum(string comment)
    {
        await using var scope = await CreateScopeAsync();
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "files", "evidence.txt"), "original");
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "file-storage");
        var path = backups.ResolveBackupPath(part.RelativePath)!;
        var timestamp = File.GetLastWriteTimeUtc(path);
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
            Assert.Single(archive.Entries).Comment = comment;
        File.SetLastWriteTimeUtc(path, timestamp);
        var changedPart = part with
        {
            SizeBytes = new FileInfo(path).Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant()
        };
        Assert.NotEqual(part.Sha256, changedPart.Sha256);
        await File.WriteAllTextAsync(backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json",
            JsonSerializer.Serialize(manifest with { Parts = manifest.Parts!.Select(item => item == part ? changedPart : item).ToArray() },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.False((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreDetectsChangedArchiveAfterPreflightEvenWithUpdatedEntryHash(bool updateEntryHash)
    {
        await using var scope = await CreateScopeAsync();
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "files", "evidence.txt"), "original");
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "file-storage");
        var path = backups.ResolveBackupPath(part.RelativePath)!;
        var changed = false;
        var settings = new CallbackDrillOptions(Path.Combine(scope.Root, "race-drills"), () =>
        {
            if (changed) return;
            changed = true;
            using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update);
            var entry = Assert.Single(archive.Entries);
            var bytes = System.Text.Encoding.UTF8.GetBytes("tampered");
            using (var output = entry.Open())
            {
                output.SetLength(0);
                output.Write(bytes);
            }
            if (updateEntryHash) entry.Comment = DatabaseBackupArchiveHash.ToComment(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        });
        var drill = new DatabaseRestoreDrillService(backups, scope.Provider.GetRequiredService<DatabaseProviderInfo>(),
            scope.Provider.GetRequiredService<IWebHostEnvironment>(), settings, NullLogger<DatabaseRestoreDrillService>.Instance);
        var report = await drill.RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        Assert.True(changed);
        Assert.Equal("failed", report.Status);
        Assert.Contains("SHA-256", report.Error!, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(report.Target, "files", "file-storage", "evidence.txt")));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(scope.Root, "files", "evidence.txt")));
    }

    [Theory]
    [InlineData("missing-format")]
    [InlineData("unknown-format")]
    [InlineData("optional")]
    [InlineData("wrong-kind")]
    public async Task NewManifestCannotBypassFileEntryHashDeclaration(string corruption)
    {
        await using var scope = await CreateScopeAsync();
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "files", "evidence.txt"), "original");
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "file-storage");
        var corruptedPart = corruption switch
        {
            "missing-format" => part with { FileEntryHashFormat = null },
            "unknown-format" => part with { FileEntryHashFormat = "unknown" },
            "optional" => part with { Required = false },
            _ => part with { Kind = "unexpected" }
        };
        await File.WriteAllTextAsync(backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json",
            JsonSerializer.Serialize(manifest with { Parts = manifest.Parts!.Select(item => item == part ? corruptedPart : item).ToArray() },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var verification = await backups.VerifyBackupAsync(created.Backup.RelativePath);
        Assert.False(verification.Verified);
        Assert.Equal("invalidManifest", verification.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedEntryExtractionRemovesOnlyItsOwnFile(bool existingTarget)
    {
        await using var scope = await CreateScopeAsync();
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "files", "evidence.txt"), "original");
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "file-storage");
        var path = backups.ResolveBackupPath(part.RelativePath)!;
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
            Assert.Single(archive.Entries).Comment = DatabaseBackupArchiveHash.ToComment(new string('0', 64));
        var changedPart = part with
        {
            SizeBytes = new FileInfo(path).Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)))
        };
        var targetRoot = Path.Combine(scope.Root, "entry-drill");
        var targetFile = Path.Combine(targetRoot, "files", "file-storage", "evidence.txt");
        if (existingTarget)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            await File.WriteAllTextAsync(targetFile, "foreign");
        }
        // Exercise the post-preflight extraction boundary directly: the public verifier
        // correctly rejects this archive before reaching the partial-file cleanup branch.
        var method = typeof(DatabaseRestoreDrillService).GetMethod("RestoreFilePartsAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var drill = scope.Provider.GetRequiredService<DatabaseRestoreDrillService>();
        var task = (Task)method.Invoke(drill,
            [manifest with { Parts = [changedPart] }, targetRoot, CancellationToken.None])!;
        if (existingTarget)
        {
            await Assert.ThrowsAnyAsync<IOException>(() => task);
            Assert.Equal("foreign", await File.ReadAllTextAsync(targetFile));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => task);
            Assert.False(File.Exists(targetFile));
        }
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(scope.Root, "files", "evidence.txt")));
    }

    [Fact]
    public async Task HistoricalVersionOneManifestWithoutInventoryRemainsVerifiable()
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var historical = manifest with
        {
            FormatVersion = 1,
            Parts = manifest.Parts!.Where(item => item.Kind is not ("file-references" or "native-file-bindings" or "managed-file-bindings")).ToArray()
        };
        var path = backups.ResolveBackupPath(created.Backup.RelativePath)!;
        await File.WriteAllTextAsync(path + ".manifest.json",
            JsonSerializer.Serialize(historical, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.Delete(path + ".references");
        Assert.True((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("optional")]
    [InlineData("duplicate")]
    [InlineData("wrong-path")]
    public async Task NewManifestRequiresExactlyOneMandatorySnapshotInventory(string corruption)
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var created = await backups.CreateManualBackupAsync();
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        Assert.Equal(5, manifest.FormatVersion);
        var reference = Assert.Single(manifest.Parts!, item => item.Kind == "file-references");
        var parts = manifest.Parts!.Where(item => item.Kind != "file-references").ToList();
        if (corruption != "absent") parts.Add(corruption switch
        {
            "optional" => reference with { Required = false },
            "wrong-path" => reference with { RelativePath = created.Backup.RelativePath },
            _ => reference
        });
        if (corruption == "duplicate") parts.Add(reference);
        var manifestPath = backups.ResolveBackupPath(created.Backup.RelativePath)! + ".manifest.json";
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest with { Parts = parts }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var verification = await backups.VerifyBackupAsync(created.Backup.RelativePath);
        Assert.False(verification.Verified);
        Assert.Equal("invalidManifest", verification.Status);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("zip")]
    public async Task CreatedBackupIncludesRequiredInventoryFromCopiedDatabase(string format)
    {
        await using var scope = await CreateScopeAsync();
        var factory = scope.Provider.GetRequiredService<IDbContextFactory<PaymentContext>>();
        int userId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var user = new User
            {
                Email = "snapshot@local.test",
                PasswordHash = "test",
                Role = new Role { Name = "snapshot" },
                PhotoUrl = "https://external.example.test/original.png"
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        await backups.UpdateSettingsAsync(false, "03:00", 14, format, cancellationToken: CancellationToken.None);
        var created = await backups.CreateManualBackupAsync();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var user = await db.Users.SingleAsync(item => item.Id == userId);
            user.PhotoUrl = "https://external.example.test/new.png";
            await db.SaveChangesAsync();
        }
        var manifest = await backups.ReadVerifiedManifestAsync(created.Backup.RelativePath);
        var part = Assert.Single(manifest.Parts!, item => item.Kind == "file-references");
        Assert.True(part.Required);
        var inventoryPath = backups.ResolveBackupPath(part.RelativePath)!;
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(inventoryPath));
        Assert.Equal(1, inventory.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(1L, inventory.RootElement.GetProperty("storedFileCount").GetInt64());
        Assert.Equal("https://external.example.test/original.png",
            Assert.Single(inventory.RootElement.GetProperty("storedFiles").EnumerateArray())
                .GetProperty("storedPath").GetString());
        var drill = await scope.Provider.GetRequiredService<DatabaseRestoreDrillService>()
            .RunAsync(created.Backup.RelativePath, false, null, CancellationToken.None);
        await using (var restored = new SqliteConnection($"Data Source={drill.Target};Mode=ReadOnly;Pooling=False"))
        {
            await restored.OpenAsync();
            await using var command = restored.CreateCommand();
            command.CommandText = "SELECT PhotoUrl FROM Users WHERE Id = $id";
            command.Parameters.AddWithValue("$id", userId);
            Assert.Equal("https://external.example.test/original.png", await command.ExecuteScalarAsync());
        }
        await File.AppendAllTextAsync(inventoryPath, "corrupt");
        Assert.False((await backups.VerifyBackupAsync(created.Backup.RelativePath)).Verified);
    }

    [Fact]
    public async Task RepeatedBackupNamesAtSameInstantAreUniqueAndKeepScheduleAndExtension()
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var method = typeof(DatabaseBackupService).GetMethod("BuildBackupFileName",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var instant = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 1000; index++)
        {
            var name = (string)method.Invoke(backups, ["crm", instant, "auto", "0900", ".backup"])!;
            Assert.True(names.Add(name));
            Assert.EndsWith("_auto_0900.backup", name, StringComparison.Ordinal);
            Assert.EndsWith(".backup", name, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileNameWithoutExtension(name).Split('_'), part => Guid.TryParseExact(part, "N", out _));
        }
    }

    [Fact]
    public async Task RepeatedManualBackupsPreserveEarlierVerifiedBytes()
    {
        await using var scope = await CreateScopeAsync();
        var backups = scope.Provider.GetRequiredService<DatabaseBackupService>();
        var first = await backups.CreateManualBackupAsync();
        var firstPath = backups.ResolveBackupPath(first.Backup.RelativePath)!;
        var originalBytes = await File.ReadAllBytesAsync(firstPath);
        var second = await backups.CreateManualBackupAsync();
        Assert.NotEqual(first.Backup.RelativePath, second.Backup.RelativePath);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(firstPath));
        Assert.True((await backups.VerifyBackupAsync(first.Backup.RelativePath)).Verified);
        Assert.True((await backups.VerifyBackupAsync(second.Backup.RelativePath)).Verified);
    }

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
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long leaseHoldMilliseconds = -1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<int>? firstAttempt = null, secondAttempt = null;
        Task<int[]> pending;
        try
        {
            firstAttempt = first.EnsureScheduledBackupsAsync(timeout.Token);
            secondAttempt = second.EnsureScheduledBackupsAsync(timeout.Token);
            pending = Task.WhenAll(firstAttempt, secondAttempt);
            await Task.Delay(250, timeout.Token);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            await externalLock.DisposeAsync();
            leaseHoldMilliseconds = clock.ElapsedMilliseconds;
            WriteSlotBoundary("external-released");
        }

        int[] counts;
        try { counts = await pending; }
        catch
        {
            WriteSlotBoundary("pending-await-failed");
            throw;
        }
        Assert.Equal(1, counts.Sum());
        var automatic = Assert.Single((await first.GetOverviewAsync()).Items, item => item.Trigger == "auto");
        Assert.True((await first.VerifyBackupAsync(automatic.RelativePath)).Verified);
        WriteSlotBoundary("verified");

        void WriteSlotBoundary(string phase)
        {
            // Test-only bounded timing/status fields; no paths, settings,
            // backup contents, identifiers or exception text are serialized.
            Console.WriteLine("PP_BACKUP_SLOT_BOUNDARY " + JsonSerializer.Serialize(new
            {
                Phase = phase,
                ElapsedMilliseconds = Math.Min(clock.ElapsedMilliseconds, 3_600_000),
                LeaseHoldMilliseconds = Math.Min(leaseHoldMilliseconds, 3_600_000),
                TimeoutCancelled = timeout.IsCancellationRequested,
                FirstTaskStatus = firstAttempt is null ? -1 : (int)firstAttempt.Status,
                SecondTaskStatus = secondAttempt is null ? -1 : (int)secondAttempt.Status,
            }));
        }
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
        // Disposable fixture databases must not keep pooled file handles alive or
        // clear pools owned by unrelated parallel tests during cleanup.
        var connectionString = $"Data Source={dbPath};Pooling=False";
        // The production normalizer deliberately enables pooling; override it
        // afterwards for this disposable fixture, not in the product defaults.
        var fixtureConnection = new SqliteConnectionStringBuilder(DatabaseProviderResolver.NormalizeSqliteConnection(connectionString)) { Pooling = false };
        Assert.False(fixtureConnection.Pooling);
        var providerInfo = new DatabaseProviderInfo(DatabaseProviderKind.Sqlite, fixtureConnection.ConnectionString);
        var s3 = new FakeS3StorageService();
        var yandexDisk = new FakeYandexDiskService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("PayPlanner.BackupSyntheticFixture")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "fixture-keys")));
        services.AddSingleton<DataProtectionRecoveryInventory>();
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
        services.AddScoped<PayPlanner.Api.Services.Storage.IStorageProvider, PayPlanner.Api.Services.Storage.S3StorageProvider>();
        services.AddScoped<PayPlanner.Api.Services.Storage.IStorageProvider, PayPlanner.Api.Services.Storage.YandexDiskStorageProvider>();
        services.AddScoped<PayPlanner.Api.Services.Storage.IStorageProviderRegistry, PayPlanner.Api.Services.Storage.StorageProviderRegistry>();
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
        public int ReadCalls { get; private set; }

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
            ReadCalls++;
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
            if (Directory.Exists(Root))
            {
                var fullRoot = Path.GetFullPath(Root);
                if (!string.Equals(Path.GetDirectoryName(fullRoot), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(fullRoot), "^payplanner-backup-s3-[a-f0-9]{32}$"))
                    throw new InvalidOperationException("Refusing to delete a directory not owned by the backup fixture.");
                // Windows can release SQLite WAL sidecar handles shortly after the
                // restored backend process exits. Retry only this owned fixture root.
                for (var attempt = 0; ; attempt++)
                {
                    if (!Directory.Exists(Root)) break;
                    try
                    {
                        Directory.Delete(Root, recursive: true);
                        break;
                    }
                    catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
                    {
                        await Task.Delay(100);
                    }
                }
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
        public Action<string>? OnFilesRead { get; set; }

        public IReadOnlyList<string> GetFiles(string directory, string pattern)
        {
            OnFilesRead?.Invoke(directory);
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
