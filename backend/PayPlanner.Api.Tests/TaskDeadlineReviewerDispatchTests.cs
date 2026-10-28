using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Models.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models.Requests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskDeadlineReviewerDispatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingWorkers_OnlyOneClaimsAndDelivers(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.SeedAsync();
        await using var firstDb = h.Context(); await using var secondDb = h.Context();
        var notification = DispatchProxy.Create<INotificationService, ProbeNotifications>();
        var probe = (ProbeNotifications)(object)notification;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.BeforeDelivery = async () => { entered.TrySetResult(); await release.Task; };
        var first = new TaskDeadlineReviewerDispatchProcessor(firstDb, notification).ProcessAsync(id, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.False(await new TaskDeadlineReviewerDispatchProcessor(secondDb, notification).ProcessAsync(id, CancellationToken.None)); }
        finally { release.TrySetResult(); }
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, probe.Calls);
        Assert.False((await secondDb.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id)).ReviewerDispatchPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDelivery_IsRecoveredAfterLeaseExpiry_AndResolvedRequestsAreSkipped(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.SeedAsync(); await using var db = h.Context();
        var notification = DispatchProxy.Create<INotificationService, ProbeNotifications>();
        var probe = (ProbeNotifications)(object)notification;
        probe.BeforeDelivery = () => throw new InvalidOperationException("Simulated temporary failure");
        var processor = new TaskDeadlineReviewerDispatchProcessor(db, notification);
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        var saved = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.True(saved.ReviewerDispatchPending); Assert.NotNull(saved.ReviewerDispatchLeaseUntil); Assert.Equal(1, saved.ReviewerDispatchAttempts);
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Equal(1, probe.Calls);
        var expiredLease = DateTime.UtcNow.AddMinutes(-1);
        await db.TaskJournalDeadlineChangeRequests.Where(item => item.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReviewerDispatchLeaseUntil, (DateTime?)expiredLease));
        probe.BeforeDelivery = null;
        // A fresh processor represents recovery after the worker or process restarts.
        Assert.True(await new TaskDeadlineReviewerDispatchProcessor(db, notification).ProcessAsync(id, CancellationToken.None)); Assert.Equal(2, probe.Calls);
        saved = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.False(saved.ReviewerDispatchPending); Assert.Equal(2, saved.ReviewerDispatchAttempts);
        await db.TaskJournalDeadlineChangeRequests.Where(item => item.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReviewerDispatchPending, true).SetProperty(item => item.Status, "approved"));
        Assert.True(await processor.ProcessAsync(id, CancellationToken.None)); Assert.Equal(2, probe.Calls);
        Assert.False((await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id)).ReviewerDispatchPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateOldAttempt_CannotClearNewReviewerAssignment(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.SeedAsync(); await using var oldDb = h.Context(); await using var newDb = h.Context();
        var oldNotification = DispatchProxy.Create<INotificationService, ProbeNotifications>(); var oldProbe = (ProbeNotifications)(object)oldNotification;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oldProbe.BeforeDelivery = async () => { entered.TrySetResult(); await release.Task; };
        var oldAttempt = new TaskDeadlineReviewerDispatchProcessor(oldDb, oldNotification).ProcessAsync(id, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var request = await newDb.TaskJournalDeadlineChangeRequests.SingleAsync(item => item.Id == id);
            request.ReviewerUserId = h.NewReviewerId; request.ReviewerName = "Новый"; request.UpdatedAt = DateTime.UtcNow;
            request.ReviewerDispatchPending = true; request.ReviewerDispatchLeaseToken = null; request.ReviewerDispatchLeaseUntil = null;
            await newDb.SaveChangesAsync();
            var freshNotification = DispatchProxy.Create<INotificationService, ProbeNotifications>();
            Assert.True(await new TaskDeadlineReviewerDispatchProcessor(newDb, freshNotification).ProcessAsync(id, CancellationToken.None));
            Assert.Equal(1, ((ProbeNotifications)(object)freshNotification).Calls);
        }
        finally { release.TrySetResult(); }
        Assert.False(await oldAttempt.WaitAsync(TimeSpan.FromSeconds(20)));
        var final = await newDb.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Equal(h.NewReviewerId, final.ReviewerUserId); Assert.False(final.ReviewerDispatchPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchFailure_DiscardsUncommittedNotificationBeforeNextWork(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.SeedAsync(); await using var db = h.Context();
        var notification = DispatchProxy.Create<INotificationService, ProbeNotifications>();
        var probe = (ProbeNotifications)(object)notification;
        probe.BeforeDelivery = () =>
        {
            db.UserNotifications.Add(new UserNotification { RecipientUserId = h.NewReviewerId, Type = "synthetic", EventKey = "not-committed", Title = "Тест", BodyPreview = "Тест", ObjectType = "task" });
            throw new InvalidOperationException("Simulated failure before notification save");
        };
        Assert.Equal(0, await new TaskDeadlineReviewerDispatchProcessor(db, notification).ProcessBatchAsync(CancellationToken.None));
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(await db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.True((await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id)).ReviewerDispatchPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfterInAppNotificationSaved_DoesNotCreateDuplicate(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres); var id = await h.SeedAsync();
        await using var firstDb = h.Context();
        var request = await firstDb.TaskJournalDeadlineChangeRequests.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
        request.TaskJournalItem.ChannelsJson = "[\"inApp\"]"; await firstDb.SaveChangesAsync(); firstDb.ChangeTracker.Clear();
        var publisher = DispatchProxy.Create<IRealtimeNotificationPublisher, PublisherProbe>();
        ((PublisherProbe)(object)publisher).ThrowOnPublish = true;
        NotificationService Service(PaymentContext db, IRealtimeNotificationPublisher realtime) => new(db,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            DispatchProxy.Create<PayPlanner.Api.Services.IEmailService, DenyExternalTransport>(),
            DispatchProxy.Create<ITelegramNotificationService, DenyExternalTransport>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance, realtime);
        Assert.False(await new TaskDeadlineReviewerDispatchProcessor(firstDb, Service(firstDb, publisher)).ProcessAsync(id, CancellationToken.None));
        Assert.Single(await firstDb.UserNotifications.AsNoTracking().Where(item => item.Type == UserNotificationTypes.TaskDeadlineChangeRequested).ToListAsync());
        var expired = DateTime.UtcNow.AddMinutes(-1);
        await firstDb.TaskJournalDeadlineChangeRequests.Where(item => item.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReviewerDispatchLeaseUntil, (DateTime?)expired));
        await using var secondDb = h.Context();
        ((PublisherProbe)(object)publisher).ThrowOnPublish = false;
        Assert.True(await new TaskDeadlineReviewerDispatchProcessor(secondDb, Service(secondDb, publisher)).ProcessAsync(id, CancellationToken.None));
        Assert.Single(await secondDb.UserNotifications.AsNoTracking().Where(item => item.Type == UserNotificationTypes.TaskDeadlineChangeRequested).ToListAsync());
        Assert.False((await secondDb.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id)).ReviewerDispatchPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewerDispatch_RespectsPersonalNotificationOptOut(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres); var id = await h.SeedAsync(); await using var db = h.Context();
        var request = await db.TaskJournalDeadlineChangeRequests.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
        request.TaskJournalItem.ChannelsJson = "[\"inApp\"]";
        db.UserSettings.Add(new UserSetting { UserId = request.ReviewerUserId!.Value, Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>> { [UserNotificationTypes.TaskDeadlineChangeRequested] = [NotificationChannels.InApp] })) });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var publisher = DispatchProxy.Create<IRealtimeNotificationPublisher, PublisherProbe>(); ((PublisherProbe)(object)publisher).ThrowOnPublish = true;
        var service = new NotificationService(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            DispatchProxy.Create<PayPlanner.Api.Services.IEmailService, DenyExternalTransport>(),
            DispatchProxy.Create<ITelegramNotificationService, DenyExternalTransport>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance, publisher);
        Assert.True(await new TaskDeadlineReviewerDispatchProcessor(db, service).ProcessAsync(id, CancellationToken.None));
        Assert.Empty(await db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.False((await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id)).ReviewerDispatchPending);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MaxQueueFailure_IsRetriedWithoutDuplicate_AndRespectsChangedPreference(bool postgres, bool disableMaxBeforeRetry)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.SeedAsync();
        await using var firstDb = h.Context();
        var request = await firstDb.TaskJournalDeadlineChangeRequests.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
        request.TaskJournalItem.ChannelsJson = "[\"inApp\"]";
        var reviewerId = request.ReviewerUserId!.Value;
        firstDb.MaxRecipientLinks.Add(new MaxRecipientLink { UserId = reviewerId, Status = MaxRecipientLinkStatuses.Verified, MaxUserId = 12345 });
        await firstDb.SaveChangesAsync();
        firstDb.ChangeTracker.Clear();
        var outbox = new FaultingMaxOutbox { Fail = true };
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.example.test" }).Build();
        NotificationService Service(PaymentContext db) => new(db, configuration,
            DispatchProxy.Create<PayPlanner.Api.Services.IEmailService, DenyExternalTransport>(),
            DispatchProxy.Create<ITelegramNotificationService, DenyExternalTransport>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance,
            maxService: new ReadyMax(), maxOutboxService: outbox);
        Assert.False(await new TaskDeadlineReviewerDispatchProcessor(firstDb, Service(firstDb)).ProcessAsync(id, CancellationToken.None));
        Assert.Single(await firstDb.UserNotifications.AsNoTracking().ToListAsync());
        Assert.True((await firstDb.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id)).ReviewerDispatchPending);
        var expired = DateTime.UtcNow.AddMinutes(-1);
        await firstDb.TaskJournalDeadlineChangeRequests.Where(item => item.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReviewerDispatchLeaseUntil, (DateTime?)expired));
        if (disableMaxBeforeRetry)
        {
            firstDb.UserSettings.Add(new UserSetting { UserId = reviewerId, Key = UserNotificationPreferenceSettings.SettingKey,
                Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>> { [UserNotificationTypes.TaskDeadlineChangeRequested] = [NotificationChannels.Max] })) });
            await firstDb.SaveChangesAsync();
        }
        outbox.Fail = false;
        await using var secondDb = h.Context();
        Assert.True(await new TaskDeadlineReviewerDispatchProcessor(secondDb, Service(secondDb)).ProcessAsync(id, CancellationToken.None));
        Assert.Single(await secondDb.UserNotifications.AsNoTracking().ToListAsync());
        Assert.False((await secondDb.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id)).ReviewerDispatchPending);
        Assert.Equal(disableMaxBeforeRetry ? 0 : 1, outbox.Queued.Count);
        Assert.Equal(disableMaxBeforeRetry ? 1 : 2, outbox.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewerWithoutVerifiedMax_DoesNotBlockNotification(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.SeedAsync();
        await using var db = h.Context();
        var request = await db.TaskJournalDeadlineChangeRequests.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
        request.TaskJournalItem.ChannelsJson = "[\"inApp\"]";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var outbox = new FaultingMaxOutbox { Fail = true };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.example.test" }).Build();
        var service = new NotificationService(db, configuration,
            DispatchProxy.Create<PayPlanner.Api.Services.IEmailService, DenyExternalTransport>(),
            DispatchProxy.Create<ITelegramNotificationService, DenyExternalTransport>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance,
            maxService: new ReadyMax(), maxOutboxService: outbox);
        Assert.True(await new TaskDeadlineReviewerDispatchProcessor(db, service).ProcessAsync(id, CancellationToken.None));
        Assert.Single(await db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Equal(0, outbox.Calls);
        Assert.False((await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id)).ReviewerDispatchPending);
    }

    private sealed class ReadyMax : IMaxNotificationService
    {
        public Task<MaxRuntimeSettings> GetRuntimeSettingsAsync(CancellationToken ct = default) => Task.FromResult(new MaxRuntimeSettings(
            true, false, "token", "secret", true, false, false, false, false, new TimeOnly(20, 0), new TimeOnly(9, 0), "Asia/Novosibirsk", 20, 30));
        public Task<MaxNotificationDiagnosticsDto> GetSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MaxNotificationDiagnosticsDto> UpdateSettingsAsync(UpdateMaxNotificationSettingsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FaultingMaxOutbox : IMaxOutboxService
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public HashSet<string> Queued { get; } = [];
        public Task<MaxOutboxMessage> QueueAsync(MaxQueueRequest request, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("Synthetic MAX queue failure");
            Queued.Add(request.EventKey);
            return Task.FromResult(new MaxOutboxMessage());
        }
        public Task<bool> RetryAsync(Guid id, int? userId, string name, CancellationToken ct) => throw new NotSupportedException();
    }

    private class PublisherProbe : DispatchProxy
    {
        public bool ThrowOnPublish { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (ThrowOnPublish) throw new InvalidOperationException("Simulated realtime failure");
            if (method?.ReturnType == typeof(Task)) return Task.CompletedTask;
            return null;
        }
    }

    private class DenyExternalTransport : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("External transports must not be called by this test.");
    }

    [Fact]
    public async Task ReassignmentClearsAClaimCreatedAfterItsRead_Postgres()
    {
        await using var h = await Harness.CreateAsync(true);
        var id = await h.SeedAsync(); await using var setup = h.Context();
        var pending = await setup.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id);
        var task = await setup.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == pending.TaskJournalItemId);
        var actorId = task.AuthorUserId!.Value;
        var gate = new PauseQueueSave(); await using var updateDb = h.Context(gate); await using var oldDb = h.Context();
        var newNotifications = DispatchProxy.Create<INotificationService, NoopNotifications>();
        var controller = new TasksController(updateDb, newNotifications)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString()), new Claim(ClaimTypes.Name, "Автор")], "test")) } },
        };
        var update = controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest { IsInternal = true, Title = task.Title, AssigneeUserId = actorId, AuthorUserId = h.NewReviewerId, DueAt = task.DueAt, Status = "inProgress" }, CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var oldNotifications = DispatchProxy.Create<INotificationService, ProbeNotifications>(); var oldProbe = (ProbeNotifications)(object)oldNotifications;
        var oldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var oldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oldProbe.BeforeDelivery = async () => { oldEntered.TrySetResult(); await oldRelease.Task; };
        var oldAttempt = new TaskDeadlineReviewerDispatchProcessor(oldDb, oldNotifications).ProcessAsync(id, CancellationToken.None);
        try
        {
            await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            gate.Release.TrySetResult();
            Assert.IsType<OkObjectResult>((await update.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        }
        finally { gate.Release.TrySetResult(); oldRelease.TrySetResult(); }
        Assert.False(await oldAttempt.WaitAsync(TimeSpan.FromSeconds(20)));
        var saved = await setup.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Equal(h.NewReviewerId, saved.ReviewerUserId);
        Assert.False(saved.ReviewerDispatchPending);
        Assert.Equal(1, ((NoopNotifications)(object)newNotifications).ReviewerCalls);
    }

    private sealed class PauseQueueSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TaskJournalDeadlineChangeRequest>().Any(item => item.State == EntityState.Modified && item.Entity.ReviewerDispatchPending))
            { Reached.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    private class NoopNotifications : DispatchProxy
    {
        public int ReviewerCalls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(INotificationService.NotifyTaskDeadlineReviewerAssignedAsync)) ReviewerCalls++;
            if (method?.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method?.ReturnType.IsGenericType == true && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = method.ReturnType.GenericTypeArguments[0];
                var value = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [value]);
            }
            return null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsecutiveFailures_UseIncreasingBoundedRetryDelays(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var id = await h.SeedAsync(); await using var db = h.Context();
        var notification = DispatchProxy.Create<INotificationService, ProbeNotifications>();
        ((ProbeNotifications)(object)notification).BeforeDelivery = () => throw new InvalidOperationException("Simulated failure");
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));
        var processor = new TaskDeadlineReviewerDispatchProcessor(db, notification, clock);
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        var saved = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(2), DateTime.SpecifyKind(saved.ReviewerDispatchLeaseUntil!.Value, DateTimeKind.Utc));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        saved = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(4), DateTime.SpecifyKind(saved.ReviewerDispatchLeaseUntil!.Value, DateTimeKind.Utc));
        Assert.Equal(2, saved.ReviewerDispatchAttempts);
        await db.TaskJournalDeadlineChangeRequests.Where(item => item.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReviewerDispatchAttempts, 30)
                .SetProperty(item => item.ReviewerDispatchLeaseUntil, (DateTime?)null));
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        saved = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddHours(1), DateTime.SpecifyKind(saved.ReviewerDispatchLeaseUntil!.Value, DateTimeKind.Utc));
        Assert.Equal(30, saved.ReviewerDispatchAttempts);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan elapsed) => current = current.Add(elapsed);
    }

    private class ProbeNotifications : DispatchProxy
    {
        public int Calls { get; private set; }
        public Func<Task>? BeforeDelivery { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(INotificationService.NotifyTaskDeadlineReviewerAssignedAsync)) return DeliverAsync();
            throw new InvalidOperationException("Unexpected notification call.");
        }
        private async Task<IReadOnlyList<NotificationDelivery>> DeliverAsync()
        {
            Calls++;
            if (BeforeDelivery is not null) await BeforeDelivery();
            return Array.Empty<NotificationDelivery>();
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly bool postgres;
        private readonly PostgreSqlModelTemplate? template;
        private readonly PostgreSqlModelTemplate.DatabaseLease? lease;
        private readonly SqliteConnection keeper;
        private readonly string connectionString;
        public int NewReviewerId { get; private set; }
        private Harness(bool postgres, PostgreSqlModelTemplate? template, PostgreSqlModelTemplate.DatabaseLease? lease, SqliteConnection keeper, string connectionString)
            => (this.postgres, this.template, this.lease, this.keeper, this.connectionString) = (postgres, template, lease, keeper, connectionString);
        public static async Task<Harness> CreateAsync(bool postgres)
        {
            var template = postgres ? new PostgreSqlModelTemplate() : null; if (template is not null) await template.InitializeAsync();
            var lease = template is not null ? await template.CreateDatabaseAsync() : null;
            var sqlite = new SqliteConnectionStringBuilder { DataSource = "pp_deadline_dispatch_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
            var keeper = new SqliteConnection(sqlite.ConnectionString); if (!postgres) await keeper.OpenAsync();
            var harness = new Harness(postgres, template, lease, keeper, postgres ? lease!.ConnectionString : sqlite.ConnectionString);
            if (!postgres) { await using var db = harness.Context(); await db.Database.EnsureCreatedAsync(); }
            return harness;
        }
        public PaymentContext Context(SaveChangesInterceptor? interceptor = null)
        {
            if (postgres)
            {
                var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connectionString);
                if (interceptor is not null) builder.AddInterceptors(interceptor);
                return new PostgreSqlPaymentContext(builder.Options);
            }
            var sqlite = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName));
            if (interceptor is not null) sqlite.AddInterceptors(interceptor);
            return new PaymentContext(sqlite.Options);
        }
        public async Task<int> SeedAsync()
        {
            await using var db = Context(); var role = new Role { Name = "admin" };
            var author = new User { Email = "dispatch-author@example.test", FullName = "Автор", PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
            var next = new User { Email = "dispatch-next@example.test", FullName = "Новый", PasswordHash = "x", IsActive = true, IsApproved = true, Role = role };
            db.Users.AddRange(author, next); await db.SaveChangesAsync(); NewReviewerId = next.Id;
            var due = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
            var task = new TaskJournalItem { Title = "Задача", ObjectName = "Тест", AuthorUser = author, AuthorName = author.FullName, DueAt = due, Status = "inProgress" };
            var request = new TaskJournalDeadlineChangeRequest { TaskJournalItem = task, RequestedByName = "Исполнитель", ReviewerUserId = author.Id, ReviewerName = author.FullName, CurrentDueAt = due, RequestedDueAt = due.AddDays(1), Reason = "Причина", Status = "pending", UpdatedAt = DateTime.UtcNow, ReviewerDispatchPending = true };
            db.TaskJournalDeadlineChangeRequests.Add(request); await db.SaveChangesAsync(); return request.Id;
        }
        public async ValueTask DisposeAsync()
        {
            await keeper.DisposeAsync(); if (lease is not null) await lease.DisposeAsync(); if (template is not null) await template.DisposeAsync();
        }
    }
}
