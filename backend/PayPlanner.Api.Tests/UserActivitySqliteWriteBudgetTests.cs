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
    public async Task SchemaLockedConnectionOpeningCannotHoldCancelledAuditUntilDefaultNativeTimeout()
    {
        await using var fixture = await Fixture.CreateAsync(sharedCache: true);
        await using var blocker = new SqliteConnection(fixture.SlowConnectionString);
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction(deferred: false);
        await using (var schema = blocker.CreateCommand())
        {
            schema.CommandText = "CREATE TABLE OwnedAuditOpeningSchemaLock (Id INTEGER PRIMARY KEY);";
            await schema.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var pending = Task.Run(() => fixture.Slow.TryWriteAsync(Entry("cancelled-open"), cancellation.Token));
        UserActivityLog? result;
        var returnedBeforeSchemaRelease = false;
        try
        {
            await fixture.SlowFactory.Opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(2));
                returnedBeforeSchemaRelease = true;
            }
            catch (TimeoutException) { }
        }
        finally
        {
            await transaction.RollbackAsync();
            result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(returnedBeforeSchemaRelease,
            "SQLite connection opening kept a cancelled audit inside the native default timeout before applying the short attempt timeout.");
        Assert.Null(result);
        Assert.Equal(30, fixture.SlowFactory.CreatedConnectionDefaultTimeout);
        Assert.Equal(fixture.SlowConnectionString, fixture.SlowFactory.CreatedConnectionString);
        await using var proof = fixture.SlowFactory.CreateDbContext();
        Assert.Empty(await proof.UserActivityLogs.ToArrayAsync());
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.NotNull(await fixture.Slow.TryWriteAsync(Entry("after-open-failure"), recovery.Token));
        Assert.Equal("after-open-failure", Assert.Single(await proof.UserActivityLogs.AsNoTracking().ToArrayAsync()).Action);
    }

    [Fact]
    public async Task ConcurrentServicesSharingSqliteFactoryQueueBeforeNativeWriterAdmission()
    {
        await using var fixture = await Fixture.CreateAsync(sharedCache: true);
        var observer = new WriterAdmissionObserver(expectedContexts: 9);
        fixture.SlowFactory.WriterAdmission = observer;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writes = new List<Task<UserActivityLog?>>
        {
            Task.Run(() => fixture.Slow.TryWriteAsync(Entry("first"), budget.Token))
        };
        var simultaneousAdmissions = false;
        UserActivityLog? independent = null;
        UserActivityLog?[] results;
        try
        {
            await observer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var index = 0; index < 8; index++)
            {
                var service = new UserActivityService(fixture.SlowFactory, new HttpContextAccessor(), fixture.SlowLogger);
                var action = $"queued-{index}";
                writes.Add(Task.Run(() => service.TryWriteAsync(Entry(action), budget.Token)));
            }
            await observer.AllContextsCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            simultaneousAdmissions = await Task.WhenAny(observer.SecondAdmission.Task, Task.Delay(200))
                == observer.SecondAdmission.Task;
            using var independentBudget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            independent = await fixture.Fast.TryWriteAsync(Entry("other-database"), independentBudget.Token);
        }
        finally
        {
            observer.ReleaseFirst.TrySetResult();
            results = await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(8));
        }

        Assert.False(simultaneousAdmissions,
            "Services sharing one SQLite factory entered concurrent synchronous writer admission instead of waiting asynchronously.");
        Assert.NotNull(independent);
        Assert.All(results, Assert.NotNull);
        Assert.Equal(0, fixture.SlowLogger.Errors);
        Assert.Equal(0, fixture.FastLogger.Errors);
        fixture.SlowFactory.WriterAdmission = null;
        await using var proof = fixture.SlowFactory.CreateDbContext();
        var actions = await proof.UserActivityLogs.Select(item => item.Action).ToArrayAsync();
        Assert.Equal(9, actions.Length);
        Assert.Equal(9, actions.Distinct().Count());
    }

    [Theory]
    [InlineData(false, "write-admission")]
    [InlineData(true, "commit")]
    public async Task CancellingQueuedAuditDoesNotEnterNativeAdmissionAndAllowsTheNextWrite(bool holdAtCommit, string expectedStage)
    {
        await using var fixture = await Fixture.CreateAsync(sharedCache: true);
        var observer = new WriterAdmissionObserver(expectedContexts: 2, holdAtCommit);
        fixture.SlowFactory.WriterAdmission = observer;
        var first = Task.Run(() => fixture.Slow.TryWriteAsync(Entry("held")));
        using var cancellation = new CancellationTokenSource();
        UserActivityLog? cancelled = null;
        UserActivityLog? completed;
        try
        {
            await observer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var pending = Task.Run(() => fixture.Slow.TryWriteAsync(Entry("cancelled-queued"), cancellation.Token));
            await observer.AllContextsCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(observer.SecondAdmission.Task.IsCompleted);
        }
        finally
        {
            observer.ReleaseFirst.TrySetResult();
            completed = await first.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.Null(cancelled);
        Assert.NotNull(completed);
        fixture.SlowFactory.WriterAdmission = null;
        using var nextBudget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.NotNull(await fixture.Slow.TryWriteAsync(Entry("after-cancel"), nextBudget.Token));
        Assert.Equal(1, fixture.SlowLogger.Errors); // The deliberately cancelled write only.
        var queueFacts = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fixture.SlowLogger.QueueFailureFacts);
        Assert.Equal(expectedStage, queueFacts["AuditQueueOwnerStage"]);
        Assert.InRange(Assert.IsType<long>(queueFacts["AuditQueueOwnerMs"]), 0, 5000);
        Assert.InRange(Assert.IsType<long>(queueFacts["AuditQueueOwnerStageMs"]), 0, 5000);
        Assert.InRange(Assert.IsType<long>(queueFacts["AuditQueueOwnerConnectionMs"]), 0, 5000);
        Assert.InRange(Assert.IsType<long>(queueFacts["AuditQueueOwnerAdmissionMs"]), 0, 5000);
        Assert.InRange(Assert.IsType<long>(queueFacts["AuditQueueOwnerLockMs"]), 0, 5000);
        Assert.InRange(Assert.IsType<long>(queueFacts["AuditQueueOwnerSaveMs"]), 0, 5000);
        Assert.InRange(Assert.IsType<long>(queueFacts["AuditQueueOwnerCommitMs"]), 0, 5000);
        Assert.Equal(0L, queueFacts["AuditQueueCompletedAttempts"]);
        Assert.Equal(1, queueFacts["AuditQueueDepth"]);
        await using var proof = fixture.SlowFactory.CreateDbContext();
        var actions = await proof.UserActivityLogs.Select(item => item.Action).OrderBy(action => action).ToArrayAsync();
        Assert.Equal(new[] { "after-cancel", "held" }, actions);
    }

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
            if (Directory.Exists(absolute))
            {
                try { Directory.Delete(absolute, recursive: true); }
                catch (IOException) when (SlowFactory.OpeningFailed.Task.IsCompleted || FastFactory.OpeningFailed.Task.IsCompleted)
                {
                    // Sqlite 9.0.9 can fail in its native connection constructor before
                    // assigning the handle to the disposable managed connection. Finalize
                    // that unreachable handle only during cleanup of this failed-open fixture.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Directory.Delete(absolute, recursive: true);
                }
            }
            return ValueTask.CompletedTask;
        }

        private static string ConnectionString(string path, bool sharedCache = false) => new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, ForeignKeys = true, Cache = sharedCache ? SqliteCacheMode.Shared : SqliteCacheMode.Default }.ToString();
    }

    private sealed class Factory(string connectionString, AuditLogger logger) : IDbContextFactory<PaymentContext>, IDisposable
    {
        private readonly ILoggerFactory _logging = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Error).AddProvider(new ErrorProvider(logger)));
        private SqliteConnection? _lastConnection;
        public WriterAdmissionObserver? WriterAdmission { get; set; }
        public string ConnectionString => connectionString;
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Opening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OpeningFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int? CreatedConnectionDefaultTimeout => _lastConnection?.DefaultTimeout;
        public string? CreatedConnectionString => _lastConnection?.ConnectionString;
        public PaymentContext CreateDbContext()
        {
            var writerAdmission = WriterAdmission;
            writerAdmission?.ContextCreated();
            var connection = new SqliteConnection(connectionString);
            _lastConnection = connection;
            var builder = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection, contextOwnsConnection: true)
                .UseLoggerFactory(_logging)
                .AddInterceptors(new OpenObserver(Opening, Opened, OpeningFailed));
            if (writerAdmission is not null) builder.AddInterceptors(writerAdmission);
            return new PaymentContext(builder.Options);
        }
        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
        public void Dispose() => _logging.Dispose();
    }

    private sealed class WriterAdmissionObserver(int expectedContexts, bool holdAtCommit = false) : DbTransactionInterceptor
    {
        private int _contexts;
        private int _admissions;
        public TaskCompletionSource AllContextsCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ContextCreated()
        {
            if (Interlocked.Increment(ref _contexts) == expectedContexts) AllContextsCreated.TrySetResult();
        }
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _admissions) == 2) SecondAdmission.TrySetResult();
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
            TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            if (!holdAtCommit && FirstStarted.TrySetResult()) await ReleaseFirst.Task.WaitAsync(cancellationToken);
            return result;
        }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (holdAtCommit && FirstStarted.TrySetResult()) await ReleaseFirst.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class ErrorProvider(AuditLogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    private sealed class OpenObserver(TaskCompletionSource opening, TaskCompletionSource opened, TaskCompletionSource failed) : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        { opening.TrySetResult(); return ValueTask.FromResult(result); }
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        { opened.TrySetResult(); return Task.CompletedTask; }
        public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        { failed.TrySetResult(); return Task.CompletedTask; }
    }

    private sealed class AuditLogger : ILogger<UserActivityService>
    {
        private int _errors;
        private IReadOnlyDictionary<string, object?>? _queueFailureFacts;
        public int Errors => Volatile.Read(ref _errors);
        public IReadOnlyDictionary<string, object?>? QueueFailureFacts => Volatile.Read(ref _queueFailureFacts);
        public TaskCompletionSource FirstBusyRetry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Interlocked.Increment(ref _errors);
            if (logLevel >= LogLevel.Error && state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                var facts = values.Where(item => item.Key is "AuditQueueOwnerStage" or "AuditQueueOwnerMs" or "AuditQueueOwnerStageMs" or "AuditQueueOwnerConnectionMs" or "AuditQueueOwnerAdmissionMs" or "AuditQueueOwnerLockMs" or "AuditQueueOwnerSaveMs" or "AuditQueueOwnerCommitMs" or "AuditQueueCompletedAttempts" or "AuditQueueDepth")
                    .ToDictionary(item => item.Key, item => item.Value);
                if (facts.Count > 0) Volatile.Write(ref _queueFailureFacts, facts);
            }
            if (logLevel == LogLevel.Warning && exception is SqliteException { SqliteErrorCode: 5 or 6 }) FirstBusyRetry.TrySetResult();
        }
    }
}
