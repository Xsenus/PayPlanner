using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using PayPlanner.Api.Services.LegalIntegrations.Models;

namespace PayPlanner.Api.Services.LegalIntegrations;

public sealed partial class SudrfCourtSyncService : ISudrfCourtSyncService
{
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
    private readonly HttpClient _httpClient;
    private readonly ILogger<SudrfCourtSyncService> _logger;

    static SudrfCourtSyncService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public SudrfCourtSyncService(HttpClient httpClient, ILogger<SudrfCourtSyncService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(25);
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PayPlanner", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
    }

    public bool CanHandle(string? sourceUrl)
        => TryCreateSudrfUri(sourceUrl, out _);

    public async Task<SudrfCourtCaseData> FetchCaseAsync(string sourceUrl, CancellationToken ct, string? courtCaseNumber = null)
    {
        if (!TryCreateSudrfUri(sourceUrl, out var uri))
        {
            throw new SudrfCourtSyncException("Автоматическая проверка поддерживает только ссылки на сайты sudrf.ru.");
        }

        try
        {
            var html = await ReadHtmlAsync(uri, ct);
            var data = ParseCaseHtml(html, uri);
            if (HasCaseCardData(data))
            {
                return data;
            }

            var linkedCardUri = ExtractCaseCardUri(html, uri, courtCaseNumber);
            if (linkedCardUri is not null && !UriEquals(linkedCardUri, uri))
            {
                var linkedHtml = await ReadHtmlAsync(linkedCardUri, ct);
                var linkedData = ParseCaseHtml(linkedHtml, linkedCardUri);
                if (HasCaseCardData(linkedData))
                {
                    return linkedData;
                }
            }

            if (string.IsNullOrWhiteSpace(courtCaseNumber))
            {
                throw new SudrfCourtSyncException("В карточке суда не удалось найти номер дела или движение дела. Для поиска укажите номер дела в суде или ссылку на найденную карточку.");
            }

            var resolvedCardUri = await FindCaseCardUriAsync(uri, courtCaseNumber, ct);
            if (resolvedCardUri is null)
            {
                throw new SudrfCourtSyncException($"На сайте суда не удалось найти карточку по номеру {courtCaseNumber}.");
            }

            var resolvedHtml = await ReadHtmlAsync(resolvedCardUri, ct);
            var resolvedData = ParseCaseHtml(resolvedHtml, resolvedCardUri);
            if (!HasCaseCardData(resolvedData))
            {
                throw new SudrfCourtSyncException("Найденная ссылка суда не содержит карточку дела.");
            }

            return resolvedData;
        }
        catch (SudrfCourtSyncException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch SUDRF court case from {SourceUrl}", uri);
            throw new SudrfCourtSyncException("Не удалось прочитать карточку дела на сайте суда.", ex);
        }
    }

    private async Task<string> ReadHtmlAsync(Uri uri, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new SudrfCourtSyncException($"Сайт суда вернул статус {(int)response.StatusCode}.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return ResolveEncoding(response.Content.Headers.ContentType?.CharSet, bytes).GetString(bytes);
    }

    private async Task<Uri?> FindCaseCardUriAsync(Uri sourceUri, string courtCaseNumber, CancellationToken ct)
    {
        foreach (var deloId in BuildDeloIdCandidates(sourceUri, courtCaseNumber))
        {
            foreach (var queryNumber in BuildCaseNumberSearchValues(courtCaseNumber))
            {
                var resultHtml = await PostCaseSearchAsync(sourceUri, deloId, queryNumber, ct);
                var cardUri = ExtractCaseCardUri(resultHtml, sourceUri, courtCaseNumber);
                if (cardUri is not null)
                {
                    return cardUri;
                }
            }
        }

        return null;
    }

    private async Task<string> PostCaseSearchAsync(Uri sourceUri, string deloId, string courtCaseNumber, CancellationToken ct)
    {
        var srvNum = FirstNotEmpty(ExtractQueryValue(sourceUri, "srv_num"), "1");
        var modulesUri = new Uri(sourceUri, "/modules.php");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "sud_delo",
            ["srv_num"] = srvNum,
            ["name_op"] = "r",
            ["delo_id"] = deloId,
            ["case_type"] = "0",
            ["new"] = "0",
            ["delo_table"] = "p2_case",
            ["p2_case__CASE_NUMBERSS"] = courtCaseNumber,
            ["list"] = "ON",
        });

