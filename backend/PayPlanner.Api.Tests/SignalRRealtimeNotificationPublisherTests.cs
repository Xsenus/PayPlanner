using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SignalRRealtimeNotificationPublisherTests
{
    [Fact]
    public async Task PublishUsersRoleChangedAsync_UsesOnlyDistinctPositiveUserGroups()
    {
        var hubContext = new RecordingHubContext();
        var publisher = new SignalRRealtimeNotificationPublisher(hubContext, NullLogger<SignalRRealtimeNotificationPublisher>.Instance);
        await publisher.PublishUsersRoleChangedAsync([4, 8, 4, -1]);
        Assert.Equal(new[] { "user:4", "user:8" }, hubContext.ClientsRecorder.RequestedGroups);
        Assert.False(hubContext.ClientsRecorder.AllWasRequested);
        Assert.Equal(NotificationsHub.UserRoleChangedEvent, hubContext.ClientsRecorder.Proxy.LastMethodName);
        Assert.Empty(hubContext.ClientsRecorder.Proxy.LastArguments);
    }
    [Fact]
    public async Task PublishUserRoleChangedAsync_TargetsOnlyThatUserWithoutPayload()
    {
        var hubContext = new RecordingHubContext();
        var publisher = new SignalRRealtimeNotificationPublisher(hubContext, NullLogger<SignalRRealtimeNotificationPublisher>.Instance);
        await publisher.PublishUserRoleChangedAsync(7);
        Assert.Equal("user:7", hubContext.ClientsRecorder.RequestedGroup);
        Assert.False(hubContext.ClientsRecorder.AllWasRequested);
        Assert.Equal(NotificationsHub.UserRoleChangedEvent, hubContext.ClientsRecorder.Proxy.LastMethodName);
        Assert.Empty(hubContext.ClientsRecorder.Proxy.LastArguments);
    }
    [Fact]
    public async Task RoleSignal_UsesRealAuthenticatedHubGroupsAcrossConnections()
    {
        var ready = new HubConnectionsReady(3);
        using var server = new TestServer(new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddAuthorization();
                services.AddSingleton(ready);
                services.AddSingleton<HubConnectionsReadyFilter>();
                services.AddSignalR(options => options.AddFilter<HubConnectionsReadyFilter>());
            })
            .Configure(app =>
            {
                app.UseWebSockets();
                app.UseRouting();
                app.Use(async (context, next) =>
                {
                    var id = context.Request.Query["syntheticUser"].ToString();
                    if (id is "7" or "8")
                        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "synthetic"));
                    await next();
                });
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapHub<NotificationsHub>(NotificationsHub.Path));
            }));
        var sockets = new List<WebSocket>();
        try
        {
            using var overallTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var id in new[] { 7, 7, 8 })
            {
                var socket = await server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost{NotificationsHub.Path}?syntheticUser={id}"), overallTimeout.Token);
                sockets.Add(socket);
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, overallTimeout.Token);
                var handshake = await ReceiveFrameAsync(socket, overallTimeout.Token);
                Assert.Equal("{}", handshake.TrimEnd('\u001e'));
            }
            await ready.AllConnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var publisher = new SignalRRealtimeNotificationPublisher(server.Services.GetRequiredService<IHubContext<NotificationsHub>>(), NullLogger<SignalRRealtimeNotificationPublisher>.Instance);
            await using var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync(overallTimeout.Token);
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(sqlite).Options);
            await db.Database.EnsureCreatedAsync(overallTimeout.Token);
            db.Roles.AddRange(new Role { Id = 17, Name = "role-seven" }, new Role { Id = 18, Name = "role-eight" }, new Role { Id = 19, Name = "admin" });
            db.Users.AddRange(
                new User { Id = 7, RoleId = 17, Email = "seven@local", PasswordHash = "test", IsActive = true, IsApproved = true },
                new User { Id = 8, RoleId = 18, Email = "eight@local", PasswordHash = "test", IsActive = true, IsApproved = true },
                new User { Id = 9, RoleId = 19, Email = "admin@local", PasswordHash = "test", IsActive = true, IsApproved = true });
            await db.SaveChangesAsync(overallTimeout.Token);
            var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "9"), new Claim(ClaimTypes.Role, "admin")], "synthetic"));
            var controller = new RolePermissionsController(db, new RoleManagementAccessService(db), roleChangePublisher: publisher)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = admin } }
            };
            Assert.IsType<NoContentResult>(await controller.Update(18, RolePermissionCatalog.CreateDto(allowRoleManagement: false), overallTimeout.Token));
            using (var message = JsonDocument.Parse((await ReceiveFrameAsync(sockets[2], overallTimeout.Token)).TrimEnd('\u001e')))
                Assert.Equal(NotificationsHub.RolePermissionsChangedEvent, message.RootElement.GetProperty("target").GetString());
            var otherRoleReads = sockets.Take(2).Select(socket => ReceiveFrameAsync(socket, overallTimeout.Token)).ToArray();
            await Task.Delay(TimeSpan.FromMilliseconds(300), overallTimeout.Token);
            Assert.All(otherRoleReads, read => Assert.False(read.IsCompleted));
            Assert.IsType<NoContentResult>(await controller.Reset(17, overallTimeout.Token));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 2; index++)
            {
                using var message = JsonDocument.Parse((await otherRoleReads[index].WaitAsync(timeout.Token)).TrimEnd('\u001e'));
                Assert.Equal(NotificationsHub.RolePermissionsChangedEvent, message.RootElement.GetProperty("target").GetString());
                Assert.Empty(message.RootElement.GetProperty("arguments").EnumerateArray());
            }
            using var noMessage = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveFrameAsync(sockets[2], noMessage.Token));
        }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }

    private static async Task<string> ReceiveFrameAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var bytes = new byte[4096];
        using var buffer = new MemoryStream();
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), cancellationToken);
            await buffer.WriteAsync(bytes.AsMemory(0, received.Count), cancellationToken);
        } while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed class HubConnectionsReady(int expected)
    {
        private int _count;
        public TaskCompletionSource AllConnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Mark() { if (Interlocked.Increment(ref _count) == expected) AllConnected.TrySetResult(); }
    }

    private sealed class HubConnectionsReadyFilter(HubConnectionsReady ready) : IHubFilter
    {
        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            await next(context);
            ready.Mark();
        }
    }
    [Fact]
    public async Task PublishRolePermissionsChangedAsync_SendsOnlyToDistinctUserGroupsWithoutPayload()
    {
        var hubContext = new RecordingHubContext();
        var publisher = new SignalRRealtimeNotificationPublisher(hubContext, NullLogger<SignalRRealtimeNotificationPublisher>.Instance);
        await publisher.PublishRolePermissionsChangedAsync([4, 8, 4, 0, -2]);
        Assert.Equal(new[] { "user:4", "user:8" }, hubContext.ClientsRecorder.RequestedGroups);
        Assert.False(hubContext.ClientsRecorder.AllWasRequested);
        Assert.Equal(NotificationsHub.RolePermissionsChangedEvent, hubContext.ClientsRecorder.Proxy.LastMethodName);
        Assert.Empty(hubContext.ClientsRecorder.Proxy.LastArguments);
    }

    [Fact]
    public async Task PublishRolePermissionsChangedAsync_IgnoresEmptyRecipientsAndDoesNotPropagateTransportFailure()
    {
        var hubContext = new RecordingHubContext();
        var publisher = new SignalRRealtimeNotificationPublisher(hubContext, NullLogger<SignalRRealtimeNotificationPublisher>.Instance);
        await publisher.PublishRolePermissionsChangedAsync([0, -1]);
        Assert.Empty(hubContext.ClientsRecorder.RequestedGroups);
        Assert.Null(hubContext.ClientsRecorder.Proxy.LastMethodName);
        hubContext.ClientsRecorder.Proxy.ThrowOnSend = true;
        await publisher.PublishRolePermissionsChangedAsync([4]);
        Assert.Equal(new[] { "user:4" }, hubContext.ClientsRecorder.RequestedGroups);
    }

    [Fact]
    public async Task PublishTaskJournalChangedAsync_SendsPayloadFreeSignalToAllConnections()
    {
        var hubContext = new RecordingHubContext();
        var publisher = new SignalRRealtimeNotificationPublisher(
            hubContext,
            NullLogger<SignalRRealtimeNotificationPublisher>.Instance);

        await publisher.PublishTaskJournalChangedAsync();

        Assert.True(hubContext.ClientsRecorder.AllWasRequested);
        Assert.Equal(NotificationsHub.TaskJournalChangedEvent, hubContext.ClientsRecorder.Proxy.LastMethodName);
        Assert.Empty(hubContext.ClientsRecorder.Proxy.LastArguments);
    }

    private sealed class RecordingHubContext : IHubContext<NotificationsHub>
    {
        public RecordingHubClients ClientsRecorder { get; } = new();

        public IHubClients Clients => ClientsRecorder;
        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class RecordingHubClients : IHubClients
    {
        public RecordingClientProxy Proxy { get; } = new();
        public bool AllWasRequested { get; private set; }
        public string? RequestedGroup { get; private set; }
        public IReadOnlyList<string> RequestedGroups { get; private set; } = [];

        public IClientProxy All
        {
            get
            {
                AllWasRequested = true;
                return Proxy;
            }
        }

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) { RequestedGroup = groupName; return Proxy; }
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) { RequestedGroups = groupNames; return Proxy; }
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public bool ThrowOnSend { get; set; }
        public string? LastMethodName { get; private set; }
        public IReadOnlyList<object?> LastArguments { get; private set; } = [];

        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend) throw new InvalidOperationException("Synthetic transport failure");
            LastMethodName = method;
            LastArguments = args;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
