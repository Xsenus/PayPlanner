using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupSnapshotInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsEveryNativeClassAndOldVersionsFromTheSnapshotNotLaterWrites(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var seed = await SeedFilesAsync(fixture.Db);
        await using var snapshot = await fixture.SnapshotAsync();
        fixture.Db.TaskJournalFiles.Add(new TaskJournalFile
        {
            TaskJournalItemId = seed.TaskId,
            DocumentKey = "task-key",
            Version = 3,
            StoragePath = "late.pdf",
            SizeBytes = 99,
        });
        await fixture.Db.SaveChangesAsync();
        var references = await CollectAsync(DatabaseBackupSnapshotInventory.ReadStoredFilesAsync(snapshot));
        Assert.Equal(8, references.Count);
        Assert.Equal(7, references.Select(item => item.SourceKind).Distinct().Count());
        Assert.Equal(new[] { 1, 2 }, references.Where(item => item.SourceKind == "task-file").Select(item => item.Version!.Value));
        Assert.DoesNotContain(references, item => item.StoredPath == "late.pdf");
        var chat = Assert.Single(references, item => item.SourceKind == "chat-attachment");
        Assert.Equal(seed.ThreadId, chat.ParentId);
        Assert.Equal(13, chat.SizeBytes);
        Assert.Equal("signature-original.pdf", Assert.Single(references, item => item.SourceKind == "esign-original").StoredPath);
        Assert.Equal(new string('c', 64), Assert.Single(references, item => item.SourceKind == "esign-artifact").Sha256);
        var photo = Assert.Single(references, item => item.SourceKind == "profile-photo");
        Assert.Equal("https://old-host.example.test/user-photos/photo.png", photo.StoredPath);
        Assert.Null(photo.SizeBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvalidNativePathOrNegativeSizeFailsRatherThanDisappearing(bool postgres, bool negativeSize)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        await SeedFilesAsync(fixture.Db);
        var file = await fixture.Db.TaskJournalFiles.FirstAsync();
        if (negativeSize) file.SizeBytes = -1;
        else file.StoragePath = "";
        await fixture.Db.SaveChangesAsync();
        await using var snapshot = await fixture.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(DatabaseBackupSnapshotInventory.ReadStoredFilesAsync(snapshot)));
    }

    [Fact]
    public async Task OrphanedChatAttachmentFailsInsteadOfBeingLostInAnInnerJoin()
    {
        await using var fixture = (await Fixture.CreateAsync(false))!;
        await SeedFilesAsync(fixture.Db);
        await fixture.Db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
        await fixture.Db.Database.ExecuteSqlRawAsync("UPDATE \"InternalChatAttachments\" SET \"MessageId\" = 999999;");
        await using var snapshot = await fixture.SnapshotAsync();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(DatabaseBackupSnapshotInventory.ReadStoredFilesAsync(snapshot)));
        Assert.Contains("chat-attachment", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsOldDeletedHeldGenerationsAllReplicaStatesAndExplicitMissingReplica(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var retainedUntil = DateTime.SpecifyKind(new DateTime(2035, 1, 2), DateTimeKind.Utc);
        var deleted = new StorageObject { CurrentGeneration = 2, IsDeleted = true };
        var old = Generation(deleted, 1);
        old.Replicas.Add(new StorageReplica { ProviderKey = "local", Locator = "old.bin", Status = StorageReplicaStatuses.DeletePending });
        old.Replicas.Add(new StorageReplica { ProviderKey = "s3", Locator = "old-key", Status = StorageReplicaStatuses.Missing });
        var current = Generation(deleted, 2);
        current.Replicas.Add(new StorageReplica { ProviderKey = "local", Locator = "current.bin", Status = StorageReplicaStatuses.DeletePending });
        var active = new StorageObject { CurrentGeneration = 1 };
        var missing = Generation(active, 1);
        fixture.Db.AddRange(old, current, missing,
            new StorageTombstone { StorageObject = deleted, Generation = 1, LegalHold = true, RetainUntil = retainedUntil.AddDays(-1) },
            new StorageTombstone { StorageObject = deleted, Generation = 2, RetainUntil = retainedUntil });
        await fixture.Db.SaveChangesAsync();
        await using var snapshot = await fixture.SnapshotAsync();
        var rows = await CollectAsync(DatabaseBackupSnapshotInventory.ReadStorageReplicasAsync(snapshot));
        Assert.Equal(4, rows.Count);
        var held = rows.Where(item => item.ObjectId == deleted.Id).ToArray();
        Assert.Equal(3, held.Length); // Two tombstones must not multiply each candidate replica.
        Assert.All(held, item =>
        {
            Assert.True(item.ObjectIsDeleted);
            Assert.True(item.LegalHold);
            Assert.Equal(retainedUntil.Ticks, item.RetainUntil!.Value.Ticks);
            Assert.Equal(7, item.SizeBytes);
            Assert.Equal(new string('a', 64), item.Sha256);
        });
        Assert.Equal(2, held.Count(item => item.Generation == 1));
        Assert.Contains(held, item => item.ReplicaStatus == StorageReplicaStatuses.Missing);
        Assert.Contains(held, item => item.ReplicaStatus == StorageReplicaStatuses.DeletePending);
        var withoutReplica = Assert.Single(rows, item => item.ObjectId == active.Id);
        Assert.Null(withoutReplica.ReplicaId);
        Assert.Null(withoutReplica.Locator);
        Assert.False(withoutReplica.ObjectIsDeleted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ObjectWithoutItsCurrentGenerationFailsIncludingDeletedHeldObjects(bool postgres, bool deleted)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var owner = new StorageObject { CurrentGeneration = 5, IsDeleted = deleted };
        fixture.Db.StorageObjects.Add(owner);
        if (deleted) fixture.Db.StorageTombstones.Add(new StorageTombstone
        { StorageObject = owner, Generation = 5, LegalHold = true });
        await fixture.Db.SaveChangesAsync();
        await using var snapshot = await fixture.SnapshotAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(DatabaseBackupSnapshotInventory.ReadStorageReplicasAsync(snapshot)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyStopAndCancellationReleaseTheReaderForAnotherSnapshotCommand(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var seed = await SeedFilesAsync(fixture.Db);
        fixture.Db.TaskJournalFiles.AddRange(Enumerable.Range(3, 600).Select(version => new TaskJournalFile
        {
            TaskJournalItemId = seed.TaskId,
            DocumentKey = "task-key",
            Version = version,
            StoragePath = $"version-{version}.pdf",
            SizeBytes = version,
        }));
        await fixture.Db.SaveChangesAsync();
        await using var snapshot = await fixture.SnapshotAsync();
        await using (var iterator = DatabaseBackupSnapshotInventory.ReadStoredFilesAsync(snapshot).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("task-file", iterator.Current.SourceKind);
        }
        await using var check = snapshot.CreateCommand("SELECT 1");
        Assert.NotNull(await check.ExecuteScalarAsync());
        using var cancelled = new CancellationTokenSource();
        await using (var iterator = DatabaseBackupSnapshotInventory.ReadStoredFilesAsync(snapshot, cancelled.Token).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        }
        Assert.NotNull(await check.ExecuteScalarAsync());
    }

    private static StorageObjectGeneration Generation(StorageObject owner, long number)
        => new() { StorageObject = owner, Generation = number, SizeBytes = 7, Sha256 = new string('a', 64) };

    private static async Task<(int TaskId, int ThreadId)> SeedFilesAsync(PaymentContext db)
    {
        var user = new User
        {
            Email = "snapshot@example.test",
            FullName = "Snapshot",
            PasswordHash = "test",
            Role = new Role { Name = "snapshot-role" },
            PhotoUrl = "https://old-host.example.test/user-photos/photo.png"
        };
        var task = new TaskJournalItem { Title = "Snapshot task" };
        var clientCase = new ClientCase { Client = new Client { Name = "Snapshot client" } };
        var thread = new InternalChatThread { Name = "Snapshot chat" };
        var message = new InternalChatMessage { Thread = thread, AuthorUser = user };
        var envelope = new ElectronicSignatureEnvelope
        {
            CreatedByUserId = 0,
            CreatedAt = DateTime.UtcNow,
            SnapshotPath = "signature-original.pdf",
            SizeBytes = 14,
            ChecksumSha256 = new string('b', 64)
        };
        db.AddRange(user, task, clientCase, message);
        await db.SaveChangesAsync();
        envelope.CreatedByUserId = user.Id;
        db.AddRange(envelope,
            new TaskJournalFile { TaskJournalItem = task, DocumentKey = "task-key", Version = 1, StoragePath = "task-v1.pdf", SizeBytes = 11 },
            new TaskJournalFile { TaskJournalItem = task, DocumentKey = "task-key", Version = 2, StoragePath = "task-v2.pdf", SizeBytes = 12 },
            new ClientCaseDocument { ClientCase = clientCase, DocumentKey = "case-key", StoragePath = "case.pdf", SizeBytes = 15 },
            new InternalChatAttachment { Message = message, StoredFileName = "chat.txt", Size = 13, UploadedByUser = user },
            new MessengerDocumentDelivery
            {
                RecipientBinding = new MessengerRecipientBinding(),
                CreatedByUserId = user.Id,
                SnapshotPath = "messenger.pdf",
                SizeBytes = 16,
                ChecksumSha256 = new string('d', 64),
                IdempotencyKey = "snapshot-test"
            },
            new ElectronicSignatureArtifact
            {
                Envelope = envelope,
                StoragePath = "signature-artifact.pdf",
                SizeBytes = 17,
                ChecksumSha256 = new string('c', 64)
            });
        await db.SaveChangesAsync();
        return (task.Id, thread.Id);
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> rows)
    {
        var result = new List<T>();
        await foreach (var row in rows) result.Add(row);
        return result;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? root;
        private readonly string? postgresAdmin;
        private readonly string? postgresName;
        private readonly SqliteConnection? sqlite;
        public PaymentContext Db { get; }
        private Fixture(PaymentContext db, string? root = null, SqliteConnection? sqlite = null,
            string? postgresAdmin = null, string? postgresName = null)
        { Db = db; this.root = root; this.sqlite = sqlite; this.postgresAdmin = postgresAdmin; this.postgresName = postgresName; }

        public static async Task<Fixture?> CreateAsync(bool postgres)
        {
            if (!postgres)
            {
                var root = Path.Combine(Path.GetTempPath(), "payplanner-snapshot-inventory-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "live.sqlite"), Pooling = false }.ConnectionString);
                await connection.OpenAsync();
                var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
                var fixture = new Fixture(db, root, connection);
                try { await db.Database.EnsureCreatedAsync(); return fixture; }
                catch { await fixture.DisposeAsync(); throw; }
            }
            var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
            if (string.IsNullOrEmpty(configured))
            {
                Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                    "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
                return null;
            }
            var builder = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
            Assert.Contains(builder.Host, new[] { "127.0.0.1", "localhost" });
            var adminString = builder.ConnectionString;
            var name = "payplanner_snapshot_inventory_" + Guid.NewGuid().ToString("N");
            await using (var admin = new NpgsqlConnection(adminString))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
                await create.ExecuteNonQueryAsync();
            }
            builder.Database = name;
            var postgresDb = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(builder.ConnectionString).Options);
            var postgresFixture = new Fixture(postgresDb, postgresAdmin: adminString, postgresName: name);
            try { await postgresDb.Database.EnsureCreatedAsync(); return postgresFixture; }
            catch { await postgresFixture.DisposeAsync(); throw; }
        }

        public async Task<DatabaseBackupSnapshotSession> SnapshotAsync()
        {
            if (sqlite is null) return await DatabaseBackupSnapshotSession.OpenPostgreSqlAsync(Db.Database.GetConnectionString()!, CancellationToken.None);
            var copy = Path.Combine(root!, Guid.NewGuid().ToString("N") + ".sqlite");
            await using var vacuum = sqlite.CreateCommand();
            vacuum.CommandText = "VACUUM INTO $path";
            vacuum.Parameters.AddWithValue("$path", copy);
            await vacuum.ExecuteNonQueryAsync();
            return await DatabaseBackupSnapshotSession.OpenSqliteCopyAsync(copy, sqlite.DataSource, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (sqlite is not null) await sqlite.DisposeAsync();
            if (root is not null) Directory.Delete(root, recursive: true);
            if (postgresName is not null)
            {
                await using var admin = new NpgsqlConnection(postgresAdmin);
                await admin.OpenAsync();
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{postgresName}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }
}
