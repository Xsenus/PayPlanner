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

// Native replay identity coverage using the existing isolated race fixture helpers.
public sealed partial class ElectronicSignaturePreparationRaceTests
{
    [Theory]
    [InlineData(false, "identical")]
    [InlineData(true, "identical")]
    [InlineData(false, "archive")]
    [InlineData(true, "archive")]
    [InlineData(false, "mra")]
    [InlineData(true, "mra")]
    [InlineData(false, "party")]
    [InlineData(true, "party")]
    [InlineData(false, "signer-mra")]
    [InlineData(true, "signer-mra")]
    [InlineData(false, "power")]
    [InlineData(true, "power")]
    [InlineData(false, "tracked-identical")]
    [InlineData(true, "tracked-identical")]
    [InlineData(false, "tracked-conflict")]
    [InlineData(true, "tracked-conflict")]
    public async Task NativeInitialPreparedReplayRejectsChangedIdentityWithoutReplacingEnvelope(bool managed, string change)
    {
        const string prefix = "pp_esign_identity_";
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
            var service = managed
                ? new ElectronicSignatureService(db, configuration, protection, environment,
                    new ProductStorageReader(db, storage, options), managedStorage: storage,
                    storagePublisher: new ProductStorageBindingService(db, policy), storageOptions: options,
                    stageCapacity: new StorageStageCapacityService(db, registry, options, environment, configuration))
                : new ElectronicSignatureService(db, configuration, protection, environment);
            var bytes = "%PDF-1.7 native initial identity"u8.ToArray();
            var request = new ElectronicSignaturePreparation("invoice", 101, 1, client.Id,
                ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential,
                "KonturDiadoc", 10, true, user.Id,
                [new("Клиент", "client@example.test"), new("Представитель", "representative@example.test", "representative",
                    RequiresMachineReadablePowerOfAttorney: true, PowerOfAttorneyReference: "МЧД-1")]);
            var original = await service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", default);
            var signerIds = original.Signers.OrderBy(s => s.Order).Select(s => s.Id).ToArray();
            Assert.Equal(managed ? 1 : 0, provider.PutCalls);
            Assert.Equal(managed ? 1 : 0, await db.StorageObjects.CountAsync());
            Assert.Equal(managed ? 1 : 0, await db.StorageProductBindings.CountAsync());
            var puts = provider.PutCalls;
            var originalPdfPaths = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            Assert.Equal(managed ? 2 : 1, originalPdfPaths.Length);
            foreach (var path in originalPdfPaths) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            if (change.StartsWith("tracked-", StringComparison.Ordinal))
            {
                original.ArchiveRetentionYears = 75;
                db.ChangeTracker.DetectChanges();
                Assert.Equal(EntityState.Modified, db.Entry(original).State);
            }
            var originalCapacityState = await db.StorageCapacityReservations.AsNoTracking().OrderBy(row => row.IdempotencyKey)
                .Select(row => new { row.IdempotencyKey, row.Status }).ToArrayAsync();
            var changed = change switch
            {
                "identical" or "tracked-identical" => request,
                "archive" or "tracked-conflict" => request with { ArchiveRetentionYears = 75 },
                "mra" => request with { RequiresMachineReadablePowerOfAttorney = false },
                "party" => request with { Signers = request.Signers.Select((s, i) => i == 0 ? s with { PartyType = "representative" } : s).ToArray() },
                "signer-mra" => request with { Signers = request.Signers.Select((s, i) => i == 1 ? s with { RequiresMachineReadablePowerOfAttorney = false } : s).ToArray() },
                "power" => request with { Signers = request.Signers.Select((s, i) => i == 1 ? s with { PowerOfAttorneyReference = "МЧД-2" } : s).ToArray() },
                _ => throw new ArgumentOutOfRangeException(nameof(change))
            };
            if (!change.StartsWith("tracked-", StringComparison.Ordinal)) db.ChangeTracker.Clear();
            using var replayPdf = new MemoryStream(bytes);
            if (managed && change.StartsWith("tracked-", StringComparison.Ordinal))
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(
                    changed, replayPdf, "invoice.pdf", default));
                Assert.Contains("Недостаточно подтверждённого места", error.Message);
                Assert.Equal(0L, replayPdf.Position);
                Assert.Equal(originalCapacityState, await db.StorageCapacityReservations.AsNoTracking().OrderBy(row => row.IdempotencyKey)
                    .Select(row => new { row.IdempotencyKey, row.Status }).ToArrayAsync());
            }
            else
            {
                if (change is "identical" or "tracked-identical")
                {
                    var replay = await service.PrepareAsync(changed, replayPdf, "invoice.pdf", default);
                    Assert.Equal(original.Id, replay.Id);
                    Assert.Equal(original.PublicId, replay.PublicId);
                    Assert.Equal(request.ArchiveRetentionYears, replay.ArchiveRetentionYears);
                    Assert.Equal(signerIds, replay.Signers.OrderBy(s => s.Order).Select(s => s.Id));
                }
                else await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(
                    changed, replayPdf, "invoice.pdf", default));
                Assert.Equal((long)bytes.Length, replayPdf.Position);
            }
            if (change.StartsWith("tracked-", StringComparison.Ordinal))
            {
                Assert.Equal(75, original.ArchiveRetentionYears);
                Assert.Equal(EntityState.Modified, db.Entry(original).State);
            }
            Assert.Equal(puts, provider.PutCalls);
            db.ChangeTracker.Clear();
            var saved = Assert.Single(await db.ElectronicSignatureEnvelopes.AsNoTracking().Include(e => e.Signers).ToArrayAsync());
            Assert.Equal(original.Id, saved.Id);
            Assert.Equal(request.ArchiveRetentionYears, saved.ArchiveRetentionYears);
            Assert.Equal(request.RequiresMachineReadablePowerOfAttorney, saved.RequiresMachineReadablePowerOfAttorney);
            Assert.Equal(signerIds, saved.Signers.OrderBy(s => s.Order).Select(s => s.Id));
            var expectedSigners = request.Signers.ToArray();
            var savedSigners = saved.Signers.OrderBy(s => s.Order).ToArray();
            for (var i = 0; i < expectedSigners.Length; i++)
            {
                Assert.Equal(expectedSigners[i].PartyType, savedSigners[i].PartyType);
                Assert.Equal(expectedSigners[i].RequiresMachineReadablePowerOfAttorney, savedSigners[i].RequiresMachineReadablePowerOfAttorney);
                Assert.Equal(expectedSigners[i].PowerOfAttorneyReference, savedSigners[i].PowerOfAttorneyReference);
            }
            Assert.Equal(bytes, await File.ReadAllBytesAsync(saved.SnapshotPath));
            Assert.Equal(originalPdfPaths, Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal));
            foreach (var path in originalPdfPaths) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
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