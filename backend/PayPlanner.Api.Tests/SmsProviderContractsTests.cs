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
        Assert.Equal(SmsProviderMessageStatuses.Accepted, sent.Status);
        Assert.Equal(sent.ProviderMessageId, status.ProviderMessageId);
        Assert.Equal(SmsProviderMessageStatuses.Delivered, status.Status);
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

    [Theory]
    [InlineData("accepted", SmsProviderMessageStatuses.Accepted)]
    [InlineData(" QUEUED ", SmsProviderMessageStatuses.Accepted)]
    [InlineData("sent", SmsProviderMessageStatuses.Accepted)]
    [InlineData("delivrd", SmsProviderMessageStatuses.Delivered)]
    [InlineData("delivered-to-handset", SmsProviderMessageStatuses.Delivered)]
    [InlineData("undelivered", SmsProviderMessageStatuses.Failed)]
    [InlineData("ttl expired", SmsProviderMessageStatuses.Expired)]
    [InlineData("blocked", SmsProviderMessageStatuses.Rejected)]
    [InlineData("provider-specific-new-status", SmsProviderMessageStatuses.Unknown)]
    [InlineData(null, SmsProviderMessageStatuses.Unknown)]
    public void NormalizeStatus_MapsProviderValuesToStableStatuses(string? value, string expected)
    {
        Assert.Equal(expected, SmsProviderStatusNormalizer.NormalizeStatus(value));
    }

    [Fact]
    public void NormalizeMessageId_TrimsAndValidatesProviderIdentifiers()
    {
        Assert.Equal("provider-message-42", SmsProviderStatusNormalizer.NormalizeMessageId("  provider-message-42  "));
        Assert.Null(SmsProviderStatusNormalizer.NormalizeMessageId(" "));
        Assert.Throws<ArgumentException>(() => SmsProviderStatusNormalizer.NormalizeMessageId("message\r\nid"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SmsProviderStatusNormalizer.NormalizeMessageId(new string('x', SmsProviderStatusNormalizer.MaxProviderMessageIdLength + 1)));
    }

    [Fact]
    public void IdempotencyKey_IsStableDistinctAndDoesNotExposeSourceData()
    {
        var first = SmsProviderIdempotencyKey.Create("task:42:reminder", "+79990000000", 7);
        var repeated = SmsProviderIdempotencyKey.Create(" task:42:reminder ", " +79990000000 ", 7);
        var anotherEvent = SmsProviderIdempotencyKey.Create("task:42:overdue", "+79990000000", 7);
        var anotherRecipient = SmsProviderIdempotencyKey.Create("task:42:reminder", "+79991111111", 7);
        var anotherSender = SmsProviderIdempotencyKey.Create("task:42:reminder", "+79990000000", 8);

        Assert.Equal(first, repeated);
        Assert.StartsWith(SmsProviderIdempotencyKey.Prefix, first, StringComparison.Ordinal);
        Assert.True(first.Length <= SmsProviderIdempotencyKey.MaxLength);
        Assert.DoesNotContain("task", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("7999", first, StringComparison.Ordinal);
        Assert.NotEqual(first, anotherEvent);
        Assert.NotEqual(first, anotherRecipient);
        Assert.NotEqual(first, anotherSender);
    }

    [Theory]
    [InlineData(null, "+79990000000")]
    [InlineData(" ", "+79990000000")]
    [InlineData("task:42", null)]
    [InlineData("task:42", " ")]
    public void IdempotencyKey_RejectsMissingIdentityParts(string? eventKey, string? recipientAddress)
    {
        Assert.Throws<ArgumentException>(() =>
            SmsProviderIdempotencyKey.Create(eventKey!, recipientAddress!, null));
    }

    [Fact]
    public void NormalizedStatuses_ExposeCompleteAndTerminalSets()
    {
        Assert.Equal(
            new[]
            {
                SmsProviderMessageStatuses.Accepted,
                SmsProviderMessageStatuses.Delivered,
                SmsProviderMessageStatuses.Expired,
                SmsProviderMessageStatuses.Failed,
                SmsProviderMessageStatuses.Rejected,
                SmsProviderMessageStatuses.Unknown,
            },
            SmsProviderMessageStatuses.All.OrderBy(value => value, StringComparer.Ordinal));
        Assert.DoesNotContain(SmsProviderMessageStatuses.Accepted, SmsProviderMessageStatuses.Terminal);
        Assert.DoesNotContain(SmsProviderMessageStatuses.Unknown, SmsProviderMessageStatuses.Terminal);
        Assert.Contains(SmsProviderMessageStatuses.Delivered, SmsProviderMessageStatuses.Terminal);
        Assert.Contains(SmsProviderMessageStatuses.Failed, SmsProviderMessageStatuses.Terminal);
        Assert.Contains(SmsProviderMessageStatuses.Expired, SmsProviderMessageStatuses.Terminal);
        Assert.Contains(SmsProviderMessageStatuses.Rejected, SmsProviderMessageStatuses.Terminal);
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
                SmsProviderStatusNormalizer.NormalizeMessageId(" provider-message-42 "),
                SmsProviderStatusNormalizer.NormalizeStatus("queued"),
                "queued"));
        }

        public Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
            string providerMessageId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderMessageStatusResult(
                SmsProviderStatusNormalizer.NormalizeMessageId(providerMessageId)!,
                SmsProviderStatusNormalizer.NormalizeStatus("delivrd"),
                new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
                "delivrd",
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
                SmsProviderMessageStatuses.Failed,
                ErrorCode: "gateway-unavailable",
                ErrorMessage: "Gateway is temporarily unavailable.",
                RetryAfter: TimeSpan.FromSeconds(30)));

        public Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
            string providerMessageId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderMessageStatusResult(
                providerMessageId,
                SmsProviderMessageStatuses.Unknown,
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
