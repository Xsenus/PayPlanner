using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageProductBindingMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteSupportsCleanAndUpgradePathsWithBindingInvariants(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var db = new PaymentContext(options);
        if (upgrade)
        {
            await db.Database.MigrateAsync("20260928044337_AddDatabaseBackupAttempts");
            await SeedPreviousCatalogAsync(db);
            await SeedPreviousTaskLinksAsync(db);
        }
        await db.Database.MigrateAsync();
        if (upgrade) Assert.Equal("existing.png", (await db.StorageObjects.AsNoTracking().SingleAsync()).LogicalName);
        Assert.Contains("20260928201521_AddStorageProductBindings", await db.Database.GetAppliedMigrationsAsync());
        await AssertInvariantsAsync(db);
        await AssertPublisherAsync(db);
        await AssertTaskLinksAsync(db, upgrade);
        await using var stale = new PaymentContext(options);
        var staleBinding = await stale.StorageProductBindings.SingleAsync(b => b.SourceId == 42);
        var current = await db.StorageProductBindings.SingleAsync(b => b.SourceId == 42);
        current.Version++;
        await db.SaveChangesAsync();
        staleBinding.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlSupportsCleanAndUpgradePathsWithBindingInvariants(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_binding_test_" + Guid.NewGuid().ToString("N");
        var adminSettings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
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
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options;
            await using var db = new PostgreSqlPaymentContext(options);
            if (upgrade)
            {
                await db.Database.MigrateAsync("20260928044359_AddDatabaseBackupAttemptsPostgres");
                await SeedPreviousCatalogAsync(db);
                await SeedPreviousTaskLinksAsync(db);
            }
            await db.Database.MigrateAsync();
            if (upgrade) Assert.Equal("existing.png", (await db.StorageObjects.AsNoTracking().SingleAsync()).LogicalName);
            Assert.Contains("20260928201537_AddStorageProductBindingsPostgres", await db.Database.GetAppliedMigrationsAsync());
            await using (var indexCheck = new NpgsqlCommand("""
                SELECT indexdef FROM pg_indexes WHERE tablename = 'StorageOperations'
                  AND indexname = 'IX_StorageOperations_CommitIdempotencyPrefix'
                """, (NpgsqlConnection)db.Database.GetDbConnection()))
            {
                await db.Database.OpenConnectionAsync();
                var definition = Assert.IsType<string>(await indexCheck.ExecuteScalarAsync());
                Assert.Contains("varchar_pattern_ops", definition);
                Assert.Contains("commit", definition);
            }
            await AssertInvariantsAsync(db);
            await AssertPublisherAsync(db);
            await AssertTaskLinksAsync(db, upgrade);
            await using var stale = new PostgreSqlPaymentContext(options);
            var staleBinding = await stale.StorageProductBindings.SingleAsync(b => b.SourceId == 42);
            var current = await db.StorageProductBindings.SingleAsync(b => b.SourceId == 42);
            current.Version++;
            await db.SaveChangesAsync();
            staleBinding.Version++;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        }
        finally
        {
            if (created)
            {
                await using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP DATABASE \"{name}\" WITH (FORCE)";
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task SeedPreviousTaskLinksAsync(PaymentContext db)
    {
        var client = new Client { Name = "Миграция связей" };
        var clientCase = new ClientCase { Client = client, Title = "Дело" };
        var task = new TaskJournalItem { ClientCase = clientCase, Title = "Задача", DueAt = DateTime.UtcNow };
        // Схема ещё старая, а модель знает SalesRequestId: колонка нужна только на время вставки, её добавит настоящая миграция.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"TaskJournalItems\" ADD COLUMN \"SalesRequestId\" INTEGER NULL");
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"TaskJournalItems\" DROP COLUMN \"SalesRequestId\"");
        var uploadedAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var keys = new[] { new string('a', 64), "ambiguous", "mismatch", "inverse", "inverse" };
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var version = i == 4 ? 3 : 2;
            object timestamp = db.Database.IsNpgsql()
                ? new NpgsqlParameter("uploadTime", NpgsqlTypes.NpgsqlDbType.Timestamp)
                { Value = DateTime.SpecifyKind(uploadedAt, DateTimeKind.Unspecified) }
                : uploadedAt;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "TaskJournalFiles" ("TaskJournalItemId", "DocumentKey", "Version", "OriginalFileName",
                    "StoredFileName", "StoragePath", "ContentType", "SizeBytes", "UploadedByName", "UploadedAt")
                VALUES ({task.Id}, {key}, {version}, {"документ.txt"}, {"retained.txt"}, {"retained/path"},
                    {"text/plain"}, {4L}, {"Автор"}, {timestamp})
                """);
            if (i == 4) continue;
            var prefix = $"task-{task.Id}-";
            var caseKey = prefix + key[..Math.Min(key.Length, 64 - prefix.Length)];
            for (var caseVersion = 1; caseVersion <= (key == "ambiguous" ? 2 : 1); caseVersion++)
                db.ClientCaseDocuments.Add(new()
                {
                    ClientCaseId = clientCase.Id,
                    DocumentKey = caseKey,
                    Version = caseVersion,
                    OriginalFileName = "документ.txt",
                    StoredFileName = "case.txt",
                    StoragePath = "case/path",
                    ContentType = "text/plain",
                    SizeBytes = key == "mismatch" ? 5 : 4,
                    UploadedByName = "Автор",
                    UploadedAt = uploadedAt,
                });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task AssertTaskLinksAsync(PaymentContext db, bool upgrade)
    {
        Assert.False(db.Database.HasPendingModelChanges());
        if (upgrade)
        {
            var files = await db.TaskJournalFiles.AsNoTracking().ToListAsync();
            Assert.Equal(5, files.Count);
            var exact = Assert.Single(files, file => file.DocumentKey.Length == 64);
            Assert.NotNull(exact.ClientCaseDocumentId);
            var linked = await db.ClientCaseDocuments.AsNoTracking().SingleAsync(d => d.Id == exact.ClientCaseDocumentId);
            Assert.Equal(2, exact.Version);
            Assert.Equal(1, linked.Version);
            Assert.Equal(64, linked.DocumentKey.Length);
            Assert.All(files.Where(file => file.Id != exact.Id), file => Assert.Null(file.ClientCaseDocumentId));
            Assert.All(files, file => { Assert.Equal("retained/path", file.StoragePath); Assert.Equal(4, file.SizeBytes); });
        }
        var task = new TaskJournalItem { Title = "Новая задача", DueAt = DateTime.UtcNow };
        var document = new ClientCaseDocument
        {
            ClientCase = new ClientCase { Client = new Client { Name = "Клиент" }, Title = "Дело" },
            DocumentKey = "fresh",
            OriginalFileName = "fresh.txt",
            StoredFileName = "fresh.txt",
            StoragePath = "fresh.txt",
        };
        var fileRecord = new TaskJournalFile
        {
            TaskJournalItem = task,
            ClientCaseDocument = document,
            DocumentKey = "fresh",
            Version = 9,
            OriginalFileName = "fresh.txt",
            StoredFileName = "fresh.txt",
            StoragePath = "fresh.txt",
        };
        db.TaskJournalFiles.Add(fileRecord);
        await db.SaveChangesAsync();
        var fileId = fileRecord.Id;
        var documentId = document.Id;
        Assert.Equal(documentId, fileRecord.ClientCaseDocumentId);
        db.ChangeTracker.Clear();
        await db.ClientCaseDocuments.Where(d => d.Id == documentId).ExecuteDeleteAsync();
        Assert.Null((await db.TaskJournalFiles.AsNoTracking().SingleAsync(file => file.Id == fileId)).ClientCaseDocumentId);
        fileRecord = await db.TaskJournalFiles.SingleAsync(file => file.Id == fileId);
        fileRecord.ClientCaseDocumentId = int.MaxValue;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        Assert.Null((await db.TaskJournalFiles.AsNoTracking().SingleAsync(file => file.Id == fileId)).ClientCaseDocumentId);
    }

    private static async Task AssertInvariantsAsync(PaymentContext db)
    {
        var baselineCount = await db.StorageObjects.CountAsync();
        var oldObject = new StorageObject { DataClass = "profile-photo", OwnerType = "profile-photo", OwnerId = "42", LogicalName = "old.png" };
        var newObject = new StorageObject { DataClass = "profile-photo", OwnerType = "profile-photo", OwnerId = "42", LogicalName = "new.png" };
        db.StorageObjects.AddRange(oldObject, newObject);
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.StorageProductBindings.Add(new() { Source = "profile-photos", SourceId = 42, StorageObjectId = oldObject.Id, Version = 1 });
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.False(await db.StorageProductBindings.AnyAsync());
        db.StorageProductBindings.Add(new() { Source = "profile-photos", SourceId = 42, StorageObjectId = oldObject.Id, Version = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.StorageProductBindings.Add(new() { Source = "profile-photos", SourceId = 42, StorageObjectId = newObject.Id, Version = 1 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.StorageObjects.Remove(new StorageObject { Id = oldObject.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var binding = await db.StorageProductBindings.SingleAsync();
        binding.StorageObjectId = newObject.Id;
        binding.Version++;
        await db.SaveChangesAsync();
        Assert.Equal(baselineCount + 2, await db.StorageObjects.CountAsync());
        Assert.Equal("old.png", (await db.StorageObjects.AsNoTracking().SingleAsync(o => o.Id == oldObject.Id)).LogicalName);
        db.ChangeTracker.Clear();
        db.StorageProductBindings.Add(new() { Source = "profile-photos", SourceId = 43, StorageObjectId = Guid.NewGuid(), Version = 1 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    private static async Task SeedPreviousCatalogAsync(PaymentContext db)
    {
        db.StorageObjects.Add(new StorageObject
        {
            DataClass = "profile-photo",
            OwnerType = "profile-photo",
            OwnerId = "1",
            LogicalName = "existing.png"
        });
        await db.SaveChangesAsync();
    }

    private static async Task AssertPublisherAsync(PaymentContext db)
    {
        var targets = new[] { "first.png", "second.png" }.Select(name => new StorageObject
        {
            DataClass = "profile-photo",
            OwnerType = "profile-photo",
            OwnerId = "84",
            LogicalName = name,
            ContentType = "image/*",
            CurrentGeneration = 1,
            Version = 1
        }).ToArray();
        foreach (var target in targets)
        {
            var generation = new StorageObjectGeneration { StorageObject = target, Generation = 1, SizeBytes = 4, Sha256 = new string('a', 64) };
            foreach (var provider in new[] { "local", "mirror" })
                generation.Replicas.Add(new()
                {
                    ProviderKey = provider,
                    Locator = $"{provider}/{target.LogicalName}",
                    Status = StorageReplicaStatuses.Available,
                    SizeBytes = 4,
                    LastVerifiedAt = DateTime.UtcNow,
                    Sha256 = generation.Sha256
                });
            target.Generations.Add(generation);
        }
        db.StorageObjects.AddRange(targets);
        await db.SaveChangesAsync();
        var service = new ProductStorageBindingService(db, new PublishPolicy());
        var first = new ProductStorageBindingRequest(LegacyStorageSources.ProfilePhotos, 84, targets[0].Id, "first.png", "image/*", 0);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SET LOCAL TIME ZONE 'Europe/Moscow'");
            var beforePublish = DateTime.UtcNow.AddSeconds(-1);
            Assert.Equal("published", (await service.PublishAsync(first, CancellationToken.None)).Status);
            var publishedAt = await db.StorageProductBindings.AsNoTracking().Where(b => b.SourceId == 84).Select(b => b.UpdatedAt).SingleAsync();
            Assert.InRange(publishedAt.Ticks, beforePublish.Ticks, DateTime.UtcNow.AddSeconds(1).Ticks);
            Assert.Equal("alreadyPublished", (await service.PublishAsync(first, CancellationToken.None)).Status);
            var second = first with { ObjectId = targets[1].Id, LogicalName = "second.png", ExpectedVersion = 1 };
            Assert.True((await service.PublishAsync(second, CancellationToken.None)).Published);
            Assert.Equal("conflict", (await service.PublishAsync(first, CancellationToken.None)).Status);
            Assert.Equal(targets[1].Id, (await db.StorageProductBindings.AsNoTracking().SingleAsync(b => b.SourceId == 84)).StorageObjectId);
            await transaction.RollbackAsync();
        }
        Assert.False(await db.StorageProductBindings.AnyAsync(b => b.SourceId == 84));
        Assert.Equal(2, await db.StorageObjects.CountAsync(o => o.OwnerId == "84"));
    }

    private sealed class PublishPolicy : IStoragePolicyService
    {
        public StoragePolicyEvaluation Evaluate(string dataClass)
            => new(dataClass, "test", true, true, true, 2, ["LOCAL", "MIRROR"], ["local", "mirror"], null, false, [], []);
    }
}
