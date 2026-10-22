using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PayPlanner.Api.Tests;

public sealed class ProductStorageReaderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("replace")]
    [InlineData("version")]
    [InlineData("removed")]
    [InlineData("lateBinding")]
    [InlineData("owner")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("class")]
    public async Task ReadRejectsProductBindingOrIdentityChangedDuringDownload(string change)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await AssertReadRaceAsync(db, change);
    }

    [Fact]
    public async Task PostgreSqlRejectsProductPointerAndIdentityRaces()
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_reader_pointer_" + Guid.NewGuid().ToString("N");
        var adminSettings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            {
                await command.ExecuteNonQueryAsync();
                created = true;
            }
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = name };
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options);
            await db.Database.MigrateAsync();
            foreach (var change in new[] { "replace", "version", "removed", "lateBinding", "owner", "name", "type", "class" })
            {
                await AssertReadRaceAsync(db, change);
                await db.StorageProductBindings.ExecuteDeleteAsync();
                await db.StorageObjects.ExecuteDeleteAsync();
                db.ChangeTracker.Clear();
            }
            output.WriteLine("Owned PostgreSQL migrations and all eight product-pointer/identity races executed.");
        }
        finally
        {
            if (created)
            {
                await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
            }
        }
    }

    private static async Task AssertReadRaceAsync(PaymentContext db, string change)
    {
        const string source = LegacyStorageSources.TaskFiles;
        byte[] bytes = [1, 2, 3, 4];
        var objectId = change == "lateBinding" ? LegacyStorageSources.GetObjectId(source, 42) : Guid.NewGuid();
        var entry = new StorageObject
        {
            Id = objectId,
            DataClass = "task-file",
            OwnerType = "task-file",
            OwnerId = "42",
            LogicalName = "file.txt",
            ContentType = "text/plain",
            CurrentGeneration = 1
        };
        var generation = new StorageObjectGeneration
        {
            StorageObject = entry,
            Generation = 1,
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        };
        generation.Replicas.Add(new()
        {
            ProviderKey = "local",
            Locator = "owned-pointer",
            Status = StorageReplicaStatuses.Available,
            SizeBytes = bytes.Length,
            Sha256 = generation.Sha256
        });
        entry.Generations.Add(generation);
        db.StorageObjects.Add(entry);
        if (change != "lateBinding") db.StorageProductBindings.Add(new() { Source = source, SourceId = 42, StorageObjectId = objectId, Version = 1 });
        await db.SaveChangesAsync();
        var provider = new ReadProvider("local", bytes);
        provider.BeforeRead = async () =>
        {
            switch (change)
            {
                case "replace":
                case "lateBinding":
                    var replacement = new StorageObject
                    {
                        DataClass = "task-file",
                        OwnerType = "task-file",
                        OwnerId = "42",
                        LogicalName = "file.txt",
                        ContentType = "text/plain",
                        CurrentGeneration = 1
                    };
                    db.StorageObjects.Add(replacement);
                    await db.SaveChangesAsync();
                    if (change == "replace") await db.StorageProductBindings.ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.StorageObjectId, replacement.Id).SetProperty(b => b.Version, 2));
                    else { db.StorageProductBindings.Add(new() { Source = source, SourceId = 42, StorageObjectId = replacement.Id, Version = 1 }); await db.SaveChangesAsync(); }
                    break;
                case "version": await db.StorageProductBindings.ExecuteUpdateAsync(s => s.SetProperty(b => b.Version, 2)); break;
                case "removed": await db.StorageProductBindings.ExecuteDeleteAsync(); break;
                case "owner": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerId, "43")); break;
                case "name": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.LogicalName, "other.txt")); break;
                case "type": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.ContentType, "image/png")); break;
                case "class": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.DataClass, "profile-photo")); break;
            }
        };
        var registry = new StorageProviderRegistry([provider]);
        var monitor = new StaticMonitor(new()
        {
            Enabled = true,
            ProductReadEnabledClasses = ["task-file"],
            DefaultPolicy = new() { WriteProviders = ["local"], ReadProviders = ["local"] }
        });
        var storage = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var result = await new ProductStorageReader(db, storage, monitor).OpenReadAsync(source, 42, "file.txt", "text/plain", CancellationToken.None);
        if (result.File is { } unexpected) await unexpected.DisposeAsync();
        Assert.Equal(ProductStorageReadStatus.Unavailable, result.Status);
        Assert.Null(result.File);
        Assert.False(provider.LastRead!.CanRead);
        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), $"pp_verified_read_{objectId:N}_*.tmp"));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("queryFailure")]
    [InlineData("generation")]
    [InlineData("objectVersion")]
    [InlineData("deleted")]
    [InlineData("tombstone")]
    [InlineData("ownerType")]
    [InlineData("unchanged")]
    public async Task FinalValidationOwnsStreamUntilSuccessfulHandoff(string change)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var entry = new StorageObject
        {
            DataClass = "task-file",
            OwnerType = "task-file",
            OwnerId = "42",
            LogicalName = "file.txt",
            ContentType = "text/plain",
            CurrentGeneration = 1
        };
        db.StorageObjects.Add(entry);
        db.StorageProductBindings.Add(new() { Source = LegacyStorageSources.TaskFiles, SourceId = 42, StorageObject = entry, Version = 1 });
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        await using var stream = new MemoryStream([1, 2, 3, 4]);
        var file = new StorageObjectReadResult(entry.Id, 1, "text/plain", "file.txt", 4, stream, "local");
        var storage = new CompletedRead(async () =>
        {
            switch (change)
            {
                case "cancel": cancellation.Cancel(); break;
                case "queryFailure": await connection.CloseAsync(); break;
                case "generation": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.CurrentGeneration, 2)); break;
                case "objectVersion": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.Version, o => o.Version + 1)); break;
                case "deleted": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true)); break;
                case "ownerType": await db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerType, "profile-photo")); break;
                case "tombstone": db.StorageTombstones.Add(new() { StorageObjectId = entry.Id }); await db.SaveChangesAsync(); break;
            }
            return file;
        });
        var monitor = new StaticMonitor(new() { Enabled = true, ProductReadEnabledClasses = ["task-file"] });
        var reader = new ProductStorageReader(db, storage, monitor);
        if (change == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.OpenReadAsync(LegacyStorageSources.TaskFiles, 42, "file.txt", "text/plain", cancellation.Token));
        else if (change == "queryFailure")
            await Assert.ThrowsAsync<SqliteException>(() => reader.OpenReadAsync(LegacyStorageSources.TaskFiles, 42, "file.txt", "text/plain", cancellation.Token));
        else
        {
            var result = await reader.OpenReadAsync(LegacyStorageSources.TaskFiles, 42, "file.txt", "text/plain", cancellation.Token);
            if (change == "unchanged")
            {
                Assert.Equal(ProductStorageReadStatus.Available, result.Status);
                Assert.Same(file, result.File);
                Assert.True(stream.CanRead);
                await file.DisposeAsync();
            }
            else { Assert.Equal(ProductStorageReadStatus.Unavailable, result.Status); Assert.Null(result.File); }
        }
        Assert.False(stream.CanRead);
    }

    private sealed class CompletedRead(Func<Task<StorageObjectReadResult?>> read) : IStorageObjectService
    {
        public Task<StorageObjectReadResult?> OpenReadAsync(Guid objectId, CancellationToken cancellationToken) => read();
        public Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid objectId, int? deletedByUserId, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("fallback", ProductStorageReadStatus.Available)]
    [InlineData("case", ProductStorageReadStatus.Available)]
    [InlineData("chat", ProductStorageReadStatus.Available)]
    [InlineData("messenger", ProductStorageReadStatus.Available)]
    [InlineData("signatureOriginal", ProductStorageReadStatus.Available)]
    [InlineData("signatureArtifact", ProductStorageReadStatus.Available)]
    [InlineData("photo", ProductStorageReadStatus.Available)]
    [InlineData("bound", ProductStorageReadStatus.Available)]
    [InlineData("boundDeleted", ProductStorageReadStatus.Deleted)]
    [InlineData("boundWrongOwner", ProductStorageReadStatus.Unavailable)]
    [InlineData("boundWrongName", ProductStorageReadStatus.Unavailable)]
    [InlineData("corruptPrimary", ProductStorageReadStatus.Available)]
    [InlineData("unavailable", ProductStorageReadStatus.Unavailable)]
    [InlineData("deleted", ProductStorageReadStatus.Deleted)]
    [InlineData("wrongOwner", ProductStorageReadStatus.Unavailable)]
    [InlineData("wrongName", ProductStorageReadStatus.Unavailable)]
    [InlineData("wrongClass", ProductStorageReadStatus.Unavailable)]
    [InlineData("wrongType", ProductStorageReadStatus.Unavailable)]
    [InlineData("disabled", ProductStorageReadStatus.Legacy)]
    [InlineData("classDisabled", ProductStorageReadStatus.Legacy)]
    [InlineData("unregistered", ProductStorageReadStatus.Legacy)]
    public async Task TaskFile_UsesVerifiedCopiesOrFailsClosed(string scenario, ProductStorageReadStatus expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        byte[] bytes = [1, 2, 3, 4];
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var source = scenario switch
        {
            "case" => LegacyStorageSources.CaseDocuments,
            "chat" => LegacyStorageSources.InternalChat,
            "messenger" => LegacyStorageSources.MessengerSnapshots,
            "signatureOriginal" => LegacyStorageSources.ElectronicSignatureOriginals,
            "signatureArtifact" => LegacyStorageSources.ElectronicSignatures,
            "photo" => LegacyStorageSources.ProfilePhotos,
            _ => LegacyStorageSources.TaskFiles,
        };
        var dataClass = scenario switch
        {
            "case" => "case-document",
            "chat" => "chat-attachment",
            "messenger" => "messenger-delivery",
            "signatureOriginal" => "esign-original",
            "signatureArtifact" => "esign-artifact",
            "photo" => "profile-photo",
            _ => "task-file",
        };
        var objectId = scenario.StartsWith("bound", StringComparison.Ordinal) ? Guid.NewGuid() : LegacyStorageSources.GetObjectId(source, 42);
        var entry = new StorageObject
        {
            Id = objectId,
            DataClass = scenario == "wrongClass" ? "case-document" : dataClass,
            OwnerType = dataClass,
            OwnerId = scenario is "wrongOwner" or "boundWrongOwner" ? "43" : "42",
            LogicalName = scenario is "wrongName" or "boundWrongName" ? "other.txt" : "file.txt",
            ContentType = scenario == "wrongType" ? "image/png" : "text/plain",
            IsDeleted = scenario is "deleted" or "boundDeleted",
            CurrentGeneration = 1,
        };
        var generation = new StorageObjectGeneration { StorageObject = entry, Generation = 1, SizeBytes = bytes.Length, Sha256 = hash };
        generation.Replicas.Add(new() { ProviderKey = "local", Locator = "primary", Status = StorageReplicaStatuses.Available, SizeBytes = bytes.Length, Sha256 = hash });
        generation.Replicas.Add(new() { ProviderKey = "mirror", Locator = "copy", Status = StorageReplicaStatuses.Available, SizeBytes = bytes.Length, Sha256 = hash });
        entry.Generations.Add(generation);
        if (scenario != "unregistered")
        {
            db.StorageObjects.Add(entry);
            if (scenario.StartsWith("bound", StringComparison.Ordinal))
                db.StorageProductBindings.Add(new() { Source = source, SourceId = 42, StorageObjectId = objectId, Version = 1 });
            await db.SaveChangesAsync();
        }
        var primary = new ReadProvider("local", scenario == "corruptPrimary" ? [9, 9, 9, 9] : null);
        var mirror = new ReadProvider("mirror", scenario == "unavailable" ? null : bytes);
        var registry = new StorageProviderRegistry([primary, mirror]);
        var monitor = new StaticMonitor(new()
        {
            Enabled = scenario != "disabled",
            ProductReadEnabledClasses = scenario == "classDisabled" ? ["case-document"] : [dataClass],
            DefaultPolicy = new() { WriteProviders = ["local", "mirror"], ReadProviders = ["local", "mirror"] },
        });
        var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var result = await new ProductStorageReader(db, service, monitor)
            .OpenReadAsync(source, 42, "file.txt", "text/plain", CancellationToken.None);
        Assert.Equal(expected, result.Status);
        if (result.File is { } file)
        {
            await using var ownedFile = file;
            Assert.Equal(objectId, file.ObjectId);
            Assert.Equal("mirror", file.ProviderKey);
            Assert.Equal(hash, file.Sha256);
            using var memory = new MemoryStream();
            await file.Content.CopyToAsync(memory);
            Assert.Equal(bytes, memory.ToArray());
        }
        else Assert.Null(result.File);
        if (expected is ProductStorageReadStatus.Legacy or ProductStorageReadStatus.Deleted
            || scenario.StartsWith("wrong", StringComparison.Ordinal) || scenario.StartsWith("boundWrong", StringComparison.Ordinal))
            Assert.Equal(0, primary.ReadCalls + mirror.ReadCalls);
        else
        {
            Assert.Equal(1, primary.ReadCalls);
            Assert.Equal(1, mirror.ReadCalls);
            Assert.Equal(StorageReplicaStatuses.Suspect,
                (await db.StorageReplicas.AsNoTracking().SingleAsync(item => item.ProviderKey == "local")).Status);
        }
    }

    private sealed class StaticMonitor(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class ReadProvider(string key, byte[]? bytes) : IStorageProvider
    {
        public string Key => key;
        public int HeadCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public Func<Task>? BeforeRead { get; set; }
        public Stream? LastRead { get; private set; }
        public StorageProviderCapabilities Capabilities { get; } = new(true, true, true, true, false, false, false, false, false);
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken)
        {
            HeadCalls++;
            return Task.FromResult(bytes is null
                ? new StorageObjectMetadata(StorageOperationStatus.NotFound, locator, 0, null, null)
                : new StorageObjectMetadata(StorageOperationStatus.Succeeded, locator, bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), DateTime.UtcNow));
        }
        public async Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken)
        {
            ReadCalls++;
            Assert.Equal(0, HeadCalls);
            if (BeforeRead is not null) await BeforeRead();
            return bytes is null
                ? new StorageReadResult(StorageOperationStatus.NotFound, locator, null, 0)
                : new StorageReadResult(StorageOperationStatus.Succeeded, locator, LastRead = new MemoryStream(bytes, false), bytes.Length);
        }
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
