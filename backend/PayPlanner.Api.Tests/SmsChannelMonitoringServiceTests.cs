using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsChannelMonitoringServiceTests
{
    [Fact]
    public void Policy_DoesNotWarnWhenChannelIsDisabled()
    {
        var settings = CreateSettings();
        settings.Enabled = false;

        var decision = SmsChannelMonitoringPolicy.Evaluate(
            settings,
            adapterAvailable: false,
            health: null,
            balance: null);

        Assert.False(decision.ShouldNotify);
        Assert.Null(decision.IssueCode);
    }

    [Theory]
    [InlineData(false, true, "configuration-incomplete")]
    [InlineData(true, false, "adapter-missing")]
    public void Policy_DetectsConfigurationAndAdapterProblems(
        bool providerConfigured,
        bool adapterAvailable,
        string expectedIssueCode)
    {
        var settings = CreateSettings();
        settings.ProviderConfigured = providerConfigured;

        var decision = SmsChannelMonitoringPolicy.Evaluate(
            settings,
            adapterAvailable,
            health: null,
            balance: null);

        Assert.True(decision.ShouldNotify);
        Assert.Equal(expectedIssueCode, decision.IssueCode);
    }

    [Fact]
    public void Policy_DetectsProviderAndBalanceDegradation()
    {
        var settings = CreateSettings();
        var checkedAt = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

        var unhealthy = SmsChannelMonitoringPolicy.Evaluate(
            settings,
            adapterAvailable: true,
            new SmsProviderHealthResult(false, checkedAt, ErrorCode: "provider-unavailable"),
            balance: null);
        var missingBalance = SmsChannelMonitoringPolicy.Evaluate(
            settings,
            adapterAvailable: true,
            new SmsProviderHealthResult(true, checkedAt),
            new SmsProviderBalanceResult(false, null, "RUB", checkedAt));
        var lowBalance = SmsChannelMonitoringPolicy.Evaluate(
            settings,
            adapterAvailable: true,
            new SmsProviderHealthResult(true, checkedAt),
            new SmsProviderBalanceResult(true, 99m, "rub", checkedAt));

        Assert.Equal(SmsChannelMonitoringIssueCodes.ProviderUnhealthy, unhealthy.IssueCode);
        Assert.Equal(SmsChannelMonitoringIssueCodes.BalanceUnavailable, missingBalance.IssueCode);
        Assert.Equal(SmsChannelMonitoringIssueCodes.LowBalance, lowBalance.IssueCode);
        Assert.Contains("99,00 RUB", lowBalance.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_DoesNotWarnForHealthyProviderOrDisabledBalanceThreshold()
    {
        var settings = CreateSettings();
        var checkedAt = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var health = new SmsProviderHealthResult(true, checkedAt);

        var healthy = SmsChannelMonitoringPolicy.Evaluate(
            settings,
            adapterAvailable: true,
            health,
            new SmsProviderBalanceResult(true, 500m, "RUB", checkedAt));
        settings.LowBalanceThreshold = 0;
        var thresholdDisabled = SmsChannelMonitoringPolicy.Evaluate(
            settings,
            adapterAvailable: true,
            health,
            new SmsProviderBalanceResult(true, 0m, "RUB", checkedAt));

        Assert.False(healthy.ShouldNotify);
        Assert.False(thresholdDisabled.ShouldNotify);
    }

    [Fact]
    public async Task RunOnceAsync_WarnsOncePerDayWhenBalanceIsLow()
    {
        var smsService = new FakeSmsNotificationService(CreateSettings());
        var publisher = new DeduplicatingWarningPublisher();
        var provider = new FakeSmsProvider
        {
            Balance = new SmsProviderBalanceResult(
                true,
                100m,
                "RUB",
                new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc)),
        };
        using var services = BuildServices(smsService, publisher, provider);
        var monitor = new SmsChannelMonitoringService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SmsChannelMonitoringService>.Instance,
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 30, 4, 0, 0, TimeSpan.Zero)));

        var first = await monitor.RunOnceAsync();
        var second = await monitor.RunOnceAsync();

        Assert.Equal(SmsChannelMonitoringIssueCodes.LowBalance, first.IssueCode);
        Assert.True(first.NotificationRequested);
        Assert.False(second.NotificationRequested);
        var warning = Assert.Single(publisher.Warnings);
        Assert.Equal("20260730", warning.EventKey);
        Assert.Contains("100,00 RUB", warning.Body, StringComparison.Ordinal);
        Assert.Equal(2, provider.HealthChecks);
        Assert.Equal(2, provider.BalanceChecks);
        Assert.NotNull(smsService.LastDiagnostics);
        Assert.True(smsService.LastDiagnostics.ProviderHealthy);
        Assert.Equal(100m, smsService.LastDiagnostics.Balance);
        Assert.Equal(SmsChannelMonitoringIssueCodes.LowBalance, smsService.LastDiagnostics.IssueCode);
    }

    [Fact]
    public async Task RunOnceAsync_ReportsMissingAdapterWithoutCallingProvider()
    {
        var smsService = new FakeSmsNotificationService(CreateSettings());
        var publisher = new DeduplicatingWarningPublisher();
        using var services = BuildServices(smsService, publisher);
        var monitor = new SmsChannelMonitoringService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SmsChannelMonitoringService>.Instance,
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 30, 4, 0, 0, TimeSpan.Zero)));

        var result = await monitor.RunOnceAsync();

        Assert.True(result.Enabled);
        Assert.False(result.Checked);
        Assert.True(result.NotificationRequested);
        Assert.Equal(SmsChannelMonitoringIssueCodes.AdapterMissing, result.IssueCode);
        Assert.Single(publisher.Warnings);
    }

    [Fact]
    public async Task RunOnceAsync_ConvertsProviderFailureToSafeDegradationWarning()
    {
        var smsService = new FakeSmsNotificationService(CreateSettings());
        var publisher = new DeduplicatingWarningPublisher();
        var provider = new FakeSmsProvider { ThrowOnHealthCheck = true };
        using var services = BuildServices(smsService, publisher, provider);
        var monitor = new SmsChannelMonitoringService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SmsChannelMonitoringService>.Instance,
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 30, 4, 0, 0, TimeSpan.Zero)));

        var result = await monitor.RunOnceAsync();

        Assert.Equal(SmsChannelMonitoringIssueCodes.ProviderUnhealthy, result.IssueCode);
        var warning = Assert.Single(publisher.Warnings);
        Assert.DoesNotContain("provider secret", warning.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunOnceAsync_SkipsDisabledChannel()
    {
        var settings = CreateSettings();
        settings.Enabled = false;
        var smsService = new FakeSmsNotificationService(settings);
        var publisher = new DeduplicatingWarningPublisher();
        var provider = new FakeSmsProvider();
        using var services = BuildServices(smsService, publisher, provider);
        var monitor = new SmsChannelMonitoringService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SmsChannelMonitoringService>.Instance);

        var result = await monitor.RunOnceAsync();

        Assert.False(result.Enabled);
        Assert.False(result.Checked);
        Assert.Empty(publisher.Warnings);
        Assert.Equal(0, provider.HealthChecks);
        Assert.Equal(0, provider.BalanceChecks);
        Assert.NotNull(smsService.LastDiagnostics);
        Assert.Null(smsService.LastDiagnostics.ProviderHealthy);
        Assert.Null(smsService.LastDiagnostics.IssueCode);
    }

    private static ServiceProvider BuildServices(
        ISmsNotificationService smsService,
        ISmsChannelWarningPublisher publisher,
        ISmsProvider? provider = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(smsService)
            .AddSingleton(publisher);
        if (provider is not null)
        {
            services.AddSingleton(provider);
        }

        return services.BuildServiceProvider();
    }

    private static SmsNotificationSettingsDto CreateSettings()
        => new()
        {
            Enabled = true,
            ProviderConfigured = true,
            AdapterConfigured = true,
            ProviderKey = "test-provider",
            SenderName = "PayPlanner",
            LowBalanceThreshold = 500m,
            QuietHoursTimeZone = "Europe/Moscow",
        };

    private sealed class FakeSmsNotificationService(SmsNotificationSettingsDto settings) : ISmsNotificationService
    {
        public SmsProviderDiagnosticsSnapshot? LastDiagnostics { get; private set; }

        public Task<SmsNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(settings);

        public Task<SmsNotificationSettingsDto> UpdateSettingsAsync(
            UpdateSmsNotificationSettingsRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(settings);

        public Task<SmsSendResult> SendMessageAsync(
            string phoneNumber,
            string text,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsSendResult(false, NotificationDeliveryStatus.Skipped));

        public Task SaveDiagnosticsAsync(
            SmsProviderDiagnosticsSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            LastDiagnostics = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class DeduplicatingWarningPublisher : ISmsChannelWarningPublisher
    {
        private readonly HashSet<string> _eventKeys = new(StringComparer.Ordinal);

        public List<(string EventKey, string IssueCode, string Title, string Body)> Warnings { get; } = [];

        public Task<int> PublishAsync(
            string eventKey,
            string issueCode,
            string title,
            string body,
            CancellationToken cancellationToken = default)
        {
            if (!_eventKeys.Add(eventKey))
            {
                return Task.FromResult(0);
            }

            Warnings.Add((eventKey, issueCode, title, body));
            return Task.FromResult(1);
        }
    }

    private sealed class FakeSmsProvider : ISmsProvider
    {
        public string Key => "test-provider";
        public int HealthChecks { get; private set; }
        public int BalanceChecks { get; private set; }
        public SmsProviderHealthResult Health { get; set; } = new(
            true,
            new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc));
        public SmsProviderBalanceResult Balance { get; set; } = new(
            true,
            1_000m,
            "RUB",
            new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc));
        public bool ThrowOnHealthCheck { get; set; }

        public Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
            string providerMessageId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SmsProviderBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default)
        {
            BalanceChecks++;
            return Task.FromResult(Balance);
        }

        public Task<SmsProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            HealthChecks++;
            if (ThrowOnHealthCheck)
            {
                throw new InvalidOperationException("provider secret must not leak");
            }

            return Task.FromResult(Health);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
