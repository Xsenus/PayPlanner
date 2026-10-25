using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CalendarSyncConnectionFailurePostgreSqlTests(PostgreSqlMigratedTemplate databases) : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentScopes_PreserveEveryOccurrenceAndNewestTimestamp(bool google)
    {
        await using var lease = await databases.CreateDatabaseAsync("pp_calendar_failure_");
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        var start = new DateTime(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
        await using (var db = new PostgreSqlPaymentContext(options, useObjectAuditModel: false))
        {
            await CalendarSyncConnectionFailureLogTests.SeedAsync(db);
            await CalendarSyncConnectionFailureLogTests.RecordAsync(db, google, start);
            await db.SaveChangesAsync();
        }
        await Task.WhenAll(Enumerable.Range(1, 16).Select(async index =>
        {
            await using var db = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
            await CalendarSyncConnectionFailureLogTests.RecordAsync(db, google, start.AddSeconds(index));
            await db.SaveChangesAsync();
        }));
        await using var verify = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var rows = await CalendarSyncConnectionFailureLogTests.ReadAsync(verify, google);
        var details = rows.Select(row => JsonSerializer.Deserialize<Dictionary<string, string>>(row.Details!)!).ToArray();
        // Under repeated CAS contention, an additional summary is preferable to losing an occurrence.
        Assert.Equal(17, details.Sum(row => long.Parse(row["failureCount"])));
        Assert.Equal(start, details.Min(row => DateTime.Parse(row["failureFirstAt"]).ToUniversalTime()));
        Assert.Equal(start.AddSeconds(16), details.Max(row => DateTime.Parse(row["failureLastAt"]).ToUniversalTime()));
        Assert.Empty(await verify.TaskJournalItems.ToListAsync());
        Assert.Empty(await verify.GoogleCalendarTaskLinks.ToListAsync());
        Assert.Empty(await verify.YandexCalendarTaskLinks.ToListAsync());
    }
}
