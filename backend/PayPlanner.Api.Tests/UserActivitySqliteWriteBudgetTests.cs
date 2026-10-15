using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.UserActivity;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class UserActivitySqliteWriteBudgetTests
{
    [Fact]
    public async Task BusyWriterCannotKeepCancelledAuditBlockedUntilNativeDefaultTimeout()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var blocker = new SqliteConnection(fixture.SlowConnectionString);
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction(deferred: false);
        using var budget = new CancellationTokenSource();
        var pending = Task.Run(() => fixture.Slow.TryWriteAsync(Entry("cancelled"), budget.Token));
        UserActivityLog? result = null;
        var returnedBeforeWriterRelease = false;
        try
        {
            await fixture.SlowFactory.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            budget.CancelAfter(TimeSpan.FromMilliseconds(500));
            try
            {
                result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
                returnedBeforeWriterRelease = true;
            }
            catch (TimeoutException) { }
        }
        finally
        {
            await transaction.RollbackAsync();
            result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(returnedBeforeWriterRelease,
            "A cancelled audit remained inside the native SQLite busy wait until the other writer released its transaction.");
        Assert.Null(result);
        Assert.Equal(30, fixture.SlowFactory.CreatedConnectionDefaultTimeout);
        await using var proof = fixture.SlowFactory.CreateDbContext();
        Assert.Empty(await proof.UserActivityLogs.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyWriterReleasesAuditSerializationAndRetriesWithoutLosingOrDuplicatingItsRecord(bool sharedCache)
    {
        await using var fixture = await Fixture.CreateAsync(sharedCache);
        await using var blocker = new SqliteConnection(fixture.SlowConnectionString);
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction(deferred: false);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = Task.Run(() => fixture.Slow.TryWriteAsync(Entry("retry"), budget.Token));
        UserActivityLog? completed;
        try
        {
            await fixture.SlowFactory.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.SlowLogger.FirstBusyRetry.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var fastBudget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var independent = await fixture.Fast.TryWriteAsync(Entry("independent"), fastBudget.Token);
            Assert.NotNull(independent);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            await transaction.RollbackAsync();
            completed = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.NotNull(completed);
        Assert.Equal(30, fixture.SlowFactory.CreatedConnectionDefaultTimeout);
        await using var slowProof = fixture.SlowFactory.CreateDbContext();
        var rows = await slowProof.UserActivityLogs.ToArrayAsync();
        var row = Assert.Single(rows);
        Assert.Equal(completed.Id, row.Id);
        Assert.Equal("retry", row.Action);
        await using var fastProof = fixture.FastFactory.CreateDbContext();
        Assert.Single(await fastProof.UserActivityLogs.Where(item => item.Category == "budget").ToArrayAsync());
        Assert.Equal(0, fixture.SlowLogger.Errors);
        Assert.Equal(0, fixture.FastLogger.Errors);
    }

    private static ActivityLogEntry Entry(string action) => new() { Category = "budget", Action = action };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _parent;
        public string SlowConnectionString { get; }
        public Factory SlowFactory { get; }
        public Factory FastFactory { get; }
        public AuditLogger SlowLogger { get; } = new();
        public AuditLogger FastLogger { get; } = new();
        public UserActivityService Slow { get; }
        public UserActivityService Fast { get; }

        private Fixture(string parent, string root, bool sharedCache)
        {
            _parent = parent;
            _root = root;
            SlowConnectionString = ConnectionString(Path.Combine(root, "slow.db"), sharedCache);
            SlowFactory = new Factory(SlowConnectionString, SlowLogger);
            FastFactory = new Factory(ConnectionString(Path.Combine(root, "fast.db")), FastLogger);
            Slow = new UserActivityService(SlowFactory, new HttpContextAccessor(), SlowLogger);
            Fast = new UserActivityService(FastFactory, new HttpContextAccessor(), FastLogger);
        }

        public static async Task<Fixture> CreateAsync(bool sharedCache = false)
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var root = Path.GetFullPath(Path.Combine(parent, "pp_activity_sqlite_budget_" + Guid.NewGuid().ToString("N")));
            Assert.StartsWith(parent, root, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(root);
            var fixture = new Fixture(parent, root, sharedCache);
            try
            {
                foreach (var connectionString in new[] { fixture.SlowConnectionString, fixture.FastFactory.ConnectionString })
                {
                    await using var destination = new SqliteConnection(connectionString);
                    await destination.OpenAsync();
                    await using var template = await SqliteTestContextFactory.CreateAsync();
                    ((SqliteConnection)template.Database.GetDbConnection()).BackupDatabase(destination);
                    await using var wal = destination.CreateCommand();
                    wal.CommandText = "PRAGMA journal_mode=WAL;";
                    Assert.Equal("wal", Convert.ToString(await wal.ExecuteScalarAsync()));
                }
                await fixture.Fast.UpdateSettingsAsync(true, 30);
                Assert.NotNull(await fixture.Fast.TryWriteAsync(new ActivityLogEntry { Category = "warmup", Action = "warmup" }));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public ValueTask DisposeAsync()
        {
            var absolute = Path.GetFullPath(_root);
            Assert.StartsWith(_parent, absolute, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("pp_activity_sqlite_budget_", Path.GetFileName(absolute));
            SlowFactory.Dispose();
            FastFactory.Dispose();
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static string ConnectionString(string path, bool sharedCache = false) => new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, Cache = sharedCache ? SqliteCacheMode.Shared : SqliteCacheMode.Default }.ToString();
    }

    private sealed class Factory(string connectionString, AuditLogger logger) : IDbContextFactory<PaymentContext>, IDisposable
    {
        private readonly ILoggerFactory _logging = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Error).AddProvider(new ErrorProvider(logger)));
        private SqliteConnection? _lastConnection;
        public string ConnectionString => connectionString;
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int? CreatedConnectionDefaultTimeout => _lastConnection?.DefaultTimeout;
        public PaymentContext CreateDbContext()
        {
            var connection = new SqliteConnection(connectionString);
            _lastConnection = connection;
            return new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection, contextOwnsConnection: true)
                .UseLoggerFactory(_logging)
                .AddInterceptors(new OpenObserver(Opened)).Options);
        }
        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
        public void Dispose() => _logging.Dispose();
    }

    private sealed class ErrorProvider(AuditLogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    private sealed class OpenObserver(TaskCompletionSource opened) : DbConnectionInterceptor
    {
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        { opened.TrySetResult(); return Task.CompletedTask; }
    }

    private sealed class AuditLogger : ILogger<UserActivityService>
    {
        private int _errors;
        public int Errors => Volatile.Read(ref _errors);
        public TaskCompletionSource FirstBusyRetry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Interlocked.Increment(ref _errors);
            if (logLevel == LogLevel.Warning && exception is SqliteException { SqliteErrorCode: 5 or 6 }) FirstBusyRetry.TrySetResult();
        }
    }
}
