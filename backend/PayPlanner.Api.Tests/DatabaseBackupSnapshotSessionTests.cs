using System.Data;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Npgsql;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupSnapshotSessionTests
{
    [Fact]
    public async Task SqliteInventoryReadsTheCopyNotLaterLiveChangesAndCannotWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "payplanner-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var livePath = Path.Combine(root, "live.sqlite");
        var copyPath = Path.Combine(root, "copy.sqlite");
        try
        {
            await using var live = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = livePath, Pooling = false }.ConnectionString);
            await live.OpenAsync();
            await using var write = live.CreateCommand();
            write.CommandText = "CREATE TABLE evidence (id INTEGER PRIMARY KEY); INSERT INTO evidence VALUES (1);";
            await write.ExecuteNonQueryAsync();
            write.CommandText = "VACUUM INTO $path";
            write.Parameters.AddWithValue("$path", copyPath);
            await write.ExecuteNonQueryAsync();
            write.Parameters.Clear();
            write.CommandText = "INSERT INTO evidence VALUES (2)";
            await write.ExecuteNonQueryAsync();
            await using var snapshot = await DatabaseBackupSnapshotSession.OpenSqliteCopyAsync(copyPath, livePath, CancellationToken.None);
            Assert.Null(snapshot.PostgreSqlSnapshotId);
            await using var read = snapshot.CreateCommand("SELECT COUNT(*) FROM evidence");
            Assert.Equal(1L, await read.ExecuteScalarAsync());
            write.CommandText = "SELECT COUNT(*) FROM evidence";
            Assert.Equal(2L, await write.ExecuteScalarAsync());
            read.CommandText = "INSERT INTO evidence VALUES (3)";
            await Assert.ThrowsAsync<SqliteException>(() => read.ExecuteNonQueryAsync());
            read.CommandText = "SELECT COUNT(*) FROM evidence";
            Assert.Equal(1L, await read.ExecuteScalarAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SqliteRejectsLiveAliasesMissingCopiesAndCancelledOpenWithoutCreatingAFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "payplanner-snapshot-guards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var live = Path.Combine(root, "live.sqlite");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseBackupSnapshotSession.OpenSqliteCopyAsync(Path.Combine(root, "nested", "..", "live.sqlite"), live, CancellationToken.None));
            var missing = Path.Combine(root, "missing.sqlite");
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                DatabaseBackupSnapshotSession.OpenSqliteCopyAsync(missing, live, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                DatabaseBackupSnapshotSession.OpenSqliteCopyAsync("relative.sqlite", live, CancellationToken.None));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                DatabaseBackupSnapshotSession.OpenSqliteCopyAsync(missing, live, new CancellationToken(true)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PostgreSqlExportedViewMatchesRealDumpDespiteLaterCommitsAndExpiresOnDispose()
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var settings = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost" });
        var name = "payplanner_snapshot_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            settings.Database = name;
            await using var writer = new NpgsqlConnection(settings.ConnectionString);
            await writer.OpenAsync();
            await using var write = new NpgsqlCommand(
                "CREATE TABLE snapshot_evidence (id integer PRIMARY KEY); INSERT INTO snapshot_evidence VALUES (1);", writer);
            await write.ExecuteNonQueryAsync();
            var snapshot = await DatabaseBackupSnapshotSession.OpenPostgreSqlAsync(settings.ConnectionString, CancellationToken.None);
            string snapshotId;
            await using (snapshot)
            {
                snapshotId = Assert.IsType<string>(snapshot.PostgreSqlSnapshotId);
                write.CommandText = "INSERT INTO snapshot_evidence VALUES (2)";
                await write.ExecuteNonQueryAsync();
                await using var read = snapshot.CreateCommand("SELECT COUNT(*) FROM snapshot_evidence");
                Assert.Equal(1L, await read.ExecuteScalarAsync());
                write.CommandText = "SELECT COUNT(*) FROM snapshot_evidence";
                Assert.Equal(2L, await write.ExecuteScalarAsync());
                var dump = await DumpAsync(settings, snapshotId);
                Assert.Contains("INSERT INTO public.snapshot_evidence VALUES (1);", dump);
                Assert.DoesNotContain("INSERT INTO public.snapshot_evidence VALUES (2);", dump);
                read.CommandText = "INSERT INTO snapshot_evidence VALUES (3)";
                var readOnlyError = await Assert.ThrowsAsync<PostgresException>(() => read.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, readOnlyError.SqlState);
            }
            Assert.Throws<ObjectDisposedException>(() => snapshot.CreateCommand("SELECT 1"));
            await snapshot.DisposeAsync(); // Disposal is idempotent.
            await using var importer = await writer.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            await using var import = new NpgsqlCommand(
                $"SET TRANSACTION SNAPSHOT '{snapshotId.Replace("'", "''")}'", writer, importer);
            await Assert.ThrowsAsync<PostgresException>(() => import.ExecuteNonQueryAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PostgreSqlRejectsCancelledOpenAndImplicitDatabase()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => DatabaseBackupSnapshotSession.OpenPostgreSqlAsync(
            "Host=localhost;Database=test", new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => DatabaseBackupSnapshotSession.OpenPostgreSqlAsync(
            "Host=localhost;Username=postgres", CancellationToken.None));
    }

    private static async Task<string> DumpAsync(NpgsqlConnectionStringBuilder source, string snapshotId)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_PG_DUMP") ?? "pg_dump")
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { "--host", source.Host!, "--port", source.Port.ToString(), "--username", source.Username!,
            "--dbname", source.Database!, "--no-password", "--data-only", "--inserts", "--snapshot", snapshotId })
            start.ArgumentList.Add(argument);
        if (!string.IsNullOrEmpty(source.Password)) start.Environment["PGPASSWORD"] = source.Password;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Test pg_dump did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(await error), "Test pg_dump reported an error.");
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(error, output);
        }
    }
}
