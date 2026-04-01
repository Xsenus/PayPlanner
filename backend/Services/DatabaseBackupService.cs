using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed record DatabaseBackupFileInfo(
    string FileName,
    string RelativePath,
    long SizeBytes,
    DateTime CreatedAt,
    string Trigger);

public sealed record DatabaseBackupOverview(
    string Provider,
    string ProviderDisplayName,
    string BackupFormat,
    string FileExtension,
    bool AutomaticEnabled,
    string ScheduleLocal,
    int RetentionDays,
    string RootDirectory,
    string TimeZone,
    string? ToolCommand,
    IReadOnlyList<DatabaseBackupFileInfo> Items);

public sealed record DatabaseBackupCreateResult(
    DatabaseBackupFileInfo Backup,
    string DownloadPath);

public sealed class DatabaseBackupService
{
    private const string AutomaticEnabledSettingKey = "database-backup.enabled";
    private const string ScheduleSettingKey = "database-backup.schedule-local";
    private const string RetentionDaysSettingKey = "database-backup.retention-days";
    private static readonly TimeOnly[] DefaultSchedule =
    [
        new(3, 0),
        new(15, 0)
    ];

    private readonly IDbContextFactory<PaymentContext> _contextFactory;
    private readonly IConfiguration _configuration;
    private readonly DatabaseProviderInfo _databaseProvider;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly SemaphoreSlim _backupSemaphore = new(1, 1);
    private readonly string _backupRootDirectory;

    public DatabaseBackupService(
        IDbContextFactory<PaymentContext> contextFactory,
        IConfiguration configuration,
        DatabaseProviderInfo databaseProvider,
        ILogger<DatabaseBackupService> logger)
    {
        _contextFactory = contextFactory;
        _configuration = configuration;
        _databaseProvider = databaseProvider;
        _logger = logger;
        _backupRootDirectory = ResolveBackupRootDirectory();
        Directory.CreateDirectory(_backupRootDirectory);
    }

    public TimeSpan PollingInterval => TimeSpan.FromMinutes(1);

    public async Task<DatabaseBackupOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var items = await ListBackupsAsync(cancellationToken);

