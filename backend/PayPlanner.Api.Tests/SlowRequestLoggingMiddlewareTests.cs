using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SlowRequestLoggingMiddlewareTests
{
    [Fact]
    public async Task SlowApiRequest_LogsOnlySafeRouteTemplateAndRequestSummary()
    {
        var logger = new RecordingLogger<SlowRequestLoggingMiddleware>();
        var middleware = CreateMiddleware(
            logger,
            async context =>
            {
                await Task.Delay(20);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            },
            enabled: true,
            thresholdMilliseconds: 1);
        var context = CreateContext(
            "/api/clients/sensitive-client-id",
            "/api/clients/{id}",
            "?access_token=secret-token");

        await middleware.InvokeAsync(context);

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Equal(SlowRequestLoggingMiddleware.SlowRequestEventId, record.EventId.Id);
        Assert.Equal("GET", record.Properties["HttpMethod"]);
        Assert.Equal("/api/clients/{id}", record.Properties["RouteTemplate"]);
        Assert.Equal(StatusCodes.Status204NoContent, record.Properties["StatusCode"]);
        Assert.DoesNotContain("sensitive-client-id", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FastOrDisabledRequest_DoesNotWriteSlowRequestLog()
    {
        var fastLogger = new RecordingLogger<SlowRequestLoggingMiddleware>();
        var fastMiddleware = CreateMiddleware(
            fastLogger,
            _ => Task.CompletedTask,
            enabled: true,
            thresholdMilliseconds: 60_000);

        await fastMiddleware.InvokeAsync(CreateContext("/api/health", "/api/health"));

        Assert.Empty(fastLogger.Records);

        var disabledLogger = new RecordingLogger<SlowRequestLoggingMiddleware>();
        var disabledMiddleware = CreateMiddleware(
            disabledLogger,
            async _ => await Task.Delay(10),
            enabled: false,
            thresholdMilliseconds: 1);

        await disabledMiddleware.InvokeAsync(CreateContext("/api/health", "/api/health"));

        Assert.Empty(disabledLogger.Records);

        var nonApiLogger = new RecordingLogger<SlowRequestLoggingMiddleware>();
        var nonApiMiddleware = CreateMiddleware(
            nonApiLogger,
            async _ => await Task.Delay(10),
            enabled: true,
            thresholdMilliseconds: 1);

        await nonApiMiddleware.InvokeAsync(CreateContext("/index.html", "/index.html"));

        Assert.Empty(nonApiLogger.Records);
    }

    [Fact]
    public async Task SlowFailedRequest_IsLoggedWithoutExceptionDetailsAndRethrown()
    {
        var logger = new RecordingLogger<SlowRequestLoggingMiddleware>();
        var middleware = CreateMiddleware(
            logger,
            async _ =>
            {
                await Task.Delay(20);
                throw new InvalidOperationException("sensitive-database-password");
            },
            enabled: true,
            thresholdMilliseconds: 1);
        var context = CreateContext("/api/failure", "/api/failure");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.Equal("sensitive-database-password", exception.Message);
        var record = Assert.Single(logger.Records);
        Assert.Equal(StatusCodes.Status500InternalServerError, record.Properties["StatusCode"]);
        Assert.Null(record.Exception);
        Assert.DoesNotContain("sensitive-database-password", record.Message, StringComparison.Ordinal);
    }

    private static SlowRequestLoggingMiddleware CreateMiddleware(
        ILogger<SlowRequestLoggingMiddleware> logger,
        RequestDelegate next,
        bool enabled,
        int thresholdMilliseconds)
        => new(
            next,
            logger,
            Options.Create(new SlowRequestLoggingOptions
            {
                Enabled = enabled,
                ThresholdMilliseconds = thresholdMilliseconds,
            }));

    private static DefaultHttpContext CreateContext(
        string requestPath,
        string routeTemplate,
        string? queryString = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = requestPath;
        context.Request.QueryString = new QueryString(queryString ?? string.Empty);
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(routeTemplate),
            order: 0,
            EndpointMetadataCollection.Empty,
            displayName: routeTemplate));
        return context;
    }

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
