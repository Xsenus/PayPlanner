using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PayPlanner.Api.Tests;

internal sealed class CalendarBatchQueryProbe(string provider) : DbCommandInterceptor
{
    public List<int> TaskPageLimits { get; } = [];
    public int LinkSelects { get; private set; }

    public void Reset()
    {
        TaskPageLimits.Clear();
        LinkSelects = 0;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        var sql = command.CommandText;
        if (sql.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal))
        {
            var limit = Regex.Match(sql, @"LIMIT\s+([@$]\w+)");
            if (limit.Success)
            {
                TaskPageLimits.Add(Convert.ToInt32(command.Parameters[limit.Groups[1].Value].Value));
            }
        }
        if (sql.Contains($"FROM \"{provider}CalendarTaskLinks\"", StringComparison.Ordinal)
            && !sql.Contains("COUNT(", StringComparison.Ordinal))
        {
            LinkSelects++;
        }
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

internal sealed class CalendarBatchColorProbe(HttpMessageHandler inner, Func<Task> verifyFirstPage,
    string? yandexDuplicateReport = null, bool failFirstYandexDuplicateRead = false) : DelegatingHandler(inner)
{
    public Dictionary<string, List<string>> PutBodies { get; } = [];
    public List<string> CalendarReadQueries { get; } = [];
    public List<string> DeletedPaths { get; } = [];
    private bool _readFailureInjected;
    public int DeleteCount { get; private set; }
    private bool _pageVerified;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if ((request.Method == HttpMethod.Get && path.Contains("/events", StringComparison.Ordinal))
            || request.Method.Method == "REPORT")
        {
            CalendarReadQueries.Add(request.RequestUri.Query);
        }
        if (request.Method.Method == "REPORT" && CalendarReadQueries.Count > 1
            && failFirstYandexDuplicateRead && !_readFailureInjected)
        {
            _readFailureInjected = true;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Synthetic permanent batch duplicate read failure"),
            };
        }
        if (request.Method.Method == "REPORT" && yandexDuplicateReport is not null && CalendarReadQueries.Count > 1)
        {
            return new HttpResponseMessage((HttpStatusCode)207)
            {
                Content = new StringContent(yandexDuplicateReport, Encoding.UTF8, "application/xml"),
            };
        }
        if (request.Method == HttpMethod.Delete)
        {
            DeleteCount++;
            DeletedPaths.Add(path);
        }
        if (request.Method == HttpMethod.Put && path.Contains("bulk-event-", StringComparison.Ordinal))
        {
            if (!PutBodies.TryGetValue(path, out var bodies))
            {
                bodies = [];
                PutBodies.Add(path, bodies);
            }
            bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (!_pageVerified && path.Contains("bulk-event-501", StringComparison.Ordinal))
            {
                _pageVerified = true;
                await verifyFirstPage();
            }
            if (request.Content.Headers.ContentType?.MediaType == "application/json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { id = path.Split('/').Last(), etag = "synthetic-color-etag" }),
                        Encoding.UTF8, "application/json"),
                };
            }
        }
        return await base.SendAsync(request, cancellationToken);
    }
}

internal sealed class CalendarBatchDeleteProbe(
    HttpMessageHandler inner, bool failOnce, Func<Task> verifyFirstPage) : DelegatingHandler(inner)
{
    public Dictionary<string, int> DeleteAttempts { get; } = [];
    private bool _pageVerified;
    private bool _failed;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Delete && path.Contains("bulk-event-", StringComparison.Ordinal))
        {
            DeleteAttempts[path] = DeleteAttempts.GetValueOrDefault(path) + 1;
            if (path.Contains("bulk-event-501", StringComparison.Ordinal))
            {
                if (!_pageVerified)
                {
                    _pageVerified = true;
                    await verifyFirstPage();
                }
                if (failOnce && !_failed)
                {
                    _failed = true;
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("Synthetic permanent delete error"),
                    };
                }
            }
        }
        return await base.SendAsync(request, cancellationToken);
    }
}

internal sealed class CalendarCleanupFailureProbe(HttpMessageHandler inner, bool google, bool fullSync,
    bool failRead, string duplicatePayload, int failureCount = 1) : DelegatingHandler(inner)
{
    public int EventWrites { get; private set; }
    public int DuplicateReadAttempts { get; private set; }
    public int DuplicateDeleteAttempts { get; private set; }
    public bool DuplicateDeleted { get; private set; }
    private int _remainingFailures = failureCount;
    private int _reportOrdinal;

    public void BeginRun() => _reportOrdinal = 0;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if ((request.Method == HttpMethod.Put && (path.Contains("/events/", StringComparison.Ordinal)
                || path.EndsWith(".ics", StringComparison.Ordinal)))
            || (request.Method == HttpMethod.Post && path.EndsWith("/events", StringComparison.Ordinal)))
        {
            EventWrites++;
        }
        if (google && request.Method == HttpMethod.Put && path.Contains("/events/", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { id = path.Split('/').Last(), etag = "synthetic-cleanup-etag" }),
                    Encoding.UTF8, "application/json"),
            };
        }
        var duplicateRead = google
            ? request.Method == HttpMethod.Get && request.RequestUri.Query.Contains("privateExtendedProperty=", StringComparison.Ordinal)
            : request.Method.Method == "REPORT" && (!fullSync || ++_reportOrdinal > 1);
        if (duplicateRead)
        {
            DuplicateReadAttempts++;
            if (failRead && _remainingFailures > 0)
            {
                _remainingFailures--;
                return PermanentFailure();
            }
            return new HttpResponseMessage(google ? HttpStatusCode.OK : (HttpStatusCode)207)
            {
                Content = new StringContent(DuplicateDeleted ? (google ? "{\"items\":[]}" : string.Empty) : duplicatePayload,
                    Encoding.UTF8, google ? "application/json" : "application/xml"),
            };
        }
        if (request.Method == HttpMethod.Delete && (path.EndsWith("/cleanup-duplicate", StringComparison.Ordinal)
            || path.EndsWith("/cleanup-duplicate.ics", StringComparison.Ordinal)))
        {
            DuplicateDeleteAttempts++;
            if (!failRead && _remainingFailures > 0)
            {
                _remainingFailures--;
                return PermanentFailure();
            }
            DuplicateDeleted = true;
        }
        return await base.SendAsync(request, cancellationToken);
    }

    private static HttpResponseMessage PermanentFailure() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("Synthetic permanent duplicate cleanup failure"),
    };
}
