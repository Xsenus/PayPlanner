using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsProviderContractsTests
{
    [Fact]
    public async Task ProviderContract_CoversSendStatusBalanceAndHealthOperations()
    {
        ISmsProvider provider = new RecordingSmsProvider();
        var request = new SmsProviderSendRequest(
            "+79990000000",
            "Тестовое сообщение",
            "PayPlanner",
            "sms:test:42");

        var sent = await provider.SendAsync(request);
        var status = await provider.GetMessageStatusAsync(sent.ProviderMessageId!);
        var balance = await provider.GetBalanceAsync();
        var health = await provider.CheckHealthAsync();

        Assert.Equal("recording", provider.Key);
        Assert.True(sent.Accepted);
        Assert.Equal("provider-message-42", sent.ProviderMessageId);
        Assert.Equal("accepted", sent.ProviderStatus);
        Assert.Equal(sent.ProviderMessageId, status.ProviderMessageId);
        Assert.Equal("delivered", status.ProviderStatus);
        Assert.True(balance.IsAvailable);
        Assert.Equal(125.50m, balance.Amount);
        Assert.Equal("RUB", balance.Currency);
        Assert.True(health.IsHealthy);
        Assert.Equal(request, Assert.Single(((RecordingSmsProvider)provider).Requests));
    }

    [Fact]
    public async Task ProviderContract_PreservesDiagnosticsWhenOperationIsUnavailable()
    {
        ISmsProvider provider = new UnavailableSmsProvider();

        var sent = await provider.SendAsync(new SmsProviderSendRequest(
            "+79990000000",
            "Тест",
            "PayPlanner",
            "sms:test:unavailable"));
        var balance = await provider.GetBalanceAsync();
        var health = await provider.CheckHealthAsync();

        Assert.False(sent.Accepted);
        Assert.Null(sent.ProviderMessageId);
        Assert.Equal("gateway-unavailable", sent.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(30), sent.RetryAfter);
        Assert.False(balance.IsAvailable);
        Assert.Null(balance.Amount);
        Assert.Equal("balance-unavailable", balance.ErrorCode);
        Assert.False(health.IsHealthy);
        Assert.Equal("health-check-failed", health.ErrorCode);
    }

    private sealed class RecordingSmsProvider : ISmsProvider
    {
        public string Key => "recording";

        public List<SmsProviderSendRequest> Requests { get; } = [];

        public Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new SmsProviderSendResult(
                true,
                "provider-message-42",
                "accepted"));
        }

        public Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
            string providerMessageId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderMessageStatusResult(
                providerMessageId,
                "delivered",
                new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 30, 9, 59, 0, DateTimeKind.Utc)));

        public Task<SmsProviderBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderBalanceResult(
                true,
                125.50m,
                "RUB",
                new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc)));

        public Task<SmsProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderHealthResult(
                true,
                new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(75)));
    }

    private sealed class UnavailableSmsProvider : ISmsProvider
    {
        public string Key => "unavailable";

        public Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderSendResult(
                false,
                null,
                ErrorCode: "gateway-unavailable",
                ErrorMessage: "Gateway is temporarily unavailable.",
                RetryAfter: TimeSpan.FromSeconds(30)));

        public Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
            string providerMessageId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderMessageStatusResult(
                providerMessageId,
                "unknown",
                new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
                ErrorCode: "status-unavailable",
                ErrorMessage: "Status is temporarily unavailable."));

        public Task<SmsProviderBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderBalanceResult(
                false,
                null,
                null,
                new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
                "balance-unavailable",
                "Balance is temporarily unavailable."));

        public Task<SmsProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderHealthResult(
                false,
                new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
                ErrorCode: "health-check-failed",
                ErrorMessage: "Health check failed."));
    }
}
