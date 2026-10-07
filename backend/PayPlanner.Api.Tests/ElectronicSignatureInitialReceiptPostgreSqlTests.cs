using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

// Native published-receipt coverage using the existing isolated race fixture helpers.
public sealed partial class ElectronicSignaturePreparationRaceTests
{
    [Theory]
    [InlineData("intact")]
    [InlineData("binding-version")]
    [InlineData("deleted")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("checksum")]
    [InlineData("replica-pending")]
    [InlineData("commit-pending")]
    [InlineData("tombstone")]
    public async Task NativeInitialManagedReplayRequiresCanonicalPublishedReceipt(string damage)
    {
        const string prefix = "pp_esign_receipt_";
        var name = prefix + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        NpgsqlConnection? admin = null;
        var created = false;
        try
        {
            var connection = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
            Assert.False(string.IsNullOrWhiteSpace(connection), "Native identity cases require PAYPLANNER_TEST_POSTGRES.");
            var settings = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false };
            Assert.True(settings.Host is "127.0.0.1" or "::1", "Disposable database requires literal loopback.");
            admin = new NpgsqlConnection(settings.ConnectionString);
            await admin.OpenAsync();
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{name}\"";
                await create.ExecuteNonQueryAsync();
                created = true;
            }
            Directory.CreateDirectory(root);
            settings.Database = name;
            var dbOptions = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
            await using var db = new PostgreSqlPaymentContext(dbOptions, useObjectAuditModel: false);
            await db.Database.MigrateAsync();
            var user = new User
            {
                Email = "identity@example.test",
                FullName = "Тест повтора подписи",
                PasswordHash = "hash",
                Role = new Role { Name = "admin", Description = "Тест" },
                IsActive = true,
                IsApproved = true
            };
            var client = new Client { Name = "Синтетический клиент", IsActive = true };
            db.AddRange(user, client);
            await db.SaveChangesAsync();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = root,
                ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "managed"),
                ["ElectronicSignature:PreparationEnabled"] = "true",
                ["ElectronicSignature:StorageRoot"] = Path.Combine(root, "esign"),
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var environment = new RaceEnvironment(root);
            var provider = new ReplayProvider(new LocalStorageProvider(environment, configuration));
            var registry = new StorageProviderRegistry([provider, new LegacyLocalStorageProvider(environment, configuration)]);
            var options = new ReplayOptions(new()
            {
                Enabled = true,
                UploadCapacityChecksEnabled = true,
                ProductWriteEnabledClasses = ["esign-original"],
                ProductReadEnabledClasses = ["esign-original"],
                DefaultPolicy = new() { RequiredCopies = 1, WriteProviders = ["local"], ReadProviders = ["local"] }
            });
            var policy = new StoragePolicyService(options, registry);
            var storage = new StorageObjectService(db, policy, registry, options);
            var service = new ElectronicSignatureService(db, configuration, protection, environment,
                    new ProductStorageReader(db, storage, options), managedStorage: storage,
                    storagePublisher: new ProductStorageBindingService(db, policy), storageOptions: options,
                    stageCapacity: new StorageStageCapacityService(db, registry, options, environment, configuration));
            var bytes = "%PDF-1.7 native initial identity"u8.ToArray();
            var request = new ElectronicSignaturePreparation("invoice", 101, 1, client.Id,
                ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential,
                "KonturDiadoc", 10, true, user.Id,
                [new("Клиент", "client@example.test"), new("Представитель", "representative@example.test", "representative",
                    RequiresMachineReadablePowerOfAttorney: true, PowerOfAttorneyReference: "МЧД-1")]);
            var original = await service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", default);
            db.ChangeTracker.Clear();
            var binding = await db.StorageProductBindings.SingleAsync();
            var storageObject = await db.StorageObjects.SingleAsync();
            var generation = await db.StorageObjectGenerations.SingleAsync();
            var replica = await db.StorageReplicas.SingleAsync();
            var commit = await db.StorageOperations.SingleAsync(row => row.OperationType == "commit");
            switch (damage)
            {
                case "intact": break;
                case "binding-version": binding.Version++; break;
                case "deleted": storageObject.IsDeleted = true; break;
                case "owner": storageObject.OwnerId = "foreign-owner"; break;
                case "generation": storageObject.CurrentGeneration++; break;
                case "checksum": generation.Sha256 = new string('a', 64); break;
                case "replica-pending": replica.Status = StorageReplicaStatuses.Pending; break;
                case "commit-pending": commit.Status = StorageOperationStatuses.Pending; break;
                case "tombstone":
                    db.StorageTombstones.Add(new StorageTombstone
                    { StorageObjectId = storageObject.Id, Generation = generation.Generation, Reason = "Synthetic receipt test" }); break;
                default: throw new ArgumentOutOfRangeException(nameof(damage));
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var paths = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            Assert.Equal(2, paths.Length);
            var puts = provider.PutCalls;
            using var replayPdf = new MemoryStream(bytes);
            if (damage == "intact")
            {
                var replay = await service.PrepareAsync(request, replayPdf, "invoice.pdf", default);
                Assert.Equal(original.Id, replay.Id);
                Assert.Equal(original.PublicId, replay.PublicId);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(
                    request, replayPdf, "invoice.pdf", default));
            }
            Assert.Equal((long)bytes.Length, replayPdf.Position);
            Assert.Equal(puts, provider.PutCalls);
            Assert.Equal(original.Id, (await db.ElectronicSignatureEnvelopes.AsNoTracking().SingleAsync()).Id);
            Assert.Equal(paths, Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal));
            foreach (var path in paths) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (admin is not null)
            {
                if (created)
                {
                    Assert.StartsWith(prefix, name);
                    Assert.True(Guid.TryParseExact(name[prefix.Length..], "N", out _));
                    await using var command = admin.CreateCommand();
                    command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = @name";
                    command.Parameters.AddWithValue("name", name);
                    for (var attempt = 0; attempt < 50 && Convert.ToInt64(await command.ExecuteScalarAsync()) != 0; attempt++)
                        await Task.Delay(100);
                    Assert.Equal(0, Convert.ToInt64(await command.ExecuteScalarAsync()));
                    command.Parameters.Clear();
                    command.CommandText = $"DROP DATABASE \"{name}\"";
                    await command.ExecuteNonQueryAsync();
                }
                await admin.DisposeAsync();
            }
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetDirectoryName(Path.GetFullPath(root)));
            Assert.Equal(name, Path.GetFileName(root));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}