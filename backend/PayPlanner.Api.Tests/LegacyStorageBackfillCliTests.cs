using System.Diagnostics;
using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class LegacyStorageBackfillCliTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCliResumesDryRunAndRegistrationOnOwnedMigratedDatabaseWithoutTouchingSources(bool capacityChecks)
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(capacityChecks: capacityChecks);
        using var login = await runtime.Client.PostAsJsonAsync("/api/auth/login",
            new { email = runtime.Email, password = runtime.Password });
        login.EnsureSuccessStatusCode();
        var auth = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(auth);
        var sourceRoot = Path.Combine(runtime.Root, "files", "task-files");
        Directory.CreateDirectory(sourceRoot);
        var originals = new Dictionary<string, byte[]>();
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled")
                .Select(s => s.Value).SingleAsync());
            var user = await db.Users.SingleAsync();
            var task = new TaskJournalItem
            {
                Title = "Synthetic historical migration",
                AuthorUserId = user.Id,
                AssigneeUserId = user.Id,
                DueAt = DateTime.UtcNow.AddDays(1),
                UpdatedAt = DateTime.UtcNow
            };
            db.TaskJournalItems.Add(task);
            for (var index = 1; index <= 3; index++)
            {
                var name = $"synthetic-legacy-{index}.txt";
                var bytes = new byte[] { 10, 20, (byte)index, 40 };
                var path = Path.Combine(sourceRoot, name);
                await File.WriteAllBytesAsync(path, bytes);
                originals[path] = bytes;
                db.TaskJournalFiles.Add(new TaskJournalFile
                {
                    TaskJournalItem = task,
                    OriginalFileName = name,
                    StoragePath = name,
                    SizeBytes = bytes.Length,
                    ContentType = "text/plain",
                    UploadedByUserId = user.Id,
                    DocumentKey = Guid.NewGuid().ToString("N"),
                    Version = 1
                });
            }
            await db.SaveChangesAsync();
        }

        var inventory = Path.Combine(runtime.Root, "inventory");
        var registration = Path.Combine(runtime.Root, "registration");
        Assert.Equal(2, await RunCliAsync(runtime, auth.Token, inventory, "--max-batches", "1"));
        Assert.False((await ReadCheckpointAsync(inventory)).GetProperty("completed").GetBoolean());
        await using (var db = runtime.OpenDatabase()) Assert.Equal(0, await db.StorageObjects.CountAsync());
        Assert.Equal(0, await RunCliAsync(runtime, auth.Token, inventory, "--resume"));
        Assert.True((await ReadCheckpointAsync(inventory)).GetProperty("completed").GetBoolean());
        Assert.Equal(2, await RunCliAsync(runtime, auth.Token, registration,
            "--apply", "--inventory", inventory, "--max-batches", "1"));
        Assert.Equal(0, await RunCliAsync(runtime, auth.Token, registration,
            "--apply", "--inventory", inventory, "--resume"));
        Assert.False((await ReadCheckpointAsync(registration)).GetProperty("replicaMigrationComplete").GetBoolean());
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal(3, await db.StorageObjects.CountAsync());
            Assert.Equal(3, await db.StorageObjectGenerations.CountAsync());
            Assert.Equal(3, await db.StorageReplicas.CountAsync());
            Assert.False(await db.StorageReplicas.AnyAsync(r => r.ProviderKey != "legacy-local"));
            Assert.Equal(0, await db.StorageOperations.CountAsync());
            foreach (var (path, bytes) in originals)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
                var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                Assert.True(await db.StorageObjectGenerations.AnyAsync(g => g.Sha256 == expected && g.SizeBytes == bytes.Length));
            }
        }
        var replicaRequest = new LegacyStorageReplicaMigrationRequest(LegacyStorageSources.TaskFiles, Guid.NewGuid());
        using (var unauthenticated = await runtime.Client.PostAsJsonAsync("/api/storage/legacy-replicas", replicaRequest))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        using (var previewResponse = await runtime.Client.PostAsJsonAsync("/api/storage/legacy-replicas", replicaRequest))
        {
            previewResponse.EnsureSuccessStatusCode();
            var preview = await previewResponse.Content.ReadFromJsonAsync<LegacyStorageReplicaMigrationReport>();
            Assert.NotNull(preview); Assert.False(preview.BatchVerified); Assert.Equal(0, preview.Queued);
            Assert.Equal(3, preview.Items.Count);
            if (capacityChecks)
            {
                Assert.True(preview.Capacity!.CanSchedule);
                Assert.Equal(24, Assert.Single(preview.Capacity.Groups).AdditionalBytes);
            }
            else Assert.Null(preview.Capacity);
            replicaRequest = replicaRequest with { ExpectedPolicyFingerprint = preview.PolicyFingerprint };
        }
        // Concurrent requests for the same page share the object lock and durable keys.
        var accepts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var response = await runtime.Client.PostAsJsonAsync("/api/storage/legacy-replicas", replicaRequest with { Apply = true });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<LegacyStorageReplicaMigrationReport>())!;
        }));
        Assert.Equal(new[] { 0, 6 }, accepts.Select(r => r.Queued).Order().ToArray());
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal(6, await db.StorageReplicas.CountAsync());
            Assert.Equal(6, await db.StorageOperations.CountAsync());
            var environment = new MigrationEnvironment(runtime.Root);
            var registry = new StorageProviderRegistry([new LegacyLocalStorageProvider(environment, runtime.Configuration),
                new LocalStorageProvider(environment, runtime.Configuration)]);
            var processor = new StorageOperationProcessor(db, registry, new MigrationOptions(new() { WorkerBatchSize = 20 }),
                TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await processor.ProcessBatchAsync("owned-cli-replica-queue", CancellationToken.None);
                var retryAt = DateTime.UtcNow.AddMinutes(-1);
                await db.StorageOperations.Where(o => o.Status == StorageOperationStatuses.Failed)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, retryAt));
            }
            Assert.Equal(6, await db.StorageOperations.CountAsync(o => o.Status == StorageOperationStatuses.Succeeded));
            Assert.Equal(0, await db.StorageProductBindings.CountAsync());
        }
        using (var completedResponse = await runtime.Client.PostAsJsonAsync("/api/storage/legacy-replicas", replicaRequest with { Apply = true }))
        {
            completedResponse.EnsureSuccessStatusCode();
            var completed = await completedResponse.Content.ReadFromJsonAsync<LegacyStorageReplicaMigrationReport>();
            Assert.NotNull(completed); Assert.True(completed.BatchVerified); Assert.Equal(0, completed.Queued);
            Assert.All(completed.Items, i => Assert.Equal("verified", i.Status));
            foreach (var item in completed.Items)
            {
                var original = originals.Single(p => Path.GetFileName(p.Key) == completed.SourceVerification.Items.Single(s => s.SourceId == item.SourceId).LogicalName);
                Assert.Equal(original.Value, await File.ReadAllBytesAsync(Path.Combine(runtime.Root, "objects", item.Locator!)));
                Assert.Equal(original.Value, await File.ReadAllBytesAsync(original.Key));
            }
        }
        // Actual CLI replica phase pins its own run, queues only new verification,
        // and never advances a page before the real worker receipts exist.
        var replicaInventory = Path.Combine(runtime.Root, "replica-inventory");
        var replicaApply = Path.Combine(runtime.Root, "replica-apply");
        Assert.Equal(0, await RunCliAsync(runtime, auth.Token, replicaInventory, "--phase", "replicas"));
        Assert.Equal(2, await RunCliAsync(runtime, auth.Token, replicaApply, "--phase", "replicas", "--apply", "--inventory", replicaInventory));
        var pinnedRun = (await ReadCheckpointAsync(replicaApply)).GetProperty("runId").GetString();
        Assert.Equal(0, (await ReadCheckpointAsync(replicaApply)).GetProperty("afterId").GetInt64());
        await DrainVerificationAsync();
        Assert.Equal(2, await RunCliAsync(runtime, auth.Token, replicaApply, "--phase", "replicas", "--apply", "--inventory", replicaInventory,
            "--resume", "--max-batches", "1"));
        Assert.Equal(2, (await ReadCheckpointAsync(replicaApply)).GetProperty("afterId").GetInt64());
        Assert.Equal(2, await RunCliAsync(runtime, auth.Token, replicaApply, "--phase", "replicas", "--apply", "--inventory", replicaInventory, "--resume"));
        Assert.Equal(2, (await ReadCheckpointAsync(replicaApply)).GetProperty("afterId").GetInt64());
        await DrainVerificationAsync();
        Assert.Equal(0, await RunCliAsync(runtime, auth.Token, replicaApply, "--phase", "replicas", "--apply", "--inventory", replicaInventory, "--resume"));
        var finalCheckpoint = await ReadCheckpointAsync(replicaApply);
        Assert.True(finalCheckpoint.GetProperty("completed").GetBoolean());
        Assert.False(finalCheckpoint.GetProperty("replicaMigrationComplete").GetBoolean());
        Assert.Equal(pinnedRun, finalCheckpoint.GetProperty("runId").GetString());
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal(6, await db.StorageReplicas.CountAsync());
            Assert.Equal(9, await db.StorageOperations.CountAsync());
            Assert.Equal(3, await db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.Replicate));
            Assert.Equal(9, await db.StorageOperations.CountAsync(o => o.Status == StorageOperationStatuses.Succeeded));
            Assert.Equal(0, await db.StorageProductBindings.CountAsync());
        }
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        var boundedCatalog = Path.Combine(runtime.Root, "bounded-catalog");
        var boundedPlan = Path.Combine(runtime.Root, "bounded-plan");
        var boundedApply = Path.Combine(runtime.Root, "bounded-apply");
        Assert.Equal(0, await RunCliAsync(runtime, auth.Token, boundedCatalog, "--end-id", "2"));
        Assert.Equal(2, (await ReadCheckpointAsync(boundedCatalog)).GetProperty("afterId").GetInt64());
        Assert.Equal(0, await RunCliAsync(runtime, auth.Token, boundedPlan, "--phase", "replicas", "--end-id", "2"));
        Assert.Equal(2, await RunCliAsync(runtime, auth.Token, boundedApply, "--phase", "replicas", "--end-id", "2",
            "--apply", "--inventory", boundedPlan));
        await DrainVerificationAsync();
        Assert.Equal(0, await RunCliAsync(runtime, auth.Token, boundedApply, "--phase", "replicas", "--end-id", "2",
            "--apply", "--inventory", boundedPlan, "--resume"));
        var boundedCheckpoint = await ReadCheckpointAsync(boundedApply);
        Assert.Equal(2, boundedCheckpoint.GetProperty("afterId").GetInt64());
        Assert.Equal(2, boundedCheckpoint.GetProperty("scope").GetProperty("endId").GetInt64());
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal(11, await db.StorageOperations.CountAsync(o => o.Status == StorageOperationStatuses.Succeeded));
            Assert.Equal(3, await db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.Replicate));
        }
        async Task DrainVerificationAsync()
        {
            await using var db = runtime.OpenDatabase();
            var environment = new MigrationEnvironment(runtime.Root);
            var registry = new StorageProviderRegistry([new LegacyLocalStorageProvider(environment, runtime.Configuration),
                new LocalStorageProvider(environment, runtime.Configuration)]);
            var processor = new StorageOperationProcessor(db, registry, new MigrationOptions(new() { WorkerBatchSize = 20 }),
                TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
            await processor.ProcessBatchAsync("owned-cli-resume-verification", CancellationToken.None);
        }
        // A new inventory sees actual source drift instead of silently skipping a registered ID.
        await File.WriteAllBytesAsync(originals.Keys.First(), [90, 80, 70, 60]);
        var drift = Path.Combine(runtime.Root, "drift");
        Assert.Equal(2, await RunCliAsync(runtime, auth.Token, drift));
        Assert.Equal(0, (await ReadCheckpointAsync(drift)).GetProperty("afterId").GetInt64());
        var denied = Path.Combine(runtime.Root, "denied");
        Assert.Equal(1, await RunCliAsync(runtime, "synthetic-invalid-jwt", denied));
        Assert.Equal(0, (await ReadCheckpointAsync(denied)).GetProperty("afterId").GetInt64());
        await using (var db = runtime.OpenDatabase()) Assert.Equal(3, await db.StorageObjects.CountAsync());
        foreach (var directory in new[] { inventory, registration, replicaInventory, replicaApply, boundedCatalog, boundedPlan, boundedApply, drift, denied })
        {
            Assert.False(File.Exists(Path.Combine(directory, "run.lock")));
            Assert.DoesNotContain(auth.Token, await File.ReadAllTextAsync(Path.Combine(directory, "checkpoint.json")));
            Assert.DoesNotContain(auth.Token, await File.ReadAllTextAsync(Path.Combine(directory, "batches.jsonl")));
        }
        runtime.AssertNoServerErrors();
    }

    private sealed class MigrationOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }

    private sealed class MigrationEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<JsonElement> ReadCheckpointAsync(string directory)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "checkpoint.json")));
        return document.RootElement.Clone();
    }

    private static async Task<int> RunCliAsync(SyntheticWritableBackendProcess runtime, string token, string output, params string[] extra)
    {
        var repository = TestRepository.FindDirectory();
        while (!File.Exists(Path.Combine(repository.FullName, "vite.config.ts")))
            repository = repository.Parent ?? throw new InvalidOperationException("Repository root not found.");
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repository.FullName
        };
        var inherited = start.Environment.ToArray();
        start.Environment.Clear();
        foreach (var (key, value) in inherited)
            if (new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP" }.Contains(key, StringComparer.OrdinalIgnoreCase))
                start.Environment[key] = value;
        start.Environment["PAYPLANNER_STORAGE_ADMIN_TOKEN"] = token;
        start.ArgumentList.Add(Path.Combine(repository.FullName, "scripts", "storage-legacy-backfill.mjs"));
        foreach (var argument in new[] { "--url", runtime.Client.BaseAddress!.AbsoluteUri.TrimEnd('/'),
            "--source", "task-files", "--output", output, "--batch-size", "2", "--interval-ms", "0" }.Concat(extra))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await process.WaitForExitAsync(timeout.Token);
            Assert.DoesNotContain(token, await stdout);
            Assert.DoesNotContain(token, await stderr);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
