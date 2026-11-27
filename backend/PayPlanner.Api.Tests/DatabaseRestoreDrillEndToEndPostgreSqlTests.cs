using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseRestoreDrillEndToEndPostgreSqlTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FullModelBackupRestoresReadableManagedGenerationsWithoutSource(bool deleted, bool withNative)
        => await RestoreFullModelAsync(deleted, withNative, "sql", longBackupDirectory: false);

    [Theory]
    [InlineData("backup")]
    [InlineData("sql")]
    public async Task RealBackupAndRestoreServicesSupportLongNativeFilePaths(string format)
        => await RestoreFullModelAsync(deleted: false, withNative: false, format: format, longBackupDirectory: true);

    private static async Task RestoreFullModelAsync(bool deleted, bool withNative, string format, bool longBackupDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var adminBuilder = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
        Assert.Contains(adminBuilder.Host, new[] { "127.0.0.1", "localhost" });
        var sourceName = "pp_runtime_source_" + Guid.NewGuid().ToString("N");
        var targetName = "pp_runtime_target_" + Guid.NewGuid().ToString("N");
        var sourceBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = sourceName };
        var targetBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = targetName, Host = "127.0.0.1" };
        var root = Path.Combine(Path.GetTempPath(), "payplanner-runtime-e2e-" + Guid.NewGuid().ToString("N"));
        var sourceCreated = false;
        var targetCreated = false;
        Directory.CreateDirectory(Path.Combine(root, "files"));
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await CreateDatabaseAsync(admin, sourceName);
            sourceCreated = true;
            await CreateDatabaseAsync(admin, targetName);
            targetCreated = true;
            var backupDirectory = longBackupDirectory
                ? Path.Combine(root, "backups", new string('a', 60), new string('b', 60), new string('c', 60), new string('d', 60))
                : Path.Combine(root, "backups");
            if (longBackupDirectory) Assert.True(Path.GetFullPath(backupDirectory).Length > 260);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseBackup:Directory"] = backupDirectory,
                ["DatabaseBackup:Format"] = format,
                ["DatabaseBackup:PgDumpPath"] = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_PG_DUMP"),
                [ApplicationFileStoragePaths.RootPathConfigurationKey] = Path.Combine(root, "files"),
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            var sourceKeys = Path.Combine(root, "source-keys");
            services.AddDataProtection().SetApplicationName("PayPlanner.PostgresRecoveryFixture")
                .PersistKeysToFileSystem(new DirectoryInfo(sourceKeys));
            services.AddSingleton<DataProtectionRecoveryInventory>();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton(new DatabaseProviderInfo(DatabaseProviderKind.PostgreSql, sourceBuilder.ConnectionString));
            services.AddDbContextFactory<PaymentContext>(options => options.UseNpgsql(sourceBuilder.ConnectionString));
            services.AddSingleton<IWebHostEnvironment>(new TestEnvironment(root));
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DatabaseBackupService>>(NullLogger<DatabaseBackupService>.Instance);
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DatabaseRestoreDrillService>>(NullLogger<DatabaseRestoreDrillService>.Instance);
            services.Configure<DatabaseRestoreDrillOptions>(options =>
            {
                options.RootDirectory = Path.Combine(root, "drills");
                options.PostgreSqlExecutionEnabled = true;
                options.PostgreSqlTargetConnectionString = targetBuilder.ConnectionString;
                options.AllowedPostgreSqlDatabases = [targetName];
                options.ConfirmationToken = "disposable-runtime-test";
                options.PsqlPath = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_PSQL") ?? "psql";
                var dumpDirectory = Path.GetDirectoryName(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_PG_DUMP"));
                var restoreFileName = OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore";
                options.PgRestorePath = string.IsNullOrEmpty(dumpDirectory) ? restoreFileName : Path.Combine(dumpDirectory, restoreFileName);
                options.PrepareLocalRuntime = true;
            });
            services.AddSingleton<DatabaseBackupService>();
            services.AddSingleton<DatabaseRestoreDrillService>();
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<PaymentContext>>();
            var protection = provider.GetRequiredService<IDataProtectionProvider>();
            using var sourceFieldKeys = new SyntheticFieldKeys();
            var recoveredFieldMaterial = sourceFieldKeys.CopyMaterial();
            var sourceFieldEncryptor = new AeadFieldEncryptor(sourceFieldKeys);
            var objectId = Guid.NewGuid();
            var payloads = new[] { "удержанная предыдущая версия"u8.ToArray(), "текущий документ"u8.ToArray() };
            var signaturePath = Path.Combine(root, "files", "electronic-signatures", "original.pdf");
            var artifactPath = Path.Combine(root, "storage", "electronic-signatures", "artifact.pdf");
            var photoPath = Path.Combine(root, deleted ? "user-photos" : "files/user-photos", "photo.png");
            var casePath = Path.Combine(root, "files", "case-documents", "case.pdf");
            var nativePaths = new[] { signaturePath, artifactPath, photoPath, casePath };
            Guid signaturePublicId = Guid.Empty;
            var password = Guid.NewGuid().ToString("N");
            var photoBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=");
            string[] sourceMigrations = [];
            await using (var source = await factory.CreateDbContextAsync())
            {
                if (withNative)
                {
                    await using var migrationDb = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                        .UseNpgsql(sourceBuilder.ConnectionString).Options);
                    await migrationDb.Database.MigrateAsync();
                    sourceMigrations = (await migrationDb.Database.GetAppliedMigrationsAsync()).ToArray();
                    Assert.NotEmpty(sourceMigrations);
                }
                else await source.Database.EnsureCreatedAsync();
                var protectedSettings = new Dictionary<string, string>
                {
                    ["integrations.s3.connections"] = JsonSerializer.Serialize(new[] { new { secretAccessKey = "protected:v1:" + protection.CreateProtector("PayPlanner.S3Storage.Secrets.v1").Protect("synthetic-postgres-s3") } }),
                    ["integrations.yandexDisk.connections"] = JsonSerializer.Serialize(new[] { new { oAuthToken = "protected:v1:" + protection.CreateProtector("PayPlanner.YandexDisk.Tokens.v1").Protect("synthetic-postgres-yandex") } }),
                    ["integrations.yandexDisk.oauthToken"] = "protected:v1:" + protection.CreateProtector("PayPlanner.YandexDisk.Tokens.v1").Protect("synthetic-postgres-legacy-yandex"),
                };
                foreach (var (key, value) in protectedSettings)
                {
                    var setting = await source.SystemSettings.SingleOrDefaultAsync(row => row.Key == key);
                    if (setting is null) source.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
                    else setting.Value = value;
                }
                var emailSetting = await source.SystemSettings.SingleOrDefaultAsync(row => row.Key == "notifications.email.enabled");
                if (emailSetting is null) source.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                else emailSetting.Value = "false";
                var item = new StorageObject
                {
                    Id = objectId,
                    CurrentGeneration = 2,
                    DataClass = "document",
                    LogicalName = "Документ.txt",
                    ContentType = "text/plain",
                    IsDeleted = deleted,
                };
                for (var index = 0; index < payloads.Length; index++)
                {
                    await File.WriteAllBytesAsync(Path.Combine(root, "files", $"version-{index}.bin"), payloads[index]);
                    var generation = new StorageObjectGeneration
                    {
                        Generation = index + 1,
                        SizeBytes = payloads[index].Length,
                        Sha256 = Convert.ToHexString(SHA256.HashData(payloads[index])),
                    };
                    generation.Replicas.Add(new StorageReplica
                    {
                        ProviderKey = "legacy-local",
                        Locator = $"files/version-{index}.bin",
                        Status = StorageReplicaStatuses.Available,
                    });
                    item.Generations.Add(generation);
                }
                if (deleted) item.Tombstones.Add(new StorageTombstone { Generation = 2, LegalHold = true });
                source.StorageObjects.Add(item);
                source.StorageOperations.Add(new StorageOperation
                {
                    StorageObject = item,
                    OperationType = StorageOperationTypes.Replicate,
                    Status = StorageOperationStatuses.Pending,
                    IdempotencyKey = "runtime-test-" + objectId,
                });
                await source.SaveChangesAsync();
                if (withNative)
                {
                    foreach (var path in nativePaths)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllBytesAsync(path, payloads[1]);
                    }
                    await File.WriteAllBytesAsync(photoPath, photoBytes);
                    var user = new User
                    {
                        Email = "native-runtime@example.test",
                        FullName = "Synthetic restore operator",
                        IsApproved = true,
                        Role = new Role { Name = "admin", Permissions = [new RolePermission { Section = "cases", CanView = true, CanEdit = true, ViewScope = "all" }] },
                        PhotoUrl = "https://old-host.example.test/user-photos/photo.png",
                    };
                    user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
                    var clientCase = new ClientCase { Title = "Native runtime", Client = new Client { Name = "Native runtime" } };
                    source.AddRange(user, clientCase);
                    await source.SaveChangesAsync();
                    var passport = new ClientPassport
                    {
                        ClientId = clientCase.ClientId!.Value,
                        Series = "4510",
                        Number = "123456",
                        IssuedBy = "Синтетический орган Ω",
                        IssuedAt = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                        DepartmentCode = "540-001",
                        BirthPlacePassport = "Синтетическое место Ω",
                        BirthPlaceFias = "synthetic-fias",
                    };
                    source.ClientPassports.Add(passport);
                    await source.SaveChangesAsync();
                    var passportConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["FieldEncryption:Enabled"] = "true",
                        ["FieldEncryption:ClientPassportDualWriteEnabled"] = "true",
                    }).Build();
                    await new ClientPassportEncryptionDualWriteService(sourceFieldEncryptor, passportConfig)
                        .ProtectAsync(passport, null, CancellationToken.None);
                    await source.SaveChangesAsync();
                    source.GoogleCalendarConnections.Add(new GoogleCalendarConnection
                    {
                        UserId = user.Id,
                        IsEnabled = false,
                        SyncEnabled = false,
                        AccessTokenProtected = protection.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1").Protect("synthetic-access"),
                        RefreshTokenProtected = protection.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1").Protect("synthetic-refresh"),
                    });
                    source.YandexCalendarConnections.Add(new YandexCalendarConnection
                    {
                        UserId = user.Id,
                        IsEnabled = false,
                        SyncEnabled = false,
                        AccessTokenProtected = protection.CreateProtector("PayPlanner.YandexCalendar.Tokens.v1").Protect("synthetic-access"),
                        RefreshTokenProtected = protection.CreateProtector("PayPlanner.YandexCalendar.Tokens.v1").Protect("synthetic-refresh"),
                    });
                    source.MessengerRecipientBindings.Add(new MessengerRecipientBinding
                    {
                        ClientId = clientCase.ClientId,
                        Channel = "telegram",
                        Provider = "synthetic",
                        ProtectedAddress = protection.CreateProtector("PayPlanner.MessengerDocumentDelivery.Address.v1").Protect("synthetic-recipient"),
                        AddressHash = "synthetic-recipient",
                        MaskedAddress = "synthetic",
                    });
                    var envelope = new ElectronicSignatureEnvelope
                    {
                        CreatedByUserId = user.Id,
                        SnapshotPath = signaturePath,
                        SizeBytes = payloads[1].Length,
                        ChecksumSha256 = Convert.ToHexString(SHA256.HashData(payloads[1])),
                    };
                    envelope.Artifacts.Add(new ElectronicSignatureArtifact
                    {
                        StoragePath = artifactPath,
                        SizeBytes = payloads[1].Length,
                        FileName = "artifact.pdf",
                        ChecksumSha256 = envelope.ChecksumSha256,
                    });
                    envelope.Signers.Add(new ElectronicSignatureSigner
                    {
                        DisplayName = "Synthetic signer",
                        ProtectedContact = protection.CreateProtector("PayPlanner.ElectronicSignature.Contact.v1").Protect("Синтетический контакт Ω"),
                        MaskedContact = "synthetic",
                    });
                    source.AddRange(envelope, new ClientCaseDocument
                    {
                        ClientCase = clientCase,
                        DocumentKey = "native-runtime",
                        StoragePath = "case.pdf",
                        OriginalFileName = "case.pdf",
                        SizeBytes = payloads[1].Length,
                    });
                    await source.SaveChangesAsync();
                    signaturePublicId = envelope.PublicId;
                }
            }
            var backups = provider.GetRequiredService<DatabaseBackupService>();
            var created = await backups.CreateManualBackupAsync();
            if (longBackupDirectory) Assert.True(backups.ResolveBackupPath(created.Backup.RelativePath)!.Length > 260);
            sourceFieldKeys.Dispose();
            Assert.Throws<InvalidOperationException>(() => sourceFieldKeys.GetKeyAsync("synthetic-restore", CancellationToken.None));
            for (var index = 0; index < payloads.Length; index++) File.Delete(Path.Combine(root, "files", $"version-{index}.bin"));
            if (withNative) foreach (var path in nativePaths) File.Delete(path);
            var report = await provider.GetRequiredService<DatabaseRestoreDrillService>()
                .RunAsync(created.Backup.RelativePath, true, "disposable-runtime-test", CancellationToken.None);
            Assert.True(report.Status == "restored", JsonSerializer.Serialize(report));
            var restoredRoot = Path.GetDirectoryName(report.ReportPath)!;
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(restoredRoot, "restored-storage-paths.json")));
            Assert.False(settings.RootElement.GetProperty("completeRuntimeConfiguration").GetBoolean());
            var runtimeInstanceId = Guid.ParseExact(settings.RootElement.GetProperty("runtimeInstanceId").GetString()!, "N");
            Assert.NotEqual(Guid.Empty, runtimeInstanceId);
            var restoredConfig = new ConfigurationBuilder().AddInMemoryCollection(settings.RootElement.GetProperty("storageSettings")
                .EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString())).Build();
            var local = new LocalStorageProvider(new TestEnvironment(restoredRoot), restoredConfig);
            var registry = new StorageProviderRegistry([local]);
            var storageOptions = new FixedStorageOptions(new StorageOrchestrationOptions());
            await using var restored = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseNpgsql(targetBuilder.ConnectionString).Options);
            Assert.Equal(runtimeInstanceId.ToString("N"),
                (await restored.SystemSettings.SingleAsync(row => row.Key == RestoredRuntimeDatabaseIdentity.SettingKey)).Value);
            restoredConfig[RestoredRuntimeIsolation.EnabledKey] = "true";
            restoredConfig[RestoredRuntimeDatabaseIdentity.ConfigurationKey] = runtimeInstanceId.ToString("N");
            await RestoredRuntimeDatabaseIdentity.VerifyAsync(restoredConfig,
                new DatabaseProviderInfo(DatabaseProviderKind.PostgreSql, targetBuilder.ConnectionString), CancellationToken.None);
            var keyDirectory = Path.Combine(restoredRoot, "recovery-keys");
            Directory.CreateDirectory(keyDirectory);
            foreach (var file in Directory.GetFiles(sourceKeys, "key-*.xml")) File.Copy(file, Path.Combine(keyDirectory, Path.GetFileName(file)));
            Directory.Move(sourceKeys, Path.Combine(root, "detached-source-keys"));
            Assert.False(Directory.Exists(sourceKeys));
            restoredConfig[RestoredRuntimeDataProtection.ArtifactPathKey] = Path.Combine(restoredRoot, "restored-storage-paths.json");
            restoredConfig[RestoredRuntimeDataProtection.KeysPathKey] = keyDirectory;
            var recovery = RestoredRuntimeDataProtection.ReadConfiguration(restoredConfig)!;
            var recoveredServices = new ServiceCollection().AddLogging();
            recoveredServices.AddDataProtection().SetApplicationName(recovery.Expected.ApplicationDiscriminator!)
                .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory)).DisableAutomaticKeyGeneration();
            recoveredServices.AddSingleton<DataProtectionRecoveryInventory>();
            using var restoredFieldKeys = new SyntheticFieldKeys(recoveredFieldMaterial);
            if (withNative) recoveredServices.AddSingleton<IFieldEncryptor>(new AeadFieldEncryptor(restoredFieldKeys));
            using var recoveredProvider = recoveredServices.BuildServiceProvider();
            var keyHashes = Directory.GetFiles(keyDirectory).Order(StringComparer.Ordinal)
                .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
            var protectedSettingBefore = await restored.SystemSettings.AsNoTracking().Where(row => row.Key == "integrations.s3.connections")
                .Select(row => row.Value).SingleAsync();
            var expectedProtectedCount = withNative ? 16 : 3;
            var restoredDatabase = new DatabaseProviderInfo(DatabaseProviderKind.PostgreSql, targetBuilder.ConnectionString);
            Assert.Equal(expectedProtectedCount, await RestoredRuntimeDataProtection.VerifyAsync(recoveredProvider,
                restoredConfig, restoredDatabase, recovery, CancellationToken.None));
            if (withNative)
            {
                var passportBefore = JsonSerializer.Serialize(await restored.ClientPassports.AsNoTracking().SingleAsync());
                using var wrongKeys = new SyntheticFieldKeys();
                using var wrongProvider = new ServiceCollection().AddSingleton<IFieldEncryptor>(new AeadFieldEncryptor(wrongKeys)).BuildServiceProvider();
                await using var readOnly = new NpgsqlConnection(targetBuilder.ConnectionString);
                await readOnly.OpenAsync();
                await using var transaction = await readOnly.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
                await using (var setReadOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", readOnly, transaction))
                    await setReadOnly.ExecuteNonQueryAsync();
                var fieldError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    RestoredRuntimeFieldEncryption.VerifyConnectionAsync(readOnly, wrongProvider, CancellationToken.None));
                Assert.Null(fieldError.InnerException);
                Assert.DoesNotContain("synthetic", fieldError.ToString());
                Assert.Equal(passportBefore, JsonSerializer.Serialize(await restored.ClientPassports.AsNoTracking().SingleAsync()));
                await transaction.RollbackAsync();
                var wrongMaterial = wrongKeys.CopyMaterial();
                try
                {
                    var startupError = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(async () =>
                    {
                        await using var rejected = await RestoredBackendProcess.StartAsync(
                            Path.Combine(restoredRoot, "restored-storage-paths.json"), targetBuilder.ConnectionString,
                            keyDirectory, DatabaseProviderKind.PostgreSql, wrongMaterial);
                    });
                    Assert.Contains("Не подтверждено чтение защищённых данных восстановленной CRM", startupError.Message);
                    Assert.DoesNotContain(Convert.ToBase64String(wrongMaterial.MasterKey), startupError.ToString());
                    Assert.DoesNotContain(Convert.ToBase64String(wrongMaterial.BlindIndexKey), startupError.ToString());
                    Assert.Equal(passportBefore, JsonSerializer.Serialize(await restored.ClientPassports.AsNoTracking().SingleAsync()));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(wrongMaterial.MasterKey);
                    CryptographicOperations.ZeroMemory(wrongMaterial.BlindIndexKey);
                }
            }
            Assert.Equal(protectedSettingBefore, await restored.SystemSettings.AsNoTracking().Where(row => row.Key == "integrations.s3.connections")
                .Select(row => row.Value).SingleAsync());
            var corrupted = JsonSerializer.Serialize(new[] { new { secretAccessKey = "protected:v1:synthetic-corrupt-value" } });
            await restored.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"SystemSettings\" SET \"Value\" = {corrupted} WHERE \"Key\" = 'integrations.s3.connections'");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RestoredRuntimeDataProtection.VerifyAsync(
                recoveredProvider, restoredConfig, restoredDatabase, recovery, CancellationToken.None));
            Assert.DoesNotContain("synthetic-corrupt-value", error.ToString());
            Assert.Null(error.InnerException);
            Assert.Equal(corrupted, await restored.SystemSettings.AsNoTracking().Where(row => row.Key == "integrations.s3.connections")
                .Select(row => row.Value).SingleAsync());
            Assert.Equal(keyHashes, Directory.GetFiles(keyDirectory).Order(StringComparer.Ordinal)
                .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray());
            await restored.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"SystemSettings\" SET \"Value\" = {protectedSettingBefore} WHERE \"Key\" = 'integrations.s3.connections'");
            var reader = new StorageObjectService(restored, new StoragePolicyService(storageOptions, registry), registry, storageOptions);
            await using var read = await reader.OpenReadAsync(objectId, CancellationToken.None);
            if (deleted) Assert.Null(read);
            else
            {
                Assert.NotNull(read);
                using var bytes = new MemoryStream();
                await read!.Content.CopyToAsync(bytes);
                Assert.Equal(payloads[1], bytes.ToArray());
                Assert.Equal("local", read.ProviderKey);
            }
            var generations = await restored.StorageObjectGenerations.OrderBy(row => row.Generation).Include(row => row.Replicas).ToListAsync();
            Assert.Equal(2, generations.Count);
            for (var index = 0; index < generations.Count; index++)
            {
                var replica = Assert.Single(generations[index].Replicas);
                Assert.Equal("local", replica.ProviderKey);
                Assert.Equal(payloads[index], await File.ReadAllBytesAsync(Path.Combine(restoredRoot, "files", replica.Locator)));
            }
            Assert.Equal(deleted, (await restored.StorageObjects.SingleAsync()).IsDeleted);
            Assert.Equal(deleted, await restored.StorageTombstones.AnyAsync(row => row.LegalHold));
            Assert.Equal(StorageOperationStatuses.DeadLetter, (await restored.StorageOperations.SingleAsync()).Status);
            Assert.Equal("false", (await restored.SystemSettings.SingleAsync(row => row.Key == "notifications.email.enabled")).Value);
            if (withNative)
            {
                await using var migratedTarget = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                    .UseNpgsql(targetBuilder.ConnectionString).Options);
                Assert.Equal(sourceMigrations, (await migratedTarget.Database.GetAppliedMigrationsAsync()).ToArray());
                var environment = new TestEnvironment(restoredRoot);
                var signatures = new ElectronicSignatureService(restored, restoredConfig,
                    recoveredProvider.GetRequiredService<IDataProtectionProvider>(), environment);
                var package = await signatures.CreateEvidencePackageAsync(signaturePublicId, CancellationToken.None);
                Assert.NotNull(package);
                using (var archive = ZipFile.OpenRead(package!.FullPath))
                    foreach (var name in new[] { "original/document.pdf", "artifacts/001-artifact.pdf" })
                    {
                        using var input = archive.GetEntry(name)!.Open();
                        using var bytes = new MemoryStream();
                        await input.CopyToAsync(bytes);
                        Assert.Equal(payloads[1], bytes.ToArray());
                    }
                var photo = (await restored.Users.SingleAsync()).PhotoUrl;
                Assert.StartsWith(UserPhotoStoragePaths.RequestPath + "/", photo);
                Assert.Equal(photoBytes, await File.ReadAllBytesAsync(UserPhotoStoragePaths.ResolvePhotoPath(
                    UserPhotoStoragePaths.ResolveRoot(environment, restoredConfig), photo)!));
                var caseStorage = new LocalCaseDocumentStorage(environment, restoredConfig);
                Assert.Equal(payloads[1], await File.ReadAllBytesAsync(caseStorage.ResolvePath((await restored.ClientCaseDocuments.SingleAsync()).StoragePath)));
                Assert.Empty(await restored.NotificationSenders.Where(row => row.Channel == "email" && row.IsEnabled).ToListAsync());
                await using var backend = await RestoredBackendProcess.StartAsync(
                    Path.Combine(restoredRoot, "restored-storage-paths.json"), targetBuilder.ConnectionString, keyDirectory,
                    DatabaseProviderKind.PostgreSql, recoveredFieldMaterial, connectionDiagnostics: true);
                using var health = await backend.Client.GetAsync("/api/health");
                health.EnsureSuccessStatusCode();
                using var login = await backend.Client.PostAsJsonAsync("/api/auth/login", new { email = "native-runtime@example.test", password });
                login.EnsureSuccessStatusCode();
                var session = await login.Content.ReadFromJsonAsync<PayPlanner.Api.Models.Responses.LoginResponse>();
                Assert.NotNull(session);
                Assert.NotEmpty(session!.Token);
                backend.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.Token);
                Assert.Equal(photoBytes, await backend.Client.GetByteArrayAsync(session.User.PhotoUrl));
                var restoredDocument = await restored.ClientCaseDocuments.SingleAsync();
                Assert.Equal(payloads[1], await backend.Client.GetByteArrayAsync($"/api/case-documents/{restoredDocument.Id}/download"));
                if (Environment.GetEnvironmentVariable("PAYPLANNER_TEST_RESTORED_BROWSER") == "true")
                    await backend.CheckBrowserAsync(runtimeInstanceId.ToString("N"), "native-runtime@example.test", password,
                        restoredDocument.ClientCaseId, "case.pdf", payloads[1]);
                backend.AssertNoServerErrors();
                Assert.Equal(sourceMigrations, (await migratedTarget.Database.GetAppliedMigrationsAsync()).ToArray());
                Assert.Equal(keyHashes, Directory.GetFiles(keyDirectory).Order(StringComparer.Ordinal)
                    .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray());
            }
            await using var original = await factory.CreateDbContextAsync();
            Assert.Equal(2, await original.StorageReplicas.CountAsync(row => row.ProviderKey == "legacy-local"));
            Assert.Equal(StorageOperationStatuses.Pending, (await original.StorageOperations.SingleAsync()).Status);
            if (withNative)
            {
                Assert.Equal(signaturePath, (await original.ElectronicSignatureEnvelopes.SingleAsync()).SnapshotPath);
                Assert.Equal(artifactPath, (await original.ElectronicSignatureArtifacts.SingleAsync()).StoragePath);
                Assert.Equal("https://old-host.example.test/user-photos/photo.png", (await original.Users.SingleAsync()).PhotoUrl);
            }
        }
        finally
        {
            if (targetCreated) await DropOwnedRuntimeDatabaseAsync(admin, targetName, "pp_runtime_target_");
            if (sourceCreated) await DropOwnedRuntimeDatabaseAsync(admin, sourceName, "pp_runtime_source_");
            var fullRoot = Path.GetFullPath(root);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(fullRoot), ignoreCase: true);
            Assert.StartsWith("payplanner-runtime-e2e-", Path.GetFileName(fullRoot), StringComparison.Ordinal);
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private sealed class FixedStorageOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }

    private sealed class SyntheticFieldKeys : IFieldEncryptionKeyStore, IDisposable
    {
        private readonly FieldEncryptionKeyMaterial _material;
        private bool _disposed;
        public SyntheticFieldKeys(FieldEncryptionKeyMaterial? material = null)
            => _material = material is null
                ? new("synthetic-restore", RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32))
                : new(material.KeyId, (byte[])material.MasterKey.Clone(), (byte[])material.BlindIndexKey.Clone());
        public FieldEncryptionKeyMaterial CopyMaterial()
        {
            if (_disposed) throw new InvalidOperationException();
            return new(_material.KeyId, (byte[])_material.MasterKey.Clone(), (byte[])_material.BlindIndexKey.Clone());
        }
        public ValueTask<FieldEncryptionKeyMaterial> GetActiveKeyAsync(CancellationToken ct) => GetKeyAsync(_material.KeyId, ct);
        public ValueTask<FieldEncryptionKeyMaterial> GetKeyAsync(string keyId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (keyId != _material.KeyId) throw new InvalidOperationException();
            return ValueTask.FromResult(CopyMaterial());
        }
        public void Dispose()
        {
            _disposed = true;
            CryptographicOperations.ZeroMemory(_material.MasterKey);
            CryptographicOperations.ZeroMemory(_material.BlindIndexKey);
        }
    }

    private static async Task DropOwnedRuntimeDatabaseAsync(NpgsqlConnection admin, string name, string prefix)
    {
        Assert.StartsWith(prefix, name, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(name[prefix.Length..], "N", out _));
        await using var active = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = @databaseName", admin);
        active.Parameters.AddWithValue("databaseName", name);
        long connections = -1;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            connections = (long)(await active.ExecuteScalarAsync())!;
            if (connections == 0) break;
            await Task.Delay(100);
        }
        Assert.Equal(0L, connections);
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
        await drop.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(1, false, "none")]
    [InlineData(2, false, "none")]
    [InlineData(3, false, "none")]
    [InlineData(4, false, "none")]
    [InlineData(5, false, "none")]
    [InlineData(5, true, "none")]
    [InlineData(5, true, "missing")]
    [InlineData(5, true, "hash")]
    public async Task RestoresRealDumpIntoEmptyDatabase_AndIsolatesEmail(int manifestVersion, bool withManaged, string corruption)
    {
        var withInventory = manifestVersion >= 2;
        var connectionString = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"payplanner-restore-e2e-{Guid.NewGuid():N}");
        var sourceName = "payplanner_restore_source_" + Guid.NewGuid().ToString("N");
        var targetName = "payplanner_restore_target_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        var objectId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var payloads = new Dictionary<string, byte[]>
        {
            ["generation-1.bin"] = System.Text.Encoding.UTF8.GetBytes("Прежняя удержанная версия"),
            ["generation-2.bin"] = Enumerable.Range(0, 81921).Select(value => (byte)(value % 251)).ToArray()
        };
        var sourceFiles = Path.Combine(root, "managed-source");
        DatabaseBackupStorageReplicaReference Reference(Guid id, long generation, long replicaId, string file)
            => new(id, objectId, generation, payloads[$"generation-{generation}.bin"].Length,
                Convert.ToHexString(SHA256.HashData(payloads[$"generation-{generation}.bin"])),
                true, true, new DateTime(2030, 1, 1), replicaId, "local", null, file, "available");
        var managedReferences = withManaged ? new[]
        {
            Reference(firstId, 1, 1, "missing.bin"), Reference(firstId, 1, 2, "generation-1.bin"),
            Reference(secondId, 2, 3, "generation-2.bin")
        } : Array.Empty<DatabaseBackupStorageReplicaReference>();
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        var sourceBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = sourceName };
        var targetBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = targetName };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await CreateDatabaseAsync(admin, sourceName);
            await CreateDatabaseAsync(admin, targetName);
            await using (var source = new NpgsqlConnection(sourceBuilder.ConnectionString))
            {
                await source.OpenAsync();
                await using var seed = source.CreateCommand();
                seed.CommandText = """
                    CREATE TABLE "SystemSettings" (
                        "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                        "Key" text NOT NULL UNIQUE, "Value" text NOT NULL,
                        "CreatedAt" timestamp NOT NULL, "UpdatedAt" timestamp NOT NULL);
                    CREATE TABLE "NotificationDeliveries" (
                        "Id" integer PRIMARY KEY, "Channel" text NOT NULL,
                        "Status" text NOT NULL, "Error" text NULL);
                    CREATE TABLE "NotificationSenders" (
                        "Id" integer PRIMARY KEY, "Channel" text NOT NULL,
                        "IsEnabled" boolean NOT NULL, "ConfigJson" text NOT NULL, "UpdatedAt" timestamp NULL);
                    INSERT INTO "NotificationSenders" ("Id", "Channel", "IsEnabled", "ConfigJson")
                    VALUES (1, 'email', true, '{"smtpHost":"test-only.invalid"}');
                    CREATE TABLE public.restore_evidence (id integer PRIMARY KEY, label text NOT NULL);
                    INSERT INTO "SystemSettings" ("Key", "Value", "CreatedAt", "UpdatedAt")
                    VALUES ('notifications.email.smtpPassword', 'copied-secret', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                    INSERT INTO "NotificationDeliveries" ("Id", "Channel", "Status") VALUES (1, 'email', 'failed');
                    INSERT INTO public.restore_evidence (id, label) VALUES (42, 'restored');
                    """;
                await seed.ExecuteNonQueryAsync();
                if (withInventory)
                {
                    // Only the columns consumed by the inventory queries are needed here;
                    // full application schemas are covered by snapshot inventory tests.
                    seed.CommandText = """
                        CREATE TABLE "TaskJournalFiles" ("Id" integer, "StoragePath" text, "SizeBytes" bigint, "Version" integer);
                        CREATE TABLE "ClientCaseDocuments" ("Id" integer, "StoragePath" text, "SizeBytes" bigint, "Version" integer);
                        CREATE TABLE "InternalChatAttachments" ("Id" integer, "StoredFileName" text, "Size" bigint, "MessageId" integer);
                        CREATE TABLE "InternalChatMessages" ("Id" integer, "ThreadId" integer);
                        CREATE TABLE "MessengerDocumentDeliveries" ("Id" integer, "SnapshotPath" text, "SizeBytes" bigint, "ChecksumSha256" text, "DocumentVersion" integer);
                        CREATE TABLE "ElectronicSignatureEnvelopes" ("Id" integer, "SnapshotPath" text, "SizeBytes" bigint, "ChecksumSha256" text, "DocumentVersion" integer);
                        CREATE TABLE "ElectronicSignatureArtifacts" ("Id" integer, "StoragePath" text, "SizeBytes" bigint, "ChecksumSha256" text, "EnvelopeId" integer);
                        CREATE TABLE "Users" ("Id" integer, "PhotoUrl" text);
                        INSERT INTO "Users" VALUES (1, '/user-photos/photo.png');
                        CREATE TABLE "StorageObjects" ("Id" uuid, "CurrentGeneration" bigint, "IsDeleted" boolean);
                        CREATE TABLE "StorageObjectGenerations" ("Id" uuid, "StorageObjectId" uuid, "Generation" bigint, "SizeBytes" bigint, "Sha256" text);
                        CREATE TABLE "StorageTombstones" ("StorageObjectId" uuid, "LegalHold" boolean, "RetainUntil" timestamp);
                        CREATE TABLE "StorageReplicas" ("Id" bigint, "StorageObjectGenerationId" uuid, "ProviderKey" text, "ConnectionId" text, "Locator" text, "Status" text);
                        """;
                    await seed.ExecuteNonQueryAsync();
                    if (withManaged)
                    {
                        seed.CommandText = """
                            INSERT INTO "StorageObjects" VALUES (@object, 2, true);
                            INSERT INTO "StorageTombstones" VALUES (@object, true, TIMESTAMP '2030-01-01');
                            INSERT INTO "StorageObjectGenerations" VALUES (@first, @object, 1, @size1, @hash1), (@second, @object, 2, @size2, @hash2);
                            INSERT INTO "StorageReplicas" VALUES
                              (1, @first, 'local', NULL, 'missing.bin', 'available'),
                              (2, @first, 'local', NULL, 'generation-1.bin', 'available'),
                              (3, @second, 'local', NULL, 'generation-2.bin', 'available');
                            """;
                        seed.Parameters.AddWithValue("object", objectId);
                        seed.Parameters.AddWithValue("first", firstId);
                        seed.Parameters.AddWithValue("second", secondId);
                        seed.Parameters.AddWithValue("size1", (long)payloads["generation-1.bin"].Length);
                        seed.Parameters.AddWithValue("size2", (long)payloads["generation-2.bin"].Length);
                        seed.Parameters.AddWithValue("hash1", managedReferences[0].Sha256);
                        seed.Parameters.AddWithValue("hash2", managedReferences[2].Sha256);
                        await seed.ExecuteNonQueryAsync();
                        Directory.CreateDirectory(sourceFiles);
                        foreach (var item in payloads) await File.WriteAllBytesAsync(Path.Combine(sourceFiles, item.Key), item.Value);
                    }
                }
            }

            var backupRoot = Path.Combine(root, "backups");
            Directory.CreateDirectory(backupRoot);
            var backupPath = Path.Combine(backupRoot, "test.sql");
            await RunPgDumpAsync(sourceBuilder, backupPath);
            var filesetPath = backupPath + ".fileset";
            using (var archive = ZipFile.Open(filesetPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(withInventory ? "file-storage/user-photos/photo.png" : "file-storage/evidence.txt");
                using (var writer = new StreamWriter(entry.Open())) await writer.WriteAsync("restored file evidence");
                if (manifestVersion >= 3)
                    entry.Comment = DatabaseBackupArchiveHash.ToComment(Convert.ToHexString(SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes("restored file evidence"))));
            }
            string filesetHash;
            await using (var filesetStream = File.OpenRead(filesetPath))
                filesetHash = Convert.ToHexString(await SHA256.HashDataAsync(filesetStream)).ToLowerInvariant();
            await using (var stream = File.OpenRead(backupPath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
                var parts = new List<DatabaseBackupPartManifest>
                {
                    new("database", "test.sql", stream.Length, hash, true),
                    new("file-storage", "test.sql.fileset", new FileInfo(filesetPath).Length, filesetHash, true,
                        FileEntryHashFormat: manifestVersion >= 3 ? DatabaseBackupArchiveHash.Format : null),
                };
                if (withInventory)
                {
                    var inventoryPath = backupPath + ".references";
                    await File.WriteAllTextAsync(inventoryPath, JsonSerializer.Serialize(new
                    {
                        formatVersion = 1,
                        storedFiles = new[] { new DatabaseBackupStoredFileReference(
                            "profile-photo", 1, "/user-photos/photo.png", null, null, null, null) },
                        storageReplicas = managedReferences,
                        storedFileCount = 1,
                        storageReplicaCount = managedReferences.Length,
                    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    await using var inventory = File.OpenRead(inventoryPath);
                    var inventoryHash = Convert.ToHexString(await SHA256.HashDataAsync(inventory)).ToLowerInvariant();
                    parts.Add(new("file-references", "test.sql.references", inventory.Length, inventoryHash, true));
                    if (manifestVersion >= 4)
                    {
                        var sourcePhotos = Path.Combine(root, "source-photos");
                        Directory.CreateDirectory(sourcePhotos);
                        var sourcePhoto = Path.Combine(sourcePhotos, "photo.png");
                        await File.WriteAllTextAsync(sourcePhoto, "restored file evidence");
                        var layout = DatabaseBackupFileLayout.Create([new("profile-photos", sourcePhotos)], backupRoot);
                        // This fixture's archive prefix is profile-photos for the binding;
                        // rename the manually created entry to use that exact producer layout.
                        using (var zip = ZipFile.Open(filesetPath, ZipArchiveMode.Update))
                        {
                            var originalEntry = zip.GetEntry("file-storage/user-photos/photo.png")!;
                            var entry = zip.CreateEntry("profile-photos/photo.png");
                            using (var input = originalEntry.Open()) using (var output = entry.Open()) input.CopyTo(output);
                            entry.Comment = originalEntry.Comment;
                            originalEntry.Delete();
                        }
                        await using (var zipSource = File.OpenRead(filesetPath))
                            filesetHash = Convert.ToHexString(await SHA256.HashDataAsync(zipSource)).ToLowerInvariant();
                        parts[1] = parts[1] with { SizeBytes = new FileInfo(filesetPath).Length, Sha256 = filesetHash, FileRoots = layout.RootMappings };
                        inventory.Position = 0;
                        using var plan = new MemoryStream();
                        await DatabaseBackupNativeBindings.WritePlanAsync(inventory, plan, new(layout), "test.sql.fileset", inventoryHash, CancellationToken.None);
                        plan.Position = 0;
                        var bindingPath = backupPath + ".nativefiles";
                        await using (var output = File.Create(bindingPath))
                        using (var zip = ZipFile.OpenRead(filesetPath))
                            await DatabaseBackupNativeBindings.SealAsync(plan, output, zip, inventoryHash, CancellationToken.None);
                        await using var binding = File.OpenRead(bindingPath);
                        var bindingHash = Convert.ToHexString(await SHA256.HashDataAsync(binding)).ToLowerInvariant();
                        parts.Add(new("native-file-bindings", "test.sql.nativefiles", binding.Length, bindingHash, true));
                        File.Delete(sourcePhoto);
                    }
                    if (manifestVersion >= 5)
                    {
                        inventory.Position = 0;
                        var managedPath = backupPath + ".managedfiles";
                        var managedArchivePath = backupPath + ".managedset";
                        using var managedArchiveBytes = File.Create(managedArchivePath);
                        await using (var mapping = File.Create(managedPath))
                        using (var managedArchive = new ZipArchive(managedArchiveBytes, ZipArchiveMode.Create, true))
                            await DatabaseBackupManagedBindings.WriteAsync(inventory, mapping, managedArchive,
                                "test.sql.managedset", inventoryHash, root, (reference, _) =>
                                {
                                    if (!withManaged) throw new InvalidOperationException("Unexpected replica");
                                    var path = Path.Combine(sourceFiles, reference.Locator!);
                                    if (!File.Exists(path)) return Task.FromResult(new PayPlanner.Api.Services.Storage.StorageReadResult(
                                        PayPlanner.Api.Services.Storage.StorageOperationStatus.NotFound, reference.Locator!, null, 0));
                                    var content = File.OpenRead(path);
                                    return Task.FromResult(new PayPlanner.Api.Services.Storage.StorageReadResult(
                                        PayPlanner.Api.Services.Storage.StorageOperationStatus.Succeeded, reference.Locator!, content, content.Length));
                                }, CancellationToken.None);
                        await managedArchiveBytes.DisposeAsync();
                        if (withManaged)
                        {
                            await using var archiveBytes = File.OpenRead(managedArchivePath);
                            var archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveBytes)).ToLowerInvariant();
                            parts.Add(new("file-storage", "test.sql.managedset", archiveBytes.Length, archiveHash, true,
                                FileEntryHashFormat: DatabaseBackupArchiveHash.Format));
                            foreach (var item in payloads) File.Delete(Path.Combine(sourceFiles, item.Key));
                        }
                        await using var managed = File.OpenRead(managedPath);
                        var managedHash = Convert.ToHexString(await SHA256.HashDataAsync(managed)).ToLowerInvariant();
                        parts.Add(new("managed-file-bindings", "test.sql.managedfiles", managed.Length, managedHash, true));
                    }
                }
                var manifest = new DatabaseBackupManifest(
                    manifestVersion, Guid.NewGuid().ToString("N"), "test.sql", "test.sql", "postgresql", "sql",
                    stream.Length, hash, "manual", DateTime.UtcNow, "test", Parts:
                    parts, RecoveryKeyReference: "external-test-key-reference");
                await File.WriteAllTextAsync(backupPath + ".manifest.json",
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }

            var activeDatabase = new DatabaseProviderInfo(DatabaseProviderKind.PostgreSql, sourceBuilder.ConnectionString);
            var services = new ServiceCollection();
            services.AddSingleton(activeDatabase);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["DatabaseBackup:Directory"] = backupRoot }).Build());
            services.AddDbContextFactory<PaymentContext>(options => options.UseNpgsql(sourceBuilder.ConnectionString));
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DatabaseBackupService>>(
                NullLogger<DatabaseBackupService>.Instance);
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DatabaseRestoreDrillService>>(
                NullLogger<DatabaseRestoreDrillService>.Instance);
            services.AddSingleton<IWebHostEnvironment>(new TestEnvironment(root));
            services.Configure<DatabaseRestoreDrillOptions>(options =>
            {
                options.RootDirectory = Path.Combine(root, "drills");
                options.PostgreSqlExecutionEnabled = true;
                options.PostgreSqlTargetConnectionString = targetBuilder.ConnectionString;
                options.ConfirmationToken = "one-time-test-token";
                options.PsqlPath = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_PSQL") ?? "psql";
            });
            services.AddSingleton<DatabaseBackupService>();
            services.AddSingleton<DatabaseRestoreDrillService>();
            using var provider = services.BuildServiceProvider();

            var backups = provider.GetRequiredService<DatabaseBackupService>();
            var missingFilesetPath = filesetPath + ".temporarily-missing";
            File.Move(filesetPath, missingFilesetPath);
            try
            {
                Assert.False((await backups.VerifyBackupAsync("test.sql")).Verified);
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    provider.GetRequiredService<DatabaseRestoreDrillService>()
                        .RunAsync("test.sql", executePostgreSql: true, "one-time-test-token", CancellationToken.None));
                await DatabaseRestoreDrillService.EnsureEmptyPostgreSqlTargetAsync(
                    targetBuilder.ConnectionString, CancellationToken.None);
            }
            finally
            {
                File.Move(missingFilesetPath, filesetPath);
            }

            if (corruption != "none")
            {
                var checkedManifest = await backups.ReadVerifiedManifestAsync("test.sql");
                var managedPart = checkedManifest.Parts!.Single(part => part.RelativePath == "test.sql.managedset");
                var managedPath = backupPath + ".managedset";
                if (corruption == "missing") File.Delete(managedPath);
                else
                {
                    using (var archive = ZipFile.Open(managedPath, ZipArchiveMode.Update))
                    {
                        var entry = archive.GetEntry($"managed-generations/{firstId:D}.bin")!;
                        var changedBytes = System.Text.Encoding.UTF8.GetBytes("different retained bytes");
                        using (var output = entry.Open()) { output.SetLength(0); output.Write(changedBytes); }
                        entry.Comment = DatabaseBackupArchiveHash.ToComment(Convert.ToHexString(SHA256.HashData(changedBytes)));
                    }
                    await using var bytes = File.OpenRead(managedPath);
                    var changedPart = managedPart with { SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(bytes)).ToLowerInvariant() };
                    await File.WriteAllTextAsync(backupPath + ".manifest.json", JsonSerializer.Serialize(checkedManifest with
                    {
                        Parts = checkedManifest.Parts!.Select(part => part == managedPart ? changedPart : part).ToArray()
                    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                }
                Assert.False((await backups.VerifyBackupAsync("test.sql")).Verified);
                await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetRequiredService<DatabaseRestoreDrillService>()
                    .RunAsync("test.sql", true, "one-time-test-token", CancellationToken.None));
                await DatabaseRestoreDrillService.EnsureEmptyPostgreSqlTargetAsync(targetBuilder.ConnectionString, CancellationToken.None);
                return;
            }

            var report = await provider.GetRequiredService<DatabaseRestoreDrillService>()
                .RunAsync("test.sql", executePostgreSql: true, "one-time-test-token", CancellationToken.None);
            Assert.Equal("restored", report.Status);
            Assert.Equal("externalNotVerified", report.RecoveryKeyStatus);
            Assert.True(report.TableCount >= 3);
            Assert.True(File.Exists(report.ReportPath));
            Assert.Equal("restored file evidence", await File.ReadAllTextAsync(
                Path.Combine(Path.GetDirectoryName(report.ReportPath)!, "files",
                    manifestVersion >= 4 ? "profile-photos/photo.png" : withInventory ? "file-storage/user-photos/photo.png" : "file-storage/evidence.txt")));
            if (withManaged)
            {
                Assert.Empty(Directory.EnumerateFiles(sourceFiles));
                Assert.Equal(payloads["generation-1.bin"], await File.ReadAllBytesAsync(Path.Combine(
                    Path.GetDirectoryName(report.ReportPath)!, "files", "managed-generations", $"{firstId:D}.bin")));
                Assert.Equal(payloads["generation-2.bin"], await File.ReadAllBytesAsync(Path.Combine(
                    Path.GetDirectoryName(report.ReportPath)!, "files", "managed-generations", $"{secondId:D}.bin")));
            }

            await using var target = new NpgsqlConnection(targetBuilder.ConnectionString);
            await target.OpenAsync();
            await using var verify = target.CreateCommand();
            verify.CommandText = "SELECT label FROM public.restore_evidence WHERE id = 42";
            Assert.Equal("restored", await verify.ExecuteScalarAsync());
            verify.CommandText = "SELECT \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = 'notifications.email.enabled'";
            Assert.Equal("false", await verify.ExecuteScalarAsync());
            verify.CommandText = "SELECT \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = 'notifications.email.smtpPassword'";
            Assert.Equal(string.Empty, await verify.ExecuteScalarAsync());
            verify.CommandText = "SELECT \"Status\" FROM \"NotificationDeliveries\" WHERE \"Id\" = 1";
            Assert.Equal("skipped", await verify.ExecuteScalarAsync());
            verify.CommandText = "SELECT \"ConfigJson\" FROM \"NotificationSenders\" WHERE \"Id\" = 1 AND NOT \"IsEnabled\"";
            Assert.Equal("{}", await verify.ExecuteScalarAsync());
            if (withInventory)
            {
                verify.CommandText = "SELECT \"PhotoUrl\" FROM \"Users\" WHERE \"Id\" = 1";
                Assert.Equal("/user-photos/photo.png", await verify.ExecuteScalarAsync());
            }
            Assert.True((await backups.VerifyBackupAsync("test.sql")).Verified);

            await using var sourceCheck = new NpgsqlConnection(sourceBuilder.ConnectionString);
            await sourceCheck.OpenAsync();
            await using var original = sourceCheck.CreateCommand();
            original.CommandText = "SELECT \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = 'notifications.email.smtpPassword'";
            Assert.Equal("copied-secret", await original.ExecuteScalarAsync());
            original.CommandText = "SELECT \"ConfigJson\" FROM \"NotificationSenders\" WHERE \"Id\" = 1 AND \"IsEnabled\"";
            Assert.Equal("{\"smtpHost\":\"test-only.invalid\"}", await original.ExecuteScalarAsync());
        }
        finally
        {
            await DropDatabaseAsync(admin, targetName);
            await DropDatabaseAsync(admin, sourceName);
            NpgsqlConnection.ClearAllPools();
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var resolvedRoot = Path.GetFullPath(root);
            if (!resolvedRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolvedRoot).StartsWith("payplanner-restore-e2e-", StringComparison.Ordinal))
                throw new InvalidOperationException("The disposable restore directory is outside the expected temp root.");
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }

    private static async Task CreateDatabaseAsync(NpgsqlConnection admin, string databaseName)
    {
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(NpgsqlConnection admin, string databaseName)
    {
        await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, databaseName);
    }

    private static async Task RunPgDumpAsync(NpgsqlConnectionStringBuilder source, string backupPath)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_PG_DUMP") ?? "pg_dump")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--host"); start.ArgumentList.Add(source.Host ?? string.Empty);
        start.ArgumentList.Add("--port"); start.ArgumentList.Add(source.Port.ToString());
        start.ArgumentList.Add("--username"); start.ArgumentList.Add(source.Username ?? string.Empty);
        start.ArgumentList.Add("--dbname"); start.ArgumentList.Add(source.Database ?? string.Empty);
        start.ArgumentList.Add("--format=plain");
        start.ArgumentList.Add("--no-owner");
        start.ArgumentList.Add("--no-privileges");
        start.ArgumentList.Add("--file"); start.ArgumentList.Add(backupPath);
        if (!string.IsNullOrWhiteSpace(source.Password)) start.Environment["PGPASSWORD"] = source.Password;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pg_dump did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var errorTask = process.StandardError.ReadToEndAsync();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(timeout.Token);
        var error = await errorTask;
        _ = await outputTask;
        Assert.True(process.ExitCode == 0, error);
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
