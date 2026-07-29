using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services.LegalIntegrations;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CourtSyncMetricsRollupServiceTests
{
    [Fact]
    public async Task RunOnceAsync_PersistsDailyAggregateInSingleBoundedSetting()
    {
        await using var fixture = await TestFixture.CreateAsync(retentionDays: 7);
        fixture.Metrics.RecordRequest(TimeSpan.FromMilliseconds(100), failed: false);
        fixture.Metrics.RecordRequest(TimeSpan.FromMilliseconds(300), failed: true);
        fixture.Metrics.RecordDuplicateSkipped();
        fixture.Metrics.RecordDocument(2_048);

        var first = await fixture.Service.RunOnceAsync(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));
        Assert.Equal(2, first.RequestCount);
        Assert.Equal(1, first.FailedRequestCount);
        Assert.Equal(200, first.AverageLatencyMs);
        Assert.Equal(300, first.MaxLatencyMs);

        fixture.Metrics.RecordRequest(TimeSpan.FromMilliseconds(50), failed: false);
        fixture.Metrics.RecordDocument(1_024);
        var second = await fixture.Service.RunOnceAsync(new DateTime(2026, 7, 2, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(3, second.RequestCount);
        Assert.Equal(2, second.DocumentCount);
        Assert.Equal(3_072, second.DocumentBytes);
        Assert.Equal("2026-07-01", second.PeriodStartedOn);
        Assert.Equal("2026-07-02", second.PeriodEndedOn);

        await using var context = fixture.CreateContext();
        Assert.Equal(1, await context.SystemSettings.CountAsync(
            item => item.Key == CourtSyncMetricsRollupService.ArchiveSettingKey));
    }

    [Fact]
    public async Task RunOnceAsync_PrunesExpiredDaysAndKeepsNewMetrics()
    {
        await using var fixture = await TestFixture.CreateAsync(retentionDays: 7);
        fixture.Metrics.RecordRequest(TimeSpan.FromMilliseconds(100), failed: false);
        await fixture.Service.RunOnceAsync(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));

        fixture.Metrics.RecordRequest(TimeSpan.FromMilliseconds(250), failed: true);
        var result = await fixture.Service.RunOnceAsync(new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, result.RequestCount);
        Assert.Equal(1, result.FailedRequestCount);
        Assert.Equal(250, result.AverageLatencyMs);
        Assert.Equal("2026-07-10", result.PeriodStartedOn);
        Assert.Equal("2026-07-10", result.PeriodEndedOn);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(90, 90)]
    [InlineData(1000, 365)]
    public void NormalizeRetentionDays_UsesSafeBounds(int value, int expected)
        => Assert.Equal(expected, CourtSyncMetricsRollupService.NormalizeRetentionDays(value));

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly DbContextOptions<PaymentContext> _options;

        private TestFixture(
            SqliteConnection connection,
            ServiceProvider provider,
            DbContextOptions<PaymentContext> options,
            CourtSyncMetrics metrics,
            CourtSyncMetricsRollupService service)
        {
            _connection = connection;
            _provider = provider;
            _options = options;
            Metrics = metrics;
            Service = service;
        }

        public CourtSyncMetrics Metrics { get; }
        public CourtSyncMetricsRollupService Service { get; }

        public static async Task<TestFixture> CreateAsync(int retentionDays)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection)
                .Options;

            await using (var context = new PaymentContext(options, useObjectAuditModel: false))
            {
                await context.Database.EnsureCreatedAsync();
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => new PaymentContext(options, useObjectAuditModel: false));
            var provider = services.BuildServiceProvider();
            var metrics = new CourtSyncMetrics();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LegalIntegrations:CourtSyncMetrics:RetentionDays"] = retentionDays.ToString(),
                    ["LegalIntegrations:CourtSyncMetrics:IntervalHours"] = "24",
                })
                .Build();
            var service = new CourtSyncMetricsRollupService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                metrics,
                NullLogger<CourtSyncMetricsRollupService>.Instance);

            return new TestFixture(connection, provider, options, metrics, service);
        }

        public PaymentContext CreateContext()
            => new(_options, useObjectAuditModel: false);

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
