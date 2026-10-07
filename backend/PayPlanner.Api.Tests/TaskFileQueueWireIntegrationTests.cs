using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskFileQueueWireIntegrationTests
{
    [Theory]
    [InlineData(null, "replay")]
    [InlineData("legacy", "replay")]
    [InlineData("modern", "replay")]
    [InlineData("legacy", "completion")]
    [InlineData("modern", "completion")]
    public Task RealHttpUploadReachesSecondCopyPublicationAndPersistentResultThroughManualQueue(string? cardMode, string flow)
        => RunBrowserQueueFlowAsync(cardMode, flow);

    [Theory]
    [InlineData("legacy")]
    [InlineData("modern")]
    public Task BrowserCreationKeepsInitialCommentAndPendingFileWithoutDuplicateTask(string cardMode)
        => RunBrowserQueueFlowAsync(cardMode, "creation");

    [Theory]
    [InlineData("legacy")]
    [InlineData("modern")]
    public Task BrowserCompletionRetainsResultAfterStatusFailureWithoutResendingPublishedFile(string cardMode)
        => RunBrowserQueueFlowAsync(cardMode, "completionSaveFailure");

    [Theory]
    [InlineData("legacy")]
    [InlineData("modern")]
    public Task BrowserReceivesLivePublicationWithClosedCardWithoutReload(string cardMode)
        => RunBrowserQueueFlowAsync(cardMode, "live");

    [Theory]
    [InlineData("legacy")]
    [InlineData("modern")]
    public Task BrowserWaitsForActualCardSettingsWithoutShowingDefaultForm(string cardMode)
        => RunBrowserQueueFlowAsync(cardMode, "replay", delayCardSettings: true);

    private static async Task RunBrowserQueueFlowAsync(string? cardMode, string flow, bool delayCardSettings = false)
    {
        await using var endpoint = await LoopbackS3Endpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(cardMode ?? "legacy", workerEnabled: true, secondCopy: endpoint);
        await using var live = flow == "live" ? await SyntheticLiveNotificationGateway.StartAsync(runtime) : null;
        using var login = await runtime.Client.PostAsJsonAsync("/api/auth/login", new { email = runtime.Email, password = runtime.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authentication = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(authentication);
        runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authentication.Token);
        int taskId;
        await using (var db = runtime.OpenDatabase())
        {
            var user = await db.Users.SingleAsync();
            var client = new Client { Name = "Synthetic wire client" };
            var clientCase = new ClientCase { Client = client, Title = "Synthetic wire case" };
            var task = new TaskJournalItem
            {
                Title = "Synthetic pending HTTP upload",
                Client = client,
                ClientCase = clientCase,
                AuthorUserId = user.Id,
                AssigneeUserId = user.Id,
                DueAt = DateTime.UtcNow.AddDays(1),
                UpdatedAt = DateTime.UtcNow
            };
            db.TaskJournalItems.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;
            var settings = await db.SystemSettings.SingleAsync(s => s.Key == "integrations.s3.connections");
            Assert.Contains("protected:v1:", settings.Value);
            Assert.DoesNotContain(endpoint.Connection.SecretAccessKey, settings.Value);
        }
        var key = Guid.NewGuid();
        var initialCommentCount = 0;
        var bytes = Encoding.UTF8.GetBytes("Synthetic queued HTTP bytes: Файл задачи");
        using var body = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        body.Add(file, "file", "queued-wire.txt");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tasks/{taskId}/files") { Content = body };
        request.Headers.Add("X-Upload-Key", key.ToString("D"));
        if (flow is "replay" or "live")
        {
            using var response = await runtime.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var receipt = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("task_file_copies_pending", receipt.RootElement.GetProperty("code").GetString());
            Assert.Equal(taskId, receipt.RootElement.GetProperty("taskId").GetInt32());
            Assert.Equal(key, receipt.RootElement.GetProperty("uploadKey").GetGuid());
            Assert.False(receipt.RootElement.TryGetProperty("fileId", out _));
        }
        if (cardMode is null) await CompleteQueueAsync();
        else
        {
            if (live is not null) await live.AssertAuthenticationAsync(authentication.Token, taskId);
            await runtime.CheckUploadBrowserAsync(taskId, cardMode, CompleteQueueAsync, flow, live?.Address, delayCardSettings);
        }

        async Task CompleteQueueAsync()
        {
            if (flow == "creation")
            {
                await using var createdDb = runtime.OpenDatabase();
                taskId = (await createdDb.TaskJournalItems.AsNoTracking()
                    .SingleAsync(t => t.Title == "Synthetic creation pending task")).Id;
                Assert.Equal(2, await createdDb.TaskJournalItems.CountAsync()); // Seed plus one UI-created task.
                var comment = await createdDb.TaskJournalComments.AsNoTracking().SingleAsync(c => c.Kind == "comment");
                Assert.Equal(taskId, comment.TaskJournalItemId);
                Assert.Equal("Synthetic creation: Первый комментарий.", comment.Text);
                Assert.Single(await createdDb.TaskJournalComments.AsNoTracking()
                    .Where(c => c.TaskJournalItemId == taskId && c.Text == "Задача создана.").ToArrayAsync());
            }
            if (flow is not ("replay" or "live"))
            {
                var pending = await runtime.Client.GetFromJsonAsync<TaskFileUploadState[]>($"/api/tasks/{taskId}/files/uploads");
                key = Assert.Single(pending!).UploadKey;
            }
            using var status = await runtime.Client.GetAsync($"/api/tasks/{taskId}/files/uploads?uploadKeys={key:D}");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            var accepted = Assert.Single((await status.Content.ReadFromJsonAsync<TaskFileUploadState[]>())!);
            Assert.Equal(key, accepted.UploadKey);
            Assert.Equal("paused", accepted.Status); // Background services remain explicitly disabled.
            Assert.Null(accepted.FileId);
            await using (var db = runtime.OpenDatabase())
            {
                Assert.Equal(2, await db.StorageObjects.CountAsync()); // Independently governed task and case document objects.
                Assert.Equal(2, await db.StorageReplicas.CountAsync(r => r.ProviderKey == "local" && r.Status == StorageReplicaStatuses.Available));
                Assert.Equal(2, await db.StorageReplicas.CountAsync(r => r.ProviderKey == "s3" && r.Status == StorageReplicaStatuses.Pending));
                Assert.Equal(1, await db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.PublishTaskFile));
                Assert.Empty(await db.TaskJournalFiles.ToArrayAsync());
                Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
                var pendingTask = await db.TaskJournalItems.AsNoTracking().SingleAsync(t => t.Id == taskId);
                Assert.NotEqual("done", pendingTask.Status);
                Assert.True(string.IsNullOrEmpty(pendingTask.Result));
                initialCommentCount = await db.TaskJournalComments.CountAsync();
            }
            Assert.Equal(0, endpoint.PutCount);
            if (live is not null) await live.WaitForRecipientAsync(authentication.User.Id);
            // The original browser request is no longer needed. Manually drive only this owned synthetic DB.
            await using (var db = runtime.OpenDatabase())
            {
                var environment = new WireEnvironment(runtime.Root);
                var monitor = new SyntheticFixedOptions<StorageOrchestrationOptions>(
                    runtime.Configuration.GetSection(StorageOrchestrationOptions.SectionName).Get<StorageOrchestrationOptions>()!);
                var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(runtime.Root, "keys")),
                    builder => builder.SetApplicationName(Path.GetFileName(runtime.Root)));
                var s3 = new S3StorageService(db, new S3StorageClientFactory(runtime.Configuration), protection,
                    new S3EndpointSecurityPolicy(new SyntheticFixedOptions<S3EndpointSecurityOptions>(new()
                    { AllowHttp = true, AllowedHosts = ["127.0.0.1"] })));
                var legacy = new LegacyLocalStorageProvider(environment, runtime.Configuration);
                var registry = new StorageProviderRegistry([new LocalStorageProvider(environment, runtime.Configuration),
                new S3StorageProvider(s3), legacy, new ManagedCompatibilityStorageProvider(db, legacy)]);
                var policies = new StoragePolicyService(monitor, registry);
                var storage = new StorageObjectService(db, policies, registry, monitor);
                var uploads = new ManagedTaskFileUploadService(db, storage, new ProductStorageBindingService(db, policies),
                    environment, runtime.Configuration, monitor);
                var notifications = new NotificationService(db, runtime.Configuration,
                    DispatchProxy.Create<IEmailService, ForbiddenWireOutbound>(),
                    DispatchProxy.Create<ITelegramNotificationService, ForbiddenWireOutbound>(), NullLogger<NotificationService>.Instance,
                    live?.Publisher ?? NoOpRealtimeNotificationPublisher.Instance);
                var publications = new TaskFilePublicationProcessor(db, uploads, new TaskFilePublicationAuthorizationService(db),
                    policies, monitor, TimeProvider.System, NullLogger<TaskFilePublicationProcessor>.Instance, notifications);
                var processor = new StorageOperationProcessor(db, registry, monitor, TimeProvider.System,
                    NullLogger<StorageOperationProcessor>.Instance, taskFilePublications: publications);
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    // Avoid a wall-clock retry delay; never touch running leases or a non-owned database.
                    await db.StorageOperations.Where(o => o.Status == StorageOperationStatuses.Pending)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
                    var batch = await processor.ProcessBatchAsync("synthetic-wire-manual-worker", CancellationToken.None);
                    Assert.Equal(0, batch.DeadLettered);
                    // Publication may wait once while the independent replicas are being copied.
                    Assert.Empty(await db.StorageOperations.AsNoTracking()
                        .Where(o => o.OperationType == StorageOperationTypes.Replicate && o.LastError != null)
                        .Select(o => o.LastError).ToArrayAsync());
                }
                Assert.Equal(4, await db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
                var saved = Assert.Single(await db.TaskJournalFiles.AsNoTracking().ToArrayAsync());
                Assert.NotNull(saved.ClientCaseDocumentId);
                Assert.Single(await db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
                Assert.Equal(initialCommentCount + 1, await db.TaskJournalComments.CountAsync());
                Assert.Single(await db.TaskJournalComments.AsNoTracking()
                    .Where(c => c.TaskJournalItemId == taskId && c.Text == "Добавлен файл: queued-wire.txt.").ToArrayAsync());
                var notice = Assert.Single(await db.UserNotifications.AsNoTracking().ToArrayAsync());
                Assert.Equal("Файл прикреплён к задаче", notice.Title);
                Assert.Equal(taskId, notice.TaskJournalItemId);
                Assert.Equal(authentication.User.Id, notice.RecipientUserId);
                Assert.False(notice.IsRead);
                Assert.Equal(1, await db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.NotifyTaskFilePublication
                    && o.Status == StorageOperationStatuses.Succeeded));
                Assert.Equal(0, (await processor.ProcessBatchAsync("synthetic-wire-replay", CancellationToken.None)).Claimed);
            }
        }
        using var completed = await runtime.Client.GetAsync($"/api/tasks/{taskId}/files/uploads?uploadKeys={key:D}");
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var published = Assert.Single((await completed.Content.ReadFromJsonAsync<TaskFileUploadState[]>())!);
        Assert.Equal("uploaded", published.Status);
        Assert.NotNull(published.FileId);
        Assert.Equal(bytes, await runtime.Client.GetByteArrayAsync($"/api/tasks/{taskId}/files/{published.FileId}/download"));
        using var inbox = await runtime.Client.GetAsync("/api/notifications/inbox?status=all&page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
        var visible = await inbox.Content.ReadFromJsonAsync<PagedResponse<UserNotificationDto>>();
        Assert.NotNull(visible);
        Assert.Equal(1, visible.Total);
        var visibleNotice = Assert.Single(visible.Items);
        Assert.Equal("Файл прикреплён к задаче", visibleNotice.Title);
        Assert.Equal(taskId, visibleNotice.TaskId);
        Assert.Equal(cardMode is not null && (flow is "replay" or "live"), visibleNotice.IsRead);
        if (flow is "completion" or "completionSaveFailure")
        {
            await using var db = runtime.OpenDatabase();
            var task = await db.TaskJournalItems.AsNoTracking().SingleAsync(t => t.Id == taskId);
            Assert.Equal("done", task.Status);
            Assert.Equal("Synthetic completion: Работа выполнена.", task.Result);
            Assert.Single(await db.TaskJournalFiles.AsNoTracking().ToArrayAsync());
        }
        if (flow == "creation")
        {
            await using var db = runtime.OpenDatabase();
            var task = await db.TaskJournalItems.AsNoTracking().SingleAsync(t => t.Id == taskId);
            Assert.NotEqual("done", task.Status);
            Assert.NotNull(task.ClientId);
            Assert.NotNull(task.ClientCaseId);
            Assert.Equal(2, await db.TaskJournalItems.CountAsync());
        }
        Assert.Equal(2, endpoint.PutCount);
        runtime.AssertNoServerErrors();
    }

    private class ForbiddenWireOutbound : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("External delivery is forbidden in synthetic wire tests.");
    }

    private sealed class WireEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api";
        public string EnvironmentName { get; set; } = "Production";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