        return new DatabaseBackupOverview(
            _databaseProvider.Key,
            _databaseProvider.DisplayName,
            _databaseProvider.IsSqlite ? "ZIP archive (.zip)" : "PostgreSQL custom dump (.dump)",
            _databaseProvider.IsSqlite ? ".zip" : ".dump",
            settings.AutomaticEnabled,
            settings.ScheduleDisplay,
            settings.RetentionDays,
            settings.RootDirectory,
            TimeZoneInfo.Local.DisplayName,
            ResolveToolCommandDisplay(settings.ToolCommand),
            items);
    }

    public Task<IReadOnlyList<DatabaseBackupFileInfo>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_backupRootDirectory);

        var searchPattern = _databaseProvider.IsSqlite ? "*.zip" : "*.dump";
        var items = Directory.EnumerateFiles(_backupRootDirectory, searchPattern, SearchOption.AllDirectories)
            .Select(path => BuildFileInfo(path, _backupRootDirectory))
            .Where(static item => item is not null)
            .Select(static item => item!)
            .OrderByDescending(item => item.CreatedAt)
            .Take(100)
            .ToList();

        return Task.FromResult<IReadOnlyList<DatabaseBackupFileInfo>>(items);
    }

    public async Task<DatabaseBackupOverview> UpdateSettingsAsync(
        bool automaticEnabled,
        string? scheduleLocal,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        var parsedSchedule = ParseSchedule(scheduleLocal, fallbackToDefault: false);
        if (parsedSchedule.Count == 0)
        {
            throw new ArgumentException("Укажите расписание в формате HH:mm,HH:mm.");
        }

        if (retentionDays < 1 || retentionDays > 365)
        {
            throw new ArgumentException("Срок хранения резервных копий должен быть от 1 до 365 дней.");
        }

        var normalizedSchedule = FormatSchedule(parsedSchedule);
        var now = DateTime.UtcNow;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await UpsertSystemSettingAsync(context, AutomaticEnabledSettingKey, automaticEnabled ? "true" : "false", now, cancellationToken);
        await UpsertSystemSettingAsync(context, ScheduleSettingKey, normalizedSchedule, now, cancellationToken);
        await UpsertSystemSettingAsync(context, RetentionDaysSettingKey, retentionDays.ToString(CultureInfo.InvariantCulture), now, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return await GetOverviewAsync(cancellationToken);
    }

    public async Task<DatabaseBackupCreateResult> CreateManualBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = await CreateBackupAsync("manual", null, cancellationToken);
        return new DatabaseBackupCreateResult(
            backup,
            $"/api/database/backups/download?relativePath={Uri.EscapeDataString(backup.RelativePath)}");
    }

    public async Task<int> EnsureScheduledBackupsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        await CleanupExpiredBackupsAsync(settings.RetentionDays, cancellationToken);

        if (!settings.AutomaticEnabled || settings.Schedule.Count == 0)
        {
            return 0;
        }

        var nowLocal = DateTimeOffset.Now;
        var currentTime = TimeOnly.FromDateTime(nowLocal.DateTime);
        var createdCount = 0;

        foreach (var scheduledTime in settings.Schedule)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scheduledTime > currentTime)
            {
                continue;
            }

            var slotKey = scheduledTime.ToString("HHmm", CultureInfo.InvariantCulture);
            if (HasBackupForSlot(settings.RootDirectory, nowLocal.Date, slotKey))
            {
                continue;
            }

            await CreateBackupAsync("auto", slotKey, cancellationToken);
            createdCount++;
        }

        return createdCount;
    }

    public string? ResolveBackupPath(string? relativePath)
    {
        var normalizedRelativePath = relativePath?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            return null;
        }

        var safeRelativePath = normalizedRelativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var fullPath = Path.GetFullPath(Path.Combine(_backupRootDirectory, safeRelativePath));
        var rootPath = NormalizeDirectory(_backupRootDirectory);
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(fullPath) ? fullPath : null;
    }

    private async Task<DatabaseBackupFileInfo> CreateBackupAsync(
        string trigger,
        string? scheduleSlot,
        CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        await _backupSemaphore.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(settings.RootDirectory);

            var nowLocal = DateTimeOffset.Now;
            var targetDirectory = Path.Combine(
                settings.RootDirectory,
                nowLocal.ToString("yyyy", CultureInfo.InvariantCulture),
                nowLocal.ToString("MM", CultureInfo.InvariantCulture),
                nowLocal.ToString("dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(targetDirectory);

            DatabaseBackupFileInfo fileInfo = _databaseProvider.IsSqlite
                ? await CreateSqliteBackupAsync(targetDirectory, nowLocal, trigger, scheduleSlot, cancellationToken)
                : await CreatePostgreSqlBackupAsync(targetDirectory, settings.ToolCommand, nowLocal, trigger, scheduleSlot, cancellationToken);

            await CleanupExpiredBackupsAsync(settings.RetentionDays, cancellationToken);
            return fileInfo;
        }
        finally
        {
            _backupSemaphore.Release();
        }
    }

    private async Task<DatabaseBackupFileInfo> CreateSqliteBackupAsync(
        string targetDirectory,
        DateTimeOffset nowLocal,
        string trigger,
        string? scheduleSlot,
        CancellationToken cancellationToken)
    {
        var connectionBuilder = new SqliteConnectionStringBuilder(_databaseProvider.ConnectionString);
        var databasePath = connectionBuilder.DataSource;
        if (string.IsNullOrWhiteSpace(databasePath) || databasePath == ":memory:")
        {
            throw new InvalidOperationException("SQLite backup is available only for a file database.");
        }

        if (!Path.IsPathRooted(databasePath))
        {
            databasePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, databasePath));
        }

        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException($"SQLite database file was not found: {databasePath}");
        }

        var baseName = SanitizeFilePart(Path.GetFileNameWithoutExtension(databasePath));
        var fileName = BuildBackupFileName(baseName, nowLocal, trigger, scheduleSlot, ".zip");
        var outputPath = Path.Combine(targetDirectory, fileName);
        var tempCopyPath = Path.Combine(targetDirectory, $"{Guid.NewGuid():N}.sqlite");

        _logger.LogInformation("Creating SQLite backup at {OutputPath}", outputPath);

        try
        {
            await using (var connection = new SqliteConnection(_databaseProvider.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);

                await using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=5000;";
                    await pragma.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "VACUUM INTO $path;";
                    command.Parameters.AddWithValue("$path", tempCopyPath);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(tempCopyPath, $"{baseName}.db", CompressionLevel.Optimal);
            }
        }
        finally
        {
            TryDeleteFile(tempCopyPath);
        }

        return BuildFileInfo(outputPath, _backupRootDirectory)
            ?? throw new InvalidOperationException("SQLite backup was created but could not be indexed.");
    }

    private async Task<DatabaseBackupFileInfo> CreatePostgreSqlBackupAsync(
        string targetDirectory,
        string? toolCommand,
        DateTimeOffset nowLocal,
        string trigger,
        string? scheduleSlot,
        CancellationToken cancellationToken)
    {
        var connectionBuilder = new NpgsqlConnectionStringBuilder(_databaseProvider.ConnectionString);
        if (string.IsNullOrWhiteSpace(connectionBuilder.Database))
        {
            throw new InvalidOperationException("Database name is missing in the PostgreSQL connection string.");
        }

        var databaseName = SanitizeFilePart(connectionBuilder.Database);
        var fileName = BuildBackupFileName(databaseName, nowLocal, trigger, scheduleSlot, ".dump");
        var outputPath = Path.Combine(targetDirectory, fileName);
        var pgDumpExecutable = ResolvePgDumpExecutable(toolCommand);

        _logger.LogInformation("Creating PostgreSQL backup at {OutputPath} using {Command}", outputPath, pgDumpExecutable);

        var startInfo = new ProcessStartInfo
        {
            FileName = pgDumpExecutable,
            WorkingDirectory = targetDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(connectionBuilder.Host) ? "127.0.0.1" : connectionBuilder.Host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(connectionBuilder.Port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--username");
        startInfo.ArgumentList.Add(connectionBuilder.Username ?? string.Empty);
        startInfo.ArgumentList.Add("--dbname");
        startInfo.ArgumentList.Add(connectionBuilder.Database);
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("custom");
        startInfo.ArgumentList.Add("--compress");
        startInfo.ArgumentList.Add("6");
        startInfo.ArgumentList.Add("--blobs");
        startInfo.ArgumentList.Add("--no-password");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(outputPath);

        if (!string.IsNullOrWhiteSpace(connectionBuilder.Password))
        {
            startInfo.Environment["PGPASSWORD"] = connectionBuilder.Password;
        }

        Process process;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start pg_dump process.");
            }
        }
        catch (Win32Exception ex)
        {
            TryDeleteFile(outputPath);
            throw new InvalidOperationException(
                $"Не удалось запустить pg_dump по пути '{pgDumpExecutable}'. {ex.Message}",
                ex);
        }

        using (process)
        {
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var stdErr = (await stdErrTask).Trim();
            _ = await stdOutTask;

            if (process.ExitCode != 0)
            {
                TryDeleteFile(outputPath);
                var detail = string.IsNullOrWhiteSpace(stdErr)
                    ? $"pg_dump exited with code {process.ExitCode}."
                    : stdErr;
                throw new InvalidOperationException(detail);
            }
        }

        return BuildFileInfo(outputPath, _backupRootDirectory)
            ?? throw new InvalidOperationException("PostgreSQL backup was created but could not be indexed.");
    }

    private bool HasBackupForSlot(string rootDirectory, DateTime localDate, string slotKey)
    {
        var dayDirectory = Path.Combine(
            rootDirectory,
            localDate.ToString("yyyy", CultureInfo.InvariantCulture),
            localDate.ToString("MM", CultureInfo.InvariantCulture),
            localDate.ToString("dd", CultureInfo.InvariantCulture));

        if (!Directory.Exists(dayDirectory))
        {
            return false;
        }

        var pattern = _databaseProvider.IsSqlite
            ? $"*_{_databaseProvider.Key}_*_auto_{slotKey}.zip"
            : $"*_{_databaseProvider.Key}_*_auto_{slotKey}.dump";

        return Directory.EnumerateFiles(dayDirectory, pattern, SearchOption.TopDirectoryOnly).Any();
    }

    private Task CleanupExpiredBackupsAsync(int retentionDays, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (retentionDays <= 0 || !Directory.Exists(_backupRootDirectory))
        {
            return Task.CompletedTask;
        }

        var thresholdUtc = DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcDateTime;
        foreach (var searchPattern in new[] { "*.zip", "*.dump" })
        {
            foreach (var filePath in Directory.EnumerateFiles(_backupRootDirectory, searchPattern, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.LastWriteTimeUtc > thresholdUtc)
                {
                    continue;
                }

                TryDeleteFile(filePath);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(_backupRootDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, false);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<BackupSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await context.SystemSettings
            .AsNoTracking()
            .Where(x => x.Key == AutomaticEnabledSettingKey || x.Key == ScheduleSettingKey || x.Key == RetentionDaysSettingKey)
            .ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);

        var scheduleRaw = GetSettingOrConfig(settings, ScheduleSettingKey, "DatabaseBackup:ScheduleLocal") ?? BuildLegacyScheduleFallback();
        var schedule = ParseSchedule(scheduleRaw, fallbackToDefault: true);
        var normalizedSchedule = FormatSchedule(schedule);

        var retentionDays = ParseRetentionDays(
            GetSettingOrConfig(settings, RetentionDaysSettingKey, "DatabaseBackup:RetentionDays"),
            schedule.Count);

        var automaticEnabled = ParseBoolean(
            GetSettingOrConfig(settings, AutomaticEnabledSettingKey, "DatabaseBackup:Enabled"),
            fallback: true);

        return new BackupSettings(
            automaticEnabled,
            normalizedSchedule,
            schedule,
            retentionDays,
            _backupRootDirectory,
            _databaseProvider.IsPostgreSql ? ResolveConfiguredPgDumpCommand() : null);
    }

    private string? GetSettingOrConfig(IReadOnlyDictionary<string, string> settings, string key, string configPath)
    {
        if (settings.TryGetValue(key, out var settingValue) && !string.IsNullOrWhiteSpace(settingValue))
        {
            return settingValue.Trim();
        }

        var configValue = _configuration[configPath];
        return string.IsNullOrWhiteSpace(configValue) ? null : configValue.Trim();
    }

    private async Task UpsertSystemSettingAsync(
        PaymentContext context,
        string key,
        string value,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var setting = await context.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting is null)
        {
            context.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            return;
        }

        setting.Value = value;
        setting.UpdatedAt = nowUtc;
    }

    private string ResolveBackupRootDirectory()
    {
        var configuredPath = _configuration["DatabaseBackup:Directory"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = _configuration["Backup:Directory"];
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "backups"));
        }

        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    private string BuildLegacyScheduleFallback()
    {
        var legacyIntervalHours = _configuration.GetValue<int?>("Backup:IntervalHours");
        if (!legacyIntervalHours.HasValue || legacyIntervalHours.Value <= 0 || legacyIntervalHours.Value > 24)
        {
            return FormatSchedule(DefaultSchedule);
        }

        var schedule = new List<TimeOnly>();
        for (var hour = 0; hour < 24; hour += legacyIntervalHours.Value)
        {
            schedule.Add(new TimeOnly(hour, 0));
        }

        return schedule.Count > 0 ? FormatSchedule(schedule) : FormatSchedule(DefaultSchedule);
    }

    private int ParseRetentionDays(string? rawValue, int scheduleSlotsPerDay)
    {
        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRetention))
        {
            return Math.Clamp(parsedRetention, 1, 365);
        }

        var legacyMaxFiles = _configuration.GetValue<int?>("Backup:MaxFiles");
        if (legacyMaxFiles.HasValue && legacyMaxFiles.Value > 0)
        {
            var slotsPerDay = Math.Max(1, scheduleSlotsPerDay);
            var approximatedRetention = (int)Math.Ceiling(legacyMaxFiles.Value / (double)slotsPerDay);
            return Math.Clamp(approximatedRetention, 1, 365);
        }

        return 14;
    }

    private static IReadOnlyList<TimeOnly> ParseSchedule(string? rawValue, bool fallbackToDefault)
    {
        var parsed = (rawValue ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time
                : (TimeOnly?)null)
            .Where(static time => time.HasValue)
            .Select(static time => time!.Value)
            .Distinct()
            .OrderBy(time => time)
            .ToList();

        if (parsed.Count > 0)
        {
            return parsed;
        }

        return fallbackToDefault ? DefaultSchedule : [];
    }

    private static string FormatSchedule(IEnumerable<TimeOnly> schedule)
        => string.Join(",", schedule.Select(time => time.ToString("HH:mm", CultureInfo.InvariantCulture)));

    private string? ResolveToolCommandDisplay(string? configuredCommand)
    {
        if (_databaseProvider.IsSqlite || string.IsNullOrWhiteSpace(configuredCommand))
        {
            return null;
        }

        return TryResolvePgDumpExecutable(configuredCommand) ?? configuredCommand;
    }

    private string ResolveConfiguredPgDumpCommand()
    {
        var configured = (_configuration["DatabaseBackup:PgDumpPath"] ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
    }

    private string ResolvePgDumpExecutable(string? configuredCommand)
    {
        var command = string.IsNullOrWhiteSpace(configuredCommand)
            ? ResolveConfiguredPgDumpCommand()
            : configuredCommand;

        return TryResolvePgDumpExecutable(command)
            ?? throw new InvalidOperationException(BuildPgDumpNotFoundMessage(command));
    }

    private static string? TryResolvePgDumpExecutable(string configuredCommand)
    {
        foreach (var candidate in GetPgDumpCandidates(configuredCommand))
        {
            var resolved = TryResolveExecutableCandidate(candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetPgDumpCandidates(string configuredCommand)
    {
        var normalizedConfiguredValue = NormalizeCommandValue(configuredCommand);
        if (!string.IsNullOrWhiteSpace(normalizedConfiguredValue))
        {
            yield return normalizedConfiguredValue;
        }

        var defaultCommand = OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
        if (!string.Equals(normalizedConfiguredValue, defaultCommand, StringComparison.OrdinalIgnoreCase))
        {
            yield return defaultCommand;
        }

        foreach (var candidate in OperatingSystem.IsWindows()
                     ? EnumerateWindowsPgDumpLocations()
                     : EnumerateUnixPgDumpLocations())
        {
            yield return candidate;
        }
    }

    private static string? TryResolveExecutableCandidate(string candidate)
    {
        var normalizedCandidate = NormalizeCommandValue(candidate);
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return null;
        }

        if (Path.IsPathRooted(normalizedCandidate)
            || normalizedCandidate.Contains(Path.DirectorySeparatorChar)
            || normalizedCandidate.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(normalizedCandidate)
                ? Path.GetFullPath(normalizedCandidate)
                : null;
        }

        if (File.Exists(normalizedCandidate))
        {
            return Path.GetFullPath(normalizedCandidate);
        }

        return TryResolveFromPath(normalizedCandidate);
    }

    private static string? TryResolveFromPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = NormalizeCommandValue(rawDirectory);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (var candidateName in EnumerateExecutableNames(executableName))
            {
                var candidatePath = Path.Combine(directory, candidateName);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateExecutableNames(string executableName)
    {
        yield return executableName;

        if (!OperatingSystem.IsWindows() || Path.HasExtension(executableName))
        {
            yield break;
        }

        var pathExtValue = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD";
        foreach (var rawExtension in pathExtValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var extension = rawExtension.StartsWith('.') ? rawExtension : $".{rawExtension}";
            yield return $"{executableName}{extension}";
        }
    }

    private static IEnumerable<string> EnumerateWindowsPgDumpLocations()
    {
        foreach (var rootVariable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            var programFilesRoot = Environment.GetEnvironmentVariable(rootVariable);
            if (string.IsNullOrWhiteSpace(programFilesRoot))
            {
                continue;
            }

            var postgresRoot = Path.Combine(programFilesRoot, "PostgreSQL");
            foreach (var versionDirectory in EnumerateDirectoriesSafe(postgresRoot).OrderByDescending(Path.GetFileName))
            {
                var candidatePath = Path.Combine(versionDirectory, "bin", "pg_dump.exe");
                if (File.Exists(candidatePath))
                {
                    yield return candidatePath;
                }
            }
        }

        var chocolateyRoot = Environment.GetEnvironmentVariable("ChocolateyInstall");
        if (!string.IsNullOrWhiteSpace(chocolateyRoot))
        {
            var candidatePath = Path.Combine(chocolateyRoot, "bin", "pg_dump.exe");
            if (File.Exists(candidatePath))
            {
                yield return candidatePath;
            }
        }
    }

    private static IEnumerable<string> EnumerateUnixPgDumpLocations()
    {
        foreach (var candidatePath in new[] { "/usr/bin/pg_dump", "/usr/local/bin/pg_dump", "/opt/homebrew/bin/pg_dump" })
        {
            if (File.Exists(candidatePath))
            {
                yield return candidatePath;
            }
        }

        foreach (var versionDirectory in EnumerateDirectoriesSafe("/usr/lib/postgresql").OrderByDescending(Path.GetFileName))
        {
            var candidatePath = Path.Combine(versionDirectory, "bin", "pg_dump");
            if (File.Exists(candidatePath))
            {
                yield return candidatePath;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string NormalizeCommandValue(string? value)
        => Environment.ExpandEnvironmentVariables((value ?? string.Empty).Trim().Trim('"'));

    private static string BuildPgDumpNotFoundMessage(string configuredCommand)
    {
        var normalizedConfiguredValue = NormalizeCommandValue(configuredCommand);
        var configuredHint = string.IsNullOrWhiteSpace(normalizedConfiguredValue)
            ? "Автоматический поиск pg_dump не дал результата."
            : $"Не удалось найти pg_dump по значению '{normalizedConfiguredValue}'.";

        return $"{configuredHint} Для Windows укажите полный путь, например 'C:\\Program Files\\PostgreSQL\\16\\bin\\pg_dump.exe', либо добавьте каталог PostgreSQL\\bin в PATH. Для Linux установите пакет postgresql-client и укажите путь вроде '/usr/bin/pg_dump', если он не попал в PATH.";
    }

    private static bool ParseBoolean(string? value, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private string BuildBackupFileName(string baseName, DateTimeOffset nowLocal, string trigger, string? scheduleSlot, string extension)
    {
        var scheduleSuffix = string.IsNullOrWhiteSpace(scheduleSlot) ? string.Empty : $"_{scheduleSlot}";
        return $"{baseName}_{_databaseProvider.Key}_{nowLocal:yyyyMMdd_HHmmss}_{trigger}{scheduleSuffix}{extension}";
    }

    private static DatabaseBackupFileInfo? BuildFileInfo(string filePath, string rootDirectory)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var fileInfo = new FileInfo(filePath);
        var relativePath = Path.GetRelativePath(rootDirectory, filePath).Replace('\\', '/');
        var trigger = fileInfo.Name.Contains("_manual", StringComparison.OrdinalIgnoreCase)
            ? "manual"
            : fileInfo.Name.Contains("_auto", StringComparison.OrdinalIgnoreCase)
                ? "auto"
                : "unknown";

        return new DatabaseBackupFileInfo(
            fileInfo.Name,
            relativePath,
            fileInfo.Length,
            DateTime.SpecifyKind(fileInfo.LastWriteTimeUtc, DateTimeKind.Utc),
            trigger);
    }

    private static string SanitizeFilePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "payplanner";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Trim()
            .Select(ch => invalidChars.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "payplanner" : sanitized;
    }

    private static string NormalizeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath + Path.DirectorySeparatorChar;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record BackupSettings(
        bool AutomaticEnabled,
        string ScheduleDisplay,
        IReadOnlyList<TimeOnly> Schedule,
        int RetentionDays,
        string RootDirectory,
        string? ToolCommand);
}
