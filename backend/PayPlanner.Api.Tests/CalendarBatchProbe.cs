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
    string? yandexDuplicateReport = null) : DelegatingHandler(inner)
{
    public Dictionary<string, List<string>> PutBodies { get; } = [];
    public List<string> CalendarReadQueries { get; } = [];
    public List<string> DeletedPaths { get; } = [];
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
