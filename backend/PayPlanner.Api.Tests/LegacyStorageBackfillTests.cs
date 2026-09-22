using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class LegacyStorageBackfillTests
{
    [Fact]
    public async Task TaskFiles_DryRunApplyResumeAndMissing_AreSafeAndIdempotent()
    {
        await using var fixture = new Fixture();
        var task = new TaskJournalItem { ObjectName = "Объект", Title = "Задача", DueAt = DateTime.UtcNow.AddDays(1) };
        fixture.Db.TaskJournalItems.Add(task);
        await fixture.Db.SaveChangesAsync();
        var existingPath = Path.Combine(task.Id.ToString(), "first.txt");
        var existingFullPath = Path.Combine(fixture.FileRoot, "task-files", existingPath);
        Directory.CreateDirectory(Path.GetDirectoryName(existingFullPath)!);
        await File.WriteAllTextAsync(existingFullPath, "данные");
        fixture.Db.TaskJournalFiles.AddRange(
            new TaskJournalFile
            {
                TaskJournalItemId = task.Id, DocumentKey = "one", Version = 1, OriginalFileName = "первый.txt",
                StoredFileName = "first.txt", StoragePath = existingPath, ContentType = "text/plain", SizeBytes = new FileInfo(existingFullPath).Length,
                UploadedByName = "Тест",
            },
            new TaskJournalFile
            {
                TaskJournalItemId = task.Id, DocumentKey = "two", Version = 1, OriginalFileName = "нет.txt",
                StoredFileName = "missing.txt", StoragePath = Path.Combine(task.Id.ToString(), "missing.txt"), ContentType = "text/plain", SizeBytes = 10,
                UploadedByName = "Тест",
            });
        await fixture.Db.SaveChangesAsync();

        var dryRun = await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, BatchSize: 1), CancellationToken.None);
        Assert.False(dryRun.Applied);
        Assert.True(dryRun.HasMore);
        Assert.Single(dryRun.Items);
        Assert.Equal("ready", dryRun.Items[0].Status);
        Assert.Empty(await fixture.Db.StorageObjects.ToListAsync());

        var applied = await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, BatchSize: 1, Apply: true), CancellationToken.None);
        Assert.Equal(1, applied.Registered);
        var storageObject = await fixture.Db.StorageObjects.Include(item => item.Generations).ThenInclude(item => item.Replicas).SingleAsync();
        Assert.Equal("legacy-local", storageObject.Generations.Single().Replicas.Single().ProviderKey);
        Assert.Equal(StorageReplicaStatuses.Available, storageObject.Generations.Single().Replicas.Single().Status);

        var retry = await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, BatchSize: 1, Apply: true), CancellationToken.None);
        Assert.Equal(1, retry.AlreadyRegistered);
        Assert.Single(await fixture.Db.StorageObjects.ToListAsync());

        var next = await fixture.Service.RunAsync(new(LegacyStorageSources.TaskFiles, applied.NextAfterId!.Value, 10, true), CancellationToken.None);
        Assert.Equal(1, next.Missing);
        Assert.Equal("missing", Assert.Single(next.Items).Status);
        Assert.Single(await fixture.Db.StorageObjects.ToListAsync());
    }

    [Fact]
    public async Task LegacyProvider_MapsOnlyConfiguredRootsAndNeverDeletes()
    {
        await using var fixture = new Fixture();
        var file = Path.Combine(fixture.FileRoot, "task-files", "1", "document.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [1, 2, 3]);

        Assert.True(fixture.Provider.TryCreateLocator(file, out var locator));
        var metadata = await fixture.Provider.HeadAsync(locator, CancellationToken.None);
        var delete = await fixture.Provider.DeleteAsync(locator, CancellationToken.None);

        Assert.Equal(StorageOperationStatus.Succeeded, metadata.Status);
        Assert.Equal(3, metadata.SizeBytes);
        Assert.Equal(StorageOperationStatus.Conflict, delete.Status);
        Assert.True(File.Exists(file));
        Assert.False(fixture.Provider.TryCreateLocator(Path.Combine(fixture.Root, "outside.bin"), out _));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Provider.HeadAsync("files/../outside.bin", CancellationToken.None));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"payplanner-legacy-backfill-{Guid.NewGuid():N}");
        public string FileRoot { get; }
        public PaymentContext Db { get; }
        public LegacyLocalStorageProvider Provider { get; }
        public LegacyStorageCatalogBackfillService Service { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            FileRoot = Path.Combine(Root, "files");
            Directory.CreateDirectory(FileRoot);
            var environment = new TestEnvironment(Root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = FileRoot,
                ["ProfilePhotos:StorageRoot"] = Path.Combine(FileRoot, "user-photos"),
            }).Build();
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            Db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(_connection).Options);
            Db.Database.EnsureCreated();
            Provider = new LegacyLocalStorageProvider(environment, configuration);
            Service = new LegacyStorageCatalogBackfillService(Db, Provider, environment, configuration);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
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
