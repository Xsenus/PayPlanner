using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

// Only connection-wide failures are grouped. Task/calendar diagnostics and successes stay individual.
public static class CalendarSyncConnectionFailureLog
{
    public static Task<bool> TryAggregateAsync(PaymentContext db, GoogleCalendarSyncLog log, string? originalDetails, CancellationToken cancellationToken)
        => TryAggregateAsync(db, log, log.ConnectionId, log.CalendarSyncCalendarId, log.TaskJournalItemId,
            log.Direction, log.Operation, log.Status, log.Message, log.DetailsJson, originalDetails, log.CreatedAt,
            (message, details) => { log.Message = message; log.DetailsJson = details; }, cancellationToken);

    public static Task<bool> TryAggregateAsync(PaymentContext db, YandexCalendarSyncLog log, string? originalDetails, CancellationToken cancellationToken)
        => TryAggregateAsync(db, log, log.ConnectionId, log.CalendarSyncCalendarId, log.TaskJournalItemId,
            log.Direction, log.Operation, log.Status, log.Message, log.DetailsJson, originalDetails, log.CreatedAt,
            (message, details) => { log.Message = message; log.DetailsJson = details; }, cancellationToken);

    private static async Task<bool> TryAggregateAsync<TLog>(
        PaymentContext db, TLog log, int? connectionId, int? calendarId, int? taskId,
        string direction, string operation, string status, string message, string? details, string? originalDetails, DateTime now,
        Action<string, string> setValues, CancellationToken cancellationToken) where TLog : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connectionId is null or <= 0 || calendarId.HasValue || taskId.HasValue || status != "error")
        {
            return false;
        }

        // Hash the complete stored diagnostic/context, not a shortened or normalized error message.
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { direction, operation, message, details, originalDetails }))));
        var prefix = "{\"failureSignature\":\"" + signature + "\"";
        var dayStart = now.Date;
        var dayEnd = dayStart.AddDays(1);
        var logs = db.Set<TLog>();
        var candidates = logs.Where(item => EF.Property<int?>(item, "ConnectionId") == connectionId
            && EF.Property<int?>(item, "CalendarSyncCalendarId") == null
            && EF.Property<int?>(item, "TaskJournalItemId") == null
            && EF.Property<string>(item, "Direction") == direction
            && EF.Property<string>(item, "Operation") == operation
            && EF.Property<string>(item, "Status") == "error"
            && EF.Property<DateTime>(item, "CreatedAt") >= dayStart
            && EF.Property<DateTime>(item, "CreatedAt") < dayEnd
            && EF.Property<string?>(item, "DetailsJson") != null
            && EF.Property<string>(item, "DetailsJson").StartsWith(prefix)
            // A recurrence after recovery is a new incident, even on the same UTC day.
            && !logs.Any(recovery => EF.Property<int?>(recovery, "ConnectionId") == connectionId
                && EF.Property<string>(recovery, "Status") == "success"
                && EF.Property<string>(recovery, "Operation") == "connection-recovered"
                && EF.Property<DateTime>(recovery, "CreatedAt") >= dayStart
                && EF.Property<DateTime>(recovery, "CreatedAt") < dayEnd
                && EF.Property<int>(recovery, "Id") > EF.Property<int>(item, "Id")));

        var pendingRecoveryAt = db.ChangeTracker.Entries<TLog>()
            .Where(entry => entry.State == EntityState.Added
                && entry.Property("ConnectionId").CurrentValue is int id && id == connectionId
                && entry.Property("Status").CurrentValue as string == "success"
                && entry.Property("Operation").CurrentValue as string == "connection-recovered")
            .Select(entry => (DateTime?)entry.Property("CreatedAt").CurrentValue).Max();
        if (pendingRecoveryAt.HasValue)
        {
            candidates = candidates.Where(item => EF.Property<DateTime>(item, "CreatedAt") > pendingRecoveryAt.Value);
        }

        // Same save batch: the first occurrence may not yet exist in the database.
        foreach (var entry in db.ChangeTracker.Entries<TLog>().Where(entry => entry.State == EntityState.Added))
        {
            if (ReferenceEquals(entry.Entity, log) || entry.Property("ConnectionId").CurrentValue is not int id || id != connectionId
                || entry.Property("CalendarSyncCalendarId").CurrentValue is not null || entry.Property("TaskJournalItemId").CurrentValue is not null
                || entry.Property("Status").CurrentValue as string != "error"
                || entry.Property("DetailsJson").CurrentValue is not string pending || !pending.StartsWith(prefix, StringComparison.Ordinal)
                || entry.Property("CreatedAt").CurrentValue is not DateTime created || created < dayStart || created >= dayEnd)
            {
                continue;
            }
            if (pendingRecoveryAt >= created)
            {
                continue;
            }
            if (TryIncrement(pending, signature, message, now, out var nextMessage, out var nextDetails))
            {
                entry.Property("Message").CurrentValue = nextMessage;
                entry.Property("DetailsJson").CurrentValue = nextDetails;
                return true;
            }
        }

        // Compare-and-swap keeps counters correct across concurrent scopes/processes.
        // A race with retention or a malformed summary must never discard a new failure.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var previous = await candidates.AsNoTracking().OrderByDescending(item => EF.Property<int>(item, "Id"))
                .Select(item => new { Id = EF.Property<int>(item, "Id"), Details = EF.Property<string>(item, "DetailsJson") })
                .FirstOrDefaultAsync(cancellationToken);
            if (previous is null || !TryIncrement(previous.Details, signature, message, now, out var nextMessage, out var nextDetails))
            {
                break;
            }
            var updated = await candidates.Where(item => EF.Property<int>(item, "Id") == previous.Id
                    && EF.Property<string>(item, "DetailsJson") == previous.Details)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => EF.Property<string>(item, "Message"), nextMessage)
                    .SetProperty(item => EF.Property<string>(item, "DetailsJson"), nextDetails), cancellationToken);
            if (updated == 0)
            {
                continue;
            }
            // ExecuteUpdate bypasses tracking; don't leave a stale unchanged log in this scope.
            foreach (var entry in db.ChangeTracker.Entries<TLog>().Where(entry => entry.State == EntityState.Unchanged
                && (int)entry.Property("Id").CurrentValue! == previous.Id).ToArray())
            {
                entry.State = EntityState.Detached;
            }
            return true;
        }

        var values = new Dictionary<string, string> { ["failureSignature"] = signature };
        if (details is not null)
        {
            foreach (var item in JsonSerializer.Deserialize<Dictionary<string, string>>(details) ?? [])
            {
                values.TryAdd(item.Key, item.Value);
            }
        }
        values["failureCount"] = "1";
        values["failureFirstAt"] = now.ToString("O", CultureInfo.InvariantCulture);
        values["failureLastAt"] = values["failureFirstAt"];
        setValues(message, JsonSerializer.Serialize(values));
        return false;
    }

    private static bool TryIncrement(string details, string signature, string message, DateTime now,
        out string nextMessage, out string nextDetails)
    {
        nextMessage = message;
        nextDetails = details;
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(details);
            if (values is null || values.GetValueOrDefault("failureSignature") != signature
                || !long.TryParse(values.GetValueOrDefault("failureCount"), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                || count < 1 || count == long.MaxValue
                || !DateTime.TryParse(values.GetValueOrDefault("failureFirstAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var first)
                || !DateTime.TryParse(values.GetValueOrDefault("failureLastAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last))
            {
                return false;
            }
            if (first.Kind != DateTimeKind.Utc || last.Kind != DateTimeKind.Utc
                || first.Date != now.Date || last.Date != now.Date || last < first)
            {
                return false;
            }
            first = first < now ? first : now;
            last = last > now ? last : now;
            values["failureCount"] = (count + 1).ToString(CultureInfo.InvariantCulture);
            values["failureFirstAt"] = first.ToString("O", CultureInfo.InvariantCulture);
            values["failureLastAt"] = last.ToString("O", CultureInfo.InvariantCulture);
            nextMessage = $"{message[..Math.Min(message.Length, 700)]} Повторений за сутки UTC: {count + 1}; первое: {first:HH:mm:ss}, последнее: {last:HH:mm:ss}.";
            nextDetails = JsonSerializer.Serialize(values);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
