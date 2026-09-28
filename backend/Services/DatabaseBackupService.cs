using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

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

public sealed record DatabaseBackupManifest(
    int FormatVersion,
    string BackupId,
    string BackupFileName,
    string BackupRelativePath,
    string DatabaseProvider,
    string BackupFormat,
    long SizeBytes,
    string Sha256,
    string Trigger,
    DateTime CreatedAtUtc,
    string ApplicationVersion,
    string Status = "complete",
    IReadOnlyList<DatabaseBackupPartManifest>? Parts = null,
    string? RecoveryKeyReference = null);

public sealed record DatabaseBackupPartManifest(
    string Kind,
    string RelativePath,
    long SizeBytes,
    string Sha256,
    bool Required,
    IReadOnlyList<DatabaseBackupFileRootMapping>? FileRoots = null,
    string? FileEntryHashFormat = null);

public sealed record DatabaseBackupVerificationResult(
    string Status,
    bool Verified,
    string BackupRelativePath,
    string? BackupId,
    long ActualSizeBytes,
    long? ExpectedSizeBytes,
    string? ActualSha256,
    string? ExpectedSha256,
    string? Error);

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
    private readonly IBackupDirectoryReader _directoryReader;
    private readonly SemaphoreSlim _backupSemaphore = new(1, 1);
    private static readonly TimeSpan RootLockTimeout = TimeSpan.FromMinutes(30);
    private string? _cachedVerifiedBackupPath;
    private string? _cachedVerifiedBackupStamp;
    private DateTime _cachedVerifiedBackupAtUtc;
    private string? _cachedVerifiedFilePartHash;
    private readonly string _backupRootDirectory;

    public DatabaseBackupService(
        IDbContextFactory<PaymentContext> contextFactory,
        IConfiguration configuration,
        DatabaseProviderInfo databaseProvider,
        ILogger<DatabaseBackupService> logger,
        IServiceScopeFactory scopeFactory,
        IBackupDirectoryReader? directoryReader = null)
    {
        _contextFactory = contextFactory;
        _configuration = configuration;
        _databaseProvider = databaseProvider;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _directoryReader = directoryReader ?? new PhysicalBackupDirectoryReader();
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

    public async Task<DatabaseBackupCatalogPage> GetCatalogAsync(
        string? after = null, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var page = await new DatabaseBackupCatalogReader(context).ReadAsync(after, pageSize, cancellationToken);
        var settings = await LoadSettingsAsync(context, cancellationToken);
        foreach (var set in page.Items)
            set.Status = DatabaseBackupSetStateEvaluator.Evaluate(set.Copies, settings.S3Upload.Enabled,
                settings.S3Upload.ConnectionIds, settings.YandexDiskUpload.Enabled);
        return page;
    }

    public Task<IReadOnlyList<DatabaseBackupFileInfo>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_backupRootDirectory);

        var items = GetBackupSearchPatterns()
            .SelectMany(pattern => EnumerateBackupFiles(pattern, cancellationToken))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => BuildFileInfo(path, _backupRootDirectory))
            .Where(static item => item is not null)
            .Select(static item => item!)
            .OrderByDescending(item => item.CreatedAt)
            .Take(100)
            .ToList();

        return Task.FromResult<IReadOnlyList<DatabaseBackupFileInfo>>(items);
    }

    public async Task<DatabaseBackupCatalogImportResult> ImportCatalogAsync(
        IReadOnlyList<string> relativePaths, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        if (relativePaths.Count is < 1 or > 20)
            throw new ArgumentException("Выберите от 1 до 20 резервных копий за один импорт.");

        await _backupSemaphore.WaitAsync(cancellationToken);
        try
        {
            await using var rootLock = await BackupDirectoryLease.AcquireAsync(
                _backupRootDirectory, RootLockTimeout, cancellationToken);
            var items = new List<DatabaseBackupCatalogImportItem>();
            foreach (var relativePath in relativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 2048
                        || Path.IsPathRooted(relativePath)
                        || relativePath.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
                        throw new ArgumentException("Нужен относительный путь внутри каталога резервных копий.");

                    var created = await EnsureBackupSetCatalogedAsync(relativePath, cancellationToken, dryRun);
                    items.Add(new(relativePath, created ? dryRun ? "ready" : "imported" : "alreadyRegistered"));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    items.Add(new(relativePath ?? string.Empty, "failed", ex.Message));
                }
            }
            return new(dryRun, items);
        }
        finally
        {
            _backupSemaphore.Release();
        }
    }

    public async Task<DatabaseBackupDiscoveryPage> DiscoverBackupsAsync(
        string? cursor = null, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        await _backupSemaphore.WaitAsync(cancellationToken);
        try
        {
            await using var rootLock = await BackupDirectoryLease.AcquireAsync(_backupRootDirectory, RootLockTimeout, cancellationToken);
            var page = new DatabaseBackupInventoryScanner(_backupRootDirectory, _directoryReader).Read(cursor, pageSize, cancellationToken);
            if (page.Items.Count == 0) return page;
            var paths = page.Items.Select(item => item.RelativePath).ToArray();
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var registered = (await context.DatabaseBackupSets.AsNoTracking().Where(item => paths.Contains(item.BackupRelativePath))
                .Select(item => item.BackupRelativePath).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            return page with { Items = page.Items.Select(item => item with { AlreadyRegistered = registered.Contains(item.RelativePath) }).ToArray() };
        }
        finally { _backupSemaphore.Release(); }
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
        var (backup, s3UploadResults, yandexDiskUploadResults) = await CreateBackupAsync("manual", null, cancellationToken)
            ?? throw new InvalidOperationException("Не удалось создать резервную копию по ручному запросу.");
        return new DatabaseBackupCreateResult(
            backup,
            $"/api/database/backups/download?relativePath={Uri.EscapeDataString(backup.RelativePath)}",
            s3UploadResults,
            yandexDiskUploadResults);
    }

    public Task<DatabaseBackupS3UploadResult> RetryS3UploadAsync(string? uploadResultId, CancellationToken cancellationToken = default)
        => WithBackupDirectoryLeaseAsync(() => RetryS3UploadCoreAsync(uploadResultId, cancellationToken), cancellationToken);

    private async Task<DatabaseBackupS3UploadResult> RetryS3UploadCoreAsync(string? uploadResultId, CancellationToken cancellationToken)
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

        if (!string.Equals(storedResult.Status, "failed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(storedResult.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Повторить можно только ошибочную или неподтверждённую S3-выгрузку. Успешная отправка повторно не выполняется.");
        }

        var resolvedPath = ResolveBackupPath(storedResult.BackupRelativePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            throw new FileNotFoundException("Локальная резервная копия для повторной отправки не найдена.");
        }

        await EnsureBackupSetCatalogedAsync(storedResult.BackupRelativePath, cancellationToken);

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
        return await UploadAndRecordS3Async(pendingResult, resolvedPath, cancellationToken);
    }

    public Task<DatabaseBackupYandexDiskUploadResult> RetryYandexDiskUploadAsync(string? uploadResultId, CancellationToken cancellationToken = default)
        => WithBackupDirectoryLeaseAsync(() => RetryYandexDiskUploadCoreAsync(uploadResultId, cancellationToken), cancellationToken);

    private async Task<DatabaseBackupYandexDiskUploadResult> RetryYandexDiskUploadCoreAsync(string? uploadResultId, CancellationToken cancellationToken)
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

        if (!string.Equals(storedResult.Status, "failed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(storedResult.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Повторить можно только ошибочную или неподтверждённую выгрузку на Яндекс.Диск. Успешная отправка повторно не выполняется.");
        }

        var resolvedPath = ResolveBackupPath(storedResult.BackupRelativePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            throw new FileNotFoundException("Локальная резервная копия для повторной отправки не найдена.");
        }

        await EnsureBackupSetCatalogedAsync(storedResult.BackupRelativePath, cancellationToken);

        var fileInfo = new FileInfo(resolvedPath);
        var pendingResult = storedResult with
        {
            Status = "pending",
            TargetPath = string.IsNullOrWhiteSpace(storedResult.TargetPath) ? storedResult.BackupFileName : storedResult.TargetPath,
            SizeBytes = fileInfo.Length,
            Error = null,
            UpdatedAt = DateTime.UtcNow,
        };
        return await UploadAndRecordYandexAsync(pendingResult, resolvedPath, cancellationToken);
    }

    internal async Task<T> WithBackupDirectoryLeaseAsync<T>(Func<Task<T>> operation, CancellationToken token)
    {
        await _backupSemaphore.WaitAsync(token);
        try
        {
            await using var rootLock = await BackupDirectoryLease.AcquireAsync(_backupRootDirectory, RootLockTimeout, token);
            return await operation();
        }
        finally { _backupSemaphore.Release(); }
    }

    public async Task<DatabaseBackupDeleteResult> DeleteBackupAsync(string? relativePath, CancellationToken cancellationToken = default)
    {
        await _backupSemaphore.WaitAsync(cancellationToken);
        try
        {
            await using var rootLock = await BackupDirectoryLease.AcquireAsync(
                _backupRootDirectory, RootLockTimeout, cancellationToken);
            var resolvedPath = ResolveBackupPath(relativePath);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                throw new FileNotFoundException("Резервная копия не найдена.");
            }

            if (!TryDeleteFile(resolvedPath))
            {
                throw new IOException("Не удалось удалить резервную копию.");
            }
            DeleteManifestParts(resolvedPath);
            TryDeleteFile(GetManifestPath(resolvedPath));
            await MarkLocalBackupCopiesDeletedAsync([Path.GetRelativePath(_backupRootDirectory, resolvedPath).Replace('\\', '/')], cancellationToken);
            CleanupEmptyDirectories(cancellationToken);
            return new DatabaseBackupDeleteResult(1, DeletedAll: false);
        }
        finally
        {
            _backupSemaphore.Release();
        }
    }

    public async Task<DatabaseBackupDeleteResult> DeleteAllBackupsAsync(CancellationToken cancellationToken = default)
    {
        await _backupSemaphore.WaitAsync(cancellationToken);
        try
        {
            await using var rootLock = await BackupDirectoryLease.AcquireAsync(
                _backupRootDirectory, RootLockTimeout, cancellationToken);
            var deletedCount = 0;
            var deletedRelativePaths = new List<string>();
            foreach (var pattern in GetBackupSearchPatterns())
            {
                foreach (var filePath in EnumerateBackupFiles(pattern, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryDeleteFile(filePath))
                    {
                        DeleteManifestParts(filePath);
                        TryDeleteFile(GetManifestPath(filePath));
                        deletedRelativePaths.Add(Path.GetRelativePath(_backupRootDirectory, filePath).Replace('\\', '/'));
                        deletedCount++;
                    }
                }
            }

            await MarkLocalBackupCopiesDeletedAsync(deletedRelativePaths, cancellationToken);
            CleanupEmptyDirectories(cancellationToken);
            return new DatabaseBackupDeleteResult(deletedCount, DeletedAll: true);
        }
        finally
        {
            _backupSemaphore.Release();
        }
    }

    public async Task<int> EnsureScheduledBackupsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        await _backupSemaphore.WaitAsync(cancellationToken);
        try
        {
            await using var rootLock = await BackupDirectoryLease.AcquireAsync(
                _backupRootDirectory, RootLockTimeout, cancellationToken);
            await CleanupExpiredBackupsAsync(settings, cancellationToken);
        }
        finally
        {
            _backupSemaphore.Release();
        }

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
            if (await CreateBackupAsync("auto", slotKey, cancellationToken) is null)
            {
                continue;
            }
            createdCount++;
        }

        return createdCount;
    }

    public async Task<DatabaseBackupVerificationResult> VerifyBackupAsync(
        string? relativePath,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = ResolveBackupPath(relativePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            throw new FileNotFoundException("Резервная копия не найдена.");
        }

        var actualSize = new FileInfo(resolvedPath).Length;
        var manifestPath = GetManifestPath(resolvedPath);
        if (!File.Exists(manifestPath))
        {
            return new DatabaseBackupVerificationResult(
                "legacyUnverified",
                false,
                Path.GetRelativePath(_backupRootDirectory, resolvedPath).Replace('\\', '/'),
                null,
                actualSize,
                null,
                null,
                null,
                "Для legacy-копии отсутствует manifest с контрольной суммой.");
        }

        DatabaseBackupManifest? manifest;
        try
        {
            await using var manifestStream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<DatabaseBackupManifest>(manifestStream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new DatabaseBackupVerificationResult(
                "invalidManifest",
                false,
                Path.GetRelativePath(_backupRootDirectory, resolvedPath).Replace('\\', '/'),
                null,
                actualSize,
                null,
                null,
                null,
                ex.Message);
        }

        if (manifest is null || manifest.FormatVersion is not (1 or 2 or 3 or 4))
        {
            return new DatabaseBackupVerificationResult(
                "invalidManifest",
                false,
                Path.GetRelativePath(_backupRootDirectory, resolvedPath).Replace('\\', '/'),
                manifest?.BackupId,
                actualSize,
                manifest?.SizeBytes,
                null,
                manifest?.Sha256,
                "Manifest отсутствует или имеет неподдерживаемую версию.");
        }
        if (!string.Equals(manifest.Status, "complete", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseBackupVerificationResult(
                "invalidManifest",
                false,
                Path.GetRelativePath(_backupRootDirectory, resolvedPath).Replace('\\', '/'),
                manifest.BackupId,
                actualSize,
                manifest.SizeBytes,
                null,
                manifest.Sha256,
                "Резервная копия не помечена как завершённая.");
        }

        try
        {
            if (manifest.FormatVersion >= 2)
            {
                var databaseParts = manifest.Parts?.Where(part => part?.Kind == "database").ToArray() ?? [];
                var referenceParts = manifest.Parts?.Where(part => part?.Kind == "file-references").ToArray() ?? [];
                if (databaseParts.Length != 1 || referenceParts.Length != 1
                    || !databaseParts[0].Required || !referenceParts[0].Required
                    || databaseParts[0].RelativePath != manifest.BackupRelativePath
                    || referenceParts[0].RelativePath != manifest.BackupRelativePath + ".references")
                    throw new InvalidDataException("Новый комплект должен включать обязательные базу и перечень файловых ссылок.");
            }
            var nativeBindings = manifest.Parts?.Where(part => part?.Kind == "native-file-bindings").ToArray() ?? [];
            if (manifest.FormatVersion >= 4)
            {
                if (nativeBindings.Length != 1 || !nativeBindings[0].Required
                    || nativeBindings[0].RelativePath != manifest.BackupRelativePath + ".nativefiles")
                    throw new InvalidDataException("Новый комплект должен включать обязательное сопоставление архивных документов.");
            }
            else if (nativeBindings.Length != 0)
                throw new InvalidDataException("Сопоставление архивных документов требует нового формата комплекта.");
            foreach (var part in manifest.Parts ?? [])
            {
                if (part is null) throw new InvalidDataException("Manifest содержит пустую часть комплекта.");
                if (manifest.FormatVersion >= 3 && part.Kind == "file-storage"
                    && (!part.Required || part.FileEntryHashFormat != DatabaseBackupArchiveHash.Format))
                    throw new InvalidDataException("Новый файловый архив должен содержать обязательные хеши документов.");
                if (part.FileEntryHashFormat is not null
                    && (part.Kind != "file-storage" || part.FileEntryHashFormat != DatabaseBackupArchiveHash.Format))
                    throw new InvalidDataException("Некорректный формат хешей файловой части комплекта.");
                if (part.FileRoots is null) continue; // Historical manifests did not declare this layout.
                if (part.Kind != "file-storage" || part.FileRoots.Count == 0)
                    throw new InvalidDataException("Сопоставления файловых корней указаны для неподходящей части комплекта.");
                DatabaseBackupFileLayout.ValidateRootMappings(part.FileRoots);
            }
        }
        catch (InvalidDataException ex)
        {
            return new DatabaseBackupVerificationResult("invalidManifest", false,
                Path.GetRelativePath(_backupRootDirectory, resolvedPath).Replace('\\', '/'), manifest.BackupId,
                actualSize, manifest.SizeBytes, null, manifest.Sha256, ex.Message);
        }

        var actualSha256 = await ComputeSha256Async(resolvedPath, cancellationToken);
        var verified = actualSize == manifest.SizeBytes
            && string.Equals(actualSha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(resolvedPath), manifest.BackupFileName, StringComparison.Ordinal);
        string? partError = null;
        if (verified && manifest.Parts is { Count: > 0 })
        {
            foreach (var part in manifest.Parts.Where(item => item.Required))
            {
                var partPath = ResolveBackupPath(part.RelativePath);
                if (partPath is null)
                {
                    verified = false;
                    partError = $"Обязательная часть backup set отсутствует: {part.RelativePath}.";
                    break;
                }

                await using var partStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                var partHash = Convert.ToHexString(await SHA256.HashDataAsync(partStream, cancellationToken)).ToLowerInvariant();
                if (partStream.Length != part.SizeBytes || !string.Equals(partHash, part.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    verified = false;
                    partError = $"Обязательная часть backup set повреждена: {part.RelativePath}.";
                    break;
                }
                if (part.Kind == "file-references" && manifest.FormatVersion >= 2)
                {
                    try
                    {
                        partStream.Position = 0;
                        await foreach (var _ in DatabaseBackupInventoryReader.ReadAsync(partStream, cancellationToken)) { }
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException)
                    {
                        verified = false;
                        partError = ex.Message;
                        break;
                    }
                }
                if (part.Kind == "native-file-bindings")
                {
                    try
                    {
                        partStream.Position = 0;
                        var referenceHash = manifest.Parts!.Single(item => item.Kind == "file-references").Sha256;
                        await foreach (var _ in DatabaseBackupNativeBindings.ReadAsync(partStream, referenceHash, true, cancellationToken)) { }
                    }
                    catch (InvalidDataException ex)
                    {
                        verified = false;
                        partError = ex.Message;
                        break;
                    }
                }
                if (part.FileEntryHashFormat == DatabaseBackupArchiveHash.Format)
                {
                    try
                    {
                        // One bounded proof cache, keyed by freshly computed content SHA,
                        // never by filename, timestamp, or a caller's declared hash.
                        if (!string.Equals(Volatile.Read(ref _cachedVerifiedFilePartHash), partHash, StringComparison.Ordinal))
                        {
                            partStream.Position = 0;
                            using var archive = new ZipArchive(partStream, ZipArchiveMode.Read, leaveOpen: true);
                            await DatabaseBackupArchiveHash.ValidateEntriesAsync(archive, cancellationToken);
                            Volatile.Write(ref _cachedVerifiedFilePartHash, partHash);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException)
                    {
                        verified = false;
                        partError = ex.Message;
                        break;
                    }
                }
            }
        }
        if (verified && manifest.FormatVersion >= 4)
        {
            try { await VerifyNativeBindingCoverageAsync(manifest, cancellationToken); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                verified = false;
                partError = ex.Message;
            }
        }
        return new DatabaseBackupVerificationResult(
            verified ? "verified" : "mismatch",
            verified,
            Path.GetRelativePath(_backupRootDirectory, resolvedPath).Replace('\\', '/'),
            manifest.BackupId,
            actualSize,
            manifest.SizeBytes,
            actualSha256,
            manifest.Sha256,
            verified ? null : partError ?? "Размер, имя файла или контрольная сумма не совпадают с manifest.");
    }

    public async Task<DatabaseBackupManifest> ReadVerifiedManifestAsync(
        string? relativePath,
        CancellationToken cancellationToken = default)
    {
        var verification = await VerifyBackupAsync(relativePath, cancellationToken);
        if (!verification.Verified)
        {
            throw new InvalidDataException(verification.Error ?? "Резервная копия не прошла проверку manifest.");
        }

        var resolvedPath = ResolveBackupPath(relativePath)
            ?? throw new FileNotFoundException("Резервная копия не найдена.");
        await using var stream = File.OpenRead(GetManifestPath(resolvedPath));
        return await JsonSerializer.DeserializeAsync<DatabaseBackupManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Manifest резервной копии пуст.");
    }

    private async Task<bool> EnsureBackupSetCatalogedAsync(
        string relativePath, CancellationToken cancellationToken, bool dryRun = false)
    {
        var backupPath = ResolveBackupPath(relativePath)
            ?? throw new FileNotFoundException("Резервная копия не найдена.");
        EnsureDirectBackupPath(backupPath);
        if (File.Exists(GetManifestPath(backupPath))) EnsureDirectBackupPath(GetManifestPath(backupPath));
        var preview = TryReadManifest(backupPath);
        foreach (var part in preview?.Parts?.Where(item => item.Required) ?? [])
        {
            var partPath = ResolveBackupPath(part.RelativePath);
            if (partPath is not null) EnsureDirectBackupPath(partPath);
        }
        var manifest = await ReadVerifiedManifestAsync(relativePath, cancellationToken);
        var canonicalPath = Path.GetRelativePath(_backupRootDirectory, backupPath).Replace('\\', '/');
        if (!string.Equals(canonicalPath, manifest.BackupRelativePath?.Replace('\\', '/'), StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.BackupId) || manifest.BackupId.Length > 64
            || string.IsNullOrWhiteSpace(manifest.DatabaseProvider) || manifest.DatabaseProvider.Length > 64
            || string.IsNullOrWhiteSpace(manifest.BackupFormat) || manifest.BackupFormat.Length > 32)
            throw new InvalidDataException("Путь или идентификаторы в описании комплекта не соответствуют резервной копии.");
        var manifestSha256 = await ComputeSha256Async(GetManifestPath(backupPath), cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.DatabaseBackupSets.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == manifest.BackupId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.ManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Описание комплекта резервной копии изменилось после регистрации в каталоге.");
            return false;
        }

        if (dryRun) return true;

        var now = DateTime.UtcNow;
        var set = new DatabaseBackupSet
        {
            Id = manifest.BackupId,
            BackupRelativePath = canonicalPath,
            DatabaseProvider = manifest.DatabaseProvider,
            BackupFormat = manifest.BackupFormat,
            ManifestSha256 = manifestSha256,
            Status = DatabaseBackupSetStatuses.LocalVerified,
            CreatedAt = manifest.CreatedAtUtc,
            UpdatedAt = now,
        };
        set.Copies.Add(new DatabaseBackupCopy
        {
            ProviderKey = "local",
            ConnectionId = string.Empty,
            Locator = canonicalPath,
            Status = DatabaseBackupCopyStatuses.Verified,
            LastVerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        var settings = await LoadSettingsAsync(context, cancellationToken);
        set.Status = DatabaseBackupSetStateEvaluator.Evaluate(set.Copies, settings.S3Upload.Enabled,
            settings.S3Upload.ConnectionIds, settings.YandexDiskUpload.Enabled);
        context.DatabaseBackupSets.Add(set);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void EnsureDirectBackupPath(string path)
    {
        var root = Path.GetFullPath(_backupRootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = path;
        while (current is not null)
        {
            if (IsReparsePoint(current))
                throw new InvalidDataException("Комплект нельзя регистрировать через символическую ссылку или недоступный путь.");
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidDataException("Путь комплекта находится вне каталога резервных копий.");
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
        IReadOnlyList<DatabaseBackupYandexDiskUploadResult> YandexDiskUploadResults)?> CreateBackupAsync(
        string trigger,
        string? scheduleSlot,
        CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        await _backupSemaphore.WaitAsync(cancellationToken);

        string? attemptId = null;
        try
        {
            await using var rootLock = await BackupDirectoryLease.AcquireAsync(
                settings.RootDirectory, RootLockTimeout, cancellationToken);

            // The shared directory lease proves that an older running attempt for this root
            // is no longer active. A stale record must never count as a successful slot.
            await MarkInterruptedAttemptsAsync(settings.RootDirectory, cancellationToken);
            var nowLocal = DateTimeOffset.Now;
            if (scheduleSlot is not null && await HasVerifiedBackupForSlotAsync(
                settings.RootDirectory, nowLocal.Date, scheduleSlot, cancellationToken))
            {
                return null;
            }
            attemptId = await StartBackupAttemptAsync(settings.RootDirectory, trigger, scheduleSlot, cancellationToken);
            var targetDirectory = Path.Combine(
                settings.RootDirectory,
                nowLocal.ToString("yyyy", CultureInfo.InvariantCulture),
                nowLocal.ToString("MM", CultureInfo.InvariantCulture),
                nowLocal.ToString("dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(targetDirectory);

            await UpdateBackupAttemptAsync(attemptId, "database", cancellationToken);
            var snapshotParts = new List<DatabaseBackupPartManifest>();
            DatabaseBackupFileInfo fileInfo = _databaseProvider.IsSqlite
                ? await CreateSqliteBackupAsync(targetDirectory, settings.Format, nowLocal, trigger, scheduleSlot, snapshotParts, cancellationToken)
                : await CreatePostgreSqlBackupAsync(targetDirectory, settings.Format, settings.ToolCommand, nowLocal, trigger, scheduleSlot, snapshotParts, cancellationToken);

            IReadOnlyList<DatabaseBackupPartManifest> additionalParts = [];
            try
            {
                await UpdateBackupAttemptAsync(attemptId, "files", cancellationToken);
                additionalParts = await CreateFileStorageArchiveAsync(fileInfo, snapshotParts.Single(part => part.Kind == "file-references"), cancellationToken);
                await UpdateBackupAttemptAsync(attemptId, "verifying", cancellationToken);
                await WriteBackupManifestAsync(fileInfo, settings.Format.Key, [.. snapshotParts, .. additionalParts], cancellationToken);
            }
            catch
            {
                // A database dump without its required file part or manifest is not a restorable backup set.
                var incompletePath = ResolveBackupPath(fileInfo.RelativePath);
                if (incompletePath is not null)
                {
                    foreach (var part in additionalParts)
                    {
                        var ownedPartPath = ResolveBackupPath(part.RelativePath);
                        if (ownedPartPath is not null) TryDeleteFile(ownedPartPath);
                    }
                    TryDeleteFile($"{incompletePath}.references");
                    TryDeleteFile(GetManifestPath(incompletePath));
                    TryDeleteFile(incompletePath);
                }
                throw;
            }

            await EnsureBackupSetCatalogedAsync(fileInfo.RelativePath, cancellationToken);
            await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
            {
                var backupSetId = await context.DatabaseBackupSets.Where(item => item.BackupRelativePath == fileInfo.RelativePath.Replace('\\', '/'))
                    .Select(item => item.Id).SingleAsync(cancellationToken);
                await UpdateBackupAttemptAsync(attemptId, "uploading", cancellationToken, backupSetId: backupSetId);
            }

            var s3UploadResults = await UploadBackupToS3Async(fileInfo, settings.S3Upload, nowLocal, cancellationToken);
            var yandexDiskUploadResults = await UploadBackupToYandexDiskAsync(fileInfo, settings.YandexDiskUpload, nowLocal, cancellationToken);
            await UpdateBackupAttemptAsync(attemptId, "cleanup", cancellationToken);
            await CleanupExpiredBackupsAsync(settings, cancellationToken);
            var status = s3UploadResults.Any(item => item.Status != "success")
                || yandexDiskUploadResults.Any(item => item.Status != "success")
                || (settings.S3Upload.Enabled && settings.S3Upload.ConnectionIds.Count == 0)
                ? DatabaseBackupAttemptStatuses.Degraded : DatabaseBackupAttemptStatuses.Succeeded;
            await UpdateBackupAttemptAsync(attemptId, "completed", cancellationToken, status);
            return (fileInfo, s3UploadResults, yandexDiskUploadResults);
        }
        catch (Exception ex)
        {
            if (attemptId is not null)
            {
                // Preserve cancellation/original failure even if the database is unavailable.
                using var recordingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await UpdateBackupAttemptAsync(attemptId, null, recordingTimeout.Token,
                        ex is OperationCanceledException ? DatabaseBackupAttemptStatuses.Cancelled : DatabaseBackupAttemptStatuses.Failed,
                        error: ex is OperationCanceledException
                            ? "Запуск отменён. Готовность копии проверяйте отдельно в журнале комплектов."
                            : "Запуск завершился ошибкой. Подробности доступны в серверном журнале.");
                }
                catch (Exception recordingError)
                {
                    _logger.LogWarning(recordingError, "Could not record backup attempt {AttemptId} failure", attemptId);
                }
            }
            throw;
        }
        finally
        {
            _backupSemaphore.Release();
        }
    }

    private async Task MarkInterruptedAttemptsAsync(string root, CancellationToken cancellationToken)
    {
        var identity = GetBackupRootIdentity(root);
        var now = DateTime.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.DatabaseBackupAttempts.Where(item => item.RootIdentity == identity
                && item.Status == DatabaseBackupAttemptStatuses.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, DatabaseBackupAttemptStatuses.Interrupted)
                .SetProperty(item => item.CompletedAt, now)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.Error, "Предыдущий запуск прерван. Готовность копии не подтверждена."), cancellationToken);
    }

    private static string GetBackupRootIdentity(string root)
    {
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private async Task<string> StartBackupAttemptAsync(string root, string trigger, string? slot, CancellationToken cancellationToken)
    {
        var attempt = new DatabaseBackupAttempt { RootIdentity = GetBackupRootIdentity(root), Trigger = trigger, ScheduleSlot = slot };
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.DatabaseBackupAttempts.Add(attempt);
        await context.SaveChangesAsync(cancellationToken);
        return attempt.Id;
    }

    private async Task UpdateBackupAttemptAsync(string id, string? stage, CancellationToken cancellationToken,
        string? status = null, string? backupSetId = null, string? error = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var attempt = await context.DatabaseBackupAttempts.SingleAsync(item => item.Id == id, cancellationToken);
        if (stage is not null) attempt.Stage = stage;
        if (backupSetId is not null) attempt.BackupSetId = backupSetId;
        if (status is not null)
        {
            attempt.Status = status;
            attempt.CompletedAt = DateTime.UtcNow;
        }
        attempt.Error = error;
        attempt.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<DatabaseBackupFileInfo> CreateSqliteBackupAsync(
        string targetDirectory,
        DatabaseBackupFormatOption format,
        DateTimeOffset nowLocal,
        string trigger,
        string? scheduleSlot,
        List<DatabaseBackupPartManifest> snapshotParts,
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
        var published = false;
        DatabaseBackupPartManifest? referencePart = null;

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

            await using (var snapshot = await DatabaseBackupSnapshotSession.OpenSqliteCopyAsync(
                tempCopyPath, databasePath, cancellationToken))
            {
                referencePart = await WriteSnapshotInventoryAsync(snapshot, outputPath, cancellationToken);
                snapshotParts.Add(referencePart);
            }

            if (string.Equals(format.Key, "sqlite", StringComparison.OrdinalIgnoreCase))
            {
                File.Move(tempCopyPath, outputPath, overwrite: false);
                published = true;
                return BuildFileInfo(outputPath, _backupRootDirectory)
                    ?? throw new InvalidOperationException("SQLite backup was created but could not be indexed.");
            }

            var outputCreated = false;
            try
            {
                using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                outputCreated = true;
                using var archive = new ZipArchive(output, ZipArchiveMode.Create);
                archive.CreateEntryFromFile(tempCopyPath, $"{baseName}.db", CompressionLevel.Optimal);
            }
            catch
            {
                if (outputCreated) TryDeleteFile(outputPath);
                throw;
            }
            published = true;
        }
        finally
        {
            TryDeleteFile(tempCopyPath);
            if (!published && referencePart is not null) TryDeleteFile($"{outputPath}.references");
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
        List<DatabaseBackupPartManifest> snapshotParts,
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
        var partialPath = Path.Combine(targetDirectory, $"{Guid.NewGuid():N}.partial");
        var applicationName = $"payplanner-backup-{Guid.NewGuid():N}";
        await using var snapshot = await DatabaseBackupSnapshotSession.OpenPostgreSqlAsync(
            _databaseProvider.ConnectionString, cancellationToken);

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
        startInfo.Environment["PGAPPNAME"] = applicationName;

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
        startInfo.ArgumentList.Add("--snapshot");
        startInfo.ArgumentList.Add(snapshot.PostgreSqlSnapshotId!);
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(partialPath);

        if (!string.IsNullOrWhiteSpace(connectionBuilder.Password))
        {
            startInfo.Environment["PGPASSWORD"] = connectionBuilder.Password;
        }

        // Reserve an exclusively owned staging file. pg_dump may truncate this file,
        // but never a previously published backup, even after a clock rollback.
        using (new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        DatabaseBackupPartManifest referencePart;
        try
        {
            referencePart = await WriteSnapshotInventoryAsync(snapshot, outputPath, cancellationToken);
            snapshotParts.Add(referencePart);
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
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
        catch (Exception ex)
        {
            TryDeleteFile(partialPath);
            TryDeleteFile($"{outputPath}.references");
            if (ex is Win32Exception)
                throw new InvalidOperationException(
                    $"Не удалось запустить pg_dump по пути '{pgDumpExecutable}'. {ex.Message}", ex);
            throw;
        }

        using (process)
        {
            _logger.LogInformation("Started PostgreSQL backup process {ProcessId} ({ApplicationName}).", process.Id, applicationName);
            // Drain both pipes until the child exits, including cancellation cleanup.
            var stdErrTask = process.StandardError.ReadToEndAsync();
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                var stdErr = (await stdErrTask).Trim();
                _ = await stdOutTask;
                if (process.ExitCode != 0)
                {
                    var detail = string.IsNullOrWhiteSpace(stdErr)
                        ? $"pg_dump exited with code {process.ExitCode}."
                        : stdErr;
                    throw new InvalidOperationException(detail);
                }
            }
            catch
            {
                // Disposing Process alone does not stop pg_dump. Keep the root lease
                // until this owned child and its redirected streams have finished.
                try
                {
                    Exception? serverCancellationError = null;
                    var producerWasRunning = !process.HasExited;
                    if (producerWasRunning)
                    {
                        try
                        {
                            using var queryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await CancelOwnedPostgreSqlDumpAsync(applicationName, queryTimeout.Token);
                        }
                        catch (Exception serverError)
                        {
                            serverCancellationError = serverError;
                        }
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                    }
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(stopTimeout.Token);
                    await Task.WhenAll(stdErrTask, stdOutTask).WaitAsync(stopTimeout.Token);
                    if (serverCancellationError is not null)
                        throw new InvalidOperationException("Не удалось отменить серверный запрос своего дампа PostgreSQL.", serverCancellationError);
                    if (producerWasRunning)
                    {
                        using var confirmationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await ConfirmOwnedPostgreSqlDumpStoppedAsync(applicationName, confirmationTimeout.Token);
                    }
                }
                catch (Exception stopError)
                {
                    _logger.LogError(stopError, "Could not confirm PostgreSQL backup process {ProcessId} stopped.", process.Id);
                    throw new InvalidOperationException("Не удалось подтвердить остановку процесса резервирования PostgreSQL. Подробности доступны в серверном журнале.", stopError);
                }
                finally
                {
                    if (process.HasExited)
                    {
                        TryDeleteFile(partialPath);
                        TryDeleteFile($"{outputPath}.references");
                    }
                }
                throw;
            }
        }

        try
        {
            File.Move(partialPath, outputPath, overwrite: false);
        }
        catch
        {
            TryDeleteFile($"{outputPath}.references");
            throw;
        }
        finally
        {
            TryDeleteFile(partialPath);
        }

        return BuildFileInfo(outputPath, _backupRootDirectory)
            ?? throw new InvalidOperationException("PostgreSQL backup was created but could not be indexed.");
    }

    private async Task<DatabaseBackupPartManifest> WriteSnapshotInventoryAsync(
        DatabaseBackupSnapshotSession snapshot, string backupPath, CancellationToken cancellationToken)
    {
        var inventoryPath = $"{backupPath}.references";
        var temporaryPath = $"{inventoryPath}.{Guid.NewGuid():N}.tmp";
        var published = false;
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, true))
            {
                using var writer = new Utf8JsonWriter(output);
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", 1);
                writer.WriteStartArray("storedFiles");
                long storedCount = 0;
                await foreach (var reference in DatabaseBackupSnapshotInventory.ReadStoredFilesAsync(snapshot, cancellationToken))
                {
                    JsonSerializer.Serialize(writer, reference, JsonOptions);
                    if (++storedCount % 128 == 0) await writer.FlushAsync(cancellationToken);
                }
                writer.WriteEndArray();
                writer.WriteStartArray("storageReplicas");
                long replicaCount = 0;
                await foreach (var reference in DatabaseBackupSnapshotInventory.ReadStorageReplicasAsync(snapshot, cancellationToken))
                {
                    JsonSerializer.Serialize(writer, reference, JsonOptions);
                    if (++replicaCount % 128 == 0) await writer.FlushAsync(cancellationToken);
                }
                writer.WriteEndArray();
                writer.WriteNumber("storedFileCount", storedCount);
                writer.WriteNumber("storageReplicaCount", replicaCount);
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, inventoryPath, overwrite: false);
            published = true;
            return new DatabaseBackupPartManifest("file-references",
                Path.GetRelativePath(_backupRootDirectory, inventoryPath).Replace('\\', '/'),
                new FileInfo(inventoryPath).Length, await ComputeSha256Async(inventoryPath, cancellationToken), true);
        }
        catch
        {
            if (published) TryDeleteFile(inventoryPath);
            throw;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task CancelOwnedPostgreSqlDumpAsync(string applicationName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_databaseProvider.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT pg_cancel_backend(pid) FROM pg_stat_activity
            WHERE datname = current_database() AND usename = session_user
              AND application_name = @applicationName AND pid <> pg_backend_pid()
            """, connection);
        command.Parameters.AddWithValue("applicationName", applicationName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) { }
    }

    private async Task ConfirmOwnedPostgreSqlDumpStoppedAsync(string applicationName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_databaseProvider.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT COUNT(*) FROM pg_stat_activity
            WHERE datname = current_database() AND usename = session_user
              AND application_name = @applicationName
            """, connection);
        command.Parameters.AddWithValue("applicationName", applicationName);
        while (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0)
            await Task.Delay(100, cancellationToken);
    }

    private async Task<bool> HasVerifiedBackupForSlotAsync(
        string rootDirectory, DateTime localDate, string slotKey, CancellationToken cancellationToken)
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
        foreach (var filePath in Directory.EnumerateFiles(dayDirectory, pattern, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GetBackupSearchPatterns().Any(searchPattern => IsFileMatchingPattern(filePath, searchPattern)))
            {
                continue;
            }

            try
            {
                if (await IsVerifiedBackupCachedAsync(filePath, cancellationToken))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not verify scheduled database backup: {BackupPath}.", filePath);
            }
        }

        return false;
    }

    private async Task CleanupExpiredBackupsAsync(BackupSettings settings, CancellationToken cancellationToken)
    {
        var retentionDays = settings.RetentionDays;
        cancellationToken.ThrowIfCancellationRequested();
        if (retentionDays <= 0 || !Directory.Exists(_backupRootDirectory))
        {
            return;
        }

        var thresholdUtc = DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcDateTime;
        var backupFiles = GetBackupSearchPatterns()
            .SelectMany(pattern => EnumerateBackupFiles(pattern, cancellationToken))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var orderedBackupFiles = backupFiles
            .Select(filePath => new { FilePath = filePath, LastWriteTimeUtc = TryGetLastWriteTimeUtc(filePath) })
            .OrderByDescending(item => item.LastWriteTimeUtc)
            .ToList();
        var newestBackupPath = orderedBackupFiles.FirstOrDefault()?.FilePath;
        if (newestBackupPath is null)
        {
            return;
        }
        if (!orderedBackupFiles.Any(item => !string.Equals(item.FilePath, newestBackupPath, StringComparison.OrdinalIgnoreCase)
            && item.LastWriteTimeUtc.HasValue && item.LastWriteTimeUtc.Value <= thresholdUtc))
        {
            return;
        }

        string? newestVerifiedBackupPath = null;
        foreach (var filePath in orderedBackupFiles.Select(item => item.FilePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var relativePath = Path.GetRelativePath(_backupRootDirectory, filePath);
                if ((await VerifyBackupAsync(relativePath, cancellationToken)).Verified)
                {
                    newestVerifiedBackupPath = filePath;
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not verify database backup before retention cleanup: {BackupPath}.", filePath);
            }
        }

        if (newestVerifiedBackupPath is null)
        {
            _logger.LogWarning("Database backup retention skipped: no verified local backup is available.");
            return;
        }

        string? replicatedBackupPath = null;
        if (settings.S3Upload.Enabled || settings.YandexDiskUpload.Enabled)
        {
            replicatedBackupPath = await FindProtectedReplicatedBackupAsync(
                orderedBackupFiles.Select(item => item.FilePath), settings, cancellationToken);
            if (replicatedBackupPath is null)
            {
                _logger.LogWarning("Database backup retention skipped: no verified set covers all configured destinations.");
                return;
            }
        }

        var deletedRelativePaths = new List<string>();
        foreach (var filePath in backupFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(filePath, newestBackupPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(filePath, newestVerifiedBackupPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(filePath, replicatedBackupPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lastWriteTimeUtc = TryGetLastWriteTimeUtc(filePath);
            if (!lastWriteTimeUtc.HasValue || lastWriteTimeUtc.Value > thresholdUtc)
            {
                continue;
            }

            if (TryDeleteFile(filePath))
            {
                DeleteManifestParts(filePath);
                TryDeleteFile(GetManifestPath(filePath));
                deletedRelativePaths.Add(Path.GetRelativePath(_backupRootDirectory, filePath).Replace('\\', '/'));
            }
        }

        await MarkLocalBackupCopiesDeletedAsync(deletedRelativePaths, cancellationToken);
        CleanupEmptyDirectories(cancellationToken);
    }

    private async Task<string?> FindProtectedReplicatedBackupAsync(
        IEnumerable<string> orderedPaths, BackupSettings settings, CancellationToken cancellationToken)
    {
        // An enabled but unconfigured destination cannot constitute a successful replica.
        if (settings.S3Upload.Enabled && settings.S3Upload.ConnectionIds.Count == 0)
            return null;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var batch in orderedPaths.Chunk(400))
        {
            var relativePaths = batch.Select(path => Path.GetRelativePath(_backupRootDirectory, path).Replace('\\', '/')).ToArray();
            var sets = await context.DatabaseBackupSets.AsNoTracking().Include(item => item.Copies).AsSplitQuery()
                .Where(item => relativePaths.Contains(item.BackupRelativePath))
                .ToDictionaryAsync(item => item.BackupRelativePath, cancellationToken);
            foreach (var path in batch)
            {
                var relativePath = Path.GetRelativePath(_backupRootDirectory, path).Replace('\\', '/');
                if (!sets.TryGetValue(relativePath, out var set)
                    || !set.Copies.Any(copy => copy.ProviderKey == "local" && copy.Status == DatabaseBackupCopyStatuses.Verified)
                    || (settings.S3Upload.Enabled && settings.S3Upload.ConnectionIds.Any(connectionId =>
                        !set.Copies.Any(copy => copy.ProviderKey == "s3" && copy.ConnectionId == connectionId
                            && copy.Status == DatabaseBackupCopyStatuses.Verified)))
                    || (settings.YandexDiskUpload.Enabled && !set.Copies.Any(copy => copy.ProviderKey == "yandexDisk"
                        && copy.Status == DatabaseBackupCopyStatuses.Verified)))
                    continue;

                try
                {
                    // Historical cloud success belongs to the exact registered manifest, not a modified file.
                    var manifest = TryReadManifest(path);
                    if (manifest?.BackupId == set.Id
                        && string.Equals(await ComputeSha256Async(GetManifestPath(path), cancellationToken),
                            set.ManifestSha256, StringComparison.OrdinalIgnoreCase)
                        && (await VerifyBackupAsync(relativePath, cancellationToken)).Verified)
                        return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not verify replicated backup before retention: {BackupPath}.", path);
                }
            }
        }
        return null;
    }

    // The scheduler calls this under _backupSemaphore once per minute. Metadata
    // detects changes in the database file, manifest and required parts without
    // rehashing a large backup at every poll. Retention and restore never use
    // this cache: they always perform full checksum verification before acting.
    private async Task<bool> IsVerifiedBackupCachedAsync(string filePath, CancellationToken cancellationToken)
    {
        var stamp = BuildBackupVerificationStamp(filePath);
        if (stamp is not null
            && string.Equals(filePath, _cachedVerifiedBackupPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(stamp, _cachedVerifiedBackupStamp, StringComparison.Ordinal)
            && DateTime.UtcNow - _cachedVerifiedBackupAtUtc < TimeSpan.FromHours(1))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(_backupRootDirectory, filePath);
        var verified = (await VerifyBackupAsync(relativePath, cancellationToken)).Verified;
        if (verified && stamp is not null && string.Equals(stamp, BuildBackupVerificationStamp(filePath), StringComparison.Ordinal))
        {
            _cachedVerifiedBackupPath = filePath;
            _cachedVerifiedBackupStamp = stamp;
            _cachedVerifiedBackupAtUtc = DateTime.UtcNow;
        }
        else if (string.Equals(filePath, _cachedVerifiedBackupPath, StringComparison.OrdinalIgnoreCase))
        {
            _cachedVerifiedBackupPath = null;
            _cachedVerifiedBackupStamp = null;
            _cachedVerifiedBackupAtUtc = default;
        }

        return verified;
    }

    private string? BuildBackupVerificationStamp(string filePath)
    {
        var manifestPath = GetManifestPath(filePath);
        var manifest = TryReadManifest(filePath);
        if (manifest is null || !string.Equals(manifest.Status, "complete", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stamp = new StringBuilder();
        foreach (var path in new[] { filePath, manifestPath }
            .Concat(manifest.Parts?.Where(part => part.Required)
                .Select(part => ResolveBackupPath(part.RelativePath)) ?? []))
        {
            if (path is null) return null;
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            stamp.Append(path).Append('|').Append(info.Length).Append('|')
                .Append(info.LastWriteTimeUtc.Ticks).Append(';');
        }

        return stamp.ToString();
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

            result = await UploadAndRecordS3Async(result, resolvedPath, cancellationToken);
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
            EnsureCloudUploadSucceeded(upload.Success, result.TargetPath);
            var manifestPath = GetManifestPath(resolvedPath);
            if (!File.Exists(manifestPath))
            {
                throw new InvalidOperationException("Manifest резервной копии отсутствует.");
            }

            var manifest = TryReadManifest(resolvedPath)
                ?? throw new InvalidDataException("Manifest резервной копии не читается.");
            await VerifyS3RemotePartAsync(s3StorageService, result.ConnectionId, result.TargetPath,
                manifest.SizeBytes, manifest.Sha256, cancellationToken);
            foreach (var part in manifest.Parts?.Where(item => item.Kind != "database") ?? [])
            {
                var partPath = ResolveBackupPath(part.RelativePath)
                    ?? throw new FileNotFoundException($"Часть backup set не найдена: {part.RelativePath}.");
                await using var partStream = File.OpenRead(partPath);
                var remotePartPath = $"{result.TargetPath}.{Path.GetFileName(partPath)}";
                var partUpload = await s3StorageService.UploadAsync(
                    result.ConnectionId,
                    remotePartPath,
                    partStream,
                    "application/octet-stream",
                    overwrite: true,
                    cancellationToken);
                EnsureCloudUploadSucceeded(partUpload.Success, part.RelativePath);
                await VerifyS3RemotePartAsync(s3StorageService, result.ConnectionId, remotePartPath,
                    part.SizeBytes, part.Sha256, cancellationToken);
            }

            var manifestSha256 = await ComputeSha256Async(manifestPath, cancellationToken);
            var remoteManifestPath = $"{result.TargetPath}.manifest.json";
            await using (var manifestStream = File.OpenRead(manifestPath))
            {
                var manifestUpload = await s3StorageService.UploadAsync(
                    result.ConnectionId,
                    remoteManifestPath,
                    manifestStream,
                    "application/json",
                    overwrite: true,
                    cancellationToken);
                EnsureCloudUploadSucceeded(manifestUpload.Success, "manifest.json");
            }
            await VerifyS3RemotePartAsync(s3StorageService, result.ConnectionId, remoteManifestPath,
                new FileInfo(manifestPath).Length, manifestSha256, cancellationToken);

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

        result = await UploadAndRecordYandexAsync(result, resolvedPath, cancellationToken);
        return [result];
    }

    private Task<DatabaseBackupS3UploadResult> UploadAndRecordS3Async(
        DatabaseBackupS3UploadResult result, string path, CancellationToken token) => UploadAndRecordAsync(result,
        SaveS3UploadResultAsync, (pending, ct) => UploadBackupFileToS3Async(pending, path, ct),
        pending => pending with { Status = "failed", Error = "Отправка отменена или прервана. Облачная копия не подтверждена.", UpdatedAt = DateTime.UtcNow }, token);

    private Task<DatabaseBackupYandexDiskUploadResult> UploadAndRecordYandexAsync(
        DatabaseBackupYandexDiskUploadResult result, string path, CancellationToken token) => UploadAndRecordAsync(result,
        SaveYandexDiskUploadResultAsync, (pending, ct) => UploadBackupFileToYandexDiskAsync(pending, path, ct),
        pending => pending with { Status = "failed", Error = "Отправка отменена или прервана. Облачная копия не подтверждена.", UpdatedAt = DateTime.UtcNow }, token);

    private async Task<T> UploadAndRecordAsync<T>(T pending, Func<T, CancellationToken, Task> save,
        Func<T, CancellationToken, Task<T>> upload, Func<T, T> cancelled, CancellationToken token)
    {
        try
        {
            await save(pending, token);
            var result = await upload(pending, token);
            await save(result, token);
            return result;
        }
        catch (OperationCanceledException)
        {
            // The caller's cancelled token cannot persist the terminal state. Do not mask cancellation.
            using var persistenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await save(cancelled(pending), persistenceTimeout.Token); }
            catch (Exception failure) { _logger.LogWarning(failure, "Could not record interrupted backup upload."); }
            throw;
        }
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
            EnsureCloudUploadSucceeded(upload.Success, result.TargetPath);
            var manifestPath = GetManifestPath(resolvedPath);
            if (!File.Exists(manifestPath))
            {
                throw new InvalidOperationException("Manifest резервной копии отсутствует.");
            }

            var manifest = TryReadManifest(resolvedPath)
                ?? throw new InvalidDataException("Manifest резервной копии не читается.");
            await VerifyYandexRemotePartAsync(yandexDiskService, result.TargetPath,
                manifest.SizeBytes, manifest.Sha256, cancellationToken);
            foreach (var part in manifest.Parts?.Where(item => item.Kind != "database") ?? [])
            {
                var partPath = ResolveBackupPath(part.RelativePath)
                    ?? throw new FileNotFoundException($"Часть backup set не найдена: {part.RelativePath}.");
                await using var partStream = File.OpenRead(partPath);
                var remotePartPath = $"{result.TargetPath}.{Path.GetFileName(partPath)}";
                var partUpload = await yandexDiskService.UploadAsync(
                    remotePartPath,
                    partStream,
                    "application/octet-stream",
                    overwrite: true,
                    cancellationToken);
                EnsureCloudUploadSucceeded(partUpload.Success, part.RelativePath);
                await VerifyYandexRemotePartAsync(yandexDiskService, remotePartPath,
                    part.SizeBytes, part.Sha256, cancellationToken);
            }

            var manifestSha256 = await ComputeSha256Async(manifestPath, cancellationToken);
            var remoteManifestPath = $"{result.TargetPath}.manifest.json";
            await using (var manifestStream = File.OpenRead(manifestPath))
            {
                var manifestUpload = await yandexDiskService.UploadAsync(
                    remoteManifestPath,
                    manifestStream,
                    "application/json",
                    overwrite: true,
                    cancellationToken);
                EnsureCloudUploadSucceeded(manifestUpload.Success, "manifest.json");
            }
            await VerifyYandexRemotePartAsync(yandexDiskService, remoteManifestPath,
                new FileInfo(manifestPath).Length, manifestSha256, cancellationToken);

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

    private static void EnsureCloudUploadSucceeded(bool success, string partName)
    {
        if (!success)
            throw new IOException($"Облачное хранилище не подтвердило загрузку части резервной копии: {Path.GetFileName(partName)}.");
    }

    private static async Task VerifyS3RemotePartAsync(
        IS3StorageService storage, string connectionId, string path, long expectedSize, string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var downloaded = await storage.OpenReadAsync(connectionId, path, cancellationToken);
        await VerifyRemotePartAsync(downloaded.Content, expectedSize, expectedSha256,
            path, cancellationToken);
    }

    private static async Task VerifyYandexRemotePartAsync(
        IYandexDiskService storage, string path, long expectedSize, string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var downloaded = await storage.OpenReadAsync(path, cancellationToken);
        await VerifyRemotePartAsync(downloaded.Content, expectedSize, expectedSha256,
            path, cancellationToken);
    }

    private static async Task VerifyRemotePartAsync(
        Stream content, long expectedSize, string expectedSha256, string partPath,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long actualSize = 0;
        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
            {
                actualSize = checked(actualSize + read);
                if (actualSize > expectedSize)
                    throw new InvalidDataException($"Размер облачной части резервной копии не совпадает: {Path.GetFileName(partPath)}.");
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (actualSize != expectedSize)
            throw new InvalidDataException($"Размер облачной части резервной копии не совпадает: {Path.GetFileName(partPath)}.");
        var actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Контрольная сумма облачной части резервной копии не совпадает: {Path.GetFileName(partPath)}.");
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

        await UpsertBackupCopyAsync(context, result.BackupRelativePath, "s3", result.ConnectionId,
            result.TargetKey ?? result.TargetPath, result.Status, result.Error, now, cancellationToken);
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

        await UpsertBackupCopyAsync(context, result.BackupRelativePath, "yandexDisk", string.Empty,
            result.TargetPath, result.Status, result.Error, now, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertBackupCopyAsync(
        PaymentContext context, string backupRelativePath, string providerKey, string connectionId,
        string locator, string uploadStatus, string? error, DateTime now, CancellationToken cancellationToken)
    {
        var set = await context.DatabaseBackupSets.Include(item => item.Copies)
            .FirstOrDefaultAsync(item => item.BackupRelativePath == backupRelativePath, cancellationToken)
            ?? throw new InvalidOperationException("Комплект резервной копии отсутствует в каталоге.");
        var copy = set.Copies.FirstOrDefault(item => item.ProviderKey == providerKey && item.ConnectionId == connectionId);
        if (copy is null)
        {
            copy = new DatabaseBackupCopy
            {
                BackupSetId = set.Id,
                ProviderKey = providerKey,
                ConnectionId = connectionId,
                CreatedAt = now,
            };
            context.DatabaseBackupCopies.Add(copy);
        }

        if (string.Equals(uploadStatus, "pending", StringComparison.OrdinalIgnoreCase))
            copy.AttemptCount++;
        copy.Locator = locator;
        copy.Status = uploadStatus switch
        {
            "success" => DatabaseBackupCopyStatuses.Verified,
            "failed" => DatabaseBackupCopyStatuses.Failed,
            _ => DatabaseBackupCopyStatuses.Pending,
        };
        copy.LastError = error is { Length: > 2000 } ? error[..2000] : error;
        copy.LastVerifiedAt = copy.Status == DatabaseBackupCopyStatuses.Verified ? now : null;
        copy.UpdatedAt = now;
        var settings = await LoadSettingsAsync(context, cancellationToken);
        set.Status = DatabaseBackupSetStateEvaluator.Evaluate(set.Copies, settings.S3Upload.Enabled,
            settings.S3Upload.ConnectionIds, settings.YandexDiskUpload.Enabled);
        set.UpdatedAt = now;
    }

    private async Task MarkLocalBackupCopiesDeletedAsync(
        IReadOnlyCollection<string> backupRelativePaths, CancellationToken cancellationToken)
    {
        if (backupRelativePaths.Count == 0) return;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var batch in backupRelativePaths.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            var sets = await context.DatabaseBackupSets
                .Include(item => item.Copies)
                .Where(item => batch.Contains(item.BackupRelativePath))
                .ToListAsync(cancellationToken);
            foreach (var set in sets)
            {
                var local = set.Copies.FirstOrDefault(item => item.ProviderKey == "local");
                if (local is null) continue;
                local.Status = DatabaseBackupCopyStatuses.Deleted;
                local.UpdatedAt = now;
                set.Status = DatabaseBackupSetStatuses.NoLocalCopy;
                set.UpdatedAt = now;
            }
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
        }
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
        return await LoadSettingsAsync(context, cancellationToken);
    }

    private async Task<BackupSettings> LoadSettingsAsync(PaymentContext context, CancellationToken cancellationToken)
    {
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

    private async Task WriteBackupManifestAsync(
        DatabaseBackupFileInfo backup,
        string backupFormat,
        IReadOnlyList<DatabaseBackupPartManifest> additionalParts,
        CancellationToken cancellationToken)
    {
        var resolvedPath = ResolveBackupPath(backup.RelativePath)
            ?? throw new InvalidOperationException("Созданная резервная копия недоступна для формирования manifest.");
        var databasePart = new DatabaseBackupPartManifest(
            "database",
            backup.RelativePath.Replace('\\', '/'),
            backup.SizeBytes,
            await ComputeSha256Async(resolvedPath, cancellationToken),
            true);
        var manifest = new DatabaseBackupManifest(
            additionalParts.Any(part => part.Kind == "native-file-bindings") ? 4 : 3,
            Guid.NewGuid().ToString("N"),
            backup.FileName,
            backup.RelativePath.Replace('\\', '/'),
            _databaseProvider.Key,
            backupFormat,
            backup.SizeBytes,
            databasePart.Sha256,
            backup.Trigger,
            backup.CreatedAt.ToUniversalTime(),
            typeof(DatabaseBackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
            "complete",
            [databasePart, .. additionalParts],
            _configuration["DatabaseBackup:RecoveryKeyReference"]);
        var manifestPath = GetManifestPath(resolvedPath);
        var temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static string GetManifestPath(string backupPath)
        => $"{backupPath}.manifest.json";

    private async Task<IReadOnlyList<DatabaseBackupPartManifest>> CreateFileStorageArchiveAsync(
        DatabaseBackupFileInfo backup,
        DatabaseBackupPartManifest referencePart,
        CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("DatabaseBackup:IncludeFileStorage", true))
        {
            return [];
        }

        using var scope = _scopeFactory.CreateScope();
        var environment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var candidates = new DatabaseBackupFileRoot[]
        {
            new("file-storage", ApplicationFileStoragePaths.ResolveRoot(environment, _configuration),
                !string.IsNullOrWhiteSpace(_configuration[ApplicationFileStoragePaths.RootPathConfigurationKey])),
            new("profile-photos", UserPhotoStoragePaths.ResolveRoot(environment, _configuration),
                !string.IsNullOrWhiteSpace(_configuration[UserPhotoStoragePaths.StorageRootConfigurationKey])),
            new("electronic-signatures", ElectronicSignatureStoragePaths.ResolveRoot(environment, _configuration),
                !string.IsNullOrWhiteSpace(_configuration[ElectronicSignatureStoragePaths.StorageRootConfigurationKey])),
            new("local-objects", PayPlanner.Api.Services.Storage.LocalStoragePaths.ResolveRoot(environment, _configuration),
                !string.IsNullOrWhiteSpace(_configuration[PayPlanner.Api.Services.Storage.LocalStoragePaths.RootConfigurationKey])),
            new("profile-photos-legacy", UserPhotoStoragePaths.ResolveLegacyRoot(environment)),
            new("electronic-signatures-legacy", ElectronicSignatureStoragePaths.ResolveLegacyRoot(environment)),
        };
        var layout = DatabaseBackupFileLayout.Create(candidates, _backupRootDirectory);
        var backupPath = ResolveBackupPath(backup.RelativePath)
            ?? throw new InvalidOperationException("Созданная резервная копия недоступна для file backup set.");
        var archivePath = $"{backupPath}.fileset";
        var temporaryPath = $"{archivePath}.{Guid.NewGuid():N}.tmp";
        var bindingPath = $"{backupPath}.nativefiles";
        var bindingTemporaryPath = $"{bindingPath}.{Guid.NewGuid():N}.tmp";
        var planPath = $"{bindingPath}.{Guid.NewGuid():N}.plan";
        var published = false;
        var temporaryCreated = false;
        var bindingPublished = false;
        var bindingTemporaryCreated = false;
        var planCreated = false;
        try
        {
            // Freeze current/legacy selection before reading any file payload. The plan
            // stays open through sealing; its original hash is checked again afterwards.
            await using var plan = new FileStream(planPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
            planCreated = true;
            await WriteNativeFilePlanAsync(referencePart, layout, plan,
                Path.GetRelativePath(_backupRootDirectory, archivePath).Replace('\\', '/'), cancellationToken);
            await plan.FlushAsync(cancellationToken);
            plan.Position = 0;
            var planHash = Convert.ToHexString(await SHA256.HashDataAsync(plan, cancellationToken));
            plan.Position = 0;
            if (layout.ArchiveRoots.Count != 0)
            {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                temporaryCreated = true;
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
                foreach (var root in layout.ArchiveRoots)
                {
                    foreach (var filePath in EnumerateFilesSafely(root.Path, layout, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (layout.IsExcludedPhysicalPath(filePath)) continue;
                        var entry = archive.CreateEntry(layout.GetArchiveEntryPath(filePath, root.Name), CompressionLevel.Fastest);
                        await using var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
                        DatabaseBackupArchiveFileHash hash;
                        await using (var destination = entry.Open())
                            hash = await DatabaseBackupArchiveHash.CopyAsync(source, destination, cancellationToken);
                        entry.Comment = DatabaseBackupArchiveHash.ToComment(hash.Sha256);
                    }
                }
            }
            }
            long bindingSize;
            string bindingHash;
            await using (var output = new FileStream(bindingTemporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                bindingTemporaryCreated = true;
                if (temporaryCreated)
                {
                    await using var archiveStream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
                    await DatabaseBackupNativeBindings.SealAsync(plan, output, archive, referencePart.Sha256, cancellationToken);
                }
                else await DatabaseBackupNativeBindings.SealAsync(plan, output, null, referencePart.Sha256, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Position = 0;
                bindingSize = output.Length;
                bindingHash = Convert.ToHexString(await SHA256.HashDataAsync(output, cancellationToken));
            }
            plan.Position = 0;
            if (planHash != Convert.ToHexString(await SHA256.HashDataAsync(plan, cancellationToken)))
                throw new InvalidDataException("План сопоставления документов изменился во время упаковки.");
            var result = new List<DatabaseBackupPartManifest>();
            if (temporaryCreated)
            {
                File.Move(temporaryPath, archivePath, overwrite: false);
                published = true;
                var info = new FileInfo(archivePath);
                result.Add(new DatabaseBackupPartManifest(
                    "file-storage",
                    Path.GetRelativePath(_backupRootDirectory, archivePath).Replace('\\', '/'),
                    info.Length,
                    await ComputeSha256Async(archivePath, cancellationToken),
                    true, layout.RootMappings, DatabaseBackupArchiveHash.Format));
            }
            File.Move(bindingTemporaryPath, bindingPath, overwrite: false);
            bindingPublished = true;
            result.Add(new("native-file-bindings", Path.GetRelativePath(_backupRootDirectory, bindingPath).Replace('\\', '/'),
                bindingSize, bindingHash, true));
            return result;
        }
        catch
        {
            if (published) TryDeleteFile(archivePath);
            if (bindingPublished) TryDeleteFile(bindingPath);
            throw;
        }
        finally
        {
            if (temporaryCreated) TryDeleteFile(temporaryPath);
            if (bindingTemporaryCreated) TryDeleteFile(bindingTemporaryPath);
            if (planCreated) TryDeleteFile(planPath);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task WriteNativeFilePlanAsync(DatabaseBackupPartManifest referencePart,
        DatabaseBackupFileLayout layout, Stream destination, string archivePartRelativePath, CancellationToken cancellationToken)
    {
        var path = ResolveBackupPath(referencePart.RelativePath)
            ?? throw new FileNotFoundException("Перечень документов резервной копии недоступен.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await VerifyInventoryStreamAsync();
        stream.Position = 0;
        await DatabaseBackupNativeBindings.WritePlanAsync(stream, destination, new(layout), archivePartRelativePath, referencePart.Sha256, cancellationToken);
        await VerifyInventoryStreamAsync();

        async Task VerifyInventoryStreamAsync()
        {
            stream.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (stream.Length != referencePart.SizeBytes || !string.Equals(hash, referencePart.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Перечень документов изменился после чтения снимка базы.");
        }
    }

    private async Task VerifyNativeBindingCoverageAsync(DatabaseBackupManifest manifest, CancellationToken cancellationToken)
    {
        var references = manifest.Parts!.Single(part => part.Kind == "file-references");
        var mapping = manifest.Parts!.Single(part => part.Kind == "native-file-bindings");
        var archiveParts = new Dictionary<string, DatabaseBackupPartManifest>(StringComparer.Ordinal);
        foreach (var part in manifest.Parts!.Where(part => part.Kind == "file-storage"))
            if (!archiveParts.TryAdd(part.RelativePath, part)) throw new InvalidDataException("Дублируется файловая часть комплекта.");
        await using var inventoryStream = OpenPart(references);
        await using var bindingStream = OpenPart(mapping);
        await VerifyPartStreamAsync(inventoryStream, references, cancellationToken);
        await VerifyPartStreamAsync(bindingStream, mapping, cancellationToken);
        inventoryStream.Position = 0;
        bindingStream.Position = 0;
        FileStream? archiveStream = null;
        ZipArchive? archive = null;
        DatabaseBackupPartManifest? currentPart = null;
        try
        {
            await DatabaseBackupNativeBindings.VerifyAsync(inventoryStream, bindingStream, references.Sha256, ReadArchivedFileAsync, cancellationToken);
            if (archiveStream is not null) await VerifyPartStreamAsync(archiveStream, currentPart!, cancellationToken);
            await VerifyPartStreamAsync(inventoryStream, references, cancellationToken);
            await VerifyPartStreamAsync(bindingStream, mapping, cancellationToken);
        }
        finally
        {
            archive?.Dispose();
            if (archiveStream is not null) await archiveStream.DisposeAsync();
        }

        async Task<DatabaseBackupArchiveFileHash?> ReadArchivedFileAsync(string partPath, string entryPath, CancellationToken token)
        {
            if (!archiveParts.TryGetValue(partPath, out var part)) return null;
            // Keep one open archive. The current producer has one part; this also
            // bounds handles when split archives are introduced later.
            if (currentPart != part)
            {
                if (archiveStream is not null)
                {
                    await VerifyPartStreamAsync(archiveStream, currentPart!, token);
                    archive?.Dispose();
                    await archiveStream.DisposeAsync();
                }
                archiveStream = OpenPart(part);
                currentPart = part;
                await VerifyPartStreamAsync(archiveStream, part, token);
                archiveStream.Position = 0;
                archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            }
            var entry = archive!.GetEntry(entryPath);
            return entry is null || string.IsNullOrEmpty(entry.Name)
                ? null : new(entry.Length, DatabaseBackupArchiveHash.FromComment(entry.Comment));
        }

        FileStream OpenPart(DatabaseBackupPartManifest part)
            => new(ResolveBackupPath(part.RelativePath) ?? throw new FileNotFoundException("Обязательная часть комплекта недоступна."),
                FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    private static async Task VerifyPartStreamAsync(FileStream stream, DatabaseBackupPartManifest part, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (stream.Length != part.SizeBytes || !string.Equals(hash, part.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Обязательная часть комплекта изменилась после предварительной проверки.");
    }

    private static bool IsPathWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullPath, fullRoot, comparison)
               || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private IEnumerable<string> EnumerateFilesSafely(string root, DatabaseBackupFileLayout layout, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (layout.IsExcludedPhysicalPath(directory)) continue;
            IReadOnlyList<string> files;
            try
            {
                files = _directoryReader.GetFiles(directory, "*");
                foreach (var child in _directoryReader.GetDirectories(directory))
                {
                    if (!layout.IsExcludedPhysicalPath(child) && !IsReparsePoint(child)) pending.Push(child);
                }
            }
            catch (Exception ex) when (IsRecoverableFileSystemEnumerationError(ex))
            {
                throw new IOException($"Не удалось полностью прочитать каталог файлов для резервной копии: {directory}.", ex);
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(file))
                    throw new IOException("Файловая ссылка не может быть включена в резервный архив вместо исходных байтов.");
                yield return file;
            }
        }
    }

    private DatabaseBackupManifest? TryReadManifest(string backupPath)
    {
        try
        {
            var manifestPath = GetManifestPath(backupPath);
            return File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<DatabaseBackupManifest>(File.ReadAllText(manifestPath), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read backup manifest for cleanup: {BackupPath}", backupPath);
            return null;
        }
    }

    private void DeleteManifestParts(string backupPath)
    {
        var manifest = TryReadManifest(backupPath);
        if (manifest?.Parts is null) return;
        var mainRelative = Path.GetRelativePath(_backupRootDirectory, backupPath).Replace('\\', '/');
        foreach (var part in manifest.Parts.Where(item => !string.Equals(item.RelativePath, mainRelative, StringComparison.OrdinalIgnoreCase)))
        {
            var partPath = ResolveBackupPath(part.RelativePath);
            if (partPath is not null) TryDeleteFile(partPath);
        }
    }

    private IReadOnlyList<string> EnumerateBackupFiles(
        string searchPattern,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_backupRootDirectory))
        {
            return [];
        }

        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(_backupRootDirectory);

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();

            try
            {
                files.AddRange(_directoryReader.GetFiles(directory, searchPattern));
            }
            catch (Exception ex) when (IsRecoverableFileSystemEnumerationError(ex))
            {
                _logger.LogWarning(ex, "Skipping inaccessible database backup directory {Directory}.", directory);
                continue;
            }

            try
            {
                foreach (var childDirectory in _directoryReader.GetDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsReparsePoint(childDirectory))
                    {
                        directories.Push(childDirectory);
                    }
                }
            }
            catch (Exception ex) when (IsRecoverableFileSystemEnumerationError(ex))
            {
                _logger.LogWarning(ex, "Skipping inaccessible database backup subdirectories under {Directory}.", directory);
            }
        }

        return files;
    }

    private IReadOnlyList<string> EnumerateBackupDirectories(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_backupRootDirectory))
        {
            return [];
        }

        var discovered = new List<string>();
        var pending = new Stack<string>();
        pending.Push(_backupRootDirectory);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var childDirectory in _directoryReader.GetDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReparsePoint(childDirectory))
                    {
                        continue;
                    }

                    discovered.Add(childDirectory);
                    pending.Push(childDirectory);
                }
            }
            catch (Exception ex) when (IsRecoverableFileSystemEnumerationError(ex))
            {
                _logger.LogWarning(ex, "Skipping inaccessible database backup subdirectories under {Directory}.", directory);
            }
        }

        return discovered;
    }

    private void CleanupEmptyDirectories(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_backupRootDirectory))
        {
            return;
        }

        foreach (var directory in EnumerateBackupDirectories(cancellationToken)
                     .OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_directoryReader.IsEmpty(directory))
                {
                    Directory.Delete(directory, false);
                }
            }
            catch (Exception ex) when (IsRecoverableFileSystemEnumerationError(ex))
            {
                _logger.LogWarning(ex, "Could not inspect or remove database backup directory {Directory}.", directory);
            }
        }
    }

    private static bool IsRecoverableFileSystemEnumerationError(Exception exception)
        => exception is UnauthorizedAccessException or IOException;

    private static bool IsReparsePoint(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (IsRecoverableFileSystemEnumerationError(ex))
        {
            return true;
        }
    }

    private static DateTime? TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (IsRecoverableFileSystemEnumerationError(ex))
        {
            return null;
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
        return $"{baseName}_{_databaseProvider.Key}_{nowLocal:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}_{trigger}{scheduleSuffix}{extension}";
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

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
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
