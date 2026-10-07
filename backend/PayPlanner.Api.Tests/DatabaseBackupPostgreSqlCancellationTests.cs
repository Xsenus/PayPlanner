using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupPostgreSqlCancellationTests
{
    [Fact]
    public async Task CancellingLockedRealDumpStopsChildBeforeReleasingLeaseAndAllowsRetry()
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var source = new NpgsqlConnectionStringBuilder(configured) { Pooling = false };
        Assert.Contains(source.Host, new[] { "127.0.0.1", "localhost" });
        var databaseName = "payplanner_dump_cancel_" + Guid.NewGuid().ToString("N");
        source.Database = databaseName;
        var root = Path.Combine(Path.GetTempPath(), "payplanner-dump-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var adminSettings = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(new DatabaseProviderInfo(DatabaseProviderKind.PostgreSql, source.ConnectionString));
            services.AddDbContextFactory<PaymentContext>(options => options.UseNpgsql(source.ConnectionString));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseBackup:Directory"] = Path.Combine(root, "backups"),
                ["DatabaseBackup:IncludeFileStorage"] = "false",
                ["DatabaseBackup:PgDumpPath"] = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_PG_DUMP"),
            }).Build());
            services.AddSingleton<IWebHostEnvironment>(new TestEnvironment(root));
            var logger = new ProcessRecordingLogger();
            services.AddSingleton<ILogger<DatabaseBackupService>>(logger);
            services.AddSingleton<DatabaseBackupService>();
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<PaymentContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.Users.Add(new User
                {
                    Email = "snapshot@local.test",
                    PasswordHash = "test",
                    Role = new Role { Name = "snapshot" },
                    PhotoUrl = "https://external.example.test/original.png"
                });
                await db.SaveChangesAsync();
            }

            await using var blocker = new NpgsqlConnection(source.ConnectionString);
            await blocker.OpenAsync();
            await using (var seed = new NpgsqlCommand("CREATE TABLE public.dump_cancellation_evidence (id integer PRIMARY KEY)", blocker))
                await seed.ExecuteNonQueryAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("LOCK TABLE public.dump_cancellation_evidence IN ACCESS EXCLUSIVE MODE", blocker, transaction))
                await hold.ExecuteNonQueryAsync();

            var unrelatedSettings = new NpgsqlConnectionStringBuilder(source.ConnectionString)
            {
                ApplicationName = "payplanner-backup-unrelated-" + Guid.NewGuid().ToString("N"),
            };
            await using var unrelated = new NpgsqlConnection(unrelatedSettings.ConnectionString);
            await unrelated.OpenAsync();
            await using var unrelatedCommand = new NpgsqlCommand("SELECT COUNT(*) FROM public.dump_cancellation_evidence", unrelated)
            {
                CommandTimeout = 60,
            };
            var unrelatedRead = unrelatedCommand.ExecuteScalarAsync();
            await WaitForDumpAsync(admin, databaseName, unrelatedSettings.ApplicationName, waitingForLock: true);

            var backups = provider.GetRequiredService<DatabaseBackupService>();
            using var cancellation = new CancellationTokenSource();
            var run = backups.CreateManualBackupAsync(cancellation.Token);
            var started = Stopwatch.StartNew();
            while (string.IsNullOrWhiteSpace(logger.ApplicationName) && started.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(100);
            var applicationName = Assert.IsType<string>(logger.ApplicationName);
            await WaitForDumpAsync(admin, databaseName, applicationName, waitingForLock: true);
            var processId = logger.ProcessId;
            Assert.True(processId > 0);
            Assert.True(IsProcessRunning(processId));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.False(IsProcessRunning(processId));
            var stopped = Stopwatch.StartNew();
            while (await CountDumpSessionsAsync(admin, databaseName, applicationName) > 0 && stopped.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(100);
            Assert.Equal(0L, await CountDumpSessionsAsync(admin, databaseName, applicationName));
            Assert.False(unrelatedRead.IsCompleted);
            Assert.Equal(1L, await CountDumpSessionsAsync(admin, databaseName, unrelatedSettings.ApplicationName));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.backup", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.sql", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.partial", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.references", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.manifest.json", SearchOption.AllDirectories));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var attempt = await db.DatabaseBackupAttempts.SingleAsync();
                Assert.Equal(DatabaseBackupAttemptStatuses.Cancelled, attempt.Status);
                Assert.Equal("database", attempt.Stage);
                Assert.Null(attempt.BackupSetId);
            }
            await transaction.RollbackAsync();
            Assert.Equal(0L, Convert.ToInt64(await unrelatedRead.WaitAsync(TimeSpan.FromSeconds(10))));
            await using var secondBlock = await blocker.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("LOCK TABLE public.dump_cancellation_evidence IN ACCESS EXCLUSIVE MODE", blocker, secondBlock))
                await hold.ExecuteNonQueryAsync();
            var retryTask = backups.CreateManualBackupAsync();
            started.Restart();
            while (logger.ApplicationName == applicationName && started.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(100);
            var retryApplication = Assert.IsType<string>(logger.ApplicationName);
            Assert.NotEqual(applicationName, retryApplication);
            await WaitForDumpAsync(admin, databaseName, retryApplication, waitingForLock: true);
            await using (var update = new NpgsqlCommand(
                "UPDATE public.\"Users\" SET \"PhotoUrl\" = 'https://external.example.test/new.png'", unrelated))
                await update.ExecuteNonQueryAsync();
            await secondBlock.RollbackAsync();
            var retried = await retryTask.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True((await backups.VerifyBackupAsync(retried.Backup.RelativePath)).Verified);
            var manifest = await backups.ReadVerifiedManifestAsync(retried.Backup.RelativePath);
            var referencePart = Assert.Single(manifest.Parts!, item => item.Kind == "file-references");
            using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(backups.ResolveBackupPath(referencePart.RelativePath)!));
            Assert.Equal("https://external.example.test/original.png",
                Assert.Single(inventory.RootElement.GetProperty("storedFiles").EnumerateArray())
                    .GetProperty("storedPath").GetString());
            var restoreExecutable = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_PG_DUMP"))!,
                OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore");
            var restoreInfo = new ProcessStartInfo(restoreExecutable)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            restoreInfo.ArgumentList.Add("--data-only");
            restoreInfo.ArgumentList.Add("--table=Users");
            restoreInfo.ArgumentList.Add("--file=-");
            var archivePath = backups.ResolveBackupPath(retried.Backup.RelativePath)!;
            restoreInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "\\\\?\\" + Path.GetFullPath(archivePath) : archivePath);
            using (var restore = Process.Start(restoreInfo)!)
            {
                var stdout = restore.StandardOutput.ReadToEndAsync();
                var stderr = restore.StandardError.ReadToEndAsync();
                try { await restore.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
                finally { if (!restore.HasExited) restore.Kill(entireProcessTree: true); }
                var restoreError = (await stderr).Trim();
                Assert.True(restore.ExitCode == 0, $"pg_restore exit {restore.ExitCode}: {restoreError}");
                Assert.Empty(restoreError);
                var restoredSql = await stdout;
                Assert.Contains("https://external.example.test/original.png", restoredSql, StringComparison.Ordinal);
                Assert.DoesNotContain("https://external.example.test/new.png", restoredSql, StringComparison.Ordinal);
            }
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.partial", SearchOption.AllDirectories));
            await using (var db = await factory.CreateDbContextAsync())
                Assert.Equal(2, await db.DatabaseBackupAttempts.CountAsync());
        }
        finally
        {
            // Only this GUID-named, disposable database is terminated/dropped.
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin))
                await drop.ExecuteNonQueryAsync();
            var resolvedRoot = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolvedRoot, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("payplanner-dump-cancel-", Path.GetFileName(resolvedRoot), StringComparison.Ordinal);
            var cleanup = Stopwatch.StartNew();
            while (true)
            {
                try { Directory.Delete(resolvedRoot, recursive: true); break; }
                catch (IOException) when (cleanup.Elapsed < TimeSpan.FromSeconds(5))
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    private static async Task WaitForDumpAsync(NpgsqlConnection admin, string databaseName, string applicationName, bool waitingForLock)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = @database AND application_name = @applicationName AND (@waiting = false OR wait_event_type = 'Lock')", admin);
            command.Parameters.AddWithValue("database", databaseName);
            command.Parameters.AddWithValue("applicationName", applicationName);
            command.Parameters.AddWithValue("waiting", waitingForLock);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The owned pg_dump did not reach the expected state.");
    }

    private static async Task<long> CountDumpSessionsAsync(NpgsqlConnection admin, string databaseName, string applicationName)
    {
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = @database AND application_name = @applicationName", admin);
        command.Parameters.AddWithValue("database", databaseName);
        command.Parameters.AddWithValue("applicationName", applicationName);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private sealed class ProcessRecordingLogger : ILogger<DatabaseBackupService>
    {
        private int _processId;
        private string? _applicationName;
        public int ProcessId => Volatile.Read(ref _processId);
        public string? ApplicationName => Volatile.Read(ref _applicationName);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            foreach (var value in values)
            {
                if (value.Key == "ProcessId" && value.Value is int id) Interlocked.Exchange(ref _processId, id);
                if (value.Key == "ApplicationName" && value.Value is string name) Volatile.Write(ref _applicationName, name);
            }
        }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
