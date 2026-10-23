using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class GoogleCalendarSyncServiceTests
{
    [Fact]
    public async Task ConnectionFailure_DoesNotStarveHealthyConnectionInSingleItemBatch()
    {
        await using var db = await CreateContextAsync();
        var failedUser = await CreateUserAsync(db, "google-broken-queue@example.local", "user");
        var healthyUser = await CreateUserAsync(db, "google-healthy-queue@example.local", "user");
        await EnableGoogleSettingsAsync(db);
        var failed = await CreateConnectionAsync(db, failedUser.Id);
        var healthy = await CreateConnectionAsync(db, healthyUser.Id);
        failed.LastIncrementalSyncAt = DateTime.UtcNow.AddDays(-3);
        failed.AccessTokenProtected = "Invalid synthetic token";
        failed.AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1);
        healthy.LastIncrementalSyncAt = DateTime.UtcNow.AddDays(-2);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler()));
        Assert.Equal(1, (await service.SyncDueConnectionsAsync(1, CancellationToken.None)).ErrorCount);
        Assert.Equal(0, (await service.SyncDueConnectionsAsync(1, CancellationToken.None)).ErrorCount);
        Assert.NotNull((await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(row => row.Id == healthy.Id)).LastFullSyncAt);
        Assert.Null((await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(row => row.Id == failed.Id)).LastFullSyncAt);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("network")]
    [InlineData("rejected")]
    public async Task ConnectionFailure_StopsBefore502TasksAndAggregatesAcrossServiceInstances(string failure)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "failed-google@example.local", "user");
        await EnableGoogleSettingsAsync(db);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 0);
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "preserved-calendar", "Preserved calendar");
        var previousSync = DateTime.UtcNow.AddDays(-2);
        connection.LastFullSyncAt = previousSync;
        connection.LastIncrementalSyncAt = previousSync;
        if (failure == "key")
        {
            connection.AccessTokenProtected = "Invalid synthetic protected token";
            connection.AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1);
        }
        await CalendarConnectionFailureProbe.SeedTasksAsync(db, user.Id);
        db.GoogleCalendarTaskLinks.Add(new GoogleCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = await db.TaskJournalItems.MinAsync(task => task.Id),
            GoogleCalendarId = calendar.GoogleCalendarId,
            GoogleEventId = "preserved-event",
            LastCrmHash = "preserved-hash",
            SyncStatus = "active",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await CalendarConnectionFailureProbe.SnapshotAsync(db, google: true);
        var handler = new CalendarConnectionFailureProbe(failure);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var service = CreateService(db, new FakeHttpClientFactory(handler));
            if (attempt == 0)
            {
                var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);
                Assert.Equal(1, result.ErrorCount);
                Assert.Equal(0, result.ProcessedTasks);
                Assert.Equal(0, result.ImportedEvents);
            }
            else
            {
                var result = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
                Assert.Equal(1, result.ErrorCount);
                Assert.Equal(0, result.ProcessedTasks);
                Assert.Equal(0, result.ImportedEvents);
            }
            db.ChangeTracker.Clear();
        }
        Assert.Equal(before, await CalendarConnectionFailureProbe.SnapshotAsync(db, google: true));
        var stored = await db.GoogleCalendarConnections.AsNoTracking().SingleAsync();
        Assert.Equal(previousSync, stored.LastFullSyncAt);
        Assert.Equal(previousSync, stored.LastIncrementalSyncAt);
        Assert.False(string.IsNullOrWhiteSpace(stored.LastError));
        var log = await db.GoogleCalendarSyncLogs.AsNoTracking().SingleAsync();
        Assert.Equal("ensure-calendars", log.Operation);
        Assert.Null(log.TaskJournalItemId);
        Assert.Equal("3", JsonSerializer.Deserialize<Dictionary<string, string>>(log.DetailsJson!)!["failureCount"]);
        Assert.Equal(failure == "key" ? 0 : 3, handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionFailure_RecoveryAndCancellationPreserveDiagnostics(bool cancelled)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "google-recovery@example.local", "user");
        await EnableGoogleSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        using var cancellation = new CancellationTokenSource();
        var handler = new CalendarConnectionFailureProbe("network", cancelled ? cancellation : null);
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SyncDueConnectionsAsync(10, cancellation.Token));
            Assert.Empty(await db.GoogleCalendarSyncLogs.ToListAsync());
            Assert.Null((await db.GoogleCalendarConnections.AsNoTracking().SingleAsync()).LastError);
            return;
        }
        Assert.Equal(1, (await service.SyncDueConnectionsAsync(10, CancellationToken.None)).ErrorCount);
        db.ChangeTracker.Clear();
        service = CreateService(db, new FakeHttpClientFactory(new RecordingGoogleHandler()));
        Assert.Equal(0, (await service.SyncDueConnectionsAsync(10, CancellationToken.None)).ErrorCount);
        Assert.Equal(0, (await service.SyncDueConnectionsAsync(10, CancellationToken.None)).ErrorCount);
        Assert.Single(await db.GoogleCalendarSyncLogs.Where(log => log.Operation == "connection-recovered").ToListAsync());
        Assert.Single(await db.GoogleCalendarSyncLogs.Where(log => log.Status == "error").ToListAsync());
        Assert.Null((await db.GoogleCalendarConnections.AsNoTracking().SingleAsync()).LastError);
        Assert.Empty(await db.GoogleCalendarTaskLinks.ToListAsync());
    }
}

