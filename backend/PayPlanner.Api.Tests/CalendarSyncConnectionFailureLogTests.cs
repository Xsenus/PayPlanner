using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CalendarSyncConnectionFailureLogTests
{
    private static readonly DateTime Start = new(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedConnectionFailure_PreservesFirstLastCountAcrossSavesAndScopes(bool google)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        await using (var db = new PaymentContext(options, useObjectAuditModel: false))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db);
            // Uncommitted occurrences must be counted as well.
            Assert.False(await RecordAsync(db, google, Start));
            Assert.True(await RecordAsync(db, google, Start.AddSeconds(1)));
            await db.SaveChangesAsync();
        }
        for (var attempt = 2; attempt < 50; attempt++)
        {
            await using var db = new PaymentContext(options, useObjectAuditModel: false);
            Assert.True(await RecordAsync(db, google, Start.AddSeconds(attempt)));
            await db.SaveChangesAsync();
        }
        await using var verify = new PaymentContext(options, useObjectAuditModel: false);
        var row = Assert.Single(await ReadAsync(verify, google));
        var details = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Details!)!;
        Assert.Equal("50", details["failureCount"]);
        Assert.Equal(Start.ToString("O"), details["failureFirstAt"]);
        Assert.Equal(Start.AddSeconds(49).ToString("O"), details["failureLastAt"]);
        Assert.Equal("Synthetic shared failure", details["detail"]);
        Assert.Equal(Start, row.CreatedAt);
        Assert.Contains("Повторений за сутки UTC: 50", row.Message);
    }

    [Theory]
    [InlineData(true, "connection")]
    [InlineData(false, "connection")]
    [InlineData(true, "day")]
    [InlineData(false, "day")]
    [InlineData(true, "operation")]
    [InlineData(false, "operation")]
    [InlineData(true, "direction")]
    [InlineData(false, "direction")]
    [InlineData(true, "message")]
    [InlineData(false, "message")]
    [InlineData(true, "context")]
    [InlineData(false, "context")]
    [InlineData(true, "longDiagnostic")]
    [InlineData(false, "longDiagnostic")]
    public async Task DistinctFailureIdentity_IsNeverMerged(bool google, string change)
    {
        await using var db = await CreateAsync();
        var original = change == "longDiagnostic" ? new string('x', 5000) + "first" : "original";
        Assert.False(await RecordAsync(db, google, Start, originalDetails: original));
        await db.SaveChangesAsync();
        Assert.False(await RecordAsync(db, google, change == "day" ? Start.AddDays(1) : Start.AddMinutes(1),
            connectionId: change == "connection" ? 2 : 1,
            operation: change == "operation" ? "import" : "ensure-calendars",
            direction: change == "direction" ? "provider-to-crm" : "system",
            message: change == "message" ? "Different message" : "Connection preparation failed",
            context: change == "context" ? "Different owner snapshot" : "Owner",
            originalDetails: change == "longDiagnostic" ? new string('x', 5000) + "second" : original));
        await db.SaveChangesAsync();
        Assert.Equal(2, (await ReadAsync(db, google)).Count);
        Assert.All(await ReadAsync(db, google), row => Assert.Equal("1",
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.Details!)!["failureCount"]));
    }

    [Theory]
    [InlineData(true, "success")]
    [InlineData(false, "success")]
    [InlineData(true, "task")]
    [InlineData(false, "task")]
    [InlineData(true, "calendar")]
    [InlineData(false, "calendar")]
    [InlineData(true, "unknown")]
    [InlineData(false, "unknown")]
    public async Task NonConnectionFailure_IsLeftUntouched(bool google, string scenario)
    {
        await using var db = await CreateAsync();
        var log = new GoogleCalendarSyncLog
        {
            ConnectionId = scenario == "unknown" ? null : 1,
            TaskJournalItemId = scenario == "task" ? 99 : null,
            CalendarSyncCalendarId = scenario == "calendar" ? 99 : null,
            Status = scenario == "success" ? "success" : "error",
            Direction = "system",
            Operation = "ensure-calendars",
            Message = "Original",
            DetailsJson = "{\"detail\":\"Exact original\"}",
            CreatedAt = Start,
        };
        if (google)
        {
            Assert.False(await CalendarSyncConnectionFailureLog.TryAggregateAsync(db, log, "Exact original", CancellationToken.None));
            Assert.Equal("{\"detail\":\"Exact original\"}", log.DetailsJson);
        }
        else
        {
            var yandex = new YandexCalendarSyncLog
            {
                ConnectionId = log.ConnectionId,
                TaskJournalItemId = log.TaskJournalItemId,
                CalendarSyncCalendarId = log.CalendarSyncCalendarId,
                Status = log.Status,
                Direction = log.Direction,
                Operation = log.Operation,
                Message = log.Message,
                DetailsJson = log.DetailsJson,
                CreatedAt = Start,
            };
            Assert.False(await CalendarSyncConnectionFailureLog.TryAggregateAsync(db, yandex, "Exact original", CancellationToken.None));
            Assert.Equal(log.DetailsJson, yandex.DetailsJson);
        }
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State == EntityState.Modified);
    }

    [Theory]
    [InlineData(true, "malformed")]
    [InlineData(false, "malformed")]
    [InlineData(true, "overflow")]
    [InlineData(false, "overflow")]
    [InlineData(true, "legacy")]
    [InlineData(false, "legacy")]
    public async Task InvalidOrHistoricalSummary_DoesNotLoseNewFailure(bool google, string scenario)
    {
        await using var db = await CreateAsync();
        await RecordAsync(db, google, Start);
        await db.SaveChangesAsync();
        var first = Assert.Single(await ReadAsync(db, google));
        var stored = first.Details!;
        if (scenario == "malformed") stored = stored[..^1];
        if (scenario == "overflow") stored = stored.Replace("\"failureCount\":\"1\"", "\"failureCount\":\"9223372036854775807\"");
        if (scenario == "legacy") stored = "{\"detail\":\"Historical diagnostic\"}";
        if (google) await db.GoogleCalendarSyncLogs.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DetailsJson, stored));
        else await db.YandexCalendarSyncLogs.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DetailsJson, stored));
        db.ChangeTracker.Clear();
        Assert.False(await RecordAsync(db, google, Start.AddMinutes(1)));
        await db.SaveChangesAsync();
        var rows = await ReadAsync(db, google);
        Assert.Equal(2, rows.Count);
        Assert.Equal(stored, rows[0].Details);
        Assert.Equal("1", JsonSerializer.Deserialize<Dictionary<string, string>>(rows[1].Details!)!["failureCount"]);
    }

    [Fact]
    public async Task ProvidersAreIsolatedAndCancellationDoesNotWrite()
    {
        await using var db = await CreateAsync();
        Assert.False(await RecordAsync(db, true, Start));
        Assert.False(await RecordAsync(db, false, Start));
        await db.SaveChangesAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CalendarSyncConnectionFailureLog.TryAggregateAsync(db,
            new GoogleCalendarSyncLog { ConnectionId = 1, Status = "error" }, null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CalendarSyncConnectionFailureLog.TryAggregateAsync(db,
            new YandexCalendarSyncLog { ConnectionId = 1, Status = "error" }, null, cancelled.Token));
        Assert.Single(await ReadAsync(db, true));
        Assert.Single(await ReadAsync(db, false));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task RecurrenceAfterRecovery_StartsNewSummary(bool google, bool savedRecovery)
    {
        await using var db = await CreateAsync();
        await RecordAsync(db, google, Start);
        await db.SaveChangesAsync();
        await RecordAsync(db, google, Start.AddMinutes(1));
        await db.SaveChangesAsync();
        if (google) db.GoogleCalendarSyncLogs.Add(new GoogleCalendarSyncLog { ConnectionId = 1, Operation = "connection-recovered", Status = "success", Message = "Recovered", CreatedAt = Start.AddMinutes(2) });
        else db.YandexCalendarSyncLogs.Add(new YandexCalendarSyncLog { ConnectionId = 1, Operation = "connection-recovered", Status = "success", Message = "Recovered", CreatedAt = Start.AddMinutes(2) });
        if (savedRecovery) await db.SaveChangesAsync();
        Assert.False(await RecordAsync(db, google, Start.AddMinutes(3)));
        await db.SaveChangesAsync();
        var rows = await ReadAsync(db, google);
        Assert.Equal(3, rows.Count);
        Assert.Equal("2", JsonSerializer.Deserialize<Dictionary<string, string>>(rows[0].Details!)!["failureCount"]);
        Assert.Equal("Recovered", rows[1].Message);
        Assert.Equal("1", JsonSerializer.Deserialize<Dictionary<string, string>>(rows[2].Details!)!["failureCount"]);
    }

    internal static async Task<bool> RecordAsync(PaymentContext db, bool google, DateTime at, int connectionId = 1,
        string operation = "ensure-calendars", string direction = "system", string message = "Connection preparation failed",
        string context = "Owner", string? originalDetails = null)
    {
        var details = JsonSerializer.Serialize(new { detail = "Synthetic shared failure", userName = context });
        if (google)
        {
            var log = new GoogleCalendarSyncLog { ConnectionId = connectionId, Operation = operation, Direction = direction, Status = "error", Message = message, DetailsJson = details, CreatedAt = at };
            var aggregated = await CalendarSyncConnectionFailureLog.TryAggregateAsync(db, log, originalDetails, CancellationToken.None);
            if (!aggregated) db.GoogleCalendarSyncLogs.Add(log);
            return aggregated;
        }
        else
        {
            var log = new YandexCalendarSyncLog { ConnectionId = connectionId, Operation = operation, Direction = direction, Status = "error", Message = message, DetailsJson = details, CreatedAt = at };
            var aggregated = await CalendarSyncConnectionFailureLog.TryAggregateAsync(db, log, originalDetails, CancellationToken.None);
            if (!aggregated) db.YandexCalendarSyncLogs.Add(log);
            return aggregated;
        }
    }

    internal static async Task SeedAsync(PaymentContext db)
    {
        for (var i = 1; i <= 2; i++)
        {
            var user = new User { FullName = "Synthetic owner", Email = $"calendar-log-{i}@example.local", PasswordHash = "hash", Role = new Role { Name = $"log-owner-{i}" } };
            db.Users.Add(user);
            db.GoogleCalendarConnections.Add(new GoogleCalendarConnection { Id = i, User = user });
            db.YandexCalendarConnections.Add(new YandexCalendarConnection { Id = i, User = user });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<PaymentContext> CreateAsync()
    {
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite("DataSource=:memory:").Options, useObjectAuditModel: false);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);
        return db;
    }

    internal static async Task<List<Row>> ReadAsync(PaymentContext db, bool google)
        => google
            ? await db.GoogleCalendarSyncLogs.AsNoTracking().OrderBy(log => log.Id).Select(log => new Row(log.Message, log.DetailsJson, log.CreatedAt)).ToListAsync()
            : await db.YandexCalendarSyncLogs.AsNoTracking().OrderBy(log => log.Id).Select(log => new Row(log.Message, log.DetailsJson, log.CreatedAt)).ToListAsync();

    internal sealed record Row(string Message, string? Details, DateTime CreatedAt);
}
