using System.Data.Common;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Security.Claims;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedTaskFileUploadServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResumeUnavailableReadAcceptsOnlyExactConcurrentPublicationWithCurrentAccess(bool publish, bool revoke)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        var operationId = await f.Db.StorageOperations.Where(o => o.IdempotencyKey ==
            StorageObjectService.GetCommitOperationKey($"task-file:{f.TaskId}:{f.UserId}:{key:N}"))
            .Select(o => o.Id).SingleAsync();
        await using var context = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        var service = f.NewService(context, decorateStorage: inner => new UnavailableReadStorage(inner, async () =>
        {
            if (!publish) return;
            await f.Worker.ProcessBatchAsync("resume-read-copies", CancellationToken.None);
            await f.AutomaticWorker(f.Db).ProcessBatchAsync("resume-read-publication", CancellationToken.None);
            if (revoke) await f.Db.Users.Where(u => u.Id == f.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }));
        var authorization = new TaskFilePublicationAuthorizationService(context);
        var result = await service.ResumeAsync(operationId, authorization.CanPublishAsync, CancellationToken.None);
        Assert.Equal(!publish ? "unavailable" : revoke ? "forbidden" : "uploaded", result.Status);
        Assert.Equal(publish ? 1 : 0, await f.Db.TaskJournalFiles.CountAsync());
        if (publish && !revoke) Assert.True(result.IsReplay);
        else Assert.Null(result.FileId);
    }

    private sealed class UnavailableReadStorage(IStorageObjectService inner, Func<Task> beforeRead) : IStorageObjectService
    {
        public Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken ct)
            => inner.WriteAsync(request, ct);
        public Task<bool> DeleteAsync(Guid objectId, int? actorId, string? reason, CancellationToken ct)
            => inner.DeleteAsync(objectId, actorId, reason, ct);
        public async Task<StorageObjectReadResult?> OpenReadAsync(Guid objectId, CancellationToken ct)
        {
            await beforeRead();
            return null;
        }
    }

    [Fact]
    public async Task CapacityCheckedTaskUploadReservesStageBeforePublishingBothCopies()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Options.UploadCapacityChecksEnabled = true;
        fixture.Options.UploadMinimumFreeBytes = 0;
        var result = await fixture.Upload(Guid.NewGuid());
        Assert.Equal("uploaded", result.Status);
        Assert.Equal(0, await fixture.Db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Pending));
        Assert.Equal(5, await fixture.Db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Released));
        Assert.Equal(2, await fixture.Db.StorageCapacityReservations.CountAsync(r => r.ResourceKey == "[\"legacy-local\",\"files\"]"));
        Assert.Equal(10, await fixture.Db.StorageCapacityEpochs.Select(e => e.Version).SingleAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task HostedWorkerRespectsLiveRecipientAndPreferencesWithoutStatusPolling(bool disabledType, bool permanentFailure)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        f.Options.WorkerEnabled = false;
        f.Options.WorkerPollSeconds = 1;
        var outsider = new User
        {
            FullName = "Live other user",
            Email = "live-other@example.test",
            PasswordHash = "hash",
            IsActive = true,
            IsApproved = true,
            RoleId = (await f.Db.Users.SingleAsync()).RoleId
        };
        f.Db.Users.Add(outsider);
        if (disabledType)
            f.Db.UserSettings.Add(new UserSetting
            {
                UserId = f.UserId,
                Key = UserNotificationPreferenceSettings.SettingKey,
                Value = "{\"disabledTypes\":[\"taskChanged\"]}"
            });
        var emailSetting = await f.Db.SystemSettings.SingleOrDefaultAsync(s => s.Key == "notifications.email.enabled");
        if (emailSetting is null) f.Db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        else emailSetting.Value = "false";
        await f.Db.SaveChangesAsync();
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        if (permanentFailure)
            await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientCaseId, (int?)null));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = f.Root, EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackgroundServices:Enabled"] = "false",
            ["Email:Enabled"] = "false",
            ["SyntheticActors:0"] = f.UserId.ToString(),
            ["SyntheticActors:1"] = outsider.Id.ToString()
        });
        Assert.False(builder.Configuration.GetValue<bool>("BackgroundServices:Enabled"));
        Assert.False(builder.Configuration.GetValue<bool>("Email:Enabled"));
        Assert.Equal("false", (await f.Db.SystemSettings.AsNoTracking().SingleAsync(s => s.Key == "notifications.email.enabled")).Value);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var errors = new LiveHostErrors();
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(errors);
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, LiveFixtureAuthentication>("fixture", _ => { });
        builder.Services.AddAuthorization();
        var readiness = new LiveHubReadiness();
        builder.Services.AddSingleton(readiness);
        builder.Services.AddSignalR(o => o.AddFilter<LiveHubReadiness>());
        builder.Services.AddScoped<IRealtimeNotificationPublisher, SignalRRealtimeNotificationPublisher>();
        builder.Services.AddScoped(_ => new PaymentContext(f.DbOptions, useObjectAuditModel: false));
        builder.Services.AddScoped<INotificationService>(sp => new NotificationService(sp.GetRequiredService<PaymentContext>(), builder.Configuration,
            DispatchProxy.Create<IEmailService, ForbiddenOutboundProxy>(), DispatchProxy.Create<ITelegramNotificationService, ForbiddenOutboundProxy>(),
            sp.GetRequiredService<ILogger<NotificationService>>(), sp.GetRequiredService<IRealtimeNotificationPublisher>()));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddSingleton<IOptionsMonitor<StorageOrchestrationOptions>>(new StaticOptions(f.Options));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<StorageStageMarkerMaintenance>();
        builder.Services.AddScoped<IStorageStageWriterMarkerRecoveryService>(sp => f.MarkerRecovery(sp.GetRequiredService<PaymentContext>()));
        builder.Services.AddScoped<IStorageOperationProcessor>(sp =>
        {
            var db = sp.GetRequiredService<PaymentContext>();
            return new LiveCompletionObserver(f.AutomaticWorker(db, notifications: sp.GetRequiredService<INotificationService>()), db, f.Options, finished);
        });
        // Only this storage worker is registered. Production Program's general jobs remain disabled.
        builder.Services.AddHostedService<StorageOperationWorker>();
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapHub<NotificationsHub>(NotificationsHub.Path);
        await app.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var ownerSocket = new ClientWebSocket();
        using var otherSocket = new ClientWebSocket();
        try
        {
            var endpoint = new UriBuilder(app.Urls.Single()) { Scheme = "ws", Path = NotificationsHub.Path }.Uri;
            using (var unauthenticated = new ClientWebSocket())
                await Assert.ThrowsAsync<WebSocketException>(() => unauthenticated.ConnectAsync(endpoint, timeout.Token));
            ownerSocket.Options.SetRequestHeader("X-Fixture-User", f.UserId.ToString());
            otherSocket.Options.SetRequestHeader("X-Fixture-User", outsider.Id.ToString());
            await ownerSocket.ConnectAsync(endpoint, timeout.Token);
            await otherSocket.ConnectAsync(endpoint, timeout.Token);
            var handshake = Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e");
            await ownerSocket.SendAsync(handshake.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            await otherSocket.SendAsync(handshake.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            Assert.Equal("{}", await ReadLiveFrame(ownerSocket, timeout.Token));
            Assert.Equal("{}", await ReadLiveFrame(otherSocket, timeout.Token));
            await readiness.WaitAsync(f.UserId, timeout.Token);
            await readiness.WaitAsync(outsider.Id, timeout.Token);
            f.Options.WorkerEnabled = true;
            int? notificationId = null;
            if (!disabledType)
            {
                using var frame = System.Text.Json.JsonDocument.Parse(await ReadLiveFrame(ownerSocket, timeout.Token));
                Assert.Equal(1, frame.RootElement.GetProperty("type").GetInt32());
                Assert.Equal(NotificationsHub.UserNotificationCreatedEvent, frame.RootElement.GetProperty("target").GetString());
                var payload = frame.RootElement.GetProperty("arguments")[0];
                Assert.Equal(f.UserId, payload.GetProperty("recipientUserId").GetInt32());
                Assert.Equal(f.TaskId, payload.GetProperty("taskJournalItemId").GetInt32());
                Assert.Equal(permanentFailure ? "Не удалось прикрепить файл к задаче" : "Файл прикреплён к задаче", payload.GetProperty("title").GetString());
                Assert.True(payload.GetProperty("toastEnabled").GetBoolean());
                using var metadata = System.Text.Json.JsonDocument.Parse(payload.GetProperty("metadataJson").GetString()!);
                Assert.Equal(key, metadata.RootElement.GetProperty("uploadKey").GetGuid());
                Assert.Equal(permanentFailure ? "failed" : "uploaded", metadata.RootElement.GetProperty("result").GetString());
                notificationId = payload.GetProperty("id").GetInt32();
            }
            await finished.Task.WaitAsync(timeout.Token);
            using var noDuplicate = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadLiveFrame(ownerSocket, noDuplicate.Token));
            using var noForeignDelivery = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadLiveFrame(otherSocket, noForeignDelivery.Token));
            otherSocket.Abort(); ownerSocket.Abort();
            await app.StopAsync(timeout.Token);
            if (disabledType) Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
            else
            {
                var stored = await f.Db.UserNotifications.AsNoTracking().SingleAsync();
                Assert.Equal(notificationId, stored.Id);
                Assert.False(stored.IsRead);
                Assert.Equal(f.UserId, stored.RecipientUserId);
            }
            Assert.Equal(permanentFailure ? 0 : 1, await f.Db.TaskJournalFiles.CountAsync());
            Assert.Equal(StorageOperationStatuses.Succeeded, (await f.Db.StorageOperations.AsNoTracking()
                .SingleAsync(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication)).Status);
            Assert.Equal(0, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("live-result-replay", CancellationToken.None)).Claimed);
            Assert.Equal(disabledType ? 0 : 1, await f.Db.UserNotifications.CountAsync());
            Assert.Empty(errors.Messages);
        }
        finally { ownerSocket.Abort(); otherSocket.Abort(); await app.StopAsync(CancellationToken.None); }
    }

    private static async Task<string> ReadLiveFrame(ClientWebSocket socket, CancellationToken ct)
    {
        var bytes = new byte[8192];
        using var content = new MemoryStream();
        while (content.Length < 65536)
        {
            var read = await socket.ReceiveAsync(bytes.AsMemory(), ct);
            Assert.Equal(WebSocketMessageType.Text, read.MessageType);
            content.Write(bytes, 0, read.Count);
            var text = Encoding.UTF8.GetString(content.ToArray());
            if (text.EndsWith('\u001e')) return text[..^1];
        }
        throw new InvalidOperationException("SignalR frame exceeded the synthetic test limit.");
    }

    private sealed class LiveCompletionObserver(IStorageOperationProcessor processor, PaymentContext db,
        StorageOrchestrationOptions settings, TaskCompletionSource finished) : IStorageOperationProcessor
    {
        public async Task<StorageOperationBatchResult> ProcessBatchAsync(string workerId, CancellationToken ct)
        {
            var result = await processor.ProcessBatchAsync(workerId, ct);
            if (await db.StorageOperations.AnyAsync(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication
                && o.Status == StorageOperationStatuses.Succeeded, ct))
            { settings.WorkerEnabled = false; finished.TrySetResult(); }
            return result;
        }
    }

    private sealed class LiveHubReadiness : IHubFilter
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _connected = new();
        public Task WaitAsync(int id, CancellationToken ct) => _connected.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(ct);
        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            await next(context);
            var id = int.Parse(context.Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);
            _connected.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
    }

    private sealed class LiveFixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var id)
                || !(configuration.GetSection("SyntheticActors").Get<int[]>() ?? []).Contains(id))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class LiveHostErrors : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new ErrorLogger(Messages);
        public void Dispose() { }
        private sealed class ErrorLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
            { if (IsEnabled(level)) messages.Enqueue(format(state, exception)); }
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadStatusSupportsPendingPagesAndAnExactBatchOutsideTheFirstPage(bool sameTimestamp)
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnableAutomaticPublication();
        var caseId = (await f.Db.TaskJournalItems.AsNoTracking().SingleAsync()).ClientCaseId;
        var keys = new List<Guid>();
        var createdAt = DateTime.UtcNow;
        for (var index = 0; index < 56; index++)
        {
            var key = Guid.NewGuid(); keys.Add(key);
            var storageObject = new StorageObject
            {
                DataClass = "task-file",
                OwnerType = "task-file",
                OwnerId = $"upload:{f.TaskId}:{f.UserId}:{key:N}",
                LogicalName = $"pending-{index}.txt",
                ContentType = "text/plain",
                CurrentGeneration = 1
            };
            var generation = new StorageObjectGeneration { Generation = 1, SizeBytes = 3, Sha256 = new string('a', 64) };
            storageObject.Generations.Add(generation);
            storageObject.Operations.Add(new StorageOperation
            {
                OperationType = "commit",
                Status = StorageOperationStatuses.Succeeded,
                StorageObjectGeneration = generation,
                CreatedAt = sameTimestamp ? createdAt : createdAt.AddMinutes(index),
                IdempotencyKey = StorageObjectService.GetCommitOperationKey($"task-file:{f.TaskId}:{f.UserId}:{key:N}"),
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { taskId = f.TaskId, actorId = f.UserId, caseId, fileName = $"pending-{index}.txt" })
            });
            f.Db.StorageObjects.Add(storageObject);
        }
        await f.Db.SaveChangesAsync();
        var reader = f.UploadStates(f.Db);
        var first = (await reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None, pendingOnly: true))!;
        var second = (await reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None, pendingOnly: true, page: 2))!;
        Assert.Equal(50, first.Count);
        Assert.Equal(6, second.Count);
        Assert.Empty(first.Select(s => s.UploadKey).Intersect(second.Select(s => s.UploadKey)));
        var cursorFirst = (await reader.ListPageAsync(f.TaskId, f.UserId, CancellationToken.None))!;
        Assert.Equal(first.Select(s => s.UploadKey), cursorFirst.Items.Select(s => s.UploadKey));
        Assert.NotNull(cursorFirst.NextCursor);
        var cursorSecond = (await reader.ListPageAsync(f.TaskId, f.UserId, CancellationToken.None, cursorFirst.NextCursor))!;
        Assert.Equal(second.Select(s => s.UploadKey), cursorSecond.Items.Select(s => s.UploadKey));
        Assert.Null(cursorSecond.NextCursor);
        var exact = Assert.Single((await reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None, [keys[0]]))!);
        Assert.Equal(keys[0], exact.UploadKey);
        Assert.Equal("pending-0.txt", exact.FileName);
        Assert.Empty((await reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None, [Guid.NewGuid()]))!);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None, page: 0));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None, Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ListPageAsync(f.TaskId, f.UserId, CancellationToken.None, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ListPageAsync(f.TaskId, f.UserId, CancellationToken.None, Guid.Empty));

        // A raw page remains navigable even if every payload in it is corrupt.
        var rawWindow = await f.Db.StorageOperations.Where(o => o.OperationType == "commit")
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(50).ToListAsync();
        foreach (var operation in rawWindow) operation.PayloadJson = "{broken";
        await f.Db.SaveChangesAsync();
        var sparse = (await reader.ListPageAsync(f.TaskId, f.UserId, CancellationToken.None))!;
        Assert.Empty(sparse.Items);
        Assert.Equal(cursorFirst.NextCursor, sparse.NextCursor);

        // Publishing the pivot removes it from pending discovery, not from the scoped cursor history.
        var pivot = await f.Db.StorageOperations.Include(o => o.StorageObject).SingleAsync(o => o.Id == sparse.NextCursor);
        pivot.StorageObject.OwnerId = "999";
        // A concurrent newer row must not shift the older page or duplicate an item.
        rawWindow[0].CreatedAt = createdAt.AddDays(1);
        await f.Db.SaveChangesAsync();
        var older = (await reader.ListPageAsync(f.TaskId, f.UserId, CancellationToken.None, sparse.NextCursor))!;
        Assert.Equal(second.Select(s => s.UploadKey), older.Items.Select(s => s.UploadKey));
        Assert.Null(older.NextCursor);
    }

    [Fact]
    public async Task UploadStatusSurvivesContextRestartAndReportsExactlyPublishedFile()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        var reader = f.UploadStates(restarted);
        var pending = Assert.Single((await reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None))!);
        Assert.Equal(key, pending.UploadKey);
        Assert.Equal("document.txt", pending.FileName);
        Assert.Equal("waiting", pending.Status);
        Assert.Null(pending.FileId);
        Assert.Equal(2, (await f.Worker.ProcessBatchAsync("status-copies", CancellationToken.None)).Succeeded);
        Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("status-publication", CancellationToken.None)).Succeeded);
        var complete = Assert.Single((await reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None))!);
        Assert.Equal("uploaded", complete.Status);
        Assert.Equal((await f.Db.TaskJournalFiles.AsNoTracking().SingleAsync()).Id, complete.FileId);
        Assert.Single(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unapproved")]
    [InlineData("deletedTask")]
    [InlineData("otherActor")]
    [InlineData("badPayload")]
    [InlineData("wrongOwner")]
    public async Task UploadStatusDoesNotDiscloseUnauthorizedOrForeignNames(string mode)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        Assert.Equal("pending", (await f.Upload(Guid.NewGuid())).Status);
        var actor = f.UserId;
        if (mode == "inactive") await f.Db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        if (mode == "unapproved") await f.Db.Users.Where(u => u.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsApproved, false));
        if (mode == "deletedTask") await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId).ExecuteDeleteAsync();
        if (mode == "badPayload") await f.Db.StorageOperations.Where(o => o.OperationType == "commit" && o.StorageObject.DataClass == "task-file")
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.PayloadJson, "{\"fileName\":\"secret.txt\",\"taskId\":999,\"actorId\":999}"));
        if (mode == "wrongOwner") await f.Db.StorageObjects.Where(o => o.DataClass == "task-file")
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerId, "upload:999:999:foreign"));
        if (mode == "otherActor")
        {
            var other = new User
            {
                FullName = "Другой администратор",
                Email = "other@example.test",
                PasswordHash = "hash",
                IsApproved = true,
                RoleId = (await f.Db.Users.AsNoTracking().SingleAsync()).RoleId
            };
            f.Db.Users.Add(other); await f.Db.SaveChangesAsync(); actor = other.Id;
        }
        var states = await f.UploadStates(f.Db).ListAsync(f.TaskId, actor, CancellationToken.None);
        if (mode is "otherActor" or "badPayload" or "wrongOwner") Assert.Empty(states!);
        else Assert.Null(states);
    }

    [Theory]
    [InlineData("manualRetry")]
    [InlineData("paused")]
    [InlineData("retrying")]
    [InlineData("failed")]
    [InlineData("caseChanged")]
    public async Task UploadStatusReportsSafeWaitingAndFailureStates(string mode)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        Assert.Equal("pending", (await f.Upload(Guid.NewGuid())).Status);
        if (mode == "manualRetry") await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishTaskFile).ExecuteDeleteAsync();
        if (mode == "paused") f.Options.AutoTaskFilePublicationEnabled = false;
        if (mode is "retrying" or "failed") await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishTaskFile)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, mode == "retrying" ? StorageOperationStatuses.Failed : StorageOperationStatuses.DeadLetter)
                .SetProperty(o => o.LastError, "secret://provider/password/internal-path"));
        if (mode == "caseChanged") await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientCaseId, (int?)null));
        var state = Assert.Single((await f.UploadStates(f.Db).ListAsync(f.TaskId, f.UserId, CancellationToken.None))!);
        Assert.Equal(mode == "caseChanged" ? "failed" : mode, state.Status);
        Assert.DoesNotContain("secret", state.Message);
        Assert.Null(state.FileId);
    }

    [Fact]
    public async Task UploadStatusPausesWhenBackgroundWorkerIsDisabled()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        f.Options.WorkerEnabled = false;
        Assert.Equal("pending", (await f.Upload(Guid.NewGuid())).Status);
        var state = Assert.Single((await f.UploadStates(f.Db).ListAsync(f.TaskId, f.UserId, CancellationToken.None))!);
        Assert.Equal("paused", state.Status);
        Assert.Null(state.FileId);
    }

    [Theory]
    [InlineData("wrongKey")]
    [InlineData("wrongObject")]
    [InlineData("wrongGeneration")]
    public async Task UploadStatusDoesNotUseAMisboundPublicationQueue(string mode)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        Assert.Equal("pending", (await f.Upload(Guid.NewGuid())).Status);
        var queue = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == StorageOperationTypes.PublishTaskFile);
        queue.Status = StorageOperationStatuses.DeadLetter;
        if (mode == "wrongKey") queue.IdempotencyKey = "unrelated:" + Guid.NewGuid().ToString("N");
        if (mode == "wrongGeneration") queue.StorageObjectGenerationId = null;
        if (mode == "wrongObject")
        {
            var foreign = new StorageObject
            {
                DataClass = "task-file",
                OwnerType = "task-file",
                OwnerId = "foreign",
                LogicalName = "foreign.txt",
                ContentType = "text/plain"
            };
            f.Db.StorageObjects.Add(foreign);
            queue.StorageObjectId = foreign.Id;
        }
        await f.Db.SaveChangesAsync();
        var state = Assert.Single((await f.UploadStates(f.Db).ListAsync(f.TaskId, f.UserId, CancellationToken.None))!);
        Assert.Equal("manualRetry", state.Status);
        Assert.Null(state.FileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadStatusConfirmsPublicationThatCompletesBetweenPageAndQueueReads(bool terminalFailure)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        var gate = new BeforeQueueRead(async () =>
        {
            await f.Worker.ProcessBatchAsync("status-race-copies", CancellationToken.None);
            await f.AutomaticWorker(f.Db).ProcessBatchAsync("status-race-publication", CancellationToken.None);
            if (terminalFailure)
                await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishTaskFile)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, StorageOperationStatuses.DeadLetter));
        });
        var options = new DbContextOptionsBuilder<PaymentContext>(f.DbOptions).AddInterceptors(gate).Options;
        await using var context = new PaymentContext(options, useObjectAuditModel: false);
        var state = Assert.Single((await f.UploadStates(context).ListPageAsync(f.TaskId, f.UserId, CancellationToken.None))!.Items);
        Assert.True(gate.Triggered);
        Assert.Single(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Equal(2, gate.PayloadReads);
        Assert.Equal("uploaded", state.Status);
        Assert.Equal(key, state.UploadKey);
        Assert.Equal((await f.Db.TaskJournalFiles.AsNoTracking().SingleAsync()).Id, state.FileId);
    }

    private sealed class BeforeQueueRead(Func<Task> publish) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public int PayloadReads { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("PayloadJson", StringComparison.Ordinal)) PayloadReads++;
            if (!Triggered && command.CommandText.Contains("'publishTaskFile'", StringComparison.Ordinal)
                && command.CommandText.Contains("IdempotencyKey", StringComparison.Ordinal))
            {
                Triggered = true;
                await publish();
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task UploadStatusResumesOnlyWithARegisteredEnabledWorker(bool registered, bool enabled)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        f.Options.WorkerEnabled = enabled;
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        var reader = f.UploadStates(f.Db, registered);
        var paused = Assert.Single((await reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None))!);
        Assert.Equal("paused", paused.Status);
        Assert.Null(paused.FileId);
        Assert.Contains("отправлять файл заново не нужно", paused.Message);
        // A live flag cannot make an absent hosted service appear after startup.
        f.Options.WorkerEnabled = true;
        var enabledState = Assert.Single((await reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None))!);
        Assert.Equal(registered ? "waiting" : "paused", enabledState.Status);
        await f.Worker.ProcessBatchAsync("paused-status-copies", CancellationToken.None);
        await f.AutomaticWorker(f.Db).ProcessBatchAsync("paused-status-publication", CancellationToken.None);
        f.Options.WorkerEnabled = false;
        var complete = Assert.Single((await reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None))!);
        Assert.Equal("uploaded", complete.Status);
        Assert.NotNull(complete.FileId);
        Assert.Single(await f.Db.TaskJournalFiles.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task UploadStatusWithoutWorkerRegistrationEvidenceFailsClosed()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        Assert.Equal("pending", (await f.Upload(Guid.NewGuid())).Status);
        var reader = new TaskFileUploadStatusReader(f.Db, new TaskFilePublicationAuthorizationService(f.Db),
            new StaticOptions(f.Options));
        Assert.Equal("paused", Assert.Single((await reader.ListAsync(f.TaskId, f.UserId, CancellationToken.None))!).Status);
    }

    [Fact]
    public async Task AutomaticQueueAcceptsCaseInsensitiveProviderNamesWithoutEndlessPendingReplay()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        f.Options.DefaultPolicy.WriteProviders = ["LOCAL", "MIRROR"];
        f.Options.ProviderConnections["MIRROR"] = "test";
        await f.EnableAutomaticPublication();
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        Assert.Equal(2, (await f.Worker.ProcessBatchAsync("mixed-case-copies", CancellationToken.None)).Succeeded);
        Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("mixed-case-publication", CancellationToken.None)).Succeeded);
        await f.AssertBytes(await f.Upload(key));
    }

    [Fact]
    public async Task CatalogAndPublicationQueueJoinTheSameCallerTransaction()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnableAutomaticPublication();
        await using (var transaction = await f.Db.Database.BeginTransactionAsync())
        {
            await f.Objects(f.Db).WriteAsync(new("task-file", "task-file", $"upload:{f.TaskId}:{f.UserId}:{Guid.NewGuid():N}",
                "rollback.txt", "text/plain", new MemoryStream([1, 2, 3]), "rollback-queue", f.UserId, "{}"), CancellationToken.None);
            Assert.Single(await f.Db.StorageObjects.AsNoTracking().ToListAsync());
            Assert.Single(await f.Db.StorageOperations.AsNoTracking().Where(o => o.OperationType == "commit").ToListAsync());
            Assert.Single(await f.Db.StorageOperations.AsNoTracking().Where(o => o.OperationType == StorageOperationTypes.PublishTaskFile).ToListAsync());
            await transaction.RollbackAsync();
        }
        f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.StorageObjects.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.StorageOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.StorageObjectGenerations.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticQueueRecoversLostDomainCommitAcknowledgementWithoutDuplicateHistory(bool workerFault)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: workerFault ? 2 : 1);
        await f.EnableAutomaticPublication();
        var key = Guid.NewGuid();
        if (workerFault)
        {
            Assert.Equal("pending", (await f.Upload(key)).Status);
            Assert.Equal(2, (await f.Worker.ProcessBatchAsync("copies", CancellationToken.None)).Succeeded);
            f.CommitFault.Enabled = true;
            Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("lost-ack", CancellationToken.None)).Retried);
            Assert.Equal(StorageOperationStatuses.Failed, (await f.Db.StorageOperations.AsNoTracking()
                .SingleAsync(o => o.OperationType == StorageOperationTypes.PublishTaskFile)).Status);
        }
        else
        {
            f.CommitFault.Enabled = true;
            await Assert.ThrowsAsync<IOException>(() => f.Upload(key));
        }
        f.Db.ChangeTracker.Clear();
        Assert.Single(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        await f.MakePublicationDue();
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        Assert.Equal(1, (await f.AutomaticWorker(restarted).ProcessBatchAsync("ack-retry", CancellationToken.None)).Succeeded);
        Assert.Single(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
        Assert.Equal(2, await f.Db.StorageObjects.CountAsync());
        await f.AssertBytes(await f.Upload(key));
    }

    [Theory]
    [InlineData("synchronous")]
    [InlineData("httpLostAck")]
    [InlineData("workerLostAck")]
    public async Task AutomaticQueueAcknowledgesPublishedFileAfterActorRevocation(string mode)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: mode == "workerLostAck" ? 2 : 1);
        await f.EnableAutomaticPublication();
        var key = Guid.NewGuid();
        if (mode == "workerLostAck")
        {
            Assert.Equal("pending", (await f.Upload(key)).Status);
            Assert.Equal(2, (await f.Worker.ProcessBatchAsync("receipt-copies", CancellationToken.None)).Succeeded);
            f.CommitFault.Enabled = true;
            Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("receipt-lost-ack", CancellationToken.None)).Retried);
        }
        else if (mode == "httpLostAck")
        {
            f.CommitFault.Enabled = true;
            await Assert.ThrowsAsync<IOException>(() => f.Upload(key));
        }
        else Assert.Equal("uploaded", (await f.Upload(key)).Status);

        f.Db.ChangeTracker.Clear();
        var originalFile = await f.Db.TaskJournalFiles.AsNoTracking().SingleAsync();
        var commitId = await f.Db.StorageOperations.Where(o => o.OperationType == "commit" && o.StorageObject.DataClass == "task-file")
            .Select(o => o.Id).SingleAsync();
        await f.Db.Users.Where(u => u.Id == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        await f.MakePublicationDue();
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        // A receipt acknowledges an existing transaction, never grants access to a revoked actor.
        Assert.Equal("forbidden", (await f.NewService(restarted).ResumeAsync(commitId, (_, _, _) => Task.FromResult(false), CancellationToken.None)).Status);
        Assert.Equal(1, (await f.AutomaticWorker(restarted).ProcessBatchAsync("receipt-after-revocation", CancellationToken.None)).Succeeded);
        Assert.Equal(originalFile.Id, (await f.Db.TaskJournalFiles.AsNoTracking().SingleAsync()).Id);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishTaskFile);
        Assert.Equal(StorageOperationStatuses.Succeeded, queue.Status);
        Assert.Null(queue.LastError);
    }

    [Theory]
    [InlineData("taskBinding")]
    [InlineData("caseBinding")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("digest")]
    [InlineData("deletedObject")]
    [InlineData("caseName")]
    [InlineData("caseActor")]
    public async Task AutomaticQueueRejectsUnverifiedPublicationReceipt(string mode)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 1);
        await f.EnableAutomaticPublication();
        Assert.Equal("uploaded", (await f.Upload(Guid.NewGuid())).Status);
        f.Db.ChangeTracker.Clear();
        var taskObjectId = await f.Db.StorageObjects.Where(o => o.DataClass == "task-file").Select(o => o.Id).SingleAsync();
        var commitId = await f.Db.StorageOperations.Where(o => o.OperationType == "commit" && o.StorageObjectId == taskObjectId)
            .Select(o => o.Id).SingleAsync();
        switch (mode)
        {
            case "taskBinding":
                await f.Db.StorageProductBindings.Where(b => b.Source == LegacyStorageSources.TaskFiles).ExecuteDeleteAsync(); break;
            case "caseBinding":
                await f.Db.StorageProductBindings.Where(b => b.Source == LegacyStorageSources.CaseDocuments).ExecuteDeleteAsync(); break;
            case "owner":
                await f.Db.StorageObjects.Where(o => o.Id == taskObjectId).ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerId, "999999")); break;
            case "generation":
                await f.Db.StorageObjects.Where(o => o.Id == taskObjectId).ExecuteUpdateAsync(s => s.SetProperty(o => o.CurrentGeneration, o => o.CurrentGeneration + 1)); break;
            case "digest":
                await f.Db.StorageObjectGenerations.Where(g => g.StorageObjectId == taskObjectId).ExecuteUpdateAsync(s => s.SetProperty(g => g.Sha256, new string('0', 64))); break;
            case "deletedObject":
                await f.Db.StorageObjects.Where(o => o.Id == taskObjectId).ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true)); break;
            case "caseName":
                await f.Db.ClientCaseDocuments.ExecuteUpdateAsync(s => s.SetProperty(d => d.OriginalFileName, "unrelated.txt")); break;
            case "caseActor":
                var otherActor = new User
                {
                    FullName = "Другой автор",
                    Email = "receipt-other@example.test",
                    PasswordHash = "hash",
                    RoleId = await f.Db.Users.Where(u => u.Id == f.UserId).Select(u => u.RoleId).SingleAsync()
                };
                f.Db.Users.Add(otherActor);
                await f.Db.SaveChangesAsync();
                await f.Db.ClientCaseDocuments.ExecuteUpdateAsync(s => s.SetProperty(d => d.UploadedByUserId, otherActor.Id)); break;
        }
        await f.Db.Users.Where(u => u.Id == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        Assert.Equal("conflict", (await f.NewService(restarted).VerifyPublishedAsync(commitId, CancellationToken.None)).Status);
        Assert.Equal(1, (await f.AutomaticWorker(restarted).ProcessBatchAsync("invalid-receipt", CancellationToken.None)).DeadLettered);
        Assert.Single(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PublicationReceiptDoesNotPublishPendingIntent()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        Assert.Equal("pending", (await f.Upload(Guid.NewGuid())).Status);
        var commitId = await f.Db.StorageOperations.Where(o => o.OperationType == "commit" && o.StorageObject.DataClass == "task-file")
            .Select(o => o.Id).SingleAsync();
        Assert.Equal("pending", (await f.NewService(f.Db).VerifyPublishedAsync(commitId, CancellationToken.None)).Status);
        Assert.Empty(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultNotificationIsDurableAndRetriesWithoutDuplicateMessage(bool lostNotificationAck)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 1);
        await f.EnableAutomaticPublication();
        Assert.Equal("uploaded", (await f.Upload(Guid.NewGuid())).Status);
        Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("notification-enqueue", CancellationToken.None)).Succeeded);
        var outbox = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication);
        Assert.Equal(StorageOperationStatuses.Pending, outbox.Status);
        // No notification consumer: the generic worker must leave this durable work alone.
        Assert.Equal(0, (await f.Worker.ProcessBatchAsync("no-consumer", CancellationToken.None)).Claimed);
        var service = DispatchProxy.Create<INotificationService, ResultNotificationProxy>();
        var proxy = (ResultNotificationProxy)service;
        proxy.LoseNextAcknowledgement = lostNotificationAck;
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        var worker = f.AutomaticWorker(restarted, notifications: service);
        var first = await worker.ProcessBatchAsync("notification-delivery", CancellationToken.None);
        Assert.Equal(lostNotificationAck ? 1 : 0, first.Retried);
        Assert.Equal(lostNotificationAck ? 0 : 1, first.Succeeded);
        if (lostNotificationAck)
        {
            await f.Db.StorageOperations.Where(o => o.Id == outbox.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
            Assert.Equal(1, (await worker.ProcessBatchAsync("notification-retry", CancellationToken.None)).Succeeded);
        }
        var envelope = Assert.Single(proxy.Messages.Values);
        Assert.Equal(f.TaskId, envelope.TaskJournalItemId);
        Assert.Equal(f.UserId, envelope.RecipientUserId);
        Assert.Equal("Файл прикреплён к задаче", envelope.Title);
        Assert.Equal(outbox.IdempotencyKey, envelope.EventKey);
        Assert.Null(envelope.Task); // Never bypass NotificationService's visibility check.
        Assert.Null(envelope.Channels); // Existing type/role/user channel preferences remain authoritative.
        Assert.Contains(NotificationChannels.InApp, envelope.EffectiveChannels);
        Assert.Equal(0, (await worker.ProcessBatchAsync("notification-again", CancellationToken.None)).Claimed);
    }

    [Fact]
    public async Task ResultNotificationDoesNotRevealTaskAfterActorRevocation()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 1);
        await f.EnableAutomaticPublication();
        Assert.Equal("uploaded", (await f.Upload(Guid.NewGuid())).Status);
        Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("revoked-notification-enqueue", CancellationToken.None)).Succeeded);
        await f.Db.Users.Where(u => u.Id == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        var service = DispatchProxy.Create<INotificationService, ResultNotificationProxy>();
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        Assert.Equal(1, (await f.AutomaticWorker(restarted, notifications: service).ProcessBatchAsync("revoked-notification", CancellationToken.None)).Succeeded);
        Assert.Empty(((ResultNotificationProxy)service).Messages);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ResultNotificationUsesPersistentCenterPreferencesAndRealtime(bool disabledType, bool lostRealtimeAck)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 1);
        await f.EnableAutomaticPublication();
        Assert.Equal("uploaded", (await f.Upload(Guid.NewGuid())).Status);
        Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("persistent-result-enqueue", CancellationToken.None)).Succeeded);
        if (disabledType)
        {
            f.Db.UserSettings.Add(new UserSetting
            {
                UserId = f.UserId,
                Key = UserNotificationPreferenceSettings.SettingKey,
                Value = "{\"disabledTypes\":[\"taskChanged\"]}"
            });
            await f.Db.SaveChangesAsync();
        }
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        var realtime = new ResultRealtimePublisher { LoseNextAcknowledgement = lostRealtimeAck };
        var service = new NotificationService(restarted, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Email:Enabled"] = "false", ["BackgroundServices:Enabled"] = "false" }).Build(),
            DispatchProxy.Create<IEmailService, ForbiddenOutboundProxy>(), DispatchProxy.Create<ITelegramNotificationService, ForbiddenOutboundProxy>(),
            NullLogger<NotificationService>.Instance, realtime);
        var worker = f.AutomaticWorker(restarted, notifications: service);
        var first = await worker.ProcessBatchAsync("persistent-result-deliver", CancellationToken.None);
        Assert.Equal(lostRealtimeAck ? 1 : 0, first.Retried);
        if (lostRealtimeAck)
        {
            await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
            Assert.Equal(1, (await worker.ProcessBatchAsync("persistent-result-retry", CancellationToken.None)).Succeeded);
        }
        Assert.Equal(disabledType ? 0 : 1, await f.Db.UserNotifications.CountAsync());
        Assert.Equal(disabledType ? 0 : 1, realtime.Published.Count);
        if (!disabledType)
        {
            var stored = await f.Db.UserNotifications.AsNoTracking().SingleAsync();
            Assert.Equal(f.TaskId, stored.TaskJournalItemId);
            Assert.Equal(f.UserId, stored.RecipientUserId);
            Assert.False(stored.IsRead);
            Assert.Equal("Файл прикреплён к задаче", stored.Title);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultNotificationSharesCompletionTransactionAndSurvivesLostCommitAck(bool lostAck)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 1);
        await f.EnableAutomaticPublication();
        Assert.Equal("uploaded", (await f.Upload(Guid.NewGuid())).Status);
        f.CommitFault.ResultCommitFault = lostAck ? "after" : "before";
        await Assert.ThrowsAsync<IOException>(() => f.AutomaticWorker(f.Db).ProcessBatchAsync("result-commit-fault", CancellationToken.None));
        f.Db.ChangeTracker.Clear();
        Assert.Equal(lostAck ? 1 : 0, await f.Db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication));
        if (!lostAck)
        {
            await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishTaskFile)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }
        var service = DispatchProxy.Create<INotificationService, ResultNotificationProxy>();
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        var worker = f.AutomaticWorker(restarted, notifications: service);
        Assert.Equal(1, (await worker.ProcessBatchAsync("result-commit-recovery", CancellationToken.None)).Succeeded);
        if (!lostAck) Assert.Equal(1, (await worker.ProcessBatchAsync("result-commit-delivery", CancellationToken.None)).Succeeded);
        Assert.Single(((ResultNotificationProxy)service).Messages);
        Assert.Equal(1, await f.Db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication));
        Assert.Single(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("disabled")]
    [InlineData("foreignSource")]
    [InlineData("foreignGeneration")]
    public async Task ResultNotificationRejectsInvalidAuthorityAndDefersDisabledJobs(string mode)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 1);
        await f.EnableAutomaticPublication();
        Assert.Equal("uploaded", (await f.Upload(Guid.NewGuid())).Status);
        Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("result-authority-enqueue", CancellationToken.None)).Succeeded);
        var operation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication);
        var service = DispatchProxy.Create<INotificationService, ResultNotificationProxy>();
        if (mode == "expired")
        {
            await f.Db.StorageOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, StorageOperationStatuses.Running)
                .SetProperty(o => o.LeaseOwner, "expired-result").SetProperty(o => o.LeaseExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            Assert.Equal("leaseLost", await f.PublicationProcessor(f.Db, notifications: service)
                .ProcessNotificationClaimedAsync(operation.Id, "expired-result", CancellationToken.None));
        }
        else if (mode == "disabled")
        {
            f.Options.AutoTaskFilePublicationEnabled = false;
            Assert.Equal(0, (await f.AutomaticWorker(f.Db, notifications: service).ProcessBatchAsync("disabled-result", CancellationToken.None)).Claimed);
            Assert.Equal(0, (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id)).AttemptCount);
        }
        else
        {
            if (mode == "foreignSource")
            {
                var foreignPayload = "{\"publicationOperationId\":\"" + Guid.NewGuid() + "\"}";
                await f.Db.StorageOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.PayloadJson,
                    foreignPayload));
            }
            else
            {
                var foreignGeneration = await f.Db.StorageObjectGenerations.Where(g => g.StorageObject.DataClass == "case-document").Select(g => g.Id).SingleAsync();
                await f.Db.StorageOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.StorageObjectGenerationId, (Guid?)foreignGeneration));
            }
            Assert.Equal(1, (await f.AutomaticWorker(f.Db, notifications: service).ProcessBatchAsync("invalid-result", CancellationToken.None)).DeadLettered);
        }
        Assert.Empty(((ResultNotificationProxy)service).Messages);
    }

    [Fact]
    public async Task ResultNotificationDefersModeDisabledDuringReceiptWithoutLosingTheMessage()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 1);
        await f.EnableAutomaticPublication();
        Assert.Equal("uploaded", (await f.Upload(Guid.NewGuid())).Status);
        Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("pause-result-enqueue", CancellationToken.None)).Succeeded);
        var service = DispatchProxy.Create<INotificationService, ResultNotificationProxy>();
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        var pausing = new PauseOnReceipt(f.NewService(restarted), () => f.Options.AutoTaskFilePublicationEnabled = false);
        Assert.Equal(1, (await f.AutomaticWorker(restarted, pausing, service).ProcessBatchAsync("pause-result-delivery", CancellationToken.None)).Retried);
        var operation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication);
        Assert.Equal(StorageOperationStatuses.Pending, operation.Status);
        Assert.Equal(0, operation.AttemptCount);
        Assert.Empty(((ResultNotificationProxy)service).Messages);
        f.Options.AutoTaskFilePublicationEnabled = true;
        await f.Db.StorageOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(1, (await f.AutomaticWorker(restarted, notifications: service).ProcessBatchAsync("resume-result-delivery", CancellationToken.None)).Succeeded);
        Assert.Single(((ResultNotificationProxy)service).Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultNotificationReportsFailureOrSubsequentVerifiedManualSuccess(bool manualSuccess)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 1);
        await f.EnableAutomaticPublication();
        var key = Guid.NewGuid();
        Assert.Equal("forbidden", (await f.NewService(f.Db).UploadAsync(f.Request(key) with
        { AuthorizePublication = _ => Task.FromResult(false) }, CancellationToken.None)).Status);
        var originalCase = await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId).Select(t => t.ClientCaseId).SingleAsync();
        await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientCaseId, (int?)null));
        Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("result-failed-enqueue", CancellationToken.None)).DeadLettered);
        if (manualSuccess)
        {
            await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientCaseId, originalCase));
            Assert.Equal("uploaded", (await f.Upload(key)).Status);
        }
        var service = DispatchProxy.Create<INotificationService, ResultNotificationProxy>();
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        Assert.Equal(1, (await f.AutomaticWorker(restarted, notifications: service).ProcessBatchAsync("result-failed-delivery", CancellationToken.None)).Succeeded);
        Assert.Equal(manualSuccess ? "Файл прикреплён к задаче" : "Не удалось прикрепить файл к задаче",
            Assert.Single(((ResultNotificationProxy)service).Messages.Values).Title);
    }

    [Fact]
    public async Task AutomaticQueueRepairsCrashBetweenTaskAndCaseObjectRegistration()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnableAutomaticPublication();
        var key = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => f.NewService(f.Db, failCaseWrite: true).UploadAsync(f.Request(key), CancellationToken.None));
        Assert.Single(await f.Db.StorageObjects.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishTaskFile).AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        Assert.Equal(1, (await f.AutomaticWorker(restarted).ProcessBatchAsync("case-registration-recovery", CancellationToken.None)).Succeeded);
        Assert.Equal(2, await f.Db.StorageObjects.CountAsync());
        Assert.Single(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
        await f.AssertBytes(await f.Upload(key));
    }

    [Fact]
    public async Task AutomaticQueueIsAtomicWaitsWithoutReadingBytesAndPublishesAfterRestart()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishTaskFile);
        var commit = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.IdempotencyKey ==
            StorageObjectService.GetCommitOperationKey($"task-file:{f.TaskId}:{f.UserId}:{key:N}"));
        Assert.Equal(commit.StorageObjectId, queue.StorageObjectId);
        Assert.Equal(commit.StorageObjectGenerationId, queue.StorageObjectGenerationId);
        await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.Replicate)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddHours(1)));
        var spy = new CountingResumes(f.NewService(f.Db));
        f.Options.WorkerMaxAttempts = 1;
        Assert.Equal(1, (await f.AutomaticWorker(f.Db, spy).ProcessBatchAsync("waiting", CancellationToken.None)).Retried);
        Assert.Equal(0, spy.ResumeCount);
        queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == queue.Id);
        Assert.Equal(StorageOperationStatuses.Pending, queue.Status);
        Assert.Equal(0, queue.AttemptCount);
        Assert.Null(queue.LeaseOwner);
        Assert.Empty(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.Replicate)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(2, (await f.Worker.ProcessBatchAsync("copies", CancellationToken.None)).Succeeded);
        await f.MakePublicationDue();
        await using var restarted = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        Assert.Equal(1, (await f.AutomaticWorker(restarted).ProcessBatchAsync("restarted", CancellationToken.None)).Succeeded);
        queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == queue.Id);
        Assert.Equal(StorageOperationStatuses.Succeeded, queue.Status);
        Assert.Equal(1, queue.AttemptCount);
        Assert.Null(queue.LastError);
        Assert.Single(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
        Assert.Equal(0, (await f.AutomaticWorker(restarted).ProcessBatchAsync("again", CancellationToken.None)).Claimed);
        var uploaded = await f.Upload(key);
        Assert.True(uploaded.IsReplay);
        await f.AssertBytes(uploaded);
    }

    [Fact]
    public async Task AutomaticQueueRepairsMissingOldIntentSchedulingAndDoesNotConsumeDisabledWork()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        Assert.Equal("pending", (await f.Upload(Guid.NewGuid())).Status);
        Assert.Empty(await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishTaskFile).ToListAsync());
        await f.EnableAutomaticPublication();
        var processor = f.PublicationProcessor(f.Db);
        await processor.EnsureScheduledAsync(CancellationToken.None);
        await processor.EnsureScheduledAsync(CancellationToken.None);
        var queue = Assert.Single(await f.Db.StorageOperations.AsNoTracking().Where(o => o.OperationType == StorageOperationTypes.PublishTaskFile).ToListAsync());
        f.Options.AutoTaskFilePublicationEnabled = false;
        Assert.Equal(2, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("disabled", CancellationToken.None)).Succeeded);
        Assert.Equal(StorageOperationStatuses.Pending, (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == queue.Id)).Status);
        Assert.Empty(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        f.Options.AutoTaskFilePublicationEnabled = true;
        Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("enabled", CancellationToken.None)).Succeeded);
        Assert.Single(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("wrongPayload")]
    [InlineData("wrongKey")]
    [InlineData("expiredLease")]
    [InlineData("taskDeleted")]
    [InlineData("caseChanged")]
    public async Task AutomaticQueueDoesNotPublishUnauthorizedInvalidOrExpiredWork(string mode)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        await f.EnableAutomaticPublication();
        Assert.Equal("pending", (await f.Upload(Guid.NewGuid())).Status);
        Assert.Equal(2, (await f.Worker.ProcessBatchAsync("copies", CancellationToken.None)).Succeeded);
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishTaskFile);
        if (mode == "revoked") await f.Db.Users.Where(u => u.Id == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        if (mode == "wrongPayload") await f.Db.StorageOperations.Where(o => o.Id == queue.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.PayloadJson, "{}"));
        if (mode == "wrongKey") await f.Db.StorageOperations.Where(o => o.Id == queue.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.IdempotencyKey, "another-queue"));
        if (mode == "taskDeleted") await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId).ExecuteDeleteAsync();
        if (mode == "caseChanged") await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientCaseId, (int?)null));
        if (mode == "expiredLease")
        {
            await f.Db.StorageOperations.Where(o => o.Id == queue.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, StorageOperationStatuses.Running)
                .SetProperty(o => o.LeaseOwner, "expired").SetProperty(o => o.LeaseExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            Assert.Equal("leaseLost", await f.PublicationProcessor(f.Db).ProcessClaimedAsync(queue.Id, "expired", CancellationToken.None));
        }
        else Assert.Equal(1, (await f.AutomaticWorker(f.Db).ProcessBatchAsync("invalid", CancellationToken.None)).DeadLettered);
        Assert.Empty(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
        Assert.Equal(2, await f.Db.StorageObjects.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingPublisherFinishingBeforeCoreReplayReturnsReplayOrConflictNotAnException(bool identical)
    {
        await using var f = await Fixture.CreateAsync();
        var key = Guid.NewGuid();
        await using var context = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        ManagedTaskFileUploadResult? winner = null;
        var service = f.NewService(context, beforeFirstWrite: async () =>
        {
            winner = await f.Upload(key, bytes: identical ? [1, 2, 3, 4] : [9, 9, 9, 9]);
        });
        var result = await service.UploadAsync(f.Request(key), CancellationToken.None);
        Assert.Equal("uploaded", winner!.Status);
        Assert.Equal(identical ? "uploaded" : "conflict", result.Status);
        if (identical)
        {
            Assert.True(result.IsReplay);
            Assert.Equal(winner.FileId, result.FileId);
            await f.AssertBytes(result);
        }
        Assert.Single(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
        Assert.Equal(2, await f.Db.StorageObjects.CountAsync());
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DurableResumePublishesWithoutBrowserFileAndReplaysWithoutDuplicates()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        var operationId = await f.Db.StorageOperations.Where(o => o.IdempotencyKey ==
            StorageObjectService.GetCommitOperationKey($"task-file:{f.TaskId}:{f.UserId}:{key:N}")).Select(o => o.Id).SingleAsync();
        Assert.Equal(2, (await f.Worker.ProcessBatchAsync("durable-resume", CancellationToken.None)).Succeeded);
        f.Db.ChangeTracker.Clear();
        await using var nextContext = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        var checks = 0;
        Task<bool> Authorize(int actorId, int taskId, CancellationToken token)
        {
            Assert.Equal(f.UserId, actorId);
            Assert.Equal(f.TaskId, taskId);
            checks++;
            return Task.FromResult(true);
        }
        var service = f.NewService(nextContext);
        var uploaded = await service.ResumeAsync(operationId, Authorize, CancellationToken.None);
        Assert.Equal("uploaded", uploaded.Status);
        Assert.Equal(2, checks); // Before bytes and again under the task lock.
        var replay = await service.ResumeAsync(operationId, Authorize, CancellationToken.None);
        Assert.Equal(uploaded.FileId, replay.FileId);
        Assert.True(replay.IsReplay);
        Assert.Equal(1, await nextContext.TaskJournalFiles.CountAsync());
        Assert.Equal(1, await nextContext.ClientCaseDocuments.CountAsync());
        Assert.Equal(1, await nextContext.TaskJournalComments.CountAsync());
        Assert.Equal(2, await nextContext.StorageObjects.CountAsync());
        await f.AssertBytes(uploaded);
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableResumeRejectsRevokedPermissionBeforeReadOrUnderPublicationLock(bool revokeAfterRead)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        Assert.Equal(2, (await f.Worker.ProcessBatchAsync("durable-revoke", CancellationToken.None)).Succeeded);
        var operationId = await f.Db.StorageOperations.Where(o => o.IdempotencyKey ==
            StorageObjectService.GetCommitOperationKey($"task-file:{f.TaskId}:{f.UserId}:{key:N}")).Select(o => o.Id).SingleAsync();
        var checks = 0;
        var service = f.NewService(f.Db);
        var result = await service.ResumeAsync(operationId, (_, _, _) =>
            Task.FromResult(++checks == 1 && revokeAfterRead), CancellationToken.None);
        Assert.Equal("forbidden", result.Status);
        Assert.Equal(revokeAfterRead ? 2 : 1, checks);
        Assert.Empty(await f.Db.TaskJournalFiles.ToListAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.ToListAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToListAsync());
        Assert.Empty(await f.Db.TaskJournalComments.ToListAsync());
        var resumed = await service.ResumeAsync(operationId, (_, _, _) => Task.FromResult(true), CancellationToken.None);
        Assert.Equal("uploaded", resumed.Status);
        await f.AssertBytes(resumed);
    }

    [Theory]
    [InlineData("taskDeleted")]
    [InlineData("caseChanged")]
    [InlineData("wrongKey")]
    [InlineData("wrongOwner")]
    [InlineData("missingBytes")]
    public async Task DurableResumeDoesNotPublishStaleOrInvalidIntent(string mode)
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        var operation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.IdempotencyKey ==
            StorageObjectService.GetCommitOperationKey($"task-file:{f.TaskId}:{f.UserId}:{key:N}"));
        if (mode != "missingBytes") Assert.Equal(2, (await f.Worker.ProcessBatchAsync("durable-invalid", CancellationToken.None)).Succeeded);
        if (mode == "taskDeleted") await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId).ExecuteDeleteAsync();
        if (mode == "caseChanged") await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientCaseId, (int?)null));
        if (mode == "wrongKey") await f.Db.StorageOperations.Where(o => o.Id == operation.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.IdempotencyKey, "write:unrelated:commit"));
        if (mode == "wrongOwner") await f.Db.StorageObjects.Where(o => o.Id == operation.StorageObjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerId, "another-owner"));
        if (mode == "missingBytes")
            foreach (var path in Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories)) File.Delete(path);
        var result = await f.NewService(f.Db).ResumeAsync(operation.Id, (_, _, _) => Task.FromResult(true), CancellationToken.None);
        Assert.Equal(mode == "missingBytes" ? "unavailable" : "conflict", result.Status);
        Assert.Empty(await f.Db.TaskJournalFiles.ToListAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.ToListAsync());
        Assert.Empty(await f.Db.TaskJournalComments.ToListAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoftDeletedCaseCanBeRestoredWithBothManagedFilesAndNoPhysicalDeleteDebt(bool loaded)
    {
        await using var f = await Fixture.CreateAsync(useObjectAuditModel: true);
        var uploaded = await f.Upload(Guid.NewGuid());
        var caseId = (await f.Db.TaskJournalItems.AsNoTracking().SingleAsync()).ClientCaseId!.Value;
        f.Db.ChangeTracker.Clear();
        if (loaded) await f.Db.ClientCaseDocuments.ToListAsync();
        var controller = f.CaseController();
        Assert.IsType<NoContentResult>(await controller.Delete(caseId, CancellationToken.None));
        Assert.False(await f.Db.ClientCases.AnyAsync(c => c.Id == caseId));
        Assert.Empty(await f.Db.ClientCaseDocuments.ToListAsync());
        Assert.Single(await f.Db.ClientCaseDocuments.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await f.Db.StorageTombstones.ToListAsync());
        Assert.False(await f.Db.StorageObjects.AnyAsync(o => o.IsDeleted));
        Assert.Equal(0, (await f.Worker.ProcessBatchAsync("soft-case-delete", CancellationToken.None)).Succeeded);
        var audit = new ObjectAuditController(f.Db) { ControllerContext = controller.ControllerContext };
        Assert.IsType<NoContentResult>(await audit.RestoreObject("client-case", caseId, CancellationToken.None));
        Assert.Single(await f.Db.ClientCaseDocuments.ToListAsync());
        await f.AssertBytes(uploaded);
    }
    [Fact]
    public async Task CaseDeletionKeepsTaskBytesButClearsExactLinkAndDoesNotGrantRawProviderDeletion()
    {
        await using var f = await Fixture.CreateAsync();
        var uploaded = await f.Upload(Guid.NewGuid());
        var caseFile = await f.Db.ClientCaseDocuments.SingleAsync();
        var taskFile = await f.Db.TaskJournalFiles.SingleAsync();
        Assert.Equal("conflict", await f.TryRawCompatibilityDeletion(caseFile.StoragePath));
        await f.DeleteCaseDocument(caseFile.Id);
        Assert.Single(await f.Db.TaskJournalFiles.ToListAsync());
        Assert.Null((await f.Db.TaskJournalFiles.SingleAsync()).ClientCaseDocumentId);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToListAsync());
        Assert.Equal(2, (await f.Worker.ProcessBatchAsync("case-delete-test", CancellationToken.None)).Succeeded);
        Assert.True(File.Exists(Path.Combine(f.Root, "files", "task-files", taskFile.StoragePath)));
        var next = await f.Upload(Guid.NewGuid(), taskFile.DocumentKey);
        Assert.Equal("uploaded", next.Status);
        Assert.NotEqual(uploaded.CaseFileId, next.CaseFileId);
    }

    [Theory]
    [InlineData("immediate")]
    [InlineData("retention")]
    [InlineData("hold")]
    [InlineData("rollback")]
    [InlineData("tamper")]
    [InlineData("tamperAfter")]
    [InlineData("holdAfter")]
    public async Task DomainDeletionIsAtomicAndCompatibilityBytesFollowRetention(string mode)
    {
        await using var f = await Fixture.CreateAsync();
        var key = Guid.NewGuid();
        var uploaded = await f.Upload(key);
        var file = await f.Db.TaskJournalFiles.AsNoTracking().SingleAsync();
        var path = Path.Combine(f.Root, "files", "task-files", file.StoragePath);
        f.ConfigureRetention(mode == "retention" ? 2 : null, mode == "hold");
        if (mode == "tamper") await File.WriteAllBytesAsync(path, [9, 9, 9, 9]);
        var result = await f.DeleteUploaded(uploaded.FileId!.Value, mode != "rollback");
        Assert.Equal(mode == "tamper" ? "conflict" : "deleted", result);
        if (mode is "rollback" or "tamper")
        {
            Assert.Single(await f.Db.TaskJournalFiles.ToListAsync());
            Assert.Empty(await f.Db.StorageTombstones.ToListAsync());
            Assert.False(await f.Db.StorageObjects.AnyAsync(o => o.IsDeleted));
            Assert.Single(await f.Db.TaskJournalComments.ToListAsync());
            Assert.True(File.Exists(path));
            Assert.Equal("uploaded", (await f.Upload(key)).Status);
            return;
        }
        Assert.Empty(await f.Db.TaskJournalFiles.ToListAsync());
        Assert.Single(await f.Db.ClientCaseDocuments.ToListAsync());
        Assert.Single(await f.Db.StorageTombstones.ToListAsync());
        Assert.Equal(2, await f.Db.TaskJournalComments.CountAsync());
        Assert.Equal("conflict", (await f.Upload(key)).Status);
        Assert.True(File.Exists(path)); // No bytes are removed before worker/policy.
        if (mode == "tamperAfter") await File.WriteAllBytesAsync(path, [9, 9, 9, 9]);
        if (mode == "holdAfter") await f.Db.StorageTombstones.ExecuteUpdateAsync(s => s.SetProperty(t => t.LegalHold, true));
        var processed = await f.Worker.ProcessBatchAsync("delete-task-test", CancellationToken.None);
        if (mode == "immediate")
        {
            Assert.Equal(2, processed.Succeeded);
            Assert.False(File.Exists(path));
        }
        else if (mode == "tamperAfter")
        {
            Assert.Equal(1, processed.Succeeded);
            Assert.Equal(1, processed.Retried);
            Assert.True(File.Exists(path));
        }
        else
        {
            Assert.Equal(0, processed.Succeeded);
            Assert.True(File.Exists(path));
            if (mode == "hold") Assert.Empty(await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.Delete).ToListAsync());
        }
    }

    [Fact]
    public async Task UploadAfterLinkingAnExistingTaskStoresExactCaseDocumentDespiteDifferentVersions()
    {
        await using var f = await Fixture.CreateAsync();
        var task = await f.Db.TaskJournalItems.SingleAsync();
        var caseId = task.ClientCaseId;
        task.ClientCaseId = null;
        await f.Db.SaveChangesAsync();
        var first = await f.Upload(Guid.NewGuid());
        Assert.Equal("uploaded", first.Status);
        Assert.Null(first.CaseFileId);
        var firstFile = await f.Db.TaskJournalFiles.SingleAsync();
        Assert.Null(firstFile.ClientCaseDocumentId);
        task.ClientCaseId = caseId;
        await f.Db.SaveChangesAsync();
        var second = await f.Upload(Guid.NewGuid(), firstFile.DocumentKey);
        Assert.Equal("uploaded", second.Status);
        var secondFile = await f.Db.TaskJournalFiles.SingleAsync(file => file.Id == second.FileId);
        var caseDocument = await f.Db.ClientCaseDocuments.SingleAsync();
        Assert.Equal(2, secondFile.Version);
        Assert.Equal(1, caseDocument.Version);
        Assert.Equal(caseDocument.Id, second.CaseFileId);
        Assert.Equal(caseDocument.Id, secondFile.ClientCaseDocumentId);
    }

    [Fact]
    public async Task PublishRetryAndNewVersionKeepBothDocumentsAndHistoryConsistent()
    {
        await using var f = await Fixture.CreateAsync();
        var key = Guid.NewGuid();
        var first = await f.Upload(key);
        Assert.Equal("uploaded", first.Status);
        Assert.False(first.IsReplay);
        Assert.NotNull(first.CaseFileId);
        Assert.Equal("uploaded", (await f.Upload(key)).Status);
        Assert.True((await f.Upload(key)).IsReplay);
        Assert.Equal("conflict", (await f.Upload(key, bytes: [9, 9, 9, 9])).Status);
        Assert.Equal(1, await f.Db.TaskJournalFiles.CountAsync());
        Assert.Equal(1, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.Equal(1, await f.Db.TaskJournalComments.CountAsync());
        Assert.Equal(2, await f.Db.StorageObjects.CountAsync());
        await f.AssertBytes(first);
        var documentKey = (await f.Db.TaskJournalFiles.AsNoTracking().SingleAsync()).DocumentKey;
        var second = await f.Upload(Guid.NewGuid(), documentKey);
        Assert.Equal("uploaded", second.Status);
        Assert.Equal(new[] { 1, 2 }, await f.Db.TaskJournalFiles.OrderBy(file => file.Version).Select(file => file.Version).ToArrayAsync());
        Assert.Equal(new[] { 1, 2 }, await f.Db.ClientCaseDocuments.OrderBy(file => file.Version).Select(file => file.Version).ToArrayAsync());
        Assert.Equal(2, await f.Db.TaskJournalComments.CountAsync());
        Assert.Equal(4, await f.Db.StorageProductBindings.CountAsync());
        await f.AssertBytes(first);
        await f.AssertBytes(second);
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PendingCopiesAreNotVisibleAndResumeThroughFreshRequestContext()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        Assert.Empty(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.Equal(2, (await f.Worker.ProcessBatchAsync("task-upload-test", CancellationToken.None)).Succeeded);
        await using var nextContext = new PaymentContext(f.DbOptions, useObjectAuditModel: false);
        var result = await f.NewService(nextContext).UploadAsync(f.Request(key), CancellationToken.None);
        Assert.Equal("uploaded", result.Status);
        await f.AssertBytes(result);
        Assert.Equal(1, await f.Db.TaskJournalComments.CountAsync());
    }

    [Fact]
    public async Task ChangedCaseRejectsDelayedPendingUploadWithoutPublishingAnything()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(key)).Status);
        var task = await f.Db.TaskJournalItems.SingleAsync();
        task.ClientCaseId = null;
        await f.Db.SaveChangesAsync();
        await f.Worker.ProcessBatchAsync("task-upload-test", CancellationToken.None);
        Assert.Equal("conflict", (await f.Upload(key)).Status);
        Assert.Empty(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.Equal(2, await f.Db.StorageObjects.CountAsync());
    }

    [Fact]
    public async Task RejectedCasePublicationRollsBackTaskFileHistoryOwnershipAndBothBindings()
    {
        await using var f = await Fixture.CreateAsync();
        var key = Guid.NewGuid();
        var service = f.NewService(f.Db, rejectCase: true);
        Assert.Equal("pending", (await service.UploadAsync(f.Request(key), CancellationToken.None)).Status);
        Assert.Empty(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.TaskJournalComments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.All(await f.Db.StorageObjects.AsNoTracking().ToListAsync(), item => Assert.StartsWith("upload:", item.OwnerId));
        Assert.Equal("uploaded", (await f.Upload(key)).Status);
        Assert.Equal(2, await f.Db.StorageObjects.CountAsync());
    }

    [Fact]
    public async Task LostCommitAcknowledgementKeepsBytesAndPublishedIntentAllowsSafeRetry()
    {
        await using var f = await Fixture.CreateAsync();
        var key = Guid.NewGuid();
        f.CommitFault.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => f.Upload(key));
        f.Db.ChangeTracker.Clear();
        Assert.Equal(1, await f.Db.TaskJournalFiles.CountAsync());
        Assert.Equal(1, await f.Db.ClientCaseDocuments.CountAsync());
        var retry = await f.Upload(key);
        Assert.Equal("uploaded", retry.Status);
        Assert.True(retry.IsReplay);
        Assert.Equal(1, await f.Db.TaskJournalComments.CountAsync());
        Assert.Equal(2, await f.Db.StorageObjects.CountAsync());
        await f.AssertBytes(retry);
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task IndependentPendingAttachmentsCanBothPublishAfterOneUpdatesTheTask()
    {
        await using var f = await Fixture.CreateAsync(requiredCopies: 2);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.Equal("pending", (await f.Upload(first)).Status);
        Assert.Equal("pending", (await f.Upload(second)).Status);
        Assert.Equal(4, (await f.Worker.ProcessBatchAsync("task-upload-test", CancellationToken.None)).Succeeded);
        Assert.Equal("uploaded", (await f.Upload(first)).Status);
        Assert.Equal("uploaded", (await f.Upload(second)).Status);
        Assert.Equal(2, await f.Db.TaskJournalFiles.CountAsync());
        Assert.Equal(2, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.Equal(2, await f.Db.TaskJournalComments.CountAsync());
    }

    [Fact]
    public async Task RevokedPermissionBeforePublicationLeavesNoDomainRowsAndRetryCanResume()
    {
        await using var f = await Fixture.CreateAsync();
        var key = Guid.NewGuid();
        var request = f.Request(key) with { AuthorizePublication = _ => Task.FromResult(false) };
        Assert.Equal("forbidden", (await f.NewService(f.Db).UploadAsync(request, CancellationToken.None)).Status);
        Assert.Empty(await f.Db.TaskJournalFiles.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.Equal("uploaded", (await f.Upload(key)).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly StaticOptions _monitor;
        private readonly StorageProviderRegistry _registry;
        private readonly IConfiguration _config;
        private readonly TestEnvironment _environment;
        public PaymentContext Db { get; }
        public DbContextOptions<PaymentContext> DbOptions { get; }
        public string Root { get; }
        public int TaskId { get; }
        public int UserId { get; }
        public UploadCommitFault CommitFault { get; }
        public StorageOperationProcessor Worker { get; }
        public StorageOrchestrationOptions Options => _monitor.CurrentValue;
        public StorageObjectService Objects(PaymentContext context)
            => new(context, new StoragePolicyService(_monitor, _registry), _registry, _monitor);
        public TaskFileUploadStatusReader UploadStates(PaymentContext context, bool workerRegistered = true)
            => new(context, new TaskFilePublicationAuthorizationService(context), _monitor, new(workerRegistered));
        public async Task EnableAutomaticPublication()
        {
            Options.AutoTaskFilePublicationEnabled = true;
            // In-memory processor tests model an enabled worker; no hosted service or sender is started.
            Options.WorkerEnabled = true;
            await Db.Users.Where(u => u.Id == UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsApproved, true));
        }
        public Task MakePublicationDue() => Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishTaskFile)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
        public TaskFilePublicationProcessor PublicationProcessor(PaymentContext context, IManagedTaskFileUploadService? uploads = null, INotificationService? notifications = null)
            => new(context, uploads ?? NewService(context), new TaskFilePublicationAuthorizationService(context),
                new StoragePolicyService(_monitor, _registry), _monitor, TimeProvider.System, NullLogger<TaskFilePublicationProcessor>.Instance, notifications);
        public StorageOperationProcessor AutomaticWorker(PaymentContext context, IManagedTaskFileUploadService? uploads = null, INotificationService? notifications = null)
            => new(context, _registry, _monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance,
                taskFilePublications: PublicationProcessor(context, uploads, notifications));
        public CasesV2Controller CaseController()
        {
            var policy = new StoragePolicyService(_monitor, _registry);
            var deletion = new ProductStorageDeletionService(Db, new StorageObjectService(Db, policy, _registry, _monitor),
                _registry, (ILegacyLocalStorageLocator)_registry.GetRequired("legacy-local"));
            return new CasesV2Controller(Db, _environment, configuration: _config, productStorageDeletion: deletion)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "fixture")),
                    },
                },
            };
        }
        public async Task<string> TryRawCompatibilityDeletion(string path)
        {
            var legacy = (LegacyLocalStorageProvider)_registry.GetRequired("legacy-local");
            var fullPath = new LocalCaseDocumentStorage(_environment, _config).ResolvePath(path);
            Assert.True(legacy.TryCreateLocator(fullPath, out var locator));
            Assert.Equal(StorageOperationStatus.Conflict, (await legacy.DeleteAsync(locator, CancellationToken.None)).Status);
            var result = await _registry.GetRequired("managed-compatibility").DeleteAsync(locator, CancellationToken.None);
            Assert.True(File.Exists(fullPath));
            return result.Status == StorageOperationStatus.Conflict ? "conflict" : "unexpected";
        }
        public async Task DeleteCaseDocument(int id)
        {
            await using var transaction = await Db.Database.BeginTransactionAsync();
            var document = await Db.ClientCaseDocuments.SingleAsync(d => d.Id == id);
            var policy = new StoragePolicyService(_monitor, _registry);
            var deletion = new ProductStorageDeletionService(Db, new StorageObjectService(Db, policy, _registry, _monitor),
                _registry, (ILegacyLocalStorageLocator)_registry.GetRequired("legacy-local"));
            Assert.Equal("deleted", await deletion.DeleteAsync(LegacyStorageSources.CaseDocuments, id, document.OriginalFileName,
                document.ContentType, new LocalCaseDocumentStorage(_environment, _config).ResolvePath(document.StoragePath), UserId, CancellationToken.None));
            Db.ClientCaseDocuments.Remove(document);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
            Db.ChangeTracker.Clear();
        }
        public void ConfigureRetention(int? days, bool hold)
        {
            _monitor.CurrentValue.DefaultPolicy.RetentionDays = days;
            _monitor.CurrentValue.DefaultPolicy.LegalHold = hold;
        }
        public async Task<string> DeleteUploaded(int fileId, bool commit)
        {
            await using var transaction = await Db.Database.BeginTransactionAsync();
            try
            {
                var file = await Db.TaskJournalFiles.SingleAsync(f => f.Id == fileId);
                var policy = new StoragePolicyService(_monitor, _registry);
                var deletion = new ProductStorageDeletionService(Db, new StorageObjectService(Db, policy, _registry, _monitor),
                    _registry, (ILegacyLocalStorageLocator)_registry.GetRequired("legacy-local"));
                var status = await deletion.DeleteAsync(LegacyStorageSources.TaskFiles, fileId, file.OriginalFileName,
                    file.ContentType, Path.Combine(Root, "files", "task-files", file.StoragePath), UserId, CancellationToken.None);
                if (status == "deleted")
                {
                    Db.TaskJournalFiles.Remove(file);
                    Db.TaskJournalComments.Add(new() { TaskJournalItemId = TaskId, AuthorUserId = UserId, AuthorName = "Автор", Kind = "system", Text = "Удаление файла" });
                    await Db.SaveChangesAsync();
                    if (commit) await transaction.CommitAsync();
                }
                return status;
            }
            finally { Db.ChangeTracker.Clear(); }
        }
        private Fixture(SqliteConnection connection, PaymentContext db, DbContextOptions<PaymentContext> dbOptions,
            string root, int taskId, int userId, UploadCommitFault fault, StaticOptions monitor, StorageProviderRegistry registry,
            IConfiguration config, TestEnvironment environment)
        {
            _connection = connection; Db = db; DbOptions = dbOptions; Root = root; TaskId = taskId; UserId = userId;
            CommitFault = fault; _monitor = monitor; _registry = registry; _config = config; _environment = environment;
            Worker = new(db, registry, monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
        }
        public IStorageStageWriterMarkerRecoveryService MarkerRecovery(PaymentContext context)
            => new StorageStageCapacityService(context, _registry, _monitor, _environment, _config);

        public ManagedTaskFileUploadService NewService(PaymentContext context, bool rejectCase = false, Func<Task>? beforeFirstWrite = null, bool failCaseWrite = false,
            Func<IStorageStageCapacityService, IStorageStageCapacityService>? decorateStage = null,
            Func<IStorageObjectService, IStorageObjectService>? decorateStorage = null)
        {
            var policy = new StoragePolicyService(_monitor, _registry);
            IProductStorageBindingService publisher = new ProductStorageBindingService(context, policy);
            if (rejectCase) publisher = new RejectCase(publisher);
            IStorageObjectService objects = new StorageObjectService(context, policy, _registry, _monitor);
            if (beforeFirstWrite is not null) objects = new BeforeFirstWriteStorage(objects, beforeFirstWrite);
            if (failCaseWrite) objects = new CaseWriteFailureStorage(objects);
            if (decorateStorage is not null) objects = decorateStorage(objects);
            IStorageStageCapacityService stage = new StorageStageCapacityService(context, _registry, _monitor, _environment, _config);
            return new(context, objects, publisher, _environment, _config, _monitor,
                decorateStage is null ? stage : decorateStage(stage));
        }
        public ManagedTaskFileUploadRequest Request(Guid key, string? documentKey = null, byte[]? bytes = null)
        {
            var content = bytes ?? [1, 2, 3, 4];
            var file = new FormFile(new MemoryStream(content), 0, content.Length, "file", "document.txt")
            { Headers = new HeaderDictionary(), ContentType = "text/plain" };
            return new(TaskId, UserId, "Автор", key, file, "document.txt", documentKey);
        }
        public Task<ManagedTaskFileUploadResult> Upload(Guid key, string? documentKey = null, byte[]? bytes = null)
            => NewService(Db).UploadAsync(Request(key, documentKey, bytes), CancellationToken.None);
        public async Task AssertBytes(ManagedTaskFileUploadResult result)
        {
            var policy = new StoragePolicyService(_monitor, _registry);
            var reader = new ProductStorageReader(Db, new StorageObjectService(Db, policy, _registry, _monitor), _monitor);
            foreach (var item in new[] { (LegacyStorageSources.TaskFiles, result.FileId!.Value), (LegacyStorageSources.CaseDocuments, result.CaseFileId!.Value) })
            {
                var read = await reader.OpenReadAsync(item.Item1, item.Item2, "document.txt", "text/plain", CancellationToken.None);
                Assert.Equal(ProductStorageReadStatus.Available, read.Status);
                await using var content = read.File!;
                using var output = new MemoryStream();
                await content.Content.CopyToAsync(output);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, output.ToArray());
            }
            var taskFile = await Db.TaskJournalFiles.AsNoTracking().SingleAsync(file => file.Id == result.FileId);
            var caseDocument = await Db.ClientCaseDocuments.AsNoTracking().SingleAsync(file => file.Id == result.CaseFileId);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(Root, "files", "task-files", taskFile.StoragePath)));
            var caseStorage = new LocalCaseDocumentStorage(_environment, _config);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(caseStorage.ResolvePath(caseDocument.StoragePath)));
        }
        public static async Task<Fixture> CreateAsync(int requiredCopies = 1, bool useObjectAuditModel = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "pp-managed-task-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fault = new UploadCommitFault();
            var dbOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(fault).Options;
            var db = new PaymentContext(dbOptions, useObjectAuditModel: useObjectAuditModel);
            await db.Database.EnsureCreatedAsync();
            var user = new User { FullName = "Автор", Email = "upload@example.test", PasswordHash = "hash", Role = new Role { Name = "admin" } };
            var client = new Client { Name = "Клиент" };
            db.AddRange(user, client); await db.SaveChangesAsync();
            var clientCase = new ClientCase { ClientId = client.Id, Title = "Дело" };
            db.ClientCases.Add(clientCase); await db.SaveChangesAsync();
            var task = new TaskJournalItem { Title = "Задача", ClientCaseId = clientCase.Id, AuthorUserId = user.Id, AssigneeUserId = user.Id, DueAt = DateTime.UtcNow.AddDays(1), UpdatedAt = DateTime.UtcNow };
            db.TaskJournalItems.Add(task); await db.SaveChangesAsync();
            var environment = new TestEnvironment(root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FileStorage:RootPath"] = Path.Combine(root, "files"), ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "objects") }).Build();
            var mirrorConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "mirror") }).Build();
            var legacy = new LegacyLocalStorageProvider(environment, config);
            var registry = new StorageProviderRegistry([new LocalStorageProvider(environment, config), new MirrorProvider(new LocalStorageProvider(environment, mirrorConfig)), legacy, new ManagedCompatibilityStorageProvider(db, legacy)]);
            var monitor = new StaticOptions(new()
            {
                Enabled = true,
                ProductWriteEnabledClasses = ["task-file", "case-document"],
                ProductReadEnabledClasses = ["task-file", "case-document"],
                ProviderConnections = new() { ["mirror"] = "test" },
                DefaultPolicy = new() { RequiredCopies = requiredCopies, WriteProviders = requiredCopies == 2 ? ["local", "mirror"] : ["local"], ReadProviders = ["local", "mirror"] }
            });
            return new(connection, db, dbOptions, root, task.Id, user.Id, fault, monitor, registry, config, environment);
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); await _connection.DisposeAsync();
            Assert.StartsWith("pp-managed-task-", Path.GetFileName(Root), StringComparison.Ordinal);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class CaseWriteFailureStorage(IStorageObjectService inner) : IStorageObjectService
    {
        public Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken ct)
            => request.DataClass == "case-document" ? throw new IOException("Injected crash before case registration.") : inner.WriteAsync(request, ct);
        public Task<StorageObjectReadResult?> OpenReadAsync(Guid objectId, CancellationToken ct) => inner.OpenReadAsync(objectId, ct);
        public Task<bool> DeleteAsync(Guid objectId, int? actorId, string? reason, CancellationToken ct) => inner.DeleteAsync(objectId, actorId, reason, ct);
    }

    private sealed class PauseOnReceipt(IManagedTaskFileUploadService inner, Action pause) : IManagedTaskFileUploadService
    {
        public bool Enabled => inner.Enabled;
        public Task<ManagedTaskFileUploadResult> UploadAsync(ManagedTaskFileUploadRequest request, CancellationToken ct) => inner.UploadAsync(request, ct);
        public Task<ManagedTaskFileUploadResult> ResumeAsync(Guid operationId, Func<int, int, CancellationToken, Task<bool>> authorizePublication, CancellationToken ct)
            => inner.ResumeAsync(operationId, authorizePublication, ct);
        public async Task<ManagedTaskFileUploadResult> VerifyPublishedAsync(Guid operationId, CancellationToken ct)
        {
            var receipt = await inner.VerifyPublishedAsync(operationId, ct);
            pause();
            return receipt;
        }
    }

    private class ForbiddenOutboundProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("External delivery must not run in local notification tests.");
    }

    private sealed class ResultRealtimePublisher : IRealtimeNotificationPublisher
    {
        public List<int> Published { get; } = [];
        public bool LoseNextAcknowledgement { get; set; }
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        {
            Assert.True(toastEnabled);
            Published.Add(notification.Id);
            if (LoseNextAcknowledgement)
            {
                LoseNextAcknowledgement = false;
                throw new IOException("Synthetic realtime acknowledgement failure.");
            }
            return Task.CompletedTask;
        }
    }

    private class ResultNotificationProxy : DispatchProxy
    {
        public Dictionary<string, NotificationEventEnvelope> Messages { get; } = [];
        public bool LoseNextAcknowledgement { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(INotificationService.NotifyAsync) || args is not { Length: 2 })
                throw new NotSupportedException("Unexpected notification call.");
            var envelope = Assert.IsType<NotificationEventEnvelope>(args[0]);
            var added = Messages.TryAdd(envelope.EventKey, envelope);
            if (LoseNextAcknowledgement)
            {
                LoseNextAcknowledgement = false;
                return Task.FromException<bool>(new IOException("Synthetic lost notification acknowledgement."));
            }
            return Task.FromResult(added);
        }
    }

    private sealed class CountingResumes(IManagedTaskFileUploadService inner) : IManagedTaskFileUploadService
    {
        public Task<ManagedTaskFileUploadResult> VerifyPublishedAsync(Guid operationId, CancellationToken ct) => inner.VerifyPublishedAsync(operationId, ct);
        public int ResumeCount { get; private set; }
        public bool Enabled => inner.Enabled;
        public Task<ManagedTaskFileUploadResult> UploadAsync(ManagedTaskFileUploadRequest request, CancellationToken ct) => inner.UploadAsync(request, ct);
        public Task<ManagedTaskFileUploadResult> ResumeAsync(Guid operationId, Func<int, int, CancellationToken, Task<bool>> authorizePublication, CancellationToken ct)
        {
            ResumeCount++;
            return inner.ResumeAsync(operationId, authorizePublication, ct);
        }
    }

    private sealed class BeforeFirstWriteStorage(IStorageObjectService inner, Func<Task> beforeWrite) : IStorageObjectService
    {
        private bool _invoked;
        public async Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken cancellationToken)
        {
            if (!_invoked)
            {
                _invoked = true;
                await beforeWrite();
            }
            return await inner.WriteAsync(request, cancellationToken);
        }
        public Task<StorageObjectReadResult?> OpenReadAsync(Guid objectId, CancellationToken cancellationToken)
            => inner.OpenReadAsync(objectId, cancellationToken);
        public Task<bool> DeleteAsync(Guid objectId, int? deletedByUserId, string? reason, CancellationToken cancellationToken)
            => inner.DeleteAsync(objectId, deletedByUserId, reason, cancellationToken);
    }

    private sealed class RejectCase(IProductStorageBindingService inner) : IProductStorageBindingService
    {
        public Task<ProductStorageBindingResult> PublishAsync(ProductStorageBindingRequest request, CancellationToken ct)
            => request.Source == LegacyStorageSources.CaseDocuments ? Task.FromResult(new ProductStorageBindingResult(false, null, "notReady")) : inner.PublishAsync(request, ct);
    }
    private sealed class UploadCommitFault : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public string? ResultCommitFault { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            if (ResultCommitFault == "before" && eventData.Context is PaymentContext context
                && context.StorageOperations.Local.Any(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication))
            { ResultCommitFault = null; throw new IOException("Injected result transaction rollback."); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (ResultCommitFault == "after" && eventData.Context is PaymentContext resultContext
                && resultContext.StorageOperations.Local.Any(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication))
            { ResultCommitFault = null; throw new IOException("Injected result commit acknowledgement loss."); }
            if (Enabled && eventData.Context is PaymentContext context && context.TaskJournalFiles.Local.Count > 0)
            { Enabled = false; throw new IOException("Injected lost acknowledgement."); }
            return Task.CompletedTask;
        }
    }
    private sealed class MirrorProvider(LocalStorageProvider inner) : IStorageProvider
    {
        public string Key => "mirror";
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct) => inner.PutAsync(request, ct);
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => inner.HeadAsync(locator, ct);
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
    }
    private sealed class StaticOptions(StorageOrchestrationOptions options) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => options;
        public StorageOrchestrationOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