public partial class YandexCalendarSyncServiceTests
{
    private static async Task EnableYandexSettingsAsync(PaymentContext db)
    {
        db.SystemSettings.AddRange(
            new SystemSetting { Key = "auth.yandex.enabled", Value = "true" },
            new SystemSetting { Key = "auth.yandex.clientId", Value = "synthetic-client" },
            new SystemSetting { Key = "auth.yandex.clientSecret", Value = "synthetic-secret" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ConnectionFailure_DoesNotStarveHealthyConnectionInSingleItemBatch()
    {
        await using var db = await CreateContextAsync();
        var failedUser = await CreateUserAsync(db, "yandex-broken-queue@example.local", "user");
        var healthyUser = await CreateUserAsync(db, "yandex-healthy-queue@example.local", "user");
        await EnableYandexSettingsAsync(db);
        var failed = await CreateConnectionAsync(db, failedUser.Id);
        var healthy = await CreateConnectionAsync(db, healthyUser.Id);
        failed.LastIncrementalSyncAt = DateTime.UtcNow.AddDays(-3);
        failed.AccessTokenProtected = "Invalid synthetic token";
        healthy.LastIncrementalSyncAt = DateTime.UtcNow.AddDays(-2);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeHttpClientFactory(new RecordingYandexHandler()));
        Assert.Equal(1, (await service.SyncDueConnectionsAsync(1, CancellationToken.None)).ErrorCount);
        Assert.Equal(0, (await service.SyncDueConnectionsAsync(1, CancellationToken.None)).ErrorCount);
        Assert.NotNull((await db.YandexCalendarConnections.AsNoTracking().SingleAsync(row => row.Id == healthy.Id)).LastFullSyncAt);
        Assert.Null((await db.YandexCalendarConnections.AsNoTracking().SingleAsync(row => row.Id == failed.Id)).LastFullSyncAt);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("network")]
    [InlineData("rejected")]
    public async Task ConnectionFailure_StopsBefore502TasksAndAggregatesAcrossServiceInstances(string failure)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "failed-yandex@example.local", "user");
        await EnableYandexSettingsAsync(db);
        await SetCalendarSyncSettingsAsync(db, manualFullSyncCooldownMinutes: 0);
        var connection = await CreateConnectionAsync(db, user.Id);
        var calendar = await CreateCalendarAsync(db, connection.Id, "preserved-calendar", "Preserved calendar");
        var previousSync = DateTime.UtcNow.AddDays(-2);
        connection.LastFullSyncAt = previousSync;
        connection.LastIncrementalSyncAt = previousSync;
        if (failure == "key") connection.AccessTokenProtected = "Invalid synthetic protected token";
        else connection.AccessTokenExpiresAt = DateTime.UtcNow.AddHours(-1);
        await CalendarConnectionFailureProbe.SeedTasksAsync(db, user.Id);
        db.YandexCalendarTaskLinks.Add(new YandexCalendarTaskLink
        {
            ConnectionId = connection.Id,
            CalendarSyncCalendarId = calendar.Id,
            TaskJournalItemId = await db.TaskJournalItems.MinAsync(task => task.Id),
            YandexCalendarId = calendar.YandexCalendarId,
            YandexEventId = "preserved-event",
            YandexEventUrl = calendar.CalendarUrl + "preserved-event.ics",
            LastCrmHash = "preserved-hash",
            SyncStatus = "active",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await CalendarConnectionFailureProbe.SnapshotAsync(db, google: false);
        var handler = new CalendarConnectionFailureProbe(failure);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var service = CreateService(db, new FakeHttpClientFactory(handler));
            if (attempt == 0)
            {
                var result = await service.SyncNowAsync(user.Id, CreateHttpRequest(user.Id), CancellationToken.None);
                Assert.Equal(1, result.ErrorCount);
                Assert.Equal(0, result.ProcessedTasks);
                Assert.Equal(0, result.ImportedEvents);
            }
            else
            {
                var result = await service.SyncDueConnectionsAsync(10, CancellationToken.None);
                Assert.Equal(1, result.ErrorCount);
                Assert.Equal(0, result.ProcessedTasks);
                Assert.Equal(0, result.ImportedEvents);
            }
            db.ChangeTracker.Clear();
        }
        Assert.Equal(before, await CalendarConnectionFailureProbe.SnapshotAsync(db, google: false));
        var stored = await db.YandexCalendarConnections.AsNoTracking().SingleAsync();
        Assert.Equal(previousSync, stored.LastFullSyncAt);
        Assert.Equal(previousSync, stored.LastIncrementalSyncAt);
        Assert.False(string.IsNullOrWhiteSpace(stored.LastError));
        var log = await db.YandexCalendarSyncLogs.AsNoTracking().SingleAsync();
        Assert.Equal("ensure-calendars", log.Operation);
        Assert.Null(log.TaskJournalItemId);
        Assert.Equal("3", JsonSerializer.Deserialize<Dictionary<string, string>>(log.DetailsJson!)!["failureCount"]);
        Assert.Equal(failure == "key" ? 0 : 3, handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionFailure_RecoveryAndCancellationPreserveDiagnostics(bool cancelled)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "yandex-recovery@example.local", "user");
        await EnableYandexSettingsAsync(db);
        var connection = await CreateConnectionAsync(db, user.Id);
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var handler = new CalendarConnectionFailureProbe("network", cancelled ? cancellation : null);
        var service = CreateService(db, new FakeHttpClientFactory(handler));
        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SyncDueConnectionsAsync(10, cancellation.Token));
            Assert.Empty(await db.YandexCalendarSyncLogs.ToListAsync());
            Assert.Null((await db.YandexCalendarConnections.AsNoTracking().SingleAsync()).LastError);
            return;
        }
        Assert.Equal(1, (await service.SyncDueConnectionsAsync(10, CancellationToken.None)).ErrorCount);
        db.ChangeTracker.Clear();
        var recoveryHandler = new TokenRecoveryYandexHandler();
        service = CreateService(db, new FakeHttpClientFactory(recoveryHandler));
        Assert.Equal(0, (await service.SyncDueConnectionsAsync(10, CancellationToken.None)).ErrorCount);
        Assert.Equal(0, (await service.SyncDueConnectionsAsync(10, CancellationToken.None)).ErrorCount);
        Assert.Single(await db.YandexCalendarSyncLogs.Where(log => log.Operation == "connection-recovered").ToListAsync());
        Assert.Single(await db.YandexCalendarSyncLogs.Where(log => log.Status == "error").ToListAsync());
        Assert.Null((await db.YandexCalendarConnections.AsNoTracking().SingleAsync()).LastError);
        Assert.Empty(await db.YandexCalendarTaskLinks.ToListAsync());
        Assert.Equal(1, recoveryHandler.RefreshRequests);
    }

    private sealed class TokenRecoveryYandexHandler() : DelegatingHandler(new RecordingYandexHandler())
    {
        public int RefreshRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                RefreshRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"synthetic-recovered-token\",\"expires_in\":3600}"),
                });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}

