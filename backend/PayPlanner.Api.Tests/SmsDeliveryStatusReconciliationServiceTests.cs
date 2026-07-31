using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsDeliveryStatusReconciliationServiceTests
{
    [Theory]
    [InlineData(SmsProviderMessageStatuses.Delivered, NotificationDeliveryStatus.Sent)]
    [InlineData(SmsProviderMessageStatuses.Failed, NotificationDeliveryStatus.Failed)]
    [InlineData(SmsProviderMessageStatuses.Expired, NotificationDeliveryStatus.Failed)]
    [InlineData(SmsProviderMessageStatuses.Rejected, NotificationDeliveryStatus.Failed)]
    public async Task RunOnceAsync_UpdatesProviderAndGenericStatus(
        string providerStatus,
        string expectedDeliveryStatus)
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new FixedStatusProvider(providerStatus);
        using var services = BuildServices(database.Context, provider);
        var delivery = await database.AddDeliveryAsync();
        var service = new SmsDeliveryStatusReconciliationService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SmsDeliveryStatusReconciliationService>.Instance);

        var result = await service.RunOnceAsync();

        database.Context.ChangeTracker.Clear();
        var updated = await database.Context.NotificationDeliveries.SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(providerStatus, updated.ProviderStatus);
        Assert.Equal(expectedDeliveryStatus, updated.Status);
        Assert.NotNull(updated.ProviderStatusCheckedAt);
        Assert.Equal(1, result.CheckedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, result.TerminalCount);
        var request = Assert.Single(provider.StatusRequests);
        Assert.Equal("provider-message-1", request.ProviderMessageId);
        Assert.Equal("+79990000001", request.RecipientAddress);
        if (expectedDeliveryStatus == NotificationDeliveryStatus.Failed)
        {
            Assert.Equal("SMS-провайдер сообщил, что сообщение не доставлено.", updated.Error);
        }
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotPollTerminalDeliveryAgain()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new FixedStatusProvider(SmsProviderMessageStatuses.Delivered);
        using var services = BuildServices(database.Context, provider);
        await database.AddDeliveryAsync(SmsProviderMessageStatuses.Delivered);
        var service = new SmsDeliveryStatusReconciliationService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SmsDeliveryStatusReconciliationService>.Instance);

        var result = await service.RunOnceAsync();

        Assert.Equal(0, result.CheckedCount);
        Assert.Empty(provider.StatusRequests);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotCallProviderWhenChannelIsDisabled()
    {
        await using var database = await TestDatabase.CreateAsync(channelEnabled: false);
        var provider = new FixedStatusProvider(SmsProviderMessageStatuses.Delivered);
        using var services = BuildServices(database.Context, provider);
        await database.AddDeliveryAsync();
        var service = new SmsDeliveryStatusReconciliationService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SmsDeliveryStatusReconciliationService>.Instance);

        var result = await service.RunOnceAsync();

        Assert.Equal(0, result.CheckedCount);
        Assert.Empty(provider.StatusRequests);
    }

    private static ServiceProvider BuildServices(PaymentContext context, ISmsProvider provider)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(context);
        collection.AddSingleton(provider);
        return collection.BuildServiceProvider();
    }

    private sealed class FixedStatusProvider(string status) : ISmsProvider
    {
        public string Key => "test-provider";
        public List<SmsProviderMessageStatusRequest> StatusRequests { get; } = [];

        public Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
            string providerMessageId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The request overload must be used.");

        public Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
            SmsProviderMessageStatusRequest request,
            CancellationToken cancellationToken = default)
        {
            StatusRequests.Add(request);
            return Task.FromResult(new SmsProviderMessageStatusResult(
                request.ProviderMessageId,
                status,
                DateTime.UtcNow,
                status));
        }

        public Task<SmsProviderBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SmsProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public PaymentContext Context { get; }

        private TestDatabase(SqliteConnection connection, PaymentContext context)
        {
            _connection = connection;
            Context = context;
        }

        public static async Task<TestDatabase> CreateAsync(bool channelEnabled = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection)
                .Options;
            var context = new PaymentContext(options);
            await context.Database.EnsureCreatedAsync();
            context.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSmsNotificationService.EnabledSettingKey,
                Value = channelEnabled ? "true" : "false",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
            return new TestDatabase(connection, context);
        }

        public async Task<NotificationDelivery> AddDeliveryAsync(string? providerStatus = null)
        {
            var delivery = new NotificationDelivery
            {
                EventKey = $"sms-status-{Guid.NewGuid():N}",
                Channel = NotificationChannels.Sms,
                SenderName = "PayPlanner",
                RecipientAddress = "+79990000001",
                Subject = "",
                BodyPreview = "Тест",
                Status = NotificationDeliveryStatus.Sent,
                ProviderKey = "test-provider",
                ProviderMessageId = "provider-message-1",
                ProviderStatus = providerStatus ?? SmsProviderMessageStatuses.Accepted,
                ProviderStatusCheckedAt = providerStatus is null ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
                SentAt = DateTime.UtcNow.AddMinutes(-30),
            };
            Context.NotificationDeliveries.Add(delivery);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return delivery;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
