using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SlowDbCommandInterceptorTests
{
    [Fact]
    public async Task SlowParameterizedCommands_LogStableFingerprintWithoutSqlOrParameterValues()
    {
        var logger = new RecordingLogger<SlowDbCommandInterceptor>();
        var interceptor = CreateInterceptor(logger, enabled: true, thresholdMilliseconds: 1);
        await using var connection = await CreateOpenConnectionAsync();
        connection.CreateFunction<int, int>("delay_ms", milliseconds =>
        {
            Thread.Sleep(milliseconds);
            return milliseconds;
        });
        await using var db = CreateContext(connection, interceptor);

        await db.Database.ExecuteSqlRawAsync(
            "SELECT delay_ms({0}) /* sensitive-command-marker */",
            20);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT delay_ms({0}) /* sensitive-command-marker */",
            25);

        Assert.Equal(2, logger.Records.Count);
        Assert.All(logger.Records, record =>
        {
            Assert.Equal(LogLevel.Warning, record.Level);
            Assert.Equal(SlowDbCommandInterceptor.SlowCommandEventId, record.EventId.Id);
            Assert.Equal("SELECT", record.Properties["Operation"]);
            Assert.Equal("ExecuteSqlRaw", record.Properties["CommandSource"]?.ToString());
            Assert.Equal(nameof(PaymentContext), record.Properties["DbContext"]);
            Assert.DoesNotContain("sensitive-command-marker", record.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT delay_ms", record.Message, StringComparison.Ordinal);
        });
        Assert.Equal(
            logger.Records[0].Properties["CommandFingerprint"],
            logger.Records[1].Properties["CommandFingerprint"]);
    }

    [Fact]
    public async Task FastOrDisabledCommand_DoesNotWriteSlowCommandLog()
    {
        var fastLogger = new RecordingLogger<SlowDbCommandInterceptor>();
        var fastInterceptor = CreateInterceptor(fastLogger, enabled: true, thresholdMilliseconds: 60_000);
        await using (var connection = await CreateOpenConnectionAsync())
        await using (var db = CreateContext(connection, fastInterceptor))
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1 /* fast-sensitive-marker */");
        }
        Assert.Empty(fastLogger.Records);

        var disabledLogger = new RecordingLogger<SlowDbCommandInterceptor>();
        var disabledInterceptor = CreateInterceptor(disabledLogger, enabled: false, thresholdMilliseconds: 1);
        await using (var connection = await CreateOpenConnectionAsync())
        {
            connection.CreateFunction<int, int>("delay_ms", milliseconds =>
            {
                Thread.Sleep(milliseconds);
                return milliseconds;
            });
            await using var db = CreateContext(connection, disabledInterceptor);
            await db.Database.ExecuteSqlRawAsync("SELECT delay_ms(20) /* disabled-sensitive-marker */");
        }
        Assert.Empty(disabledLogger.Records);
    }

    [Fact]
    public async Task SlowFailedCommand_IsLoggedWithoutExceptionOrSqlDetails()
    {
        var logger = new RecordingLogger<SlowDbCommandInterceptor>();
        var interceptor = CreateInterceptor(logger, enabled: true, thresholdMilliseconds: 1);
        await using var connection = await CreateOpenConnectionAsync();
        connection.CreateFunction<int>("slow_failure", () =>
        {
            Thread.Sleep(20);
            throw new InvalidOperationException("sensitive-database-password");
        });
        await using var db = CreateContext(connection, interceptor);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync("SELECT slow_failure() /* failed-sensitive-marker */"));

        var record = Assert.Single(logger.Records);
        Assert.Equal(SlowDbCommandInterceptor.SlowFailedCommandEventId, record.EventId.Id);
        Assert.Null(record.Exception);
        Assert.DoesNotContain("sensitive-database-password", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed-sensitive-marker", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT slow_failure", record.Message, StringComparison.Ordinal);
    }

    private static SlowDbCommandInterceptor CreateInterceptor(
        ILogger<SlowDbCommandInterceptor> logger,
        bool enabled,
        int thresholdMilliseconds)
        => new(
            logger,
            Options.Create(new SlowDbCommandLoggingOptions
            {
                Enabled = enabled,
                ThresholdMilliseconds = thresholdMilliseconds,
            }));

    private static async Task<SqliteConnection> CreateOpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static PaymentContext CreateContext(
        SqliteConnection connection,
        SlowDbCommandInterceptor interceptor)
        => new(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogRecord> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Records.Add(new LogRecord(logLevel, eventId, formatter(state, exception), exception, properties));
        }
    }

    private sealed record LogRecord(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);
}
