using System.Text.RegularExpressions;
using System.Text.Json;

namespace PayPlanner.Api.Tests;

// Diagnostic state belongs to one child. Normal log traffic cannot erase failures.
internal sealed class RestoredServerErrorRecorder
{
    private const int MaximumNames = 32;
    // Leave room for the assertion framework's own message around this summary.
    private const int MaximumSummaryLength = 8000;
    private static readonly Regex CategoryHeader = new(
        @"^fail:\s*(?<name>[A-Za-z_][A-Za-z0-9_.+`]{0,191})\[(?<event>\d{1,9})\]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ExceptionHeader = new(
        @"^\s*(?:--->\s*)?(?<name>[A-Za-z_][A-Za-z0-9_.]{0,191}Exception|Exception)(?:\s|:|\()",
        RegexOptions.CultureInvariant);
    private readonly object _sync = new();
    private readonly HashSet<string> _categories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exceptionTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _connectionDiagnostics = new(StringComparer.Ordinal);
    private readonly HashSet<string> _auditDiagnostics = new(StringComparer.Ordinal);
    private readonly Queue<string> _slowCommands = new();
    private long _failureCount;

    public void Receive(string line)
    {
        if (ReadSlowCommandTiming(line) is { } slowCommand)
        {
            lock (_sync)
            {
                _slowCommands.Enqueue(slowCommand);
                while (_slowCommands.Count > 8) _slowCommands.Dequeue();
            }
            return;
        }
        if (line.StartsWith(OwnedAuditFailureDiagnostics.Prefix, StringComparison.Ordinal))
        {
            var diagnostic = ReadAuditDiagnostic(line[OwnedAuditFailureDiagnostics.Prefix.Length..]);
            lock (_sync)
                if (_auditDiagnostics.Count < 4) _auditDiagnostics.Add(diagnostic);
            return;
        }
        const string diagnosticPrefix = "PP_RESTORED_CONNECTION_DIAGNOSTIC ";
        if (line.StartsWith(diagnosticPrefix, StringComparison.Ordinal))
        {
            var diagnostic = ReadConnectionDiagnostic(line[diagnosticPrefix.Length..]);
            lock (_sync)
                if (_connectionDiagnostics.Count < 4) _connectionDiagnostics.Add(diagnostic);
            return;
        }
        var failureIndex = line.IndexOf("fail:", StringComparison.OrdinalIgnoreCase);
        var exception = line.Contains("Exception", StringComparison.Ordinal)
            ? ExceptionHeader.Match(line[..Math.Min(line.Length, 512)]) : null;
        if (failureIndex < 0 && exception?.Success != true) return;
        lock (_sync)
        {
            if (failureIndex >= 0)
            {
                if (_failureCount < long.MaxValue) _failureCount++;
                var header = CategoryHeader.Match(line.Substring(failureIndex,
                    Math.Min(line.Length - failureIndex, 512)));
                var category = header.Success
                    ? $"fail: {header.Groups["name"].Value}[{header.Groups["event"].Value}]"
                    : "fail: [unknown category]";
                if (_categories.Count < MaximumNames) _categories.Add(category);
            }
            if (exception?.Success == true && _exceptionTypes.Count < MaximumNames)
                _exceptionTypes.Add(exception.Groups["name"].Value);
        }
    }

    public string FailureSummary()
    {
        lock (_sync)
        {
            if (_failureCount == 0) return string.Empty;
            var summary = $"Server fail log entries: {_failureCount}." + Environment.NewLine
                + string.Join(Environment.NewLine, _categories.OrderBy(name => name, StringComparer.Ordinal)
                    .Concat(_exceptionTypes.OrderBy(name => name, StringComparer.Ordinal))
                    .Concat(_connectionDiagnostics.OrderBy(name => name, StringComparer.Ordinal))
                    .Concat(_auditDiagnostics.OrderBy(name => name, StringComparer.Ordinal))
                    .Concat(_slowCommands));
            if (summary.Length <= MaximumSummaryLength) return summary;
            const string omitted = "\nAdditional diagnostic names omitted; failures remain recorded.";
            return summary[..(MaximumSummaryLength - omitted.Length)] + omitted;
        }
    }