internal sealed class CalendarConnectionFailureProbe(string failure, CancellationTokenSource? cancellation = null) : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        Assert.EndsWith("/token", request.RequestUri!.AbsolutePath);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (failure == "network") throw new HttpRequestException("Synthetic unavailable token endpoint");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"Synthetic expired authorization\"}"),
        });
    }

    internal static async Task SeedTasksAsync(PaymentContext db, int userId)
    {
        db.TaskJournalItems.AddRange(Enumerable.Range(1, 502).Select(i => new TaskJournalItem
        {
            ObjectName = "Synthetic preserved object",
            Title = $"Preserved task {i}",
            AssigneeUserId = userId,
            AssigneeName = "User",
            DueAt = DateTime.UtcNow.Date.AddDays(7),
            PlannedDurationMinutes = 60,
            Status = "new",
            Priority = "normal",
        }));
        await db.SaveChangesAsync();
    }

    internal static async Task<string> SnapshotAsync(PaymentContext db, bool google)
    {
        var tasks = await db.TaskJournalItems.AsNoTracking().OrderBy(task => task.Id).ToListAsync();
        object links = google
            ? await db.GoogleCalendarTaskLinks.AsNoTracking().OrderBy(link => link.Id).ToListAsync()
            : await db.YandexCalendarTaskLinks.AsNoTracking().OrderBy(link => link.Id).ToListAsync();
        return JsonSerializer.Serialize(new { tasks, links });
    }
}
