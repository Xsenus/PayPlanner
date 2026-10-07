using System.IO.Compression;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ElectronicSignaturePostgreSqlTests
{
    [Fact]
    public async Task PreparationFailureBoundariesPreserveDatabaseLinksAndExactPdfBytes()
    {
        var connectionString = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var databaseName = "pp_esign_atomic_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), databaseName);
        var adminSettings = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        Assert.Contains(adminSettings.Host, new[] { "127.0.0.1", "::1" });
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
                created = true;
                Console.WriteLine($"Owned signature regression database: {databaseName}");
            }
            Directory.CreateDirectory(root);
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = databaseName };
            await using (var migration = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options))
            {
                await migration.Database.MigrateAsync();
                Assert.False(migration.Database.HasPendingModelChanges());
            }
            var failure = new PreparationFailure();
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString)
                .AddInterceptors(failure, new PreparationSaveFailure(failure)).Options;
            int userId;
            int clientId;
            await using (var setup = new PostgreSqlPaymentContext(options, useObjectAuditModel: false))
            {
                var email = await setup.SystemSettings.SingleOrDefaultAsync(r => r.Key == "notifications.email.enabled");
                if (email is null) setup.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
                else email.Value = "false";
                var user = new User
                {
                    Email = "esign-atomic@example.test",
                    FullName = "Тест подписи",
                    PasswordHash = "hash",
                    Role = new Role { Name = "admin", Description = "Администратор" },
                    IsActive = true,
                    IsApproved = true
                };
                var client = new Client { Name = "Синтетический клиент подписи", IsActive = true };
                setup.AddRange(user, client);
                await setup.SaveChangesAsync();
                userId = user.Id;
                clientId = client.Id;
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ElectronicSignature:PreparationEnabled"] = "true",
                ["ElectronicSignature:StorageRoot"] = Path.Combine(root, "esign"),
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var version = 0;
            foreach (var stage in new[] { "before-save", "after-save", "before-commit", "after-commit", "rollback" })
            {
                await using var db = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
                var service = new ElectronicSignatureService(db, configuration, protection, new TestEnvironment(root));
                var bytes = System.Text.Encoding.UTF8.GetBytes("%PDF-1.7 синтетическая подпись " + stage);
                var request = new ElectronicSignaturePreparation("invoice", 101, ++version, clientId,
                    ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential,
                    "KonturDiadoc", 10, false, userId, [new("Клиент", "client@example.test")]);
                var previousFiles = Directory.Exists(Path.Combine(root, "esign"))
                    ? Directory.GetFiles(Path.Combine(root, "esign"), "*.pdf", SearchOption.AllDirectories) : [];
                failure.Stage = stage;
                await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", CancellationToken.None));
                failure.Stage = null;
                await using var verification = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
                var saved = await verification.ElectronicSignatureEnvelopes.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.DocumentVersion == version);
                var newFiles = Directory.GetFiles(Path.Combine(root, "esign"), "*.pdf", SearchOption.AllDirectories)
                    .Except(previousFiles).ToArray();
                if (stage == "after-commit")
                {
                    Assert.NotNull(saved);
                    Assert.Equal(saved!.SnapshotPath, Assert.Single(newFiles));
                    Assert.Equal(bytes, await File.ReadAllBytesAsync(saved.SnapshotPath));
                    var replay = await service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", CancellationToken.None);
                    Assert.Equal(saved.Id, replay.Id);
                    Assert.Equal(1, await verification.ElectronicSignatureEnvelopes.CountAsync());
                    Assert.Equal(1, await verification.ElectronicSignatureSigners.CountAsync());
                    Assert.Equal(1, await verification.ElectronicSignatureEvents.CountAsync());
                }
                else
                {
                    Assert.Null(saved);
                    if (stage is "before-commit" or "rollback")
                        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(newFiles)));
                    else Assert.Empty(newFiles);
                    await db.SaveChangesAsync();
                    Assert.False(await verification.ElectronicSignatureEnvelopes.AnyAsync(item => item.DocumentVersion == version));
                }
                Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
            }
            Console.WriteLine("Owned migrated PostgreSQL: five signature preparation failure boundaries executed.");
        }
        finally
        {
            if (created)
            {
                await using var sessions = admin.CreateCommand();
                sessions.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = @name";
                sessions.Parameters.AddWithValue("name", databaseName);
                for (var attempt = 0; attempt < 50 && (long)(await sessions.ExecuteScalarAsync())! != 0; attempt++)
                    await Task.Delay(100);
                Assert.Equal(0L, (long)(await sessions.ExecuteScalarAsync())!);
                Assert.Equal("pp_esign_atomic_", databaseName[..16]);
                Assert.True(Guid.TryParseExact(databaseName[16..], "N", out _));
                await using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP DATABASE \"{databaseName}\"";
                await drop.ExecuteNonQueryAsync();
            }
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(root)));
            Assert.Equal(databaseName, Path.GetFileName(root));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PreparationFailure : DbTransactionInterceptor
    {
        public string? Stage { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Stage == "before-commit") throw new IOException("Owned synthetic signature commit failure.");
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Stage == "after-commit") throw new IOException("Owned synthetic signature commit acknowledgement failure.");
            return Task.CompletedTask;
        }
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Stage == "rollback") throw new IOException("Owned synthetic signature rollback failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PreparationSaveFailure(PreparationFailure failure) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (failure.Stage is "before-save" or "rollback") throw new IOException("Owned synthetic signature save failure.");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (failure.Stage == "after-save") throw new IOException("Owned synthetic signature save acknowledgement failure.");
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EvidencePackageUsesFreshMigratedDatabaseAndRejectsChangedSources(bool managedRead, bool managedWrite)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        const string prefix = "pp_esign_test_";
        var databaseName = prefix + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), databaseName);
        Directory.CreateDirectory(root);
        var adminSettings = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        Assert.Contains(adminSettings.Host, new[] { "127.0.0.1", "::1" });
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
                created = true;
                Console.WriteLine($"Owned signature regression database: {databaseName}");
            }
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = databaseName };
            await using (var migration = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options))
            {
                await migration.Database.MigrateAsync();
                Assert.False(migration.Database.HasPendingModelChanges());
            }
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options;
            await using var db = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
            var email = await db.SystemSettings.SingleOrDefaultAsync(r => r.Key == "notifications.email.enabled");
            if (email is null) db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" });
            else email.Value = "false";
            var user = new User
            {
                Email = "esign@example.test",
                FullName = "Тест подписи",
                PasswordHash = "hash",
                Role = new Role { Name = "admin", Description = "Администратор" },
                IsActive = true,
                IsApproved = true
            };
            var client = new Client { Name = "Тест подписи PostgreSQL", IsActive = true };
            db.AddRange(user, client);
            await db.SaveChangesAsync();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = Path.Combine(root, "files"),
                ["ElectronicSignature:StorageRoot"] = Path.Combine(root, "esign"),
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            var environment = new TestEnvironment(root);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var service = new ElectronicSignatureService(db, configuration, protection, environment);
            if (managedWrite)
            {
                var registry = new StorageProviderRegistry([new LocalStorageProvider(environment, configuration), new LegacyLocalStorageProvider(environment, configuration)]);
                var monitor = new StaticStorageOptions(new()
                {
                    Enabled = true,
                    UploadCapacityChecksEnabled = true,
                    ProductReadEnabledClasses = ["esign-original"],
                    ProductWriteEnabledClasses = ["esign-original"]
                });
                var policy = new StoragePolicyService(monitor, registry);
                var objects = new StorageObjectService(db, policy, registry, monitor);
                service = new ElectronicSignatureService(db, configuration, protection, environment,
                    new ProductStorageReader(db, objects, monitor), managedStorage: objects,
                    storagePublisher: new ProductStorageBindingService(db, policy), storageOptions: monitor, stageCapacity: new StorageStageCapacityService(db, registry, monitor, environment, configuration));
            }
            var originalBytes = "%PDF-1.7 PostgreSQL evidence"u8.ToArray();
            var envelope = await service.PrepareAsync(new("invoice", 101, 1, client.Id,
                ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential, "KonturDiadoc",
                10, false, user.Id, [new("Клиент", "client@example.test")]), new MemoryStream(originalBytes), "invoice.pdf", CancellationToken.None);
            var artifactBytes = "detached signature PostgreSQL"u8.ToArray();
            var artifactPath = Path.Combine(Path.GetDirectoryName(envelope.SnapshotPath)!, "signature.bin");
            await File.WriteAllBytesAsync(artifactPath, artifactBytes);
            var artifact = new ElectronicSignatureArtifact
            {
                EnvelopeId = envelope.Id,
                ArtifactType = "detached-signature",
                FileName = "signature.bin",
                StoragePath = artifactPath,
                SizeBytes = artifactBytes.Length,
                ChecksumSha256 = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant()
            };
            db.ElectronicSignatureArtifacts.Add(artifact);
            await db.SaveChangesAsync();
            LegacyStorageCatalogBackfillService? backfill = null;
            if (managedWrite)
            {
                Assert.Equal(envelope.Id, (await db.StorageProductBindings.AsNoTracking().SingleAsync()).SourceId);
                Assert.Equal(envelope.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    (await db.StorageObjects.AsNoTracking().SingleAsync()).OwnerId);
                var request = new ElectronicSignaturePreparation("invoice", 101, 1, client.Id,
                    ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential, "KonturDiadoc",
                    10, false, user.Id, [new("Клиент", "client@example.test")]);
                Assert.Equal(envelope.Id, (await service.PrepareAsync(request, new MemoryStream(originalBytes), "invoice.pdf", CancellationToken.None)).Id);
                File.Delete(envelope.SnapshotPath);
            }
            else if (managedRead)
            {
                var legacy = new LegacyLocalStorageProvider(environment, configuration);
                backfill = new LegacyStorageCatalogBackfillService(db, legacy, environment, configuration);
                foreach (var source in new[] { LegacyStorageSources.ElectronicSignatureOriginals, LegacyStorageSources.ElectronicSignatures })
                {
                    var dryRun = await backfill.RunAsync(new(source), CancellationToken.None);
                    Assert.Equal(1, dryRun.Scanned);
                    Assert.Equal(0, dryRun.Registered);
                    Assert.Equal(0, dryRun.Invalid);
                    var applied = await backfill.RunAsync(new(source, Apply: true), CancellationToken.None);
                    Assert.Equal(1, applied.Registered);
                    Assert.Equal(1, (await backfill.RunAsync(new(source, Apply: true), CancellationToken.None)).AlreadyRegistered);
                }
                var registry = new StorageProviderRegistry([legacy]);
                var monitor = new StaticStorageOptions(new()
                {
                    Enabled = true,
                    ProductReadEnabledClasses = ["esign-original", "esign-artifact"],
                    UploadCapacityChecksEnabled = true,
                    DefaultPolicy = new() { WriteProviders = ["legacy-local"], ReadProviders = ["legacy-local"] }
                });
                var objects = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
                service = new ElectronicSignatureService(db, configuration, protection, environment, new ProductStorageReader(db, objects, monitor),
                    storageOptions: monitor, stageCapacity: new StorageStageCapacityService(db, registry, monitor, environment, configuration));
            }
            var package = await service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None);
            Assert.NotNull(package);
            using (var archive = ZipFile.OpenRead(package!.FullPath))
            {
                foreach (var (entryName, expected) in new[] { ("original/document.pdf", originalBytes), ("artifacts/001-signature.bin", artifactBytes) })
                {
                    await using var input = archive.GetEntry(entryName)!.Open();
                    using var copy = new MemoryStream();
                    await input.CopyToAsync(copy);
                    Assert.Equal(expected, copy.ToArray());
                }
                await using var manifestStream = archive.GetEntry("manifest.json")!.Open();
                using var manifest = await JsonDocument.ParseAsync(manifestStream);
                Assert.Equal(2, manifest.RootElement.GetProperty("files").GetArrayLength());
                Assert.Equal(envelope.PublicId, manifest.RootElement.GetProperty("envelope").GetProperty("publicId").GetGuid());
            }
            if (managedRead || managedWrite)
            {
                var stage = await db.StorageCapacityReservations.AsNoTracking()
                    .SingleAsync(row => row.LocalLocator.EndsWith(".zip"));
                Assert.Equal(StorageCapacityReservationStatuses.Released, stage.Status);
                Assert.Equal(new FileInfo(package.FullPath).Length, stage.Bytes);
                Assert.NotNull(stage.ProductContextJson);
                Assert.True(StorageStageFileReceipt.TryParse(stage.StageFileReceiptJson, out var checkpoint));
                Assert.Equal(stage.Bytes, checkpoint!.PlannedBytes);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package.FullPath))).ToLowerInvariant(),
                    checkpoint.PlannedSha256);
                Assert.NotNull(stage.StageRootIdentity);
            }
            File.Delete(package.FullPath);
            // Keep size unchanged to prove checksum validation, not merely a size check.
            artifactBytes[0] ^= 1;
            await File.WriteAllBytesAsync(artifactPath, artifactBytes);
            if (backfill is not null)
            {
                var rejected = await backfill.RunAsync(new(LegacyStorageSources.ElectronicSignatures, Apply: true), CancellationToken.None);
                Assert.Equal(1, rejected.Invalid);
                Assert.Equal(2, await db.StorageObjects.CountAsync());
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateEvidencePackageAsync(envelope.PublicId, CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories));
            Assert.Equal(envelope.SnapshotPath, (await db.ElectronicSignatureEnvelopes.AsNoTracking().SingleAsync()).SnapshotPath);
        }
        finally
        {
            if (created)
            {
                await using var drop = admin.CreateCommand();
                Assert.True(databaseName.StartsWith(prefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(databaseName[prefix.Length..], "N", out _));
                await using var count = admin.CreateCommand();
                count.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = @name";
                count.Parameters.AddWithValue("name", databaseName);
                // Physical close and server-side visibility may differ briefly.
                // Keep the owned database if sessions do not end; never force a drop.
                long connections = -1;
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    connections = Convert.ToInt64(await count.ExecuteScalarAsync());
                    if (connections == 0) break;
                    await Task.Delay(100);
                }
                Assert.Equal(0L, connections);
                drop.CommandText = $"DROP DATABASE \"{databaseName}\"";
                await drop.ExecuteNonQueryAsync();
            }
            // Only the exact directory allocated above belongs to this fixture.
            Assert.Equal(databaseName, Path.GetFileName(root));
            Assert.StartsWith(prefix, databaseName, StringComparison.Ordinal);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticStorageOptions(StorageOrchestrationOptions settings) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => settings;
        public StorageOrchestrationOptions Get(string? name) => settings;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
