using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtNotificationChannelIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCourtScheduling_IdleRunAndCountDoNotWriteRecoveryState(bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        await SeedReminderAsync(db);
        var settings = JsonSerializer.Serialize(await db.SystemSettings.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        var domain = await ReminderDomainAsync(db);
        var realtime = new ReminderAdmissionRealtime();
        var services = new ServiceCollection();
        services.AddScoped<PaymentContext>(_ => postgres ? OpenPostgres(database!.ConnectionString)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options));
        services.AddScoped<INotificationService>(provider => ReminderService(provider.GetRequiredService<PaymentContext>(), realtime));
        using var provider = services.BuildServiceProvider();
        var runner = new NotificationRetrySchedulerService(provider.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(),
            NullLogger<NotificationRetrySchedulerService>.Instance);
        Assert.Equal(0, await runner.CountEligibleRetriesAsync());
        await runner.RunOnceAsync();
        Assert.Equal(settings, JsonSerializer.Serialize(await db.SystemSettings.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
        Assert.Equal(domain, await ReminderDomainAsync(db));
        Assert.Empty(realtime.Delivered);
        Assert.Empty(await db.UserNotifications.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCourtScheduling_BoundedCursorPassesUnresolvableRowsWithoutChangingHistory(bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        for (var i = 0; i < 12; i++) db.NotificationDeliveries.Add(new()
        {
            RecipientUserId = seed.UserId,
            EventKey = $"case:01:procedural-deadline:1:updated:{i + 1}:{seed.UserId}",
            Channel = NotificationChannels.WebPush,
            Status = NotificationDeliveryStatus.Failed,
            RecipientAddress = "https://push.synthetic.local/invalid-legacy",
            CreatedAt = DateTime.UtcNow.AddHours(-2)
        });
        await db.SaveChangesAsync();
        var old = await SeedLegacyDeadlineDeliveryAsync(db, seed);
        var valid = await db.NotificationDeliveries.SingleAsync(row => row.Id == old.Id);
        valid.CreatedAt = DateTime.UtcNow.AddHours(-2);
        db.SystemSettings.AddRange(new SystemSetting { Key = NotificationRetrySchedulerService.EnabledSettingKey, Value = "true" },
            new SystemSetting { Key = NotificationRetrySchedulerService.DelayMinutesSettingKey, Value = "1" },
            new SystemSetting { Key = NotificationRetrySchedulerService.BatchSizeSettingKey, Value = "1" },
            new SystemSetting { Key = NotificationRetrySchedulerService.MaxAttemptsSettingKey, Value = "3" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var domain = await ReminderDomainAsync(db);
        var history = JsonSerializer.Serialize(await db.NotificationDeliveries.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        var realtime = new ReminderAdmissionRealtime();
        var services = new ServiceCollection();
        services.AddScoped<PaymentContext>(_ => postgres ? OpenPostgres(database!.ConnectionString)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options));
        services.AddScoped<INotificationService>(provider => ReminderService(provider.GetRequiredService<PaymentContext>(), realtime));
        using var provider = services.BuildServiceProvider();
        var runner = new NotificationRetrySchedulerService(provider.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(),
            NullLogger<NotificationRetrySchedulerService>.Instance);
        await runner.CountEligibleRetriesAsync();
        Assert.False(await db.SystemSettings.AnyAsync(row => row.Key == "notifications.courtLegacyRecovery.cursor"));
        for (var i = 0; i < 14; i++) await runner.RunOnceAsync();
        Assert.Single(realtime.Delivered);
        Assert.Single(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "current-state").ToArrayAsync());
        Assert.Single(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "legacy-origin").ToArrayAsync());
        Assert.Equal(2, await db.UserNotifications.CountAsync());
        Assert.Equal(domain, await ReminderDomainAsync(db));
        Assert.Equal(history, JsonSerializer.Serialize(await db.NotificationDeliveries.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCourtScheduling_ConcurrentOriginsKeepOneFreshNotice(bool separateOrigin)
    {
        await using var database = await SecondaryTemplate.CreateDatabaseAsync();
        ReminderSeed seed;
        NotificationDelivery old;
        int secondId;
        string domain;
        await using (var setup = OpenPostgres(database.ConnectionString))
        {
            seed = await SeedReminderAsync(setup);
            old = await SeedLegacyDeadlineDeliveryAsync(setup, seed);
            secondId = old.Id;
            if (separateOrigin)
            {
                var second = new NotificationDelivery
                {
                    RecipientUserId = seed.UserId,
                    EventKey = old.EventKey,
                    Channel = NotificationChannels.WebPush,
                    Status = NotificationDeliveryStatus.Failed,
                    RecipientAddress = "https://push.synthetic.local/second-legacy-origin"
                };
                setup.NotificationDeliveries.Add(second); await setup.SaveChangesAsync(); secondId = second.Id;
            }
            domain = await ReminderDomainAsync(setup);
        }
        var gate = new CurrentCourtAdoptionGate();
        var realtime = new ReminderAdmissionRealtime();
        await using var first = await ReminderContextAsync(database.ConnectionString, gate);
        var writer = ReminderService(first, realtime).RetryDeliveryAsync(old.Id);
        await using var secondContext = OpenPostgres(database.ConnectionString);
        var secondService = ReminderService(secondContext, realtime);
        try
        {
            var ready = await Task.WhenAny(writer, gate.Started.Task).WaitAsync(TimeSpan.FromSeconds(15));
            if (ready == writer) await writer;
            Assert.True(gate.Started.Task.IsCompleted);
            var conflict = await Record.ExceptionAsync(() => secondService.RetryDeliveryAsync(secondId));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(conflict).SqlState);
        }
        finally { gate.Release.TrySetResult(); }
        await writer.WaitAsync(TimeSpan.FromSeconds(15));
        await secondService.RetryDeliveryAsync(secondId);
        await using var verify = OpenPostgres(database.ConnectionString);
        Assert.Single(await verify.CaseCourtNotificationOutboxes.Where(row => row.Kind == "current-state").ToArrayAsync());
        Assert.Equal(separateOrigin ? 2 : 1, await verify.CaseCourtNotificationOutboxes.CountAsync(row => row.Kind == "legacy-origin"));
        Assert.Equal(2, await verify.UserNotifications.CountAsync());
        Assert.Single(realtime.Delivered);
        Assert.Equal(domain, await ReminderDomainAsync(verify));
    }

    private sealed class CurrentCourtAdoptionGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<CaseCourtNotificationOutbox>().Any(row => row.State == EntityState.Added && row.Entity.Kind == "legacy-origin"))
            {
                Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
