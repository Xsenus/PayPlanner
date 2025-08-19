using Microsoft.Data.Sqlite;
using System.IO.Compression;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Периодическое резервное копирование SQLite базы данных в ZIP-архивы.
    /// </summary>
    public class DatabaseBackupService : BackgroundService
    {
        private readonly string _backupDir;
        private readonly string _dbPath;
        private readonly TimeSpan _interval;
        private readonly ILogger<DatabaseBackupService> _logger;
        private readonly int _maxFiles;

        public DatabaseBackupService(ILogger<DatabaseBackupService> logger, IConfiguration cfg)
        {
            _logger = logger;
            _interval = TimeSpan.FromHours(cfg.GetValue<double?>("Backup:IntervalHours") ?? 6);
            _maxFiles = cfg.GetValue<int?>("Backup:MaxFiles") ?? 50;

            var dirFromCfg = cfg.GetValue<string>("Backup:Directory");
            var baseDir = AppContext.BaseDirectory;
            _backupDir = string.IsNullOrWhiteSpace(dirFromCfg)
                ? Path.Combine(baseDir, "backups")
                : (Path.IsPathRooted(dirFromCfg) ? dirFromCfg : Path.Combine(baseDir, dirFromCfg));

            var cs = cfg.GetSection("ConnectionStrings")["Default"] ?? "Data Source=payplanner.db";
            var dataSource = new SqliteConnectionStringBuilder(cs).DataSource;

            _dbPath = Path.IsPathRooted(dataSource)
                ? dataSource
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dataSource));

            Directory.CreateDirectory(_backupDir);
        }

        /// <summary>
        /// Удаляет старые ZIP сверх лимита _maxFiles (самые старые уходят первыми).
        /// </summary>
        private void CleanupOldBackups()
        {
            try
            {
                var files = Directory.GetFiles(_backupDir, "payplanner_*.zip")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .ToList();

                if (files.Count <= _maxFiles) return;

                foreach (var f in files.Skip(_maxFiles))
                {
                    _logger.LogInformation("Removing old backup {File}", f.FullName);
                    f.Delete();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CleanupOldBackups failed");
            }
        }

        /// <summary>
        /// Делает консистентную копию БД в отдельный .db-файл через VACUUM INTO, затем упаковывает в ZIP.
        /// </summary>
        private async Task CreateBackupAsync(CancellationToken ct)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var tempDbCopyPath = Path.Combine(_backupDir, $"payplanner_{timestamp}.db");
            var zipPath = Path.Combine(_backupDir, $"payplanner_{timestamp}.zip");

            _logger.LogInformation("Starting SQLite VACUUM INTO -> {TempDb}", tempDbCopyPath);

            await using (var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
            {
                await conn.OpenAsync(ct);

                using (var v = conn.CreateCommand())
                {
                    v.CommandText = "select sqlite_version()";
                    var ver = (string?)await v.ExecuteScalarAsync(ct);
                    _logger.LogInformation("SQLite version: {ver}", ver);
                }

                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=5000";
                    await pragma.ExecuteNonQueryAsync(ct);
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "VACUUM INTO $path";
                    cmd.Parameters.AddWithValue("$path", tempDbCopyPath);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            _logger.LogInformation("VACUUM INTO done, creating ZIP -> {Zip}", zipPath);

            try
            {
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(tempDbCopyPath, "payplanner.db", CompressionLevel.Optimal);
            }
            finally
            {
                if (File.Exists(tempDbCopyPath))
                    File.Delete(tempDbCopyPath);
            }

            _logger.LogInformation("Backup created: {Zip}", zipPath);
        }

        /// <summary>
        /// Основной цикл: первая задержка 1 минута, затем бэкап по расписанию, с ротацией архивов.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DatabaseBackupService started. Interval: {Interval}, dir: {Dir}", _interval, _backupDir);

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CreateBackupAsync(stoppingToken);
                    CleanupOldBackups();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backup failed");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (TaskCanceledException) { }
            }

            _logger.LogInformation("DatabaseBackupService stopped.");
        }
    }
}