        using var response = await _httpClient.PostAsync(modulesUri, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new SudrfCourtSyncException($"Поиск на сайте суда вернул статус {(int)response.StatusCode}.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return ResolveEncoding(response.Content.Headers.ContentType?.CharSet, bytes).GetString(bytes);
    }

    public static SudrfCourtCaseData ParseCaseHtml(string html, Uri sourceUri)
    {
        var labelValues = ExtractLabelValues(html, "cont1")
            .Concat(ExtractLabelValues(html, "cont2"))
            .ToDictionary(item => NormalizeLabel(item.Key), item => item.Value, StringComparer.OrdinalIgnoreCase);
        var movements = ExtractMovementEvents(html);
        var participants = ExtractParticipants(html);
        var nextHearing = ResolveNextHearing(movements);
        var caseNumber = FirstNotEmpty(
            GetValue(labelValues, "номер дела"),
            ExtractCaseNumber(html));
        var courtName = FirstNotEmpty(ExtractCourtName(html), GetValue(labelValues, "суд"));
        var category = FirstNotEmpty(GetValue(labelValues, "категория дела"), GetValue(labelValues, "категория"));
        var judgeName = FirstNotEmpty(GetValue(labelValues, "судья"), GetValue(labelValues, "докладчик"));
        var lowerCourtCaseNumber = FirstNotEmpty(
            GetValue(labelValues, "номер дела в суде нижестоящей инстанции"),
            GetValue(labelValues, "номер дела нижестоящего суда"));
        var sourceText = StripTags(html);

        return new SudrfCourtCaseData
        {
            SourceUrl = sourceUri.ToString(),
            CourtName = courtName,
            CaseNumber = caseNumber,
            JudicialUid = FirstNotEmpty(ExtractQueryValue(sourceUri, "judicial_uid"), ExtractJudicialUid(html)),
            Category = category,
            JudgeName = judgeName,
            LowerCourtName = FirstNotEmpty(GetValue(labelValues, "суд первой инстанции"), GetValue(labelValues, "нижестоящий суд")),
            LowerCourtCaseNumber = lowerCourtCaseNumber,
            Instance = ResolveInstance(sourceUri, sourceText),
            ReceivedAt = ParseDate(GetValue(labelValues, "дата поступления")),
            NextHearingAt = nextHearing?.ScheduledAt,
            NextHearingPlace = nextHearing?.Place ?? string.Empty,
            MovementEvents = movements,
            Participants = participants,
        };
    }

    private static bool HasCaseCardData(SudrfCourtCaseData data)
        => !string.IsNullOrWhiteSpace(data.CaseNumber) || data.MovementEvents.Count > 0;

    private static IReadOnlyList<string> BuildDeloIdCandidates(Uri sourceUri, string courtCaseNumber)
    {
        var values = new List<string>();
        AddIfPresent(ExtractQueryValue(sourceUri, "delo_id"));

        var normalized = courtCaseNumber.Trim().ToLowerInvariant();
        if (normalized.StartsWith("33", StringComparison.Ordinal)
            || normalized.Contains("апелля", StringComparison.OrdinalIgnoreCase))
        {
            AddIfPresent("42");
        }

        AddIfPresent("1540005");
        AddIfPresent("1540001");
        AddIfPresent("41");
        return values;

        void AddIfPresent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || values.Contains(value, StringComparer.Ordinal))
            {
                return;
            }

            values.Add(value);
        }
    }

    private static IReadOnlyList<string> BuildCaseNumberSearchValues(string courtCaseNumber)
    {
        var values = new List<string>();
        AddIfPresent(courtCaseNumber);
        var dashIndex = courtCaseNumber.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0 && dashIndex + 1 < courtCaseNumber.Length)
        {
            AddIfPresent(courtCaseNumber[(dashIndex + 1)..]);
        }

        var normalized = courtCaseNumber.Replace('–', '-').Replace('—', '-');
        if (!string.Equals(normalized, courtCaseNumber, StringComparison.Ordinal))
        {
            AddIfPresent(normalized);
        }

        return values;

        void AddIfPresent(string? value)
        {
            var normalizedValue = NormalizeOptional(value);
            if (normalizedValue is null || values.Contains(normalizedValue, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            values.Add(normalizedValue);
        }
    }

    private static bool UriEquals(Uri left, Uri right)
        => string.Equals(left.GetLeftPart(UriPartial.Path), right.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Query, right.Query, StringComparison.OrdinalIgnoreCase);

    private static Uri? ExtractCaseCardUri(string html, Uri sourceUri, string? courtCaseNumber)
    {
        var matches = CaseCardLinkRegex().Matches(html);
        if (matches.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(courtCaseNumber))
        {
            return new Uri(sourceUri, WebUtility.HtmlDecode(matches[0].Groups["href"].Value));
        }

        var expectedNumbers = BuildCaseNumberSearchValues(courtCaseNumber);
        foreach (Match match in matches)
        {
            var rowText = NormalizeText(match.Groups["row"].Value);
            if (expectedNumbers.Any(number => rowText.Contains(number, StringComparison.OrdinalIgnoreCase)))
            {
                return new Uri(sourceUri, WebUtility.HtmlDecode(match.Groups["href"].Value));
            }
        }

        return new Uri(sourceUri, WebUtility.HtmlDecode(matches[0].Groups["href"].Value));
    }

    private static bool TryCreateSudrfUri(string? sourceUrl, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!parsed.Host.EndsWith(".sudrf.ru", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static Encoding ResolveEncoding(string? charset, byte[] bytes)
    {
        var normalized = charset?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            try
            {
                return Encoding.GetEncoding(normalized);
            }
            catch (ArgumentException)
            {
            }
        }

        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var match = CharsetRegex().Match(head);
        if (match.Success)
        {
            try
            {
                return Encoding.GetEncoding(match.Groups["charset"].Value);
            }
            catch (ArgumentException)
            {
            }
        }

        return Encoding.UTF8;
    }

    private static Dictionary<string, string> ExtractLabelValues(string html, string className)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tableMatch in TableByClassRegex(className).Matches(html))
        {
            foreach (var row in ExtractRows(tableMatch.Groups["body"].Value))
            {
                if (row.Count < 2)
                {
                    continue;
                }

                var label = NormalizeLabel(row[0]);
                var value = NormalizeText(row[1]);
                if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(value))
                {
                    values[label] = value;
                }
            }
        }

        return values;
    }

    private static IReadOnlyList<SudrfCourtMovementEvent> ExtractMovementEvents(string html)
    {
        var events = new List<SudrfCourtMovementEvent>();
        foreach (Match tableMatch in TableByClassRegex("cont3").Matches(html))
        {
            var rows = ExtractRows(tableMatch.Groups["body"].Value);
            var headers = Array.Empty<string>();
            foreach (var row in rows)
            {
                if (row.Count < 2)
                {
                    continue;
                }

                if (IsHeaderRow(row))
                {
                    headers = row.Select(NormalizeLabel).ToArray();
                    continue;
                }

                var title = GetCell(headers, row, "наименование события", "событие");
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = row[0];
                }

                var dateText = FirstNotEmpty(GetCell(headers, row, "дата"), row.FirstOrDefault(ContainsDate));
                var timeText = FirstNotEmpty(GetCell(headers, row, "время"), row.FirstOrDefault(ContainsTime));
                var scheduledAt = ParseDateTime(dateText, timeText);
                if (string.IsNullOrWhiteSpace(title) || scheduledAt is null && !ContainsDate(string.Join(' ', row)))
                {
                    continue;
                }

                var place = GetCell(headers, row, "место", "зал");
                var result = GetCell(headers, row, "результат");
                var reason = GetCell(headers, row, "основание", "причина");
                var posted = ParseDate(GetCell(headers, row, "дата размещения", "размещено"));

                events.Add(new SudrfCourtMovementEvent
                {
                    Title = title,
                    ScheduledAt = scheduledAt,
                    Place = place,
                    Result = result,
                    Reason = reason,
                    PostedAt = posted,
                    SourceSummary = BuildMovementSummary(title, scheduledAt, place, result, reason),
                });
            }
        }

        return events;
    }

    private static IReadOnlyList<SudrfCourtParticipant> ExtractParticipants(string html)
    {
        var participants = new List<SudrfCourtParticipant>();
        foreach (Match tableMatch in TableByClassRegex("cont4").Matches(html))
        {
            foreach (var row in ExtractRows(tableMatch.Groups["body"].Value))
            {
                if (row.Count < 2 || IsHeaderRow(row) || IsParticipantHeaderRow(row))
                {
                    continue;
                }

                var role = row[0];
                var name = row[1];
                if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                participants.Add(new SudrfCourtParticipant
                {
                    Role = role,
                    Name = name,
                    Details = row.Count > 2 ? string.Join("; ", row.Skip(2).Where(static value => !string.IsNullOrWhiteSpace(value))) : string.Empty,
                });
            }
        }

        return participants;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ExtractRows(string tableHtml)
        => RowRegex()
            .Matches(tableHtml)
            .Select(static rowMatch => CellRegex()
                .Matches(rowMatch.Groups["row"].Value)
                .Select(static cellMatch => NormalizeText(cellMatch.Groups["cell"].Value))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToList())
            .Where(static row => row.Count > 0)
            .ToList();

    private static string GetCell(IReadOnlyList<string> headers, IReadOnlyList<string> row, params string[] headerParts)
    {
        for (var i = 0; i < headers.Count && i < row.Count; i++)
        {
            if (headerParts.Any(part => headers[i].Contains(part, StringComparison.OrdinalIgnoreCase)))
            {
                return row[i];
            }
        }

        return string.Empty;
    }

    private static bool IsHeaderRow(IReadOnlyList<string> row)
    {
        var normalized = row.Select(NormalizeLabel).ToArray();
        return normalized.Any(static value => value.Contains("дата", StringComparison.OrdinalIgnoreCase))
            && normalized.Any(static value =>
                value.Contains("событие", StringComparison.OrdinalIgnoreCase)
                || value.Contains("наименование", StringComparison.OrdinalIgnoreCase)
                || value.Contains("участник", StringComparison.OrdinalIgnoreCase)
                || value.Contains("лицо", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsParticipantHeaderRow(IReadOnlyList<string> row)
    {
        if (row.Count < 2)
        {
            return false;
        }

        var first = NormalizeLabel(row[0]);
        var second = NormalizeLabel(row[1]);
        return (first.Contains("лицо", StringComparison.OrdinalIgnoreCase)
                || first.Contains("участник", StringComparison.OrdinalIgnoreCase)
                || first.Contains("тип", StringComparison.OrdinalIgnoreCase))
            && (second.Contains("фио", StringComparison.OrdinalIgnoreCase)
                || second.Contains("наименование", StringComparison.OrdinalIgnoreCase)
                || second.Contains("имя", StringComparison.OrdinalIgnoreCase));
    }

    private static SudrfCourtMovementEvent? ResolveNextHearing(IReadOnlyList<SudrfCourtMovementEvent> movements)
    {
        var hearings = movements
            .Where(static item => item.ScheduledAt.HasValue && item.Title.Contains("судебное заседание", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static item => item.ScheduledAt)
            .ToList();
        if (hearings.Count == 0)
        {
            return null;
        }

        var now = DateTime.Now;
        return hearings.FirstOrDefault(item => item.ScheduledAt!.Value >= now.Date) ?? hearings[^1];
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = DateRegex().Match(value);
        return match.Success && DateTime.TryParseExact(match.Value, "dd.MM.yyyy", RussianCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static DateTime? ParseDateTime(string? dateText, string? timeText)
    {
        var date = ParseDate(dateText);
        if (date is null)
        {
            return null;
        }

        var timeMatch = TimeRegex().Match($"{dateText} {timeText}");
        if (timeMatch.Success && TimeSpan.TryParseExact(timeMatch.Value, "hh\\:mm", RussianCulture, out var time))
        {
            return date.Value.Date.Add(time);
        }

        return date.Value.Date;
    }

    private static string ExtractCourtName(string html)
    {
        var titleMatch = TitleRegex().Match(html);
        if (!titleMatch.Success)
        {
            return string.Empty;
        }

        var title = NormalizeText(titleMatch.Groups["title"].Value);
        var dashIndex = title.IndexOf(" - ", StringComparison.Ordinal);
        return dashIndex > 0 ? title[..dashIndex].Trim() : title;
    }

    private static string ExtractCaseNumber(string html)
    {
        var text = StripTags(html);
        var match = CaseNumberRegex().Match(text);
        return match.Success ? NormalizeText(match.Groups["number"].Value) : string.Empty;
    }

    private static string ExtractJudicialUid(string html)
    {
        var match = JudicialUidRegex().Match(WebUtility.HtmlDecode(html));
        return match.Success ? match.Groups["uid"].Value.Trim() : string.Empty;
    }

    private static string ExtractQueryValue(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=', StringComparison.Ordinal);
            var key = index >= 0 ? part[..index] : part;
            if (!string.Equals(Uri.UnescapeDataString(key), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return index >= 0 ? Uri.UnescapeDataString(part[(index + 1)..]) : string.Empty;
        }

        return string.Empty;
    }

    private static string ResolveInstance(Uri sourceUri, string pageText)
    {
        var deloId = ExtractQueryValue(sourceUri, "delo_id");
        if (string.Equals(deloId, "42", StringComparison.Ordinal))
        {
            return "appeal";
        }

        return pageText.Contains("апелля", StringComparison.OrdinalIgnoreCase)
            ? "appeal"
            : "first";
    }

    private static string BuildMovementSummary(string title, DateTime? scheduledAt, string place, string result, string reason)
    {
        var parts = new List<string> { title };
        if (scheduledAt.HasValue)
        {
            parts.Add(scheduledAt.Value.ToString("dd.MM.yyyy HH:mm", RussianCulture));
        }

        if (!string.IsNullOrWhiteSpace(place))
        {
            parts.Add($"место: {place}");
        }

        if (!string.IsNullOrWhiteSpace(result))
        {
            parts.Add($"результат: {result}");
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            parts.Add($"основание: {reason}");
        }

        return string.Join("; ", parts);
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(NormalizeLabel(key), out var value) ? value : string.Empty;

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static bool ContainsDate(string? value)
        => !string.IsNullOrWhiteSpace(value) && DateRegex().IsMatch(value);

    private static bool ContainsTime(string? value)
        => !string.IsNullOrWhiteSpace(value) && TimeRegex().IsMatch(value);

    private static string NormalizeLabel(string value)
        => NormalizeText(value).TrimEnd(':').ToLowerInvariant();

    private static string NormalizeText(string value)
    {
        var text = Regex.Replace(value, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        text = StripTags(text);
        return WhitespaceRegex().Replace(text.Replace('\u00a0', ' '), " ").Trim();
    }

    private static string StripTags(string value)
    {
        var withoutScripts = Regex.Replace(value, @"<script\b[^>]*>.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        withoutScripts = Regex.Replace(withoutScripts, @"<style\b[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return WebUtility.HtmlDecode(TagsRegex().Replace(withoutScripts, " "));
    }

    private static Regex TableByClassRegex(string className)
        => new($@"<table\b[^>]*class\s*=\s*[""'][^""']*\b{Regex.Escape(className)}\b[^""']*[""'][^>]*>(?<body>.*?)</table>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"charset\s*=\s*[""']?(?<charset>[a-z0-9_\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CharsetRegex();

    [GeneratedRegex(@"<tr\b[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<tr\b[^>]*>(?<row>.*?<a\b[^>]*href\s*=\s*[""'](?<href>[^""']*name_op=case[^""']*case_id=[^""']*)[""'][^>]*>.*?</tr>)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CaseCardLinkRegex();

    [GeneratedRegex(@"<t[dh]\b[^>]*>(?<cell>.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<title\b[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"ДЕЛО\s*№\s*(?<number>[^\r\n<]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CaseNumberRegex();

    [GeneratedRegex(@"judicial_uid=(?<uid>[0-9A-ZА-Яa-zа-я\-_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JudicialUidRegex();

    [GeneratedRegex(@"\d{2}\.\d{2}\.\d{4}", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\d{2}:\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagsRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
