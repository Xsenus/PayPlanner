using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

using Microsoft.Extensions.Options;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Tests;

public sealed partial class ElectronicSignaturePreparationRaceTests
{
    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "missing-binding")]
    [InlineData(true, "missing-binding")]
    [InlineData(false, "foreign-owner")]
    [InlineData(true, "foreign-owner")]
    [InlineData(false, "missing-copy")]
    [InlineData(true, "missing-copy")]
    [InlineData(false, "binding-client")]
    [InlineData(true, "binding-client")]
    [InlineData(false, "signer")]
    [InlineData(true, "signer")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "cancelled")]
    [InlineData(false, "unrelated-error")]
    [InlineData(true, "unrelated-error")]
    public async Task ManagedSignatureWinnerAfterLookupReusesPublishedCopiesWithoutAnotherPut(bool postgres, string fault)
    {
        var name = "pp_esign_replay_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(root);
        PostgreSqlMigratedTemplate.DatabaseLease? lease = null;
        try
        {
            DbContextOptionsBuilder normal = postgres ? new DbContextOptionsBuilder<PostgreSqlPaymentContext>() : new DbContextOptionsBuilder<PaymentContext>();
            DbContextOptionsBuilder racing = postgres ? new DbContextOptionsBuilder<PostgreSqlPaymentContext>() : new DbContextOptionsBuilder<PaymentContext>();
            if (postgres)
            {
                lease = await postgresTemplate.CreateDatabaseAsync("pp_esign_replay_");
                normal.UseNpgsql(lease.ConnectionString);
                racing.UseNpgsql(lease.ConnectionString);
            }
            else
            {
                var connection = $"Data Source={Path.Combine(root, "race.db")};Pooling=False";
                normal.UseSqlite(connection);
                racing.UseSqlite(connection);
            }
            normal.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            racing.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            PaymentContext Create(DbContextOptions options) => postgres
                ? new PostgreSqlPaymentContext((DbContextOptions<PostgreSqlPaymentContext>)options, useObjectAuditModel: false)
                : new PaymentContext(options, useObjectAuditModel: false);
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
            (ElectronicSignatureService Service, ReplayProvider Primary, ReplayProvider Mirror, StorageOperationProcessor Worker) Runtime(
                PaymentContext db, Func<Task>? beforeWrite = null)
            {
                var primary = new ReplayProvider(new LocalStorageProvider(environment, configuration));
                var mirrorConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "mirror") }).Build();
                var mirror = new ReplayProvider(new LocalStorageProvider(environment, mirrorConfig), "mirror");
                var registry = new StorageProviderRegistry([primary, mirror, new LegacyLocalStorageProvider(environment, configuration)]);
                var options = new ReplayOptions(new()
                {
                    Enabled = true,
                    UploadCapacityChecksEnabled = true,
                    WorkerEnabled = true,
                    ProductWriteEnabledClasses = ["esign-original"],
                    ProductReadEnabledClasses = ["esign-original"],
                    ProviderConnections = new() { ["mirror"] = "owned" },
                    DefaultPolicy = new() { RequiredCopies = 2, WriteProviders = ["local", "mirror"], ReadProviders = ["local", "mirror"] }
                });
                var policy = new StoragePolicyService(options, registry);
                var storage = new StorageObjectService(db, policy, registry, options);
                return (new ElectronicSignatureService(db, configuration, protection, environment,
                    new ProductStorageReader(db, storage, options), managedStorage: new BeforeReplay(storage, beforeWrite),
                    storagePublisher: new ProductStorageBindingService(db, policy), storageOptions: options,
                    stageCapacity: new StorageStageCapacityService(db, registry, options, environment, configuration)), primary, mirror,
                    new StorageOperationProcessor(db, registry, options, TimeProvider.System,
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<StorageOperationProcessor>.Instance));
            }
            int userId;
            int clientId;
            await using (var setup = Create(normal.Options))
            {
                if (!postgres) await setup.Database.EnsureCreatedAsync();
                var user = new User
                {
                    Email = "replay@example.test",
                    FullName = "Тест гонки",
                    PasswordHash = "hash",
                    Role = new Role { Name = "admin", Description = "Тест" },
                    IsActive = true,
                    IsApproved = true
                };
                var client = new Client { Name = "Синтетический клиент", IsActive = true };
                setup.AddRange(user, client);
                await setup.SaveChangesAsync();
                userId = user.Id; clientId = client.Id;
            }
            var bytes = "%PDF-1.7 managed winner replay"u8.ToArray();
            var request = new ElectronicSignaturePreparation("invoice", 101, 1, clientId,
                ElectronicSignatureTypes.Qualified, ElectronicSignatureSigningOrders.Sequential,
                "KonturDiadoc", 10, false, userId, [new("Клиент", "client@example.test")]);
            Task<ElectronicSignatureEnvelope> Prepare(ElectronicSignatureService service) =>
                service.PrepareAsync(request, new MemoryStream(bytes), "invoice.pdf", default);
            ElectronicSignatureEnvelope? winner = null;
            await using (var db = Create(racing.Options))
            {
                var runtime = Runtime(db, async () =>
                {
                    await using var other = Create(normal.Options);
                    var winning = Runtime(other);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => Prepare(winning.Service));
                    await winning.Worker.ProcessBatchAsync("owned-messenger-replay-mirror", default);
                    winner = await Prepare(winning.Service);
                    Assert.Equal(1, winning.Primary.PutCalls); Assert.Equal(1, winning.Mirror.PutCalls);
                    switch (fault)
                    {
                        case "missing-binding":
                            other.StorageProductBindings.RemoveRange(await other.StorageProductBindings.ToArrayAsync()); break;
                        case "foreign-owner":
                            var changedObject = await other.StorageObjects.SingleAsync();
                            await other.Entry(changedObject).ReloadAsync();
                            changedObject.OwnerId = "foreign"; break;
                        case "missing-copy":
                            (await other.StorageReplicas.FirstAsync(r => r.ProviderKey == "mirror")).Status = StorageReplicaStatuses.Missing; break;
                        case "binding-client":
                            var changed = new Client { Name = "Другой синтетический клиент", IsActive = true };
                            other.Clients.Add(changed); await other.SaveChangesAsync();
                            (await other.ElectronicSignatureEnvelopes.SingleAsync()).ClientId = changed.Id; break;
                        case "signer":
                            (await other.ElectronicSignatureSigners.SingleAsync()).DisplayName = "Другой подписант"; break;
                        case "cancelled":
                            (await other.ElectronicSignatureEnvelopes.SingleAsync()).Status = ElectronicSignatureEnvelopeStatuses.Cancelled; break;
                    }
                    await other.SaveChangesAsync();
                    if (fault == "unrelated-error") throw new InvalidOperationException("Owned unrelated managed failure");
                });
                if (fault == "none")
                {
                    var result = await Prepare(runtime.Service);
                    Assert.NotNull(winner);
                    Assert.Equal(winner.Id, result.Id);
                    Assert.Equal(winner.Signers.Single().Id, result.Signers.Single().Id);
                    File.Delete(result.SnapshotPath);
                    var package = await runtime.Service.CreateEvidencePackageAsync(result.PublicId, default);
                    Assert.NotNull(package);
                    using var archive = System.IO.Compression.ZipFile.OpenRead(package.FullPath);
                    await using var content = archive.GetEntry("original/document.pdf")!.Open();
                    using var received = new MemoryStream();
                    await content.CopyToAsync(received);
                    Assert.Equal(bytes, received.ToArray());
                }
                else
                {
                    var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Prepare(runtime.Service));
                    if (fault == "unrelated-error") Assert.Equal("Owned unrelated managed failure", failure.Message);
                    else Assert.Equal("Ключ повторной загрузки уже используется для другого файла.", failure.Message);
                }
                Assert.Equal(0, runtime.Primary.PutCalls); Assert.Equal(0, runtime.Mirror.PutCalls);
                Assert.DoesNotContain(db.ChangeTracker.Entries<ElectronicSignatureEnvelope>(), e => e.State == EntityState.Added);
                await db.SaveChangesAsync();
            }
            await using (var verification = Create(normal.Options))
            {
                Assert.Single(await verification.ElectronicSignatureEnvelopes.AsNoTracking().ToArrayAsync());
                Assert.Single(await verification.ElectronicSignatureSigners.AsNoTracking().ToArrayAsync());
                Assert.Single(await verification.ElectronicSignatureEvents.AsNoTracking().ToArrayAsync());
                Assert.Equal(fault == "missing-binding" ? 0 : 1, await verification.StorageProductBindings.CountAsync());
                var item = Assert.Single(await verification.StorageObjects.AsNoTracking().ToArrayAsync());
                Assert.Equal(fault == "foreign-owner" ? "foreign" : winner!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), item.OwnerId);
                var replicas = await verification.StorageReplicas.AsNoTracking().ToArrayAsync();
                Assert.Equal(2, replicas.Length);
                Assert.Equal(fault == "missing-copy" ? 1 : 2, replicas.Count(r => r.Status == StorageReplicaStatuses.Available));
                Assert.DoesNotContain(await verification.StorageCapacityReservations.AsNoTracking().ToArrayAsync(),
                    r => r.Status == StorageCapacityReservationStatuses.Pending);
            }
            Assert.Equal(fault == "none" ? 0 : 1, Directory.GetFiles(Path.Combine(root, "esign"), "*.pdf", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class ReplayOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class ReplayProvider(IStorageProvider inner, string? key = null) : IStorageProvider, IStorageCapacityProvider
    {
        public int PutCalls { get; private set; }
        public string Key => key ?? inner.Key;
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId, long minimumFreeBytes, CancellationToken ct)
            => ((IStorageCapacityProvider)inner).ObserveCapacityAsync(resourceKey, key is null ? connectionId : null, minimumFreeBytes, ct);
        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
        { PutCalls++; return await inner.PutAsync(request, ct); }
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => inner.HeadAsync(locator, ct);
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
    }
    private sealed class BeforeReplay(IStorageObjectService inner, Func<Task>? beforeWrite) : IStorageObjectService
    {
        private Func<Task>? _beforeWrite = beforeWrite;
        public async Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken ct)
        {
            var before = _beforeWrite; _beforeWrite = null;
            if (before is not null) await before();
            return await inner.WriteAsync(request, ct);
        }
        public Task<StorageObjectReadResult?> OpenReadAsync(Guid objectId, CancellationToken ct) => inner.OpenReadAsync(objectId, ct);
        public Task<bool> DeleteAsync(Guid objectId, int? userId, string? reason, CancellationToken ct) => inner.DeleteAsync(objectId, userId, reason, ct);
    }
}
