using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Npgsql;

namespace PayPlanner.Api.Services;

public sealed class DatabaseRestoreDrillOptions
{
    public const string SectionName = "DatabaseRestoreDrill";
    public string RootDirectory { get; set; } = string.Empty;
    public bool PostgreSqlExecutionEnabled { get; set; }
    public string PostgreSqlTargetConnectionString { get; set; } = string.Empty;
    public string ConfirmationToken { get; set; } = string.Empty;
    public List<string> AllowedPostgreSqlDatabases { get; set; } = [];
    public string PgRestorePath { get; set; } = string.Empty;
    public string PsqlPath { get; set; } = string.Empty;
    public int TimeoutMinutes { get; set; } = 30;
}

public sealed record DatabaseRestoreDrillReport(
    string DrillId,
    string Status,
    string BackupId,
    string BackupRelativePath,
    string DatabaseProvider,
    string BackupFormat,
    string Target,
    int TableCount,
    int ForeignKeyViolationCount,
    string IntegrityStatus,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    long DurationMilliseconds,
    string ReportPath,
    string? Error = null);

public sealed class DatabaseRestoreDrillService(
    DatabaseBackupService backups,
    DatabaseProviderInfo activeDatabase,
    IWebHostEnvironment environment,
    IOptionsMonitor<DatabaseRestoreDrillOptions> options,
    ILogger<DatabaseRestoreDrillService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<DatabaseRestoreDrillReport> RunAsync(
        string relativePath,
        bool executePostgreSql,
        string? confirmationToken,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var manifest = await backups.ReadVerifiedManifestAsync(relativePath, cancellationToken);
        var backupPath = backups.ResolveBackupPath(relativePath)
            ?? throw new FileNotFoundException("Резервная копия не найдена.");
        var drillId = $"restore-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var root = ResolveRoot();
        var targetDirectory = Path.Combine(root, drillId);
        Directory.CreateDirectory(targetDirectory);

        DatabaseRestoreDrillReport report;
        try
        {
            report = string.Equals(manifest.DatabaseProvider, "sqlite", StringComparison.OrdinalIgnoreCase)
                ? await RestoreSqliteAsync(manifest, backupPath, targetDirectory, started, stopwatch, cancellationToken)
                : await RestorePostgreSqlAsync(manifest, backupPath, targetDirectory, executePostgreSql, confirmationToken, started, stopwatch, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restore drill {DrillId} failed for backup {BackupId}", drillId, manifest.BackupId);
            report = new DatabaseRestoreDrillReport(
                drillId, "failed", manifest.BackupId, manifest.BackupRelativePath, manifest.DatabaseProvider,
                manifest.BackupFormat, targetDirectory, 0, 0, "failed", started, DateTime.UtcNow,
                stopwatch.ElapsedMilliseconds, string.Empty, ex.Message);
        }

        var reportPath = Path.Combine(targetDirectory, "restore-drill-report.json");
        report = report with { ReportPath = reportPath };
        var tempPath = reportPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        File.Move(tempPath, reportPath, overwrite: true);
        return report;
    }

    private async Task<DatabaseRestoreDrillReport> RestoreSqliteAsync(
        DatabaseBackupManifest manifest,
        string backupPath,
        string targetDirectory,
        DateTime started,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(targetDirectory, "restored.sqlite");
        if (string.Equals(manifest.BackupFormat, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await CopyFileAsync(backupPath, targetPath, cancellationToken);
        }
        else if (string.Equals(manifest.BackupFormat, "zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(backupPath);
            var databaseEntries = archive.Entries.Where(entry =>
                !string.IsNullOrEmpty(entry.Name)
                && (entry.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (databaseEntries.Length != 1)
                throw new InvalidDataException("SQLite ZIP должен содержать ровно один файл базы данных.");
            var entry = databaseEntries[0];
            if (!string.Equals(entry.FullName, entry.Name, StringComparison.Ordinal))
                throw new InvalidDataException("SQLite ZIP содержит небезопасный вложенный путь.");
            await using var source = entry.Open();
            await using var destination = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(destination, cancellationToken);
        }
        else
        {
            throw new InvalidDataException($"Формат SQLite '{manifest.BackupFormat}' не поддерживается restore drill.");
        }

        EnsureNotActiveSqliteDatabase(targetPath);
        var builder = new SqliteConnectionStringBuilder { DataSource = targetPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var integrity = await ExecuteScalarStringAsync(connection, "PRAGMA integrity_check;", cancellationToken);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite integrity_check завершился со статусом: {integrity}");
        var tableCount = await ExecuteScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';", cancellationToken);
        var foreignKeyViolations = await CountRowsAsync(connection, "PRAGMA foreign_key_check;", cancellationToken);
        if (foreignKeyViolations > 0)
            throw new InvalidDataException($"В восстановленной SQLite обнаружено нарушений внешних ключей: {foreignKeyViolations}.");
        await RestoreFilePartsAsync(manifest, targetDirectory, cancellationToken);

        return new DatabaseRestoreDrillReport(
            Path.GetFileName(targetDirectory), "restored", manifest.BackupId, manifest.BackupRelativePath,
            manifest.DatabaseProvider, manifest.BackupFormat, targetPath, tableCount, foreignKeyViolations,
            integrity, started, DateTime.UtcNow, stopwatch.ElapsedMilliseconds, string.Empty);
    }

    private async Task<DatabaseRestoreDrillReport> RestorePostgreSqlAsync(
        DatabaseBackupManifest manifest,
        string backupPath,
        string targetDirectory,
        bool execute,
        string? confirmationToken,
        DateTime started,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!execute)
        {
            return new DatabaseRestoreDrillReport(
                Path.GetFileName(targetDirectory), "validatedOnly", manifest.BackupId, manifest.BackupRelativePath,
                manifest.DatabaseProvider, manifest.BackupFormat, "disposable PostgreSQL target required", 0, 0,
                "manifest verified; restore not executed", started, DateTime.UtcNow, stopwatch.ElapsedMilliseconds, string.Empty);
        }
        if (!settings.PostgreSqlExecutionEnabled
            || string.IsNullOrWhiteSpace(settings.ConfirmationToken)
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(settings.ConfirmationToken),
                System.Text.Encoding.UTF8.GetBytes(confirmationToken ?? string.Empty)))
            throw new InvalidOperationException("Выполнение PostgreSQL restore drill не разрешено или confirmation token неверен.");

        var target = new NpgsqlConnectionStringBuilder(settings.PostgreSqlTargetConnectionString);
        var active = activeDatabase.IsPostgreSql ? new NpgsqlConnectionStringBuilder(activeDatabase.ConnectionString) : null;
        if (string.IsNullOrWhiteSpace(target.Database)
            || (active is not null && string.Equals(active.Database, target.Database, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Restore drill запрещён для активной PostgreSQL базы.");
        var allowed = settings.AllowedPostgreSqlDatabases.Any(item => string.Equals(item, target.Database, StringComparison.OrdinalIgnoreCase));
        if (!allowed && !target.Database.StartsWith("payplanner_restore_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Целевая PostgreSQL база отсутствует в allowlist и не имеет префикс payplanner_restore_.");

        // A drill must never clean or overwrite an already populated database, even if its name is allowlisted.
        await EnsureEmptyPostgreSqlTargetAsync(target.ConnectionString, cancellationToken);

        var executable = string.Equals(manifest.BackupFormat, "sql", StringComparison.OrdinalIgnoreCase)
            ? (string.IsNullOrWhiteSpace(settings.PsqlPath) ? "psql" : settings.PsqlPath)
            : (string.IsNullOrWhiteSpace(settings.PgRestorePath) ? "pg_restore" : settings.PgRestorePath);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--host"); startInfo.ArgumentList.Add(target.Host ?? string.Empty);
        startInfo.ArgumentList.Add("--port"); startInfo.ArgumentList.Add(target.Port.ToString());
        startInfo.ArgumentList.Add("--username"); startInfo.ArgumentList.Add(target.Username ?? string.Empty);
        startInfo.ArgumentList.Add("--dbname"); startInfo.ArgumentList.Add(target.Database);
        if (string.Equals(manifest.BackupFormat, "sql", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--file"); startInfo.ArgumentList.Add(backupPath);
            startInfo.ArgumentList.Add("--set"); startInfo.ArgumentList.Add("ON_ERROR_STOP=on");
        }
        else
        {
            startInfo.ArgumentList.Add("--no-owner"); startInfo.ArgumentList.Add("--no-privileges");
            startInfo.ArgumentList.Add(backupPath);
        }
        if (!string.IsNullOrWhiteSpace(target.Password)) startInfo.Environment["PGPASSWORD"] = target.Password;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(settings.TimeoutMinutes, 1, 240)));
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить PostgreSQL restore tool.");
        var errorTask = process.StandardError.ReadToEndAsync();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* The process exited while cancellation was handled. */ }
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(stopTimeout.Token);
            }
            throw;
        }
        var error = (await errorTask).Trim();
        _ = await outputTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Restore tool exit code: {process.ExitCode}." : error);

        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var tableCount = 0;
        await using (var count = new NpgsqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE';", connection))
            tableCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        if (tableCount == 0)
            throw new InvalidDataException("Восстановление PostgreSQL завершилось без таблиц приложения.");
        await using (var disableEmail = new NpgsqlCommand("UPDATE \"SystemSettings\" SET \"Value\" = 'false', \"UpdatedAt\" = CURRENT_TIMESTAMP WHERE \"Key\" = 'notifications.email.enabled';", connection))
            await disableEmail.ExecuteNonQueryAsync(cancellationToken);
        await RestoreFilePartsAsync(manifest, targetDirectory, cancellationToken);

        return new DatabaseRestoreDrillReport(
            Path.GetFileName(targetDirectory), "restored", manifest.BackupId, manifest.BackupRelativePath,
            manifest.DatabaseProvider, manifest.BackupFormat, $"{target.Host}:{target.Port}/{target.Database}", tableCount, 0,
            "restore completed; outbound email database flag disabled", started, DateTime.UtcNow, stopwatch.ElapsedMilliseconds, string.Empty);
    }

    public static async Task EnsureEmptyPostgreSqlTargetAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_namespace
                WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'public')
                  AND nspname NOT LIKE 'pg_toast%'
                  AND nspname NOT LIKE 'pg_temp_%'
                UNION ALL
                SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                UNION ALL
                SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'public'
            );
            """, connection);
        if (Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken)))
            throw new InvalidOperationException("Restore drill разрешён только в пустую отдельную PostgreSQL базу.");
    }

    private string ResolveRoot()
    {
        var configured = options.CurrentValue.RootDirectory?.Trim();
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "restore-drills")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        return root;
    }

    private async Task RestoreFilePartsAsync(
        DatabaseBackupManifest manifest,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var fileParts = manifest.Parts?.Where(item => item.Kind == "file-storage").ToArray() ?? [];
        if (fileParts.Length == 0) return;
        var filesRoot = Path.GetFullPath(Path.Combine(targetDirectory, "files"));
        Directory.CreateDirectory(filesRoot);
        foreach (var part in fileParts)
        {
            var archivePath = backups.ResolveBackupPath(part.RelativePath)
                ?? throw new FileNotFoundException($"Часть backup set не найдена: {part.RelativePath}.");
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var targetPath = Path.GetFullPath(Path.Combine(filesRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!targetPath.StartsWith(filesRoot + Path.DirectorySeparatorChar, comparison))
                    throw new InvalidDataException("File backup set содержит небезопасный путь.");
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await using var source = entry.Open();
                await using var destination = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await source.CopyToAsync(destination, cancellationToken);
            }
        }
    }

    private void EnsureNotActiveSqliteDatabase(string targetPath)
    {
        if (!activeDatabase.IsSqlite) return;
        var activePath = new SqliteConnectionStringBuilder(activeDatabase.ConnectionString).DataSource;
        if (string.Equals(Path.GetFullPath(activePath), Path.GetFullPath(targetPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Restore drill запрещён поверх активной SQLite базы.");
    }

    private static async Task CopyFileAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static async Task<string> ExecuteScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
    }

    private static async Task<int> ExecuteScalarIntAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> CountRowsAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var count = 0; while (await reader.ReadAsync(cancellationToken)) count++;
        return count;
    }
}
