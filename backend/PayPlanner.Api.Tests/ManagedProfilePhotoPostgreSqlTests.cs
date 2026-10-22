using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ManagedProfilePhotoPostgreSqlTests
{
    [Fact]
    public async Task MigratedCatalogServesPhotoOverHttpAndFailsClosedAfterReplicaCorruption()
    {
        var connection = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        const string prefix = "pp_photo_pg_test_";
        var name = prefix + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(root);
        var adminSettings = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{name}\"";
                await create.ExecuteNonQueryAsync();
                created = true;
            }
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = name };
            var dbOptions = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
            await using var db = new PostgreSqlPaymentContext(dbOptions, useObjectAuditModel: false);
            await db.Database.MigrateAsync();
            var user = new User
            {
                Email = "photo-pg@example.test",
                FullName = "Фото PostgreSQL",
                PasswordHash = "hash",
                Role = new Role { Name = "admin", Description = "Администратор" },
                IsActive = true,
                IsApproved = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var url = $"/user-photos/{user.Id}/photo.png";
            user.PhotoUrl = "https://crm.example.test" + url;
            await db.SaveChangesAsync();
            var photos = Path.Combine(root, "photos");
            var directory = Path.Combine(photos, user.Id.ToString());
            Directory.CreateDirectory(directory);
            var originalPath = Path.Combine(directory, "photo.png");
            byte[] bytes = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(originalPath, bytes);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = Path.Combine(root, "files"),
                ["ProfilePhotos:StorageRoot"] = photos,
                ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "objects"),
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            var environment = new TestEnvironment(root);
            var legacy = new LegacyLocalStorageProvider(environment, config);
            var local = new LocalStorageProvider(environment, config);
            var backfill = new LegacyStorageCatalogBackfillService(db, legacy, environment, config);
            var report = await backfill.RunAsync(new(LegacyStorageSources.ProfilePhotos, Apply: true), CancellationToken.None);
            Assert.Equal(1, report.Registered);
            var generation = await db.StorageObjectGenerations.SingleAsync();
            const string mirrorLocator = "photo/mirror.png";
            await using (var content = new MemoryStream(bytes))
            {
                var write = await local.PutAsync(new(mirrorLocator, content, "image/png", bytes.Length, generation.Sha256), CancellationToken.None);
                Assert.Equal(StorageOperationStatus.Succeeded, write.Status);
            }
            db.StorageReplicas.Add(new()
            {
                StorageObjectGenerationId = generation.Id,
                ProviderKey = "local",
                Locator = mirrorLocator,
                Status = StorageReplicaStatuses.Available,
                SizeBytes = bytes.Length,
                Sha256 = generation.Sha256
            });
            await db.SaveChangesAsync();
            var monitor = new StaticOptions(new()
            {
                Enabled = true,
                ProductReadEnabledClasses = ["profile-photo"],
                ProductWriteEnabledClasses = ["profile-photo"],
                DefaultPolicy = new() { WriteProviders = ["local"], ReadProviders = ["legacy-local", "local"] }
            });
            var registry = new StorageProviderRegistry([legacy, local]);
            using var photoProvider = new PhysicalFileProvider(photos);
            using var server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddScoped<PaymentContext>(_ => new PostgreSqlPaymentContext(dbOptions, useObjectAuditModel: false));
                services.AddSingleton<IOptionsMonitor<StorageOrchestrationOptions>>(monitor);
                services.AddScoped<IStorageObjectService>(sp => new StorageObjectService(sp.GetRequiredService<PaymentContext>(),
                    new StoragePolicyService(monitor, registry), registry, monitor));
                services.AddScoped<IProductStorageReader>(sp => new ProductStorageReader(sp.GetRequiredService<PaymentContext>(),
                    sp.GetRequiredService<IStorageObjectService>(), monitor));
            }).Configure(app =>
            {
                app.UseMiddleware<ManagedProfilePhotoMiddleware>();
                app.UseStaticFiles(new StaticFileOptions { FileProvider = photoProvider, RequestPath = "/user-photos" });
            }));
            using var client = server.CreateClient();
            // Corrupt the original without changing size; the real resolver must use its healthy copy.
            await File.WriteAllBytesAsync(originalPath, [9, 9, 9, 9]);
            using (var response = await client.GetAsync(url))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
                Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
                Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            }
            Assert.Equal(StorageReplicaStatuses.Suspect,
                (await db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == "legacy-local")).Status);
            using (var request = new HttpRequestMessage(HttpMethod.Head, url))
            using (var response = await client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            }
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 2);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
                Assert.Equal(new byte[] { 2, 3 }, await response.Content.ReadAsByteArrayAsync());
            }
            await File.WriteAllBytesAsync(Path.Combine(root, "objects", "photo", "mirror.png"), [8, 8, 8, 8]);
            using (var response = await client.GetAsync(url))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            }
            var entry = await db.StorageObjects.SingleAsync();
            entry.IsDeleted = true;
            await db.SaveChangesAsync();
            await File.WriteAllBytesAsync(originalPath, bytes);
            using (var response = await client.GetAsync(url)) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(user.PhotoUrl, (await db.Users.AsNoTracking().SingleAsync()).PhotoUrl);

            // Real upload and binding replacement on a fully migrated database,
            // followed by HTTP reading through a separate request scope.
            var policy = new StoragePolicyService(monitor, registry);
            var uploader = new ManagedProfilePhotoUploadService(db, new StorageObjectService(db, policy, registry, monitor),
                new ProductStorageBindingService(db, policy), environment, config, monitor);
            static IFormFile UploadFile(byte[] content) => new FormFile(new MemoryStream(content), 0, content.Length, "file", "upload.png")
            { Headers = new HeaderDictionary(), ContentType = "image/png" };
            var firstKey = Guid.NewGuid();
            var first = await uploader.UploadAsync(user.Id, firstKey, UploadFile(bytes), "https://crm.example.test", CancellationToken.None);
            Assert.Equal("uploaded", first.Status);
            Assert.Equal("uploaded", (await uploader.UploadAsync(user.Id, firstKey, UploadFile(bytes), "https://crm.example.test", CancellationToken.None)).Status);
            Assert.Equal(1, (await db.StorageProductBindings.AsNoTracking().SingleAsync()).Version);
            var replacement = await uploader.UploadAsync(user.Id, Guid.NewGuid(), UploadFile([5, 6, 7, 8]), "https://crm.example.test", CancellationToken.None);
            Assert.Equal("uploaded", replacement.Status);
            Assert.Equal("conflict", (await uploader.UploadAsync(user.Id, firstKey, UploadFile(bytes), "https://crm.example.test", CancellationToken.None)).Status);
            Assert.Equal(2, (await db.StorageProductBindings.AsNoTracking().SingleAsync()).Version);
            using (var response = await client.GetAsync(new Uri(replacement.PhotoUrl!).AbsolutePath))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(new byte[] { 5, 6, 7, 8 }, await response.Content.ReadAsByteArrayAsync());
            }
            Assert.True(File.Exists(originalPath));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));

            // Both requests capture the same domain snapshot before either can
            // publish. Separate contexts/connections exercise real row locking.
            await using var firstDb = new PostgreSqlPaymentContext(dbOptions, useObjectAuditModel: false);
            await using var secondDb = new PostgreSqlPaymentContext(dbOptions, useObjectAuditModel: false);
            var barrier = new UploadBarrier();
            ManagedProfilePhotoUploadService ConcurrentUploader(PaymentContext context) => new(context,
                new GatedStorage(new StorageObjectService(context, policy, registry, monitor), barrier),
                new ProductStorageBindingService(context, policy), environment, config, monitor);
            var competing = await Task.WhenAll(
                ConcurrentUploader(firstDb).UploadAsync(user.Id, Guid.NewGuid(), UploadFile([10, 11, 12]), "https://crm.example.test", CancellationToken.None),
                ConcurrentUploader(secondDb).UploadAsync(user.Id, Guid.NewGuid(), UploadFile([20, 21, 22]), "https://crm.example.test", CancellationToken.None));
            var winner = Assert.Single(competing, result => result.Status == "uploaded");
            Assert.Single(competing, result => result.Status == "conflict");
            Assert.Equal(3, (await db.StorageProductBindings.AsNoTracking().SingleAsync()).Version);
            Assert.Equal(winner.PhotoUrl, (await db.Users.AsNoTracking().SingleAsync()).PhotoUrl);
            using (var response = await client.GetAsync(new Uri(winner.PhotoUrl!).AbsolutePath))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var content = await response.Content.ReadAsByteArrayAsync();
                Assert.True(content.SequenceEqual(new byte[] { 10, 11, 12 }) || content.SequenceEqual(new byte[] { 20, 21, 22 }));
            }
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (created)
            {
                await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
            }
            Assert.Equal(name, Path.GetFileName(root));
            Assert.StartsWith(prefix, name, StringComparison.Ordinal);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private sealed class StaticOptions(StorageOrchestrationOptions settings) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => settings;
        public StorageOrchestrationOptions Get(string? name) => settings;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class UploadBarrier
    {
        private int _arrived;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }
    private sealed class GatedStorage(IStorageObjectService inner, UploadBarrier barrier) : IStorageObjectService
    {
        public async Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken ct)
        {
            var result = await inner.WriteAsync(request, ct);
            await barrier.ArriveAsync(ct);
            return result;
        }
        public Task<StorageObjectReadResult?> OpenReadAsync(Guid id, CancellationToken ct) => inner.OpenReadAsync(id, ct);
        public Task<bool> DeleteAsync(Guid id, int? userId, string? reason, CancellationToken ct) => inner.DeleteAsync(id, userId, reason, ct);
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
