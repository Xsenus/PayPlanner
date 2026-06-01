using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

public sealed record DatabaseBackupS3UploadSettings(
    bool Enabled,
    IReadOnlyList<string> ConnectionIds,
    string PrefixTemplate);

public sealed record DatabaseBackupS3UploadResult(
    string Id,
    string BackupRelativePath,
    string BackupFileName,
    string ConnectionId,
    string Status,
    string TargetPath,
    string? TargetKey,
    long SizeBytes,
    string? Error,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record DatabaseBackupYandexDiskUploadSettings(
    bool Enabled,
    string PrefixTemplate);

public sealed record DatabaseBackupYandexDiskUploadResult(
    string Id,
    string BackupRelativePath,
    string BackupFileName,
    string Status,
    string TargetPath,
    long SizeBytes,
    string? Error,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record DatabaseBackupFormatOption(
    string Key,
    string Label,
    string Description,
    string FileExtension,
    bool IsDefault);

public sealed record DatabaseBackupOverview(
    string Provider,
    string ProviderDisplayName,
    string BackupFormat,
    string FileExtension,
    string SelectedFormat,
    IReadOnlyList<DatabaseBackupFormatOption> AvailableFormats,
    bool AutomaticEnabled,
    string ScheduleLocal,
    int RetentionDays,
    string RootDirectory,
    string TimeZone,
    string? ToolCommand,
    DatabaseBackupS3UploadSettings S3Upload,
    IReadOnlyList<DatabaseBackupS3UploadResult> S3UploadResults,
    DatabaseBackupYandexDiskUploadSettings YandexDiskUpload,
    IReadOnlyList<DatabaseBackupYandexDiskUploadResult> YandexDiskUploadResults,
    IReadOnlyList<DatabaseBackupFileInfo> Items);

public sealed record DatabaseBackupCreateResult(
    DatabaseBackupFileInfo Backup,
    string DownloadPath,
    IReadOnlyList<DatabaseBackupS3UploadResult> S3UploadResults,
    IReadOnlyList<DatabaseBackupYandexDiskUploadResult> YandexDiskUploadResults);

public sealed record DatabaseBackupDeleteResult(
    int DeletedCount,
    bool DeletedAll);

public sealed class DatabaseBackupService
{
    private const string AutomaticEnabledSettingKey = "database-backup.enabled";
    private const string ScheduleSettingKey = "database-backup.schedule-local";
    private const string RetentionDaysSettingKey = "database-backup.retention-days";
    private const string FormatSettingKey = "database-backup.format";
    private const string S3UploadEnabledSettingKey = "database-backup.s3.enabled";
    private const string S3ConnectionIdsSettingKey = "database-backup.s3.connection-ids";
    private const string S3PrefixTemplateSettingKey = "database-backup.s3.prefix-template";
    private const string S3UploadResultsSettingKey = "database-backup.s3.results";
    private const string YandexDiskUploadEnabledSettingKey = "database-backup.yandex-disk.enabled";
    private const string YandexDiskPrefixTemplateSettingKey = "database-backup.yandex-disk.prefix-template";
    private const string YandexDiskUploadResultsSettingKey = "database-backup.yandex-disk.results";
    private const string DefaultS3PrefixTemplate = "backups/database/{yyyy}/{MM}/";
    private const string DefaultYandexDiskPrefixTemplate = "backups/database/{yyyy}/{MM}/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeOnly[] DefaultSchedule =
    [
        new(3, 0),
        new(15, 0)
    ];

    private readonly IDbContextFactory<PaymentContext> _contextFactory;
    private readonly IConfiguration _configuration;
    private readonly DatabaseProviderInfo _databaseProvider;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _backupSemaphore = new(1, 1);
    private readonly string _backupRootDirectory;

    public DatabaseBackupService(
        IDbContextFactory<PaymentContext> contextFactory,
        IConfiguration configuration,
        DatabaseProviderInfo databaseProvider,
        ILogger<DatabaseBackupService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _contextFactory = contextFactory;
        _configuration = configuration;
        _databaseProvider = databaseProvider;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _backupRootDirectory = ResolveBackupRootDirectory();
        Directory.CreateDirectory(_backupRootDirectory);
    }

    public TimeSpan PollingInterval => TimeSpan.FromMinutes(1);

    public async Task<DatabaseBackupOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var items = await ListBackupsAsync(cancellationToken);
        var s3Results = await LoadS3UploadResultsAsync(cancellationToken);
        var yandexDiskResults = await LoadYandexDiskUploadResultsAsync(cancellationToken);

        return new DatabaseBackupOverview(
            _databaseProvider.Key,
            _databaseProvider.DisplayName,
            settings.Format.Label,
            settings.Format.FileExtension,
            settings.Format.Key,
            settings.AvailableFormats,
            settings.AutomaticEnabled,
            settings.ScheduleDisplay,
            settings.RetentionDays,
            settings.RootDirectory,
            TimeZoneInfo.Local.DisplayName,
            ResolveToolCommandDisplay(settings.ToolCommand),
            settings.S3Upload,
            s3Results,
            settings.YandexDiskUpload,
            yandexDiskResults,
            items);
    }

    public Task<IReadOnlyList<DatabaseBackupFileInfo>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_backupRootDirectory);

        var items = GetBackupSearchPatterns()
            .SelectMany(pattern => Directory.EnumerateFiles(_backupRootDirectory, pattern, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
        string? backupFormat,
        bool? s3UploadEnabled = null,
        IReadOnlyList<string>? s3ConnectionIds = null,
        string? s3PrefixTemplate = null,
        bool? yandexDiskUploadEnabled = null,
        string? yandexDiskPrefixTemplate = null,
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
        var format = ResolveSelectedFormat(backupFormat, fallbackToDefault: false);
        var now = DateTime.UtcNow;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await UpsertSystemSettingAsync(context, AutomaticEnabledSettingKey, automaticEnabled ? "true" : "false", now, cancellationToken);
        await UpsertSystemSettingAsync(context, ScheduleSettingKey, normalizedSchedule, now, cancellationToken);
        await UpsertSystemSettingAsync(context, RetentionDaysSettingKey, retentionDays.ToString(CultureInfo.InvariantCulture), now, cancellationToken);
        await UpsertSystemSettingAsync(context, FormatSettingKey, format.Key, now, cancellationToken);
        if (s3UploadEnabled.HasValue)
        {
            await UpsertSystemSettingAsync(context, S3UploadEnabledSettingKey, s3UploadEnabled.Value ? "true" : "false", now, cancellationToken);
        }

        if (s3ConnectionIds is not null)
        {
            var normalizedIds = NormalizeS3ConnectionIds(s3ConnectionIds);
            await UpsertSystemSettingAsync(context, S3ConnectionIdsSettingKey, JsonSerializer.Serialize(normalizedIds, JsonOptions), now, cancellationToken);
        }

        if (s3PrefixTemplate is not null)
        {
            await UpsertSystemSettingAsync(context, S3PrefixTemplateSettingKey, NormalizeCloudBackupPrefixTemplate(s3PrefixTemplate, DefaultS3PrefixTemplate), now, cancellationToken);
        }

        if (yandexDiskUploadEnabled.HasValue)
        {
            await UpsertSystemSettingAsync(context, YandexDiskUploadEnabledSettingKey, yandexDiskUploadEnabled.Value ? "true" : "false", now, cancellationToken);
        }

        if (yandexDiskPrefixTemplate is not null)
        {
            await UpsertSystemSettingAsync(context, YandexDiskPrefixTemplateSettingKey, NormalizeCloudBackupPrefixTemplate(yandexDiskPrefixTemplate, DefaultYandexDiskPrefixTemplate), now, cancellationToken);
        }
        await context.SaveChangesAsync(cancellationToken);

        return await GetOverviewAsync(cancellationToken);
    }

    public async Task<DatabaseBackupCreateResult> CreateManualBackupAsync(CancellationToken cancellationToken = default)
    {
        var (backup, s3UploadResults, yandexDiskUploadResults) = await CreateBackupAsync("manual", null, cancellationToken);
        return new DatabaseBackupCreateResult(
            backup,
            $"/api/database/backups/download?relativePath={Uri.EscapeDataString(backup.RelativePath)}",
            s3UploadResults,
            yandexDiskUploadResults);
    }

    public async Task<DatabaseBackupS3UploadResult> RetryS3UploadAsync(string? uploadResultId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uploadResultId))
        {
            throw new ArgumentException("Укажите S3-выгрузку для повторной отправки.");
        }

        var storedResults = await LoadS3UploadResultsAsync(cancellationToken);
        var storedResult = storedResults.FirstOrDefault(result =>
            string.Equals(result.Id, uploadResultId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (storedResult is null)
        {
            throw new FileNotFoundException("S3-выгрузка резервной копии не найдена.");
        }

        if (!string.Equals(storedResult.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Повторить можно только S3-выгрузку, которая завершилась ошибкой.");
        }

        var resolvedPath = ResolveBackupPath(storedResult.BackupRelativePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            throw new FileNotFoundException("Локальная резервная копия для повторной отправки не найдена.");
        }

        var fileInfo = new FileInfo(resolvedPath);
        var pendingResult = storedResult with
        {
            Status = "pending",
            TargetPath = string.IsNullOrWhiteSpace(storedResult.TargetPath) ? storedResult.BackupFileName : storedResult.TargetPath,
            TargetKey = null,
            SizeBytes = fileInfo.Length,
            Error = null,
            UpdatedAt = DateTime.UtcNow,
        };
        await SaveS3UploadResultAsync(pendingResult, cancellationToken);

        var completedResult = await UploadBackupFileToS3Async(pendingResult, resolvedPath, cancellationToken);
        await SaveS3UploadResultAsync(completedResult, cancellationToken);

        return completedResult;
    }

    public async Task<DatabaseBackupYandexDiskUploadResult> RetryYandexDiskUploadAsync(string? uploadResultId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uploadResultId))
        {
            throw new ArgumentException("Укажите выгрузку Яндекс.Диска для повторной отправки.");
        }

        var storedResults = await LoadYandexDiskUploadResultsAsync(cancellationToken);
        var storedResult = storedResults.FirstOrDefault(result =>
            string.Equals(result.Id, uploadResultId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (storedResult is null)
        {
            throw new FileNotFoundException("Выгрузка резервной копии на Яндекс.Диск не найдена.");
        }

        if (!string.Equals(storedResult.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Повторить можно только выгрузку на Яндекс.Диск, которая завершилась ошибкой.");
        }

        var resolvedPath = ResolveBackupPath(storedResult.BackupRelativePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            throw new FileNotFoundException("Локальная резервная копия для повторной отправки не найдена.");
        }

        var fileInfo = new FileInfo(resolvedPath);
        var pendingResult = storedResult with
        {
            Status = "pending",
            TargetPath = string.IsNullOrWhiteSpace(storedResult.TargetPath) ? storedResult.BackupFileName : storedResult.TargetPath,
            SizeBytes = fileInfo.Length,
            Error = null,
            UpdatedAt = DateTime.UtcNow,
        };
        await SaveYandexDiskUploadResultAsync(pendingResult, cancellationToken);

        var completedResult = await UploadBackupFileToYandexDiskAsync(pendingResult, resolvedPath, cancellationToken);
        await SaveYandexDiskUploadResultAsync(completedResult, cancellationToken);

        return completedResult;
    }

    public Task<DatabaseBackupDeleteResult> DeleteBackupAsync(string? relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedPath = ResolveBackupPath(relativePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            throw new FileNotFoundException("Резервная копия не найдена.");
        }

        TryDeleteFile(resolvedPath);
        CleanupEmptyDirectories();
        return Task.FromResult(new DatabaseBackupDeleteResult(1, DeletedAll: false));
    }

    public Task<DatabaseBackupDeleteResult> DeleteAllBackupsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var deletedCount = 0;
        foreach (var pattern in GetBackupSearchPatterns())
        {
            foreach (var filePath in Directory.EnumerateFiles(_backupRootDirectory, pattern, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDeleteFile(filePath);
                deletedCount++;
            }
        }

        CleanupEmptyDirectories();
        return Task.FromResult(new DatabaseBackupDeleteResult(deletedCount, DeletedAll: true));
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

    private async Task<(
        DatabaseBackupFileInfo Backup,
        IReadOnlyList<DatabaseBackupS3UploadResult> S3UploadResults,
        IReadOnlyList<DatabaseBackupYandexDiskUploadResult> YandexDiskUploadResults)> CreateBackupAsync(
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
                ? await CreateSqliteBackupAsync(targetDirectory, settings.Format, nowLocal, trigger, scheduleSlot, cancellationToken)
                : await CreatePostgreSqlBackupAsync(targetDirectory, settings.Format, settings.ToolCommand, nowLocal, trigger, scheduleSlot, cancellationToken);

            await CleanupExpiredBackupsAsync(settings.RetentionDays, cancellationToken);
            var s3UploadResults = await UploadBackupToS3Async(fileInfo, settings.S3Upload, nowLocal, cancellationToken);
            var yandexDiskUploadResults = await UploadBackupToYandexDiskAsync(fileInfo, settings.YandexDiskUpload, nowLocal, cancellationToken);
            return (fileInfo, s3UploadResults, yandexDiskUploadResults);
        }
        finally
        {
            _backupSemaphore.Release();
        }
    }

    private async Task<DatabaseBackupFileInfo> CreateSqliteBackupAsync(
        string targetDirectory,
        DatabaseBackupFormatOption format,
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
        var fileName = BuildBackupFileName(baseName, nowLocal, trigger, scheduleSlot, format.FileExtension);
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

            if (string.Equals(format.Key, "sqlite", StringComparison.OrdinalIgnoreCase))
            {
                File.Move(tempCopyPath, outputPath, overwrite: true);
                return BuildFileInfo(outputPath, _backupRootDirectory)
                    ?? throw new InvalidOperationException("SQLite backup was created but could not be indexed.");
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
        DatabaseBackupFormatOption format,
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
        var fileName = BuildBackupFileName(databaseName, nowLocal, trigger, scheduleSlot, format.FileExtension);
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
        startInfo.ArgumentList.Add(string.Equals(format.Key, "sql", StringComparison.OrdinalIgnoreCase) ? "plain" : "custom");
        if (!string.Equals(format.Key, "sql", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--compress");
            startInfo.ArgumentList.Add("6");
            startInfo.ArgumentList.Add("--blobs");
        }
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

        var pattern = $"*_{_databaseProvider.Key}_*_auto_{slotKey}.*";
        return Directory.EnumerateFiles(dayDirectory, pattern, SearchOption.TopDirectoryOnly)
            .Any(filePath => GetBackupSearchPatterns().Any(searchPattern =>
                IsFileMatchingPattern(filePath, searchPattern)));
    }

    private Task CleanupExpiredBackupsAsync(int retentionDays, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (retentionDays <= 0 || !Directory.Exists(_backupRootDirectory))
        {
            return Task.CompletedTask;
        }

        var thresholdUtc = DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcDateTime;
        foreach (var searchPattern in GetBackupSearchPatterns())
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

        CleanupEmptyDirectories(cancellationToken);

        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<DatabaseBackupS3UploadResult>> UploadBackupToS3Async(
        DatabaseBackupFileInfo backup,
        DatabaseBackupS3UploadSettings settings,
        DateTimeOffset nowLocal,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled || settings.ConnectionIds.Count == 0)
        {
            return [];
        }

        var resolvedPath = ResolveBackupPath(backup.RelativePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return [];
        }

        var results = new List<DatabaseBackupS3UploadResult>();

        foreach (var connectionId in settings.ConnectionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startedAt = DateTime.UtcNow;
            var targetPath = BuildCloudBackupTargetPath(settings.PrefixTemplate, backup.FileName, nowLocal, DefaultS3PrefixTemplate);
            var result = new DatabaseBackupS3UploadResult(
                Guid.NewGuid().ToString("N"),
                backup.RelativePath,
                backup.FileName,
                connectionId,
                "pending",
                targetPath,
                null,
                backup.SizeBytes,
                null,
                startedAt,
                startedAt);

            await SaveS3UploadResultAsync(result, cancellationToken);

            result = await UploadBackupFileToS3Async(result, resolvedPath, cancellationToken);

            await SaveS3UploadResultAsync(result, cancellationToken);
            results.Add(result);
        }

        return results;
    }

    private async Task<DatabaseBackupS3UploadResult> UploadBackupFileToS3Async(
        DatabaseBackupS3UploadResult result,
        string resolvedPath,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var s3StorageService = scope.ServiceProvider.GetRequiredService<IS3StorageService>();

        try
        {
            await using var stream = File.OpenRead(resolvedPath);
            var upload = await s3StorageService.UploadAsync(
                result.ConnectionId,
                result.TargetPath,
                stream,
                "application/octet-stream",
                overwrite: true,
                cancellationToken);

            return result with
            {
                Status = "success",
                TargetPath = upload.Path,
                TargetKey = upload.Key,
                Error = null,
                UpdatedAt = DateTime.UtcNow,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to upload database backup {BackupPath} to S3 connection {ConnectionId}.", result.BackupRelativePath, result.ConnectionId);
            return result with
            {
                Status = "failed",
                Error = ex.Message,
                UpdatedAt = DateTime.UtcNow,
            };
        }
    }

    private async Task<IReadOnlyList<DatabaseBackupYandexDiskUploadResult>> UploadBackupToYandexDiskAsync(
        DatabaseBackupFileInfo backup,
        DatabaseBackupYandexDiskUploadSettings settings,
        DateTimeOffset nowLocal,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            return [];
        }

        var resolvedPath = ResolveBackupPath(backup.RelativePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return [];
        }

        var startedAt = DateTime.UtcNow;
        var targetPath = BuildCloudBackupTargetPath(settings.PrefixTemplate, backup.FileName, nowLocal, DefaultYandexDiskPrefixTemplate);
        var result = new DatabaseBackupYandexDiskUploadResult(
            Guid.NewGuid().ToString("N"),
            backup.RelativePath,
            backup.FileName,
            "pending",
            targetPath,
            backup.SizeBytes,
            null,
            startedAt,
            startedAt);

        await SaveYandexDiskUploadResultAsync(result, cancellationToken);

        result = await UploadBackupFileToYandexDiskAsync(result, resolvedPath, cancellationToken);

        await SaveYandexDiskUploadResultAsync(result, cancellationToken);
        return [result];
    }

    private async Task<DatabaseBackupYandexDiskUploadResult> UploadBackupFileToYandexDiskAsync(
        DatabaseBackupYandexDiskUploadResult result,
        string resolvedPath,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var yandexDiskService = scope.ServiceProvider.GetRequiredService<IYandexDiskService>();

        try
        {
            await using var stream = File.OpenRead(resolvedPath);
            var upload = await yandexDiskService.UploadAsync(
                result.TargetPath,
                stream,
                "application/octet-stream",
                overwrite: true,
                cancellationToken);

            return result with
            {
                Status = "success",
                TargetPath = upload.Path,
                Error = null,
                UpdatedAt = DateTime.UtcNow,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to upload database backup {BackupPath} to Yandex Disk.", result.BackupRelativePath);
            return result with
            {
                Status = "failed",
                Error = ex.Message,
                UpdatedAt = DateTime.UtcNow,
            };
        }
    }

    private async Task<IReadOnlyList<DatabaseBackupS3UploadResult>> LoadS3UploadResultsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var raw = await context.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == S3UploadResultsSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return DeserializeS3UploadResults(raw);
    }

    private async Task SaveS3UploadResultAsync(DatabaseBackupS3UploadResult result, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var setting = await context.SystemSettings.FirstOrDefaultAsync(item => item.Key == S3UploadResultsSettingKey, cancellationToken);
        var results = DeserializeS3UploadResults(setting?.Value)
            .Where(item => !string.Equals(item.Id, result.Id, StringComparison.OrdinalIgnoreCase))
            .Prepend(result)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(100)
            .ToList();

        var value = JsonSerializer.Serialize(results, JsonOptions);
        if (setting is null)
        {
            context.SystemSettings.Add(new SystemSetting
            {
                Key = S3UploadResultsSettingKey,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<DatabaseBackupS3UploadResult> DeserializeS3UploadResults(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DatabaseBackupS3UploadResult>>(raw, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<DatabaseBackupYandexDiskUploadResult>> LoadYandexDiskUploadResultsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var raw = await context.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == YandexDiskUploadResultsSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return DeserializeYandexDiskUploadResults(raw);
    }

    private async Task SaveYandexDiskUploadResultAsync(DatabaseBackupYandexDiskUploadResult result, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var setting = await context.SystemSettings.FirstOrDefaultAsync(item => item.Key == YandexDiskUploadResultsSettingKey, cancellationToken);
        var results = DeserializeYandexDiskUploadResults(setting?.Value)
            .Where(item => !string.Equals(item.Id, result.Id, StringComparison.OrdinalIgnoreCase))
            .Prepend(result)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(100)
            .ToList();

        var value = JsonSerializer.Serialize(results, JsonOptions);
        if (setting is null)
        {
            context.SystemSettings.Add(new SystemSetting
            {
                Key = YandexDiskUploadResultsSettingKey,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<DatabaseBackupYandexDiskUploadResult> DeserializeYandexDiskUploadResults(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DatabaseBackupYandexDiskUploadResult>>(raw, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<BackupSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await context.SystemSettings
            .AsNoTracking()
            .Where(x =>
                x.Key == AutomaticEnabledSettingKey
                || x.Key == ScheduleSettingKey
                || x.Key == RetentionDaysSettingKey
                || x.Key == FormatSettingKey
                || x.Key == S3UploadEnabledSettingKey
                || x.Key == S3ConnectionIdsSettingKey
                || x.Key == S3PrefixTemplateSettingKey
                || x.Key == YandexDiskUploadEnabledSettingKey
                || x.Key == YandexDiskPrefixTemplateSettingKey)
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
        var selectedFormat = ResolveSelectedFormat(
            GetSettingOrConfig(settings, FormatSettingKey, "DatabaseBackup:Format"),
            fallbackToDefault: true);
        var availableFormats = GetAvailableFormats();
        var s3Upload = new DatabaseBackupS3UploadSettings(
            ParseBoolean(GetSettingOrConfig(settings, S3UploadEnabledSettingKey, "DatabaseBackup:S3:Enabled"), fallback: false),
            ParseS3ConnectionIds(GetSettingOrConfig(settings, S3ConnectionIdsSettingKey, "DatabaseBackup:S3:ConnectionIds")),
            NormalizeCloudBackupPrefixTemplate(GetSettingOrConfig(settings, S3PrefixTemplateSettingKey, "DatabaseBackup:S3:PrefixTemplate") ?? DefaultS3PrefixTemplate, DefaultS3PrefixTemplate));
        var yandexDiskUpload = new DatabaseBackupYandexDiskUploadSettings(
            ParseBoolean(GetSettingOrConfig(settings, YandexDiskUploadEnabledSettingKey, "DatabaseBackup:YandexDisk:Enabled"), fallback: false),
            NormalizeCloudBackupPrefixTemplate(GetSettingOrConfig(settings, YandexDiskPrefixTemplateSettingKey, "DatabaseBackup:YandexDisk:PrefixTemplate") ?? DefaultYandexDiskPrefixTemplate, DefaultYandexDiskPrefixTemplate));

        return new BackupSettings(
            automaticEnabled,
            normalizedSchedule,
            schedule,
            retentionDays,
            selectedFormat,
            availableFormats,
            _backupRootDirectory,
            _databaseProvider.IsPostgreSql ? ResolveConfiguredPgDumpCommand() : null,
            s3Upload,
            yandexDiskUpload);
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

    private IReadOnlyList<DatabaseBackupFormatOption> GetAvailableFormats()
    {
        if (_databaseProvider.IsSqlite)
        {
            return
            [
                new DatabaseBackupFormatOption("zip", "SQLite archive (.zip)", "Консистентная копия SQLite, упакованная в ZIP.", ".zip", true),
                new DatabaseBackupFormatOption("sqlite", "SQLite file (.sqlite)", "Отдельный консистентный файл SQLite без архива.", ".sqlite", false),
            ];
        }

        return
        [
            new DatabaseBackupFormatOption("backup", "PostgreSQL backup (.backup)", "Custom backup через pg_dump, подходит для pg_restore.", ".backup", true),
            new DatabaseBackupFormatOption("sql", "SQL script (.sql)", "Обычный SQL-дамп через pg_dump --format=plain.", ".sql", false),
        ];
    }

    private DatabaseBackupFormatOption ResolveSelectedFormat(string? requestedFormat, bool fallbackToDefault)
    {
        var availableFormats = GetAvailableFormats();
        var normalizedRequestedFormat = (requestedFormat ?? string.Empty).Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(normalizedRequestedFormat))
        {
          var selected = availableFormats.FirstOrDefault(option => option.Key == normalizedRequestedFormat);
          if (selected is not null)
          {
              return selected;
          }
        }

        if (!fallbackToDefault)
        {
            var supported = string.Join(", ", availableFormats.Select(option => option.Key));
            throw new ArgumentException($"Неподдерживаемый формат резервной копии. Доступно: {supported}.");
        }

        return availableFormats.First(option => option.IsDefault);
    }

    private IReadOnlyList<string> GetBackupSearchPatterns()
    {
        if (_databaseProvider.IsSqlite)
        {
            return ["*.zip", "*.sqlite"];
        }

        return ["*.backup", "*.dump", "*.sql"];
    }

    private void CleanupEmptyDirectories(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_backupRootDirectory))
        {
            return;
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
    }

    private static bool IsFileMatchingPattern(string filePath, string searchPattern)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(searchPattern.TrimStart('*'), extension, StringComparison.OrdinalIgnoreCase);
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

    private static IReadOnlyList<string> ParseS3ConnectionIds(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(rawValue, JsonOptions);
            if (parsed is not null)
            {
                return NormalizeS3ConnectionIds(parsed);
            }
        }
        catch (JsonException)
        {
        }

        return NormalizeS3ConnectionIds(rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static IReadOnlyList<string> NormalizeS3ConnectionIds(IEnumerable<string> connectionIds)
        => connectionIds
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

    private static string NormalizeCloudBackupPrefixTemplate(string? value, string fallback)
    {
        var template = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Replace('\\', '/');
        template = template.Trim('/');
        if (string.IsNullOrWhiteSpace(template))
        {
            template = fallback.Trim('/');
        }

        var parts = template.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(part => part == "." || part == ".."))
        {
            throw new ArgumentException("Путь облачного хранения резервных копий не должен содержать переходы между каталогами.");
        }

        return string.Join('/', parts);
    }

    private static string BuildCloudBackupTargetPath(string prefixTemplate, string fileName, DateTimeOffset nowLocal, string fallback)
    {
        var prefix = NormalizeCloudBackupPrefixTemplate(prefixTemplate, fallback)
            .Replace("{yyyy}", nowLocal.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{MM}", nowLocal.ToString("MM", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{dd}", nowLocal.ToString("dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        return string.IsNullOrWhiteSpace(prefix)
            ? fileName
            : $"{prefix.TrimEnd('/')}/{fileName}";
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
        DatabaseBackupFormatOption Format,
        IReadOnlyList<DatabaseBackupFormatOption> AvailableFormats,
        string RootDirectory,
        string? ToolCommand,
        DatabaseBackupS3UploadSettings S3Upload,
        DatabaseBackupYandexDiskUploadSettings YandexDiskUpload);
}
