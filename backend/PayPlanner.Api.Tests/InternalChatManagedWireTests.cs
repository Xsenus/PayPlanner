using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class InternalChatManagedWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealBrowserChatYandexCopiesKeepChangedDraftAcrossReloadAndExplicitResume(bool lostAcknowledgement)
    {
        await using var endpoint = await LoopbackYandexDiskEndpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(workerEnabled: true,
            uploadCapacityChecks: true, yandexCopy: endpoint, chatAttachments: true);
        using var login = await runtime.Client.PostAsJsonAsync("/api/auth/login", new { email = runtime.Email, password = runtime.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authentication = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authentication.Token);
        using var channel = await runtime.Client.PostAsJsonAsync("/api/internal-chat/channels", new { name = "Owned browser chat" });
        Assert.Equal(HttpStatusCode.Created, channel.StatusCode);
        var thread = (await channel.Content.ReadFromJsonAsync<InternalChatThreadDto>())!;
        await runtime.CheckCaseRetirementBrowserAsync(thread.Id, Guid.NewGuid(), lostAcknowledgement, "chat-pending", chatAttachments: true,
            applyBoundary: async () =>
            {
                await using (var pending = runtime.OpenDatabase())
                {
                    Assert.NotNull(Assert.Single(await pending.InternalChatSendAttempts.ToArrayAsync()).AcceptedAt);
                    Assert.Empty(await pending.InternalChatMessages.ToArrayAsync());
                    Assert.Empty(await pending.InternalChatAttachments.ToArrayAsync());
                    Assert.Empty(await pending.StorageProductBindings.ToArrayAsync());
                    Assert.Equal(2, await pending.StorageObjects.CountAsync());
                    Assert.Equal(0, endpoint.PutAttempts);
                    var now = DateTime.UtcNow.AddSeconds(-1);
                    await pending.StorageOperations.Where(item => item.Status == StorageOperationStatuses.Pending)
                        .ExecuteUpdateAsync(set => set.SetProperty(item => item.NextAttemptAt, now));
                }
                Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).DeadLettered);
                await using var ready = runtime.OpenDatabase();
                Assert.Equal(4, await ready.StorageReplicas.CountAsync(item => item.Status == StorageReplicaStatuses.Available));
                Assert.Empty(await ready.InternalChatMessages.ToArrayAsync());
            });
        await using var proof = runtime.OpenDatabase();
        var message = Assert.Single(await proof.InternalChatMessages.ToArrayAsync());
        Assert.Equal(message.Id, Assert.Single(await proof.InternalChatSendAttempts.ToArrayAsync()).PublishedMessageId);
        Assert.Equal("Frozen browser chat message", message.Text);
        Assert.Equal(2, await proof.InternalChatAttachments.CountAsync());
        Assert.Equal(2, await proof.StorageProductBindings.CountAsync());
        Assert.Equal(2, endpoint.PutCount);
        Assert.Equal("false", await proof.SystemSettings.Where(item => item.Key == "notifications.email.enabled").Select(item => item.Value).SingleAsync());
        runtime.AssertNoServerErrors();
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("lost-ack")]
    [InlineData("low-quota")]
    [InlineData("unknown-quota")]
    public async Task RealHttpChatYandexCopiesResumeFrozenAggregateAcrossRestart(string failure)
    {
        await using var endpoint = await LoopbackYandexDiskEndpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(workerEnabled: true,
            uploadCapacityChecks: true, yandexCopy: endpoint, chatAttachments: true);
        using var login = await runtime.Client.PostAsJsonAsync("/api/auth/login", new { email = runtime.Email, password = runtime.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authentication = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authentication.Token);
        using var channel = await runtime.Client.PostAsJsonAsync("/api/internal-chat/channels", new { name = "Owned native chat" });
        Assert.Equal(HttpStatusCode.Created, channel.StatusCode);
        var thread = (await channel.Content.ReadFromJsonAsync<InternalChatThreadDto>())!;
        var key = Guid.NewGuid();
        byte[][] originals = [[1, 2, 3], [4, 5, 6]];
        var prefix = $"/api/internal-chat/threads/{thread.Id}";
        if (failure == "low-quota") endpoint.TotalCapacityBytes = 1;
        if (failure == "unknown-quota") endpoint.FailCapacity = true;
        using (var sent = await SendAsync())
        {
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
            Assert.True(sent.Headers.CacheControl!.NoStore);
            var state = (await sent.Content.ReadFromJsonAsync<InternalChatSendStateDto>())!;
            Assert.Equal(key, state.ClientRequestId);
            Assert.Equal(failure is "low-quota" or "unknown-quota" ? "needsInput" : "paused", state.Status);
            Assert.Null(state.Message);
        }
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Single(await db.InternalChatSendAttempts.ToArrayAsync());
            Assert.Empty(await db.InternalChatMessages.ToArrayAsync());
            Assert.Empty(await db.InternalChatAttachments.ToArrayAsync());
            Assert.Empty(await db.StorageProductBindings.ToArrayAsync());
            Assert.Equal("false", await db.SystemSettings.Where(item => item.Key == "notifications.email.enabled").Select(item => item.Value).SingleAsync());
            if (failure is "low-quota" or "unknown-quota") Assert.Empty(await db.StorageObjects.ToArrayAsync());
        }
        Assert.Equal(0, endpoint.PutAttempts);
        await runtime.RestartAsync(TimeSpan.Zero);
        endpoint.TotalCapacityBytes = 10 * 1024 * 1024;
        endpoint.FailCapacity = false;
        if (failure is "low-quota" or "unknown-quota")
        {
            using var exact = await SendAsync();
            Assert.Equal(HttpStatusCode.Accepted, exact.StatusCode);
        }
        using (var discovery = await runtime.Client.GetAsync(prefix + "/sends?limit=1"))
        {
            Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
            Assert.True(discovery.Headers.CacheControl!.NoStore);
            var page = (await discovery.Content.ReadFromJsonAsync<PayPlanner.Api.Services.Storage.InternalChatSendDiscoveryResult>())!;
            Assert.Equal(key, Assert.Single(page.Items).ClientRequestId);
        }
        endpoint.FailNextPutAfterCommit = failure == "lost-ack";
        for (var retry = 0; retry < 5; retry++)
        {
            await using (var due = runtime.OpenDatabase())
            {
                var now = DateTime.UtcNow.AddSeconds(-1);
                await due.StorageOperations.Where(item => item.Status != StorageOperationStatuses.Succeeded && item.Status != StorageOperationStatuses.DeadLetter)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.NextAttemptAt, now));
            }
            Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).DeadLettered);
            await using var proof = runtime.OpenDatabase();
            if (await proof.StorageReplicas.CountAsync(item => item.Status == StorageReplicaStatuses.Available) == 4) break;
        }
        InternalChatMessageDto message;
        using (var resumed = await runtime.Client.PostAsync(prefix + $"/sends/{key}/resume", null))
        {
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            var state = (await resumed.Content.ReadFromJsonAsync<InternalChatSendStateDto>())!;
            Assert.Equal("published", state.Status);
            message = Assert.IsType<InternalChatMessageDto>(state.Message);
            Assert.Equal(2, message.Attachments.Count);
        }
        foreach (var attachment in message.Attachments)
        {
            var index = attachment.FileName == "first.txt" ? 0 : 1;
            Assert.Equal(originals[index], await runtime.Client.GetByteArrayAsync($"/api/internal-chat/attachments/{attachment.Id}"));
        }
        await runtime.RestartAsync(TimeSpan.Zero);
        var puts = endpoint.PutAttempts;
        using (var exact = await SendAsync())
        {
            Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
            Assert.Equal(message.Id, (await exact.Content.ReadFromJsonAsync<InternalChatMessageDto>())!.Id);
        }
        using (var changed = await SendAsync(true)) Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(puts, endpoint.PutAttempts);
        Assert.Equal(2, endpoint.PutCount);
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal(message.Id, Assert.Single(await db.InternalChatMessages.ToArrayAsync()).Id);
            Assert.Equal(message.Id, Assert.Single(await db.InternalChatSendAttempts.ToArrayAsync()).PublishedMessageId);
            Assert.Equal(2, await db.InternalChatAttachments.CountAsync());
            Assert.Equal(2, await db.StorageObjects.CountAsync());
            Assert.Equal(2, await db.StorageProductBindings.CountAsync());
            Assert.Equal(4, await db.StorageReplicas.CountAsync(item => item.Status == StorageReplicaStatuses.Available));
        }
        runtime.AssertNoServerErrors();

        async Task<HttpResponseMessage> SendAsync(bool changed = false)
        {
            using var body = new MultipartFormDataContent();
            body.Add(new StringContent(key.ToString()), "clientRequestId");
            body.Add(new StringContent("Frozen native chat message"), "text");
            for (var index = 0; index < 2; index++)
            {
                var file = new ByteArrayContent(changed && index == 1 ? [6, 5, 4] : originals[index]);
                file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                body.Add(file, "files", index == 0 ? "first.txt" : "second.txt");
            }
            return await runtime.Client.PostAsync(prefix + "/messages", body);
        }
    }
}
