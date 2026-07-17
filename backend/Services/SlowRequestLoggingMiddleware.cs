using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace PayPlanner.Api.Services;

public sealed class SlowRequestLoggingOptions
{
    public const string SectionName = "Diagnostics:SlowHttpRequests";

    public bool Enabled { get; set; } = true;
    public int ThresholdMilliseconds { get; set; } = 1_000;
}

public sealed class SlowRequestLoggingMiddleware
{
    public const int SlowRequestEventId = 5101;

    private static readonly EventId SlowRequestEvent = new(SlowRequestEventId, "SlowHttpRequest");

    private readonly RequestDelegate _next;
    private readonly ILogger<SlowRequestLoggingMiddleware> _logger;
    private readonly SlowRequestLoggingOptions _options;

    public SlowRequestLoggingMiddleware(
        RequestDelegate next,
        ILogger<SlowRequestLoggingMiddleware> logger,
        IOptions<SlowRequestLoggingOptions> options)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled ||
            !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        Exception? requestException = null;
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            requestException = ex;
            throw;
        }
        finally
        {
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var thresholdMilliseconds = Math.Max(1, _options.ThresholdMilliseconds);
            if (elapsedMilliseconds >= thresholdMilliseconds)
            {
                var statusCode = requestException is null
                    ? context.Response.StatusCode
                    : StatusCodes.Status500InternalServerError;
                var routeTemplate = ResolveSafeRouteTemplate(context);

                try
                {
                    _logger.LogWarning(
                        SlowRequestEvent,
                        "Slow HTTP request {HttpMethod} {RouteTemplate} returned {StatusCode} in {ElapsedMilliseconds:F2} ms.",
                        context.Request.Method,
                        routeTemplate,
                        statusCode,
                        elapsedMilliseconds);
                }
                catch
                {
                    // A diagnostics provider must never affect the request being measured.
                }
            }
        }
    }

    private static string ResolveSafeRouteTemplate(HttpContext context)
    {
        var rawTemplate = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (string.IsNullOrWhiteSpace(rawTemplate))
        {
            return "<unmatched-api-route>";
        }

        return rawTemplate.StartsWith('/') ? rawTemplate : $"/{rawTemplate}";
    }
}
