using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedProfilePhotoUploadServiceTests
{
    [Fact]
    public async Task CapacityCheckedPhotoUploadReservesAndReleasesItsTemporaryCopy()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Options.UploadCapacityChecksEnabled = true;
        fixture.Options.UploadMinimumFreeBytes = 0;
        var uploaded = await fixture.Upload(Guid.NewGuid());
        Assert.Equal("uploaded", uploaded.Status);
        Assert.Equal(3, await fixture.Db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Released));
        Assert.Equal(0, await fixture.Db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Pending));
        Assert.Equal(1, await fixture.Db.StorageCapacityReservations.CountAsync(r => r.ResourceKey == "[\"legacy-local\",\"profile-current\"]"));
        Assert.Equal(6, await fixture.Db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
    }

    [Fact]
    public async Task UploadRetryReplacementAndStaleRetryPreserveVersions()
    {
        await using var f = await Fixture.CreateAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.Equal("uploaded", (await f.Upload(first)).Status);
        var firstUrl = (await f.Db.Users.AsNoTracking().SingleAsync()).PhotoUrl;
        Assert.Equal("uploaded", (await f.Upload(first)).Status);
        Assert.Equal(1, (await f.Db.StorageProductBindings.AsNoTracking().SingleAsync()).Version);
        Assert.Equal("conflict", (await f.Upload(first, [9, 9, 9, 9])).Status);
        Assert.Equal("uploaded", (await f.Upload(second)).Status);
        Assert.Equal("conflict", (await f.Upload(first)).Status);
        Assert.NotEqual(firstUrl, (await f.Db.Users.AsNoTracking().SingleAsync()).PhotoUrl);
        Assert.Equal(2, await f.Db.StorageObjects.CountAsync());
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(f.Root, "photos"), "*.png", SearchOption.AllDirectories).Count());
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
        await f.AssertCurrentBytesAsync();
    }

    [Fact]
    public async Task PendingCopyDoesNotChangePhotoAndCanResumeAfterRealReplication()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        Assert.Equal(f.OldUrl, (await f.Db.Users.AsNoTracking().SingleAsync()).PhotoUrl);
        Assert.False(await f.Db.StorageProductBindings.AnyAsync());
        Assert.Equal(1, (await f.Worker.ProcessBatchAsync("photo-test", CancellationToken.None)).Succeeded);
        Assert.Equal("uploaded", (await f.Upload(key)).Status);
        await f.AssertCurrentBytesAsync();
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EarlierPendingRequestCannotReplaceACompletedNewerRequest()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        var oldKey = Guid.NewGuid();
        var newKey = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(oldKey)).Status);
        Assert.Equal("pending", (await f.Upload(newKey)).Status);
        Assert.Equal(2, (await f.Worker.ProcessBatchAsync("photo-test", CancellationToken.None)).Succeeded);
        Assert.Equal("uploaded", (await f.Upload(newKey)).Status);
        var currentUrl = (await f.Db.Users.AsNoTracking().SingleAsync()).PhotoUrl;
        Assert.Equal("conflict", (await f.Upload(oldKey)).Status);
        Assert.Equal(currentUrl, (await f.Db.Users.AsNoTracking().SingleAsync()).PhotoUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseFailureNeverDeletesDurableBytesAndRetryRecovers(bool afterCommit)
    {
        await using var f = await Fixture.CreateAsync();
        var key = Guid.NewGuid();
        f.Fault.Mode = afterCommit ? "after" : "before";
        await Assert.ThrowsAsync<IOException>(() => f.Upload(key));
        f.Db.ChangeTracker.Clear();
        Assert.Equal(f.OldUrl, (await f.Db.Users.AsNoTracking().SingleAsync()).PhotoUrl);
        Assert.False(await f.Db.StorageProductBindings.AnyAsync());
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(f.Root, "objects"), "upload-*.png", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal("uploaded", (await f.Upload(key)).Status);
        await f.AssertCurrentBytesAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualSizeIsBoundedAndDeclaredSizeMustMatch(bool oversized)
    {
        await using var f = await Fixture.CreateAsync();
        var bytes = oversized ? new byte[4 * 1024 * 1024 + 1] : new byte[4];
        Assert.Equal("invalid", (await f.Service.UploadAsync(f.UserId, Guid.NewGuid(),
            new UntrustedFile(bytes, oversized ? 1 : 8), "https://crm.example.test", CancellationToken.None)).Status);
        Assert.False(await f.Db.StorageObjects.AnyAsync());
        Assert.Equal(f.OldUrl, (await f.Db.Users.AsNoTracking().SingleAsync()).PhotoUrl);
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public PaymentContext Db { get; }
        public string Root { get; }
        public int UserId { get; }
        public string OldUrl { get; }
        public CommitFault Fault { get; }
        public ManagedProfilePhotoUploadService Service { get; }
        public Func<Func<IStorageStageCapacityService, IStorageStageCapacityService>, ManagedProfilePhotoUploadService> WithStage { get; }
        public StorageOperationProcessor Worker { get; }
        public StorageOrchestrationOptions Options { get; }
        private readonly ProductStorageReader _reader;
        private Fixture(SqliteConnection connection, PaymentContext db, string root, User user, CommitFault fault,
            IStorageProviderRegistry registry, StaticOptions monitor, IWebHostEnvironment environment, IConfiguration config)
        {
            _connection = connection; Db = db; Root = root; UserId = user.Id; OldUrl = user.PhotoUrl!; Fault = fault;
            Options = monitor.CurrentValue;
            var policy = new StoragePolicyService(monitor, registry);
            var storage = new StorageObjectService(db, policy, registry, monitor);
            Service = new(db, storage, new ProductStorageBindingService(db, policy), environment, config, monitor,
                new StorageStageCapacityService(db, registry, monitor, environment, config));
            WithStage = decorate => new(db, storage, new ProductStorageBindingService(db, policy), environment, config, monitor,
                decorate(new StorageStageCapacityService(db, registry, monitor, environment, config)));
            Worker = new(db, registry, monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
            _reader = new(db, storage, monitor);
        }
        public Task<ManagedPhotoUploadResult> Upload(Guid key, byte[]? bytes = null)
            => Service.UploadAsync(UserId, key, new UntrustedFile(bytes ?? [1, 2, 3, 4], (bytes ?? [1, 2, 3, 4]).Length), "https://crm.example.test", CancellationToken.None);
        public async Task AssertCurrentBytesAsync()
        {
            var url = (await Db.Users.AsNoTracking().SingleAsync()).PhotoUrl!;
            var result = await _reader.OpenReadAsync(LegacyStorageSources.ProfilePhotos, UserId, url, "image/*", CancellationToken.None);
            Assert.Equal(ProductStorageReadStatus.Available, result.Status);
            await using var file = result.File!;
            using var copy = new MemoryStream();
            await file.Content.CopyToAsync(copy);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, copy.ToArray());
        }
        public static async Task<Fixture> CreateAsync(int requiredCopies = 1)
        {
            var root = Path.Combine(Path.GetTempPath(), "pp-photo-upload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fault = new CommitFault();
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(fault).Options, useObjectAuditModel: false);
            await db.Database.EnsureCreatedAsync();
            var user = new User { Email = "photo-upload@example.test", FullName = "Фото", PasswordHash = "hash", Role = new Role { Name = "admin", Description = "Администратор" } };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            user.PhotoUrl = $"https://crm.example.test/user-photos/{user.Id}/old.png";
            await db.SaveChangesAsync();
            Directory.CreateDirectory(Path.Combine(root, "photos", user.Id.ToString()));
            await File.WriteAllBytesAsync(Path.Combine(root, "photos", user.Id.ToString(), "old.png"), [9]);
            var env = new TestEnvironment(root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = Path.Combine(root, "files"),
                ["ProfilePhotos:StorageRoot"] = Path.Combine(root, "photos"),
                ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "objects")
            }).Build();
            var mirrorConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "mirror") }).Build();
            var registry = new StorageProviderRegistry([new LocalStorageProvider(env, config),
                new LegacyLocalStorageProvider(env, config), new MirrorProvider(new LocalStorageProvider(env, mirrorConfig))]);
            var monitor = new StaticOptions(new()
            {
                Enabled = true,
                ProductWriteEnabledClasses = ["profile-photo"],
                ProductReadEnabledClasses = ["profile-photo"],
                ProviderConnections = new() { ["mirror"] = "test" },
                DefaultPolicy = new() { RequiredCopies = requiredCopies, WriteProviders = requiredCopies == 2 ? ["local", "mirror"] : ["local"], ReadProviders = ["local", "mirror"] }
            });
            return new(connection, db, root, user, fault, registry, monitor, env, config);
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); await _connection.DisposeAsync();
            Assert.StartsWith("pp-photo-upload-", Path.GetFileName(Root), StringComparison.Ordinal);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class CommitFault : SaveChangesInterceptor
    {
        public string? Mode { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Mode == "before" && eventData.Context!.ChangeTracker.Entries<StorageOperation>().Any(e => e.Entity.OperationType == "commit"))
            { Mode = null; throw new IOException("Injected failure before commit."); }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Mode == "after" && eventData.Context!.ChangeTracker.Entries<StorageOperation>().Any(e => e.Entity.OperationType == "commit"))
            { Mode = null; throw new IOException("Injected failure after commit."); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class UntrustedFile(byte[] bytes, long length) : IFormFile
    {
        public string ContentType => "image/png"; public string ContentDisposition => "";
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length => length; public string Name => "file"; public string FileName => "photo.png";
        public Stream OpenReadStream() => new MemoryStream(bytes);
        public void CopyTo(Stream target) => target.Write(bytes);
        public Task CopyToAsync(Stream target, CancellationToken ct = default) => target.WriteAsync(bytes, ct).AsTask();
    }
    private sealed class MirrorProvider(LocalStorageProvider inner) : IStorageProvider
    {
        public string Key => "mirror"; public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest r, CancellationToken ct) => inner.PutAsync(r, ct);
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => inner.HeadAsync(locator, ct);
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
    }
    private sealed class StaticOptions(StorageOrchestrationOptions settings) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => settings;
        public StorageOrchestrationOptions Get(string? name) => settings;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