    private static string ReadConnectionDiagnostic(string payload)
    {
        const string invalid = "Owned connection diagnostic unavailable or invalid; server errors remain failures.";
        if (payload.Length > 4096) return invalid;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            var value = document.RootElement;
            static string SafeName(JsonElement name)
            {
                var text = name.ValueKind == JsonValueKind.String ? name.GetString() : null;
                return text is { Length: <= 192 } && Regex.IsMatch(text, @"^[A-Za-z_][A-Za-z0-9_.+`<>]*$") ? text : "unknown";
            }
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("ExceptionTypes", out var types) || types.ValueKind != JsonValueKind.Array
                || !value.TryGetProperty("ApplicationFrames", out var frames) || frames.ValueKind != JsonValueKind.Array
                || !value.TryGetProperty("RequestAction", out var action)
                || !value.TryGetProperty("Canceled", out var canceled) || canceled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !value.TryGetProperty("DurationMilliseconds", out var duration) || !duration.TryGetDouble(out var milliseconds)
                || !double.IsFinite(milliseconds) || milliseconds is < 0 or > 3_600_000) return invalid;
            // Rebuild only the allowlisted, bounded diagnostic fields. Never retain the original JSON.
            return "Owned connection diagnostic: " + JsonSerializer.Serialize(new
            {
                ExceptionTypes = types.EnumerateArray().Take(4).Select(SafeName).ToArray(),
                Canceled = canceled.GetBoolean(),
                DurationMilliseconds = milliseconds,
                RequestAction = SafeName(action),
                CancellationSource = value.TryGetProperty("CancellationSource", out var source)
                    && source.ValueKind == JsonValueKind.String && source.GetString() is "request" or "other"
                        ? source.GetString() : "unavailable",
                RequestAbortedAtFailure = value.TryGetProperty("RequestAbortedAtFailure", out var aborted)
                    && aborted.ValueKind is JsonValueKind.True or JsonValueKind.False ? (bool?)aborted.GetBoolean() : null,
                ApplicationFrames = frames.EnumerateArray().Take(8).Select(SafeName).ToArray()
            });
        }
        catch (JsonException) { return invalid; }
        catch (InvalidOperationException) { return invalid; }
    }

    private static string ReadAuditDiagnostic(string payload)
    {
        const string invalid = "Owned audit diagnostic unavailable or invalid; server errors remain failures.";
        if (payload.Length > 4096) return invalid;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            var value = document.RootElement;
            static string SafeName(JsonElement name)
            {
                var text = name.ValueKind == JsonValueKind.String ? name.GetString() : null;
                return text is { Length: <= 192 } && Regex.IsMatch(text, @"^[A-Za-z_][A-Za-z0-9_.+`<>]*$") ? text : "unknown";
            }
            static bool? SafeBoolean(JsonElement root, string name)
                => root.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? property.GetBoolean() : null;
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("ExceptionTypes", out var types) || types.ValueKind != JsonValueKind.Array
                || !value.TryGetProperty("ApplicationFrames", out var frames) || frames.ValueKind != JsonValueKind.Array
                || !value.TryGetProperty("RequestAction", out var action)
                || SafeBoolean(value, "Canceled") is not { } canceled) return invalid;
            return "Owned audit diagnostic: " + JsonSerializer.Serialize(new
            {
                ExceptionTypes = types.EnumerateArray().Take(4).Select(SafeName).ToArray(),
                ApplicationFrames = frames.EnumerateArray().Take(8).Select(SafeName).ToArray(),
                ProviderFrames = value.TryGetProperty("ProviderFrames", out var providerFrames)
                    && providerFrames.ValueKind == JsonValueKind.Array
                        ? providerFrames.EnumerateArray().Take(4).Select(SafeName)
                            .Select(name => OwnedAuditFailureDiagnostics.IsProviderSymbol(name) ? name : "unknown").ToArray()
                        : [],
                RequestAction = SafeName(action),
                Canceled = canceled,
                CancellationSource = value.TryGetProperty("CancellationSource", out var source)
                    && source.ValueKind == JsonValueKind.String && source.GetString() is "request" or "other"
                        ? source.GetString() : "unavailable",
                CancellationTokenRequested = SafeBoolean(value, "CancellationTokenRequested"),
                RequestAbortedAtFailure = SafeBoolean(value, "RequestAbortedAtFailure"),
                AuditStage = value.TryGetProperty("AuditStage", out var stage) && stage.ValueKind == JsonValueKind.String
                    && stage.GetString() is "settings" or "write-lock" or "context" or "connection" or "save" or "completed"
                        ? stage.GetString() : "unavailable",
                AuditTotalMs = SafeMilliseconds(value, "AuditTotalMs"),
                AuditLockWaitMs = SafeMilliseconds(value, "AuditLockWaitMs"),
                AuditContextMs = SafeMilliseconds(value, "AuditContextMs"),
                AuditConnectionMs = SafeMilliseconds(value, "AuditConnectionMs"),
                AuditSaveMs = SafeMilliseconds(value, "AuditSaveMs"),
                ThreadPoolThreads = SafeMilliseconds(value, "ThreadPoolThreads"),
                ThreadPoolPending = SafeMilliseconds(value, "ThreadPoolPending"),
                GcPauseTotalMs = SafeMilliseconds(value, "GcPauseTotalMs")
            });
        }
        catch (JsonException) { return invalid; }
        catch (InvalidOperationException) { return invalid; }
    }

    private static double? SafeMilliseconds(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number) && number is >= 0 and <= 3_600_000
                ? number : null;

    private static string? ReadSlowCommandTiming(string line)
    {
        if (line.Length > 512) return null;
        var match = Regex.Match(line,
            @"\A\s*Slow database command (?<operation>SELECT|INSERT|UPDATE|DELETE|PRAGMA|OTHER) \((?<source>SaveChanges|LinqQuery|ExecuteSqlRaw|Migrations|Unknown), (?:Microsoft.EntityFrameworkCore.Sqlite|Npgsql.EntityFrameworkCore.PostgreSQL), (?:PaymentContext|PostgreSqlPaymentContext)\) (?<result>completed in|failed after) (?<elapsed>\d{1,9}(?:[.,]\d{1,3})?) ms \[Fingerprint=[A-F0-9]{16}\]\.\s*\z",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups["elapsed"].Value.Replace(',', '.'),
            System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture,
            out var elapsed) || elapsed is < 0 or > 3_600_000) return null;
        // Retain only fixed labels and finite timing. SQL, its fingerprint and
        // unrelated log text never enter the assertion's diagnostic summary.
        return "Owned slow command: " + match.Groups["operation"].Value + " " + match.Groups["source"].Value
            + " " + match.Groups["result"].Value + " " + elapsed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms";
    }
}
