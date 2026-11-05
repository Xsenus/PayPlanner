using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtNotificationChannelIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyCourtCurrent_SummaryShowsCurrentCaseAndDeadlineStatuses(bool closed, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var old = await SeedLegacyDeadlineDeliveryAsync(db, seed);
        if (closed)
        {
            var clientCase = await db.ClientCases.SingleAsync(row => row.Id == seed.Case.Id);
            var deadline = await db.ClientCaseProceduralDeadlines.SingleAsync(row => row.Id == seed.Deadline.Id);
            clientCase.Status = ClientCaseStatus.Closed; deadline.Status = "closed";
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        }
        var domain = await ReminderDomainAsync(db);
        var realtime = new ReminderAdmissionRealtime();
        Assert.Empty(await ReminderService(db, realtime).RetryDeliveryAsync(old.Id));
        var fresh = Assert.Single(realtime.Delivered);
        Assert.Contains(closed ? "Статус дела: закрыто." : "Статус дела: открыто.", fresh.BodyPreview);
        Assert.Contains(closed ? "Состояние: закрыт." : "Состояние: активен.", fresh.BodyPreview);
        Assert.Equal(domain, await ReminderDomainAsync(db));
    }

    public static IEnumerable<object[]> CurrentCourtDeniedCases() =>
        from mode in new[] { "approved", "inactive", "access", "personal", "read", "pending", "key-user", "bad-key", "max-pending", "max-timeout" }
        from postgres in new[] { false, true }
        select new object[] { mode, postgres };

    [Theory]
    [MemberData(nameof(CurrentCourtDeniedCases))]
    public async Task LegacyCourtCurrent_AdmissionHonorsCurrentAccessPreferencesHistoryAndUnknownOutcomes(string mode, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var old = await SeedLegacyDeadlineDeliveryAsync(db, seed);
        if (mode == "approved") await db.Users.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (mode == "inactive") await db.Users.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (mode is "access" or "personal") await ChangeReminderAsync(db, seed, mode);
        if (mode == "read") await db.UserNotifications.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsRead, true));
        if (mode == "pending")
        {
            db.NotificationDeliveries.Add(new()
            {
                RecipientUserId = seed.UserId,
                EventKey = old.EventKey,
                Channel = NotificationChannels.Email,
                Status = NotificationDeliveryStatus.Pending,
                RecipientAddress = "unknown@synthetic.local"
            });
            await db.SaveChangesAsync();
        }
        if (mode is "max-pending" or "max-timeout")
        {
            db.MaxRecipientLinks.Add(new()
            {
                UserId = seed.UserId,
                MaxUserId = 79001,
                Status = MaxRecipientLinkStatuses.Verified,
                LinkTokenHash = Guid.NewGuid().ToString("N"),
                VerifiedAt = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
            var max = await new MaxOutboxService(db, new CourtMaxSettings(), new EphemeralDataProtectionProvider(), TimeProvider.System)
                .QueueAsync(new(seed.UserId, MaxMessengerScenarios.Notifications, old.EventKey, "Синтетическое уведомление",
                    ActionUrl: "https://crm.synthetic.local/", NotificationType: UserNotificationTypes.CaseProceduralDeadline), default);
            max.Status = mode == "max-pending" ? MaxOutboxStatuses.Processing : MaxOutboxStatuses.Failed;
            max.LastErrorCode = mode == "max-timeout" ? "provider-timeout" : null;
            await db.SaveChangesAsync();
        }
        if (mode is "key-user" or "bad-key")
        {
            var key = mode == "key-user" ? old.EventKey[..old.EventKey.LastIndexOf(':')] + ":999999" : "case:01:procedural-deadline:1:updated:123:1";
            await db.NotificationDeliveries.Where(row => row.Id == old.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.EventKey, key));
        }
        db.ChangeTracker.Clear();
        var domain = await ReminderDomainAsync(db);
        var history = JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().ToArrayAsync());
        var deliveries = JsonSerializer.Serialize(await db.NotificationDeliveries.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        var realtime = new ReminderAdmissionRealtime();
        Assert.Empty(await ReminderService(db, realtime).RetryDeliveryAsync(old.Id));
        Assert.Empty(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "current-state" || row.Kind == "legacy-origin").ToArrayAsync());
        Assert.Empty(realtime.Delivered);
        Assert.Equal(domain, await ReminderDomainAsync(db));
        Assert.Equal(history, JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().ToArrayAsync()));
        Assert.Equal(deliveries, JsonSerializer.Serialize(await db.NotificationDeliveries.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
    }

    public static IEnumerable<object[]> CurrentCourtLateCases() =>
        from mode in new[] { "source", "access", "personal", "timezone", "metadata", "aba" }
        from postgres in new[] { false, true }
        select new object[] { mode, postgres };

    [Theory]
    [MemberData(nameof(CurrentCourtLateCases))]
    public async Task LegacyCourtCurrent_RecoveryRechecksAcceptedSourceVersionAndRecipient(string mode, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var old = await SeedLegacyDeadlineDeliveryAsync(db, seed);
        var realtime = new ReminderAdmissionRealtime { Fail = true };
        var service = ReminderService(db, realtime);
        Assert.Empty(await service.RetryDeliveryAsync(old.Id));
        var current = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "current-state");
        Assert.Null(current.CompletedAt);
        Assert.Equal(2, await db.UserNotifications.CountAsync());
        if (mode is "source" or "access" or "personal") await ChangeReminderAsync(db, seed, mode);
        if (mode == "timezone")
        {
            db.UserSettings.Add(new() { UserId = seed.UserId, Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey, Value = "Pacific/Kiritimati" });
            await db.SaveChangesAsync();
        }
        if (mode == "metadata")
        {
            var deadline = await db.ClientCaseProceduralDeadlines.SingleAsync(row => row.Id == seed.Deadline.Id);
            deadline.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        if (mode == "aba")
        {
            foreach (var date in new[] { seed.Deadline.DueAt.AddDays(1), seed.Deadline.DueAt })
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                var deadline = await db.ClientCaseProceduralDeadlines.SingleAsync(row => row.Id == seed.Deadline.Id);
                deadline.DueAt = date; await db.SaveChangesAsync();
                await new CaseCourtNotificationQueue(db).EnqueueBatchAsync(seed.Case, 0, "CRM", [new("deadline", "updated", null, deadline, false)], default);
                await transaction.CommitAsync(); db.ChangeTracker.Clear();
            }
        }
        db.ChangeTracker.Clear(); realtime.Fail = false;
        var domain = await ReminderDomainAsync(db);
        var history = JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == current.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, realtime).ProcessAsync(current.Id, default));
        Assert.Equal(mode == "metadata" ? 1 : 0, realtime.Delivered.Count);
        Assert.Equal(domain, await ReminderDomainAsync(db));
        Assert.Equal(history, JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCourtCurrent_MovedSourceDoesNotExposeAnotherCase(bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var old = await SeedLegacyDeadlineDeliveryAsync(db, seed);
        var other = new ClientCase { ClientId = seed.Case.ClientId, Title = "SECRET-OTHER-CASE", Stage = "Суд" };
        db.ClientCases.Add(other); await db.SaveChangesAsync();
        await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.Deadline.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.ClientCaseId, other.Id).SetProperty(row => row.Title, "SECRET-OTHER-SOURCE"));
        db.ChangeTracker.Clear(); var domain = await ReminderDomainAsync(db);
        var realtime = new ReminderAdmissionRealtime();
        Assert.Empty(await ReminderService(db, realtime).RetryDeliveryAsync(old.Id));
        var fresh = Assert.Single(realtime.Delivered);
        Assert.Contains("больше недоступна в этом деле", fresh.BodyPreview);
        Assert.DoesNotContain("SECRET", fresh.BodyPreview);
        Assert.Equal(seed.Case.Id, fresh.ObjectId);
        Assert.Equal(domain, await ReminderDomainAsync(db));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task LegacyCourtCurrent_AdoptionFailureRollsBackBothIntentAndOrigin(bool after, bool cancel, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        var fault = new CurrentCourtAdoptionFault(after, cancel);
        await using PaymentContext db = await ReminderContextAsync(database?.ConnectionString, fault);
        var seed = await SeedReminderAsync(db);
        var old = await SeedLegacyDeadlineDeliveryAsync(db, seed);
        var domain = await ReminderDomainAsync(db);
        var history = JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().ToArrayAsync());
        var realtime = new ReminderAdmissionRealtime();
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = () => cancellation.Cancel(); fault.Armed = true;
        var error = await Record.ExceptionAsync(() => ReminderService(db, realtime).RetryDeliveryAsync(old.Id, cancellation.Token));
        Assert.NotNull(error); Assert.True(fault.Called);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "current-state" || row.Kind == "legacy-origin").ToArrayAsync());
        Assert.Empty(realtime.Delivered);
        Assert.Equal(domain, await ReminderDomainAsync(db));
        Assert.Equal(history, JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().ToArrayAsync()));
    }

    private static async Task<NotificationDelivery> SeedLegacyDeadlineDeliveryAsync(PaymentContext db, ReminderSeed seed)
    {
        var key = $"case:{seed.Case.Id}:procedural-deadline:{seed.Deadline.Id}:updated:{DateTime.UtcNow.Ticks}:{seed.UserId}";
        db.UserNotifications.Add(new()
        {
            RecipientUserId = seed.UserId,
            Type = UserNotificationTypes.CaseProceduralDeadline,
            EventKey = key,
            Title = "LEGACY-OLD",
            BodyPreview = "LEGACY-OLD",
            ObjectType = "case",
            ObjectId = seed.Case.Id
        });
        var delivery = new NotificationDelivery
        {
            RecipientUserId = seed.UserId,
            EventKey = key,
            Channel = NotificationChannels.WebPush,
            Status = NotificationDeliveryStatus.Failed,
            RecipientAddress = "https://push.synthetic.local/legacy-current",
            Subject = "LEGACY-OLD",
            BodyPreview = "LEGACY-OLD"
        };
        db.NotificationDeliveries.Add(delivery); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); return delivery;
    }

    private sealed class CurrentCourtAdoptionFault(bool after, bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public Action? Cancel { get; set; }
        private bool trigger;
        private void Fail(CancellationToken ct)
        {
            Called = true;
            if (cancel) { Cancel!(); throw new OperationCanceledException(ct); }
            throw new IOException("Synthetic current court adoption failure");
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            trigger = Armed && eventData.Context!.ChangeTracker.Entries<CaseCourtNotificationOutbox>().Any(row => row.State == EntityState.Added && row.Entity.Kind == "legacy-origin");
            if (trigger && !after) Fail(cancellationToken);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (trigger && after) { trigger = false; Fail(cancellationToken); }
            return ValueTask.FromResult(result);
        }
    }
}
