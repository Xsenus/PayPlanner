using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SignalRRealtimeNotificationPublisherTests
{
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
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public string? LastMethodName { get; private set; }
        public IReadOnlyList<object?> LastArguments { get; private set; } = [];

        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
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
