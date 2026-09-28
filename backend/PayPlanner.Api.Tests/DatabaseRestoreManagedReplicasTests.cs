using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseRestoreManagedReplicasTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoreThanOneBatchIsPreparedWithBoundedCatalogQueries(bool postgres)
    {
        if (!CanRun(postgres)) return;
        await using var fixture = await Fixture.CreateAsync("none", postgres);
        await using (var db = fixture.OpenDb()) await fixture.PrepareAsync(db, CancellationToken.None);
        Assert.Equal(2, fixture.Counter.Lookups);
        await using var checkedDb = fixture.OpenDb();
        Assert.Equal(251, await checkedDb.StorageReplicas.CountAsync(row => row.ProviderKey == "local"
            && row.Status == StorageReplicaStatuses.Available && row.ConnectionId == null));
        Assert.Equal(251, await checkedDb.StorageReplicas.CountAsync(row => row.ProviderKey == "legacy-local"
            && row.Status == StorageReplicaStatuses.Missing));
        Assert.Equal(3, await checkedDb.StorageOperations.CountAsync(row => row.Status == StorageOperationStatuses.DeadLetter
            && row.LeaseOwner == null && row.LeaseExpiresAt == null));
        Assert.Equal(251, await checkedDb.StorageObjectGenerations.CountAsync());
        Assert.Equal(251, (await checkedDb.StorageObjects.SingleAsync()).CurrentGeneration);
    }

    [Theory]
    [InlineData("file", false)]
    [InlineData("footer", false)]
    [InlineData("cancel", false)]
    [InlineData("facts", false)]
    [InlineData("hash", false)]
    [InlineData("file", true)]
    [InlineData("footer", true)]
    [InlineData("cancel", true)]
    [InlineData("facts", true)]
    [InlineData("hash", true)]
    public async Task LateFailureRollsBackPreviouslySavedBatch(string failure, bool postgres)
    {
        if (!CanRun(postgres)) return;
        await using var fixture = await Fixture.CreateAsync(failure, postgres);
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancel") fixture.Counter.OnSecondLookup = cancellation.Cancel;
        await using (var db = fixture.OpenDb())
        {
            if (failure == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PrepareAsync(db, cancellation.Token));
            else await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PrepareAsync(db, CancellationToken.None));
        }
        Assert.Equal(failure == "hash" ? 0 : failure == "footer" ? 1 : 2, fixture.Counter.Lookups);
        await using var checkedDb = fixture.OpenDb();
        Assert.Equal(251, await checkedDb.StorageReplicas.CountAsync(row => row.ProviderKey == "s3"
            && row.Status == StorageReplicaStatuses.Available && row.ConnectionId == "original-cloud"));
        Assert.Equal(251, await checkedDb.StorageReplicas.CountAsync(row => row.ProviderKey == "legacy-local"
            && row.Status == StorageReplicaStatuses.Available));
        Assert.False(await checkedDb.StorageReplicas.AnyAsync(row => row.ProviderKey == "local"));
        Assert.Equal(3, await checkedDb.StorageOperations.CountAsync(row => row.Status != StorageOperationStatuses.DeadLetter
            && row.LeaseOwner == "source-worker"));
    }

    private static bool CanRun(bool postgres)
    {
        if (!postgres || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES"))) return true;
        Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
            "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
        return false;
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "footer")]
    [InlineData(true, "footer")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task NativePhotosUseBoundedQueriesAndRollbackLateFailures(bool postgres, string failure)
    {
        if (!CanRun(postgres)) return;
        await using var fixture = await Fixture.CreateAsync("none", postgres);
        await fixture.InitializeNativeAsync(failure);
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancel") fixture.Counter.OnSecondNativeLookup = cancellation.Cancel;
        await using (var db = fixture.OpenDb())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            if (failure == "none")
            {
                await fixture.PrepareNativeAsync(db, cancellation.Token);
                await transaction.CommitAsync();
            }
            else if (failure == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PrepareNativeAsync(db, cancellation.Token));
            else await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PrepareNativeAsync(db, cancellation.Token));
        }
        Assert.Equal(failure == "footer" ? 0 : 2, fixture.Counter.NativeLookups);
        await using var checkedDb = fixture.OpenDb();
        Assert.Equal(251, await checkedDb.Users.CountAsync(row => row.PhotoUrl!.StartsWith(
            failure == "none" ? "/user-photos/profile-photo-" : "/user-photos/original-")));
        if (failure == "none") await fixture.VerifyNativePhotosAsync(checkedDb);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRuntimeDoesNotFollowOutputDirectoryLinks(bool postgres)
    {
        if (!CanRun(postgres)) return;
        await using var fixture = await Fixture.CreateAsync("none", postgres);
        await fixture.InitializeNativeAsync("none");
        fixture.Counter.OnFirstNativeLookup = fixture.CreateOutputLink;
        await using (var db = fixture.OpenDb())
        await using (var transaction = await db.Database.BeginTransactionAsync())
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PrepareNativeAsync(db, CancellationToken.None));
        fixture.VerifyNoOutputEscaped();
        await using var checkedDb = fixture.OpenDb();
        Assert.Equal(251, await checkedDb.Users.CountAsync(row => row.PhotoUrl!.StartsWith("/user-photos/original-")));
    }

    [Theory]
    [InlineData(false, "exact")]
    [InlineData(true, "exact")]
    [InlineData(false, "insufficient")]
    [InlineData(true, "insufficient")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "unavailable")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task NativeCapacityIsCheckedBeforeDirectoriesAndDatabaseUpdates(bool postgres, string scenario)
    {
        if (!CanRun(postgres)) return;
        await using var fixture = await Fixture.CreateAsync("none", postgres);
        await fixture.InitializeNativeAsync("none");
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        long Available(string directory)
        {
            calls++;
            Assert.True(Directory.Exists(directory));
            if (scenario == "unavailable") throw new IOException("Fixture capacity unavailable.");
            if (scenario == "cancel") cancellation.Cancel();
            var required = 64L * 1024 * 1024 + 251 * "восстановленная фотография"u8.Length;
            return scenario == "insufficient" ? required - 1 : required;
        }
        await using (var db = fixture.OpenDb())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            if (scenario == "exact")
            {
                await fixture.PrepareNativeAsync(db, cancellation.Token, Available);
                await transaction.CommitAsync();
            }
            else if (scenario == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PrepareNativeAsync(db, cancellation.Token, Available));
            else await Assert.ThrowsAsync<IOException>(() => fixture.PrepareNativeAsync(db, cancellation.Token, Available));
        }
        Assert.Equal(1, calls);
        Assert.Equal(scenario == "exact", fixture.HasNativeRuntimeDirectory);
        Assert.Equal(scenario == "exact" ? 2 : 0, fixture.Counter.NativeLookups);
        await using var checkedDb = fixture.OpenDb();
        Assert.Equal(251, await checkedDb.Users.CountAsync(row => row.PhotoUrl!.StartsWith(
            scenario == "exact" ? "/user-photos/profile-photo-" : "/user-photos/original-")));
        if (scenario == "exact") await fixture.VerifyNativePhotosAsync(checkedDb);
    }

    private sealed class LookupCounter : DbCommandInterceptor
    {
        public int Lookups { get; set; }
        public Action? OnSecondLookup { get; set; }
        public int NativeLookups { get; set; }
        public Action? OnFirstNativeLookup { get; set; }
        public Action? OnSecondNativeLookup { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("FROM \"StorageReplicas\"", StringComparison.Ordinal)
                && command.CommandText.Contains("JOIN \"StorageObjectGenerations\"", StringComparison.Ordinal))
            {
                if (++Lookups == 2) OnSecondLookup?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("FROM \"Users\"", StringComparison.Ordinal))
            {
                if (++NativeLookups == 1) OnFirstNativeLookup?.Invoke();
                if (NativeLookups == 2) OnSecondNativeLookup?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly string root = Path.Combine(Path.GetTempPath(), "payplanner-restored-managed-" + Guid.NewGuid().ToString("N"));
        private DbContextOptions<PaymentContext> options = null!;
        private DatabaseBackupPartManifest part = null!;
        private DatabaseBackupPartManifest nativePart = null!;
        private DatabaseBackupManifest nativeManifest = null!;
        private const string DatabasePrefix = "pp_restored_managed_";
        private readonly string databaseName = DatabasePrefix + Guid.NewGuid().ToString("N");
        private string? adminConnectionString;
        private bool databaseCreated;
        private const string InventorySha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public LookupCounter Counter { get; } = new();
        private string FilesRoot => Path.Combine(root, "files");
        private string MappingPath => Path.Combine(root, "mapping.jsonl");
        private string NativeMappingPath => Path.Combine(root, "native.jsonl");
        private string RuntimeRoot => Path.Combine(root, "runtime-native");
        public bool HasNativeRuntimeDirectory => Directory.Exists(RuntimeRoot);
        public PaymentContext OpenDb() => new(options);

        public static async Task<Fixture> CreateAsync(string failure, bool postgres)
        {
            var fixture = new Fixture();
            try { await fixture.InitializeAsync(failure, postgres); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task InitializeAsync(string failure, bool postgres)
        {
            Directory.CreateDirectory(Path.Combine(FilesRoot, "managed-generations"));
            var builder = new DbContextOptionsBuilder<PaymentContext>();
            if (postgres)
            {
                var adminBuilder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES"))
                { Database = "postgres", Pooling = false };
                Assert.Contains(adminBuilder.Host, new[] { "127.0.0.1", "localhost" });
                adminConnectionString = adminBuilder.ConnectionString;
                await using var admin = new NpgsqlConnection(adminConnectionString);
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
                await create.ExecuteNonQueryAsync();
                databaseCreated = true;
                var target = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName };
                builder.UseNpgsql(target.ConnectionString);
            }
            else builder.UseSqlite($"Data Source={Path.Combine(root, "restored.db")};Pooling=False");
            options = builder.AddInterceptors(Counter).Options;
            await using var db = OpenDb();
            await db.Database.EnsureCreatedAsync();
            var item = new StorageObject { CurrentGeneration = 251, DataClass = "document", LogicalName = "batch" };
            var selected = new List<StorageReplica>();
            var hashes = new List<string>();
            for (var index = 1; index <= 251; index++)
            {
                var generation = new StorageObjectGeneration
                {
                    Id = Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"),
                    Generation = index,
                };
                var payload = Encoding.UTF8.GetBytes("поколение " + index);
                generation.SizeBytes = payload.Length;
                generation.Sha256 = Convert.ToHexString(SHA256.HashData(payload));
                hashes.Add(generation.Sha256);
                var replica = new StorageReplica
                {
                    ProviderKey = "s3",
                    ConnectionId = "original-cloud",
                    Locator = "original-cloud/" + index,
                    Status = StorageReplicaStatuses.Available,
                    StorageObjectGeneration = generation,
                };
                selected.Add(replica);
                generation.Replicas.Add(replica);
                generation.Replicas.Add(new StorageReplica
                {
                    ProviderKey = "legacy-local",
                    Locator = "files/original/" + index,
                    Status = StorageReplicaStatuses.Available,
                });
                item.Generations.Add(generation);
                await File.WriteAllBytesAsync(Path.Combine(FilesRoot, "managed-generations", $"{generation.Id:D}.bin"),
                    failure == "file" && index == 251 ? "changed"u8.ToArray() : payload);
            }
            db.StorageObjects.Add(item);
            foreach (var status in new[] { StorageOperationStatuses.Pending, StorageOperationStatuses.Running, StorageOperationStatuses.Unknown })
                db.StorageOperations.Add(new StorageOperation
                {
                    StorageObject = item,
                    OperationType = StorageOperationTypes.Replicate,
                    Status = status,
                    IdempotencyKey = "fixture-" + status,
                    LeaseOwner = "source-worker",
                    LeaseExpiresAt = DateTime.UtcNow.AddHours(1),
                });
            await db.SaveChangesAsync();
            var lines = new List<string> { JsonSerializer.Serialize(new { kind = "header", formatVersion = 1, inventorySha256 = InventorySha }, Json) };
            for (var index = 0; index < selected.Count; index++)
            {
                var replica = selected[index];
                var generation = replica.StorageObjectGeneration;
                var reference = new DatabaseBackupStorageReplicaReference(generation.Id, item.Id, generation.Generation,
                    generation.SizeBytes, hashes[index], false, false, null, replica.Id, "s3", "original-cloud", replica.Locator, replica.Status);
                var binding = new DatabaseBackupManagedBinding(reference, "fixture.managedset",
                    $"managed-generations/{generation.Id:D}.bin", generation.SizeBytes, hashes[index]);
                lines.Add(JsonSerializer.Serialize(new { kind = "binding", binding }, Json));
            }
            lines.Add(JsonSerializer.Serialize(new { kind = "footer", bindingCount = failure == "footer" ? 0 : selected.Count }, Json));
            await File.WriteAllTextAsync(MappingPath, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
            var bytes = await File.ReadAllBytesAsync(MappingPath);
            part = new("managed-file-bindings", "fixture.managedfiles", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), true);
            if (failure == "facts")
            {
                selected[^1].StorageObjectGeneration.Sha256 = new string('b', 64);
                await db.SaveChangesAsync();
            }
            if (failure == "hash") await File.AppendAllTextAsync(MappingPath, " ");
            Counter.Lookups = 0;
        }

        public async Task InitializeNativeAsync(string failure)
        {
            var payload = "восстановленная фотография"u8.ToArray();
            var sha = Convert.ToHexString(SHA256.HashData(payload));
            var role = new Role { Name = "native-batch" };
            await using var db = OpenDb();
            var users = Enumerable.Range(1, 251).Select(index => new User
            {
                Email = $"native-{index}@example.test",
                PasswordHash = "test",
                Role = role,
                PhotoUrl = $"/user-photos/original-{index}.png",
            }).ToArray();
            db.Users.AddRange(users);
            await db.SaveChangesAsync();
            Directory.CreateDirectory(Path.Combine(FilesRoot, "profile-photos"));
            var lines = new List<string>
            {
                JsonSerializer.Serialize(new { kind = "header", formatVersion = 1, inventorySha256 = InventorySha, state = "complete" }, Json),
            };
            foreach (var user in users)
            {
                var path = $"profile-photos/{user.Id}.png";
                await File.WriteAllBytesAsync(Path.Combine(FilesRoot, path.Replace('/', Path.DirectorySeparatorChar)), payload);
                lines.Add(JsonSerializer.Serialize(new DatabaseBackupNativeBinding("binding",
                    new("profile-photo", user.Id, user.PhotoUrl!, null, null, null, null), "fixture.fileset", path,
                    false, payload.Length, sha), Json));
            }
            lines.Add(JsonSerializer.Serialize(new { kind = "footer", bindingCount = failure == "footer" ? 0 : users.Length }, Json));
            await File.WriteAllTextAsync(NativeMappingPath, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
            var bytes = await File.ReadAllBytesAsync(NativeMappingPath);
            nativePart = new("native-file-bindings", "fixture.nativefiles", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), true);
            nativeManifest = new(5, Guid.NewGuid().ToString("N"), "fixture", "fixture", "sqlite", "sqlite", 0, InventorySha,
                "manual", DateTime.UtcNow, "test", Parts: [new("file-storage", "fixture.fileset", 0, InventorySha, true,
                    FileRoots: [new("profile-photos", "profile-photos")])]);
            Counter.NativeLookups = 0;
        }

        public async Task PrepareNativeAsync(PaymentContext db, CancellationToken token, Func<string, long>? availableFreeBytes = null)
        {
            await using var mapping = File.OpenRead(NativeMappingPath);
            var type = typeof(DatabaseRestoreDrillService).Assembly.GetType("PayPlanner.Api.Services.DatabaseRestoreNativeRuntime")!;
            await (Task)type.GetMethod("PrepareAsync", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [db, nativeManifest, mapping, nativePart, InventorySha, FilesRoot, RuntimeRoot, token, availableFreeBytes])!;
        }

        public async Task VerifyNativePhotosAsync(PaymentContext db)
        {
            foreach (var user in await db.Users.AsNoTracking().ToListAsync())
                Assert.Equal("восстановленная фотография"u8.ToArray(), await File.ReadAllBytesAsync(
                    UserPhotoStoragePaths.ResolvePhotoPath(Path.Combine(RuntimeRoot, "photos"), user.PhotoUrl)!));
        }

        public void CreateOutputLink()
        {
            var outside = Path.Combine(root, "outside-output");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "sentinel"), "keep");
            var link = Path.Combine(RuntimeRoot, "photos");
            if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, outside); return; }
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(5000), "Directory link creation timed out.");
            Assert.True(process.ExitCode == 0, output + error);
        }

        public void VerifyNoOutputEscaped()
        {
            var outside = Path.Combine(root, "outside-output");
            Assert.Equal("sentinel", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(outside))));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "sentinel")));
            Directory.Delete(Path.Combine(RuntimeRoot, "photos")); // Remove only our link, not its target.
        }

        public async Task PrepareAsync(PaymentContext db, CancellationToken token)
        {
            await using var mapping = File.OpenRead(MappingPath);
            var type = typeof(DatabaseRestoreDrillService).Assembly.GetType("PayPlanner.Api.Services.DatabaseRestoreManagedReplicas")!;
            await (Task)type.GetMethod("PrepareAsync", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [db, mapping, part, InventorySha, FilesRoot, token])!;
        }

        public async ValueTask DisposeAsync()
        {
            if (databaseCreated)
            {
                Assert.StartsWith(DatabasePrefix, databaseName, StringComparison.Ordinal);
                Assert.True(Guid.TryParseExact(databaseName[DatabasePrefix.Length..], "N", out _));
                await using var admin = new NpgsqlConnection(adminConnectionString);
                await admin.OpenAsync();
                await using var active = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = @databaseName", admin);
                active.Parameters.AddWithValue("databaseName", databaseName);
                // Physical close and pg_stat_activity visibility need not happen in the same instant.
                // Never terminate sessions: wait briefly, then retain the database on failure.
                long connections = -1;
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    connections = (long)(await active.ExecuteScalarAsync())!;
                    if (connections == 0) break;
                    await Task.Delay(100);
                }
                Assert.Equal(0L, connections);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
                await drop.ExecuteNonQueryAsync();
                databaseCreated = false;
            }
            var fullRoot = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullRoot).StartsWith("payplanner-restored-managed-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid owned fixture path.");
            var outputLink = Path.Combine(fullRoot, "runtime-native", "photos");
            if (Directory.Exists(outputLink) && (File.GetAttributes(outputLink) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(outputLink); // Unlink before recursively removing our fixture directory.
            if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, recursive: true);
        }
    }
}
