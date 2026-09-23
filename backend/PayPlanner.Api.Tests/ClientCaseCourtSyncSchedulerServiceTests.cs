using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Reflection;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientCaseCourtSyncSchedulerServiceTests
{
    [Fact]
    public async Task RunOnceAsync_UsesDatabaseAdminInsteadOfFirstActiveUser()
    {
        await using var fixture = await SchedulerFixture.CreateAsync();
        await fixture.SeedUserAsync("employee", "employee@example.com");
        await fixture.SeedUserAsync("admin", "admin@example.com");
        var caseId = await fixture.SeedCaseAsync("https://court.test/case-1");

        var processed = await fixture.Scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal([caseId], fixture.Provider.ReadCaseIds);
        Assert.Equal(
            caseId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await fixture.ReadSettingAsync(ClientCaseCourtSyncSchedulerService.CursorCaseIdSettingKey));
    }

    [Fact]
    public async Task RunOnceAsync_AdvancesCursorPastUnsupportedCasesAndDoesNotStarveLaterCase()
    {
        await using var fixture = await SchedulerFixture.CreateAsync(batchSize: 1);
        await fixture.SeedUserAsync("admin", "admin@example.com");
        for (var index = 1; index <= 5; index++)
        {
            await fixture.SeedCaseAsync($"https://unsupported.test/case-{index}");
        }

        var supportedCaseId = await fixture.SeedCaseAsync("https://court.test/case-6");

        var firstRun = await fixture.Scheduler.RunOnceAsync(CancellationToken.None);
        var cursorAfterFirstRun = await fixture.ReadSettingAsync(ClientCaseCourtSyncSchedulerService.CursorCaseIdSettingKey);
        var secondRun = await fixture.Scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, firstRun);
        Assert.Equal("5", cursorAfterFirstRun);
        Assert.Equal(1, secondRun);
        Assert.Equal([supportedCaseId], fixture.Provider.ReadCaseIds);
    }

    [Fact]
    public async Task RunOnceAsync_WrapsCursorAndChecksSupportedCasesInRounds()
    {
        await using var fixture = await SchedulerFixture.CreateAsync(batchSize: 1);
        await fixture.SeedUserAsync("admin", "admin@example.com");
        var firstCaseId = await fixture.SeedCaseAsync("https://court.test/case-1");
        var secondCaseId = await fixture.SeedCaseAsync("https://court.test/case-2");

        Assert.Equal(1, await fixture.Scheduler.RunOnceAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.Scheduler.RunOnceAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.Scheduler.RunOnceAsync(CancellationToken.None));

        Assert.Equal([firstCaseId, secondCaseId, firstCaseId], fixture.Provider.ReadCaseIds);
    }

    [Fact]
    public async Task RunOnceAsync_SkipsCasesRemovedFromMonitoring()
    {
        await using var fixture = await SchedulerFixture.CreateAsync();
        await fixture.SeedUserAsync("admin", "admin@example.com");
        var pausedCaseId = await fixture.SeedCaseAsync("https://court.test/case-1");
        var activeCaseId = await fixture.SeedCaseAsync("https://court.test/case-2");
        await fixture.SetMonitoringAsync(pausedCaseId, false);

        Assert.Equal(1, await fixture.Scheduler.RunOnceAsync(CancellationToken.None));
        Assert.Equal([activeCaseId], fixture.Provider.ReadCaseIds);
    }

    [Fact]
    public async Task RunOnceAsync_NotifiesResponsibleWhenBackgroundSyncFails()
    {
        await using var fixture = await SchedulerFixture.CreateAsync();
        await fixture.SeedUserAsync("admin", "admin@example.com");
        var responsibleId = await fixture.SeedUserAsync("employee", "lawyer@example.com");
        var caseId = await fixture.SeedCaseAsync("https://court.test/case-1", responsibleId);
        fixture.Provider.FailureMessage = "Судебный источник не ответил";

        Assert.Equal(1, await fixture.Scheduler.RunOnceAsync(CancellationToken.None));
        var notification = Assert.Single(fixture.NotificationRecorder.Failures);
        Assert.Equal(caseId, notification.CaseId);
        Assert.Contains(responsibleId, notification.RecipientUserIds);
        Assert.Contains("Судебный источник не ответил", notification.Summary);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotNotifyResponsibleWithoutCaseAccess()
    {
        await using var fixture = await SchedulerFixture.CreateAsync();
        await fixture.SeedUserAsync("admin", "admin@example.com");
        var responsibleId = await fixture.SeedUserAsync("employee", "no-case-access@example.com", canViewCases: false);
        await fixture.SeedCaseAsync("https://court.test/case-1", responsibleId);
        fixture.Provider.FailureMessage = "Судебный источник не ответил";

        Assert.Equal(1, await fixture.Scheduler.RunOnceAsync(CancellationToken.None));
        Assert.Empty(fixture.NotificationRecorder.Failures);
    }

    private sealed class SchedulerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;

        private SchedulerFixture(
            SqliteConnection connection,
            ServiceProvider services,
            RecordingCourtSyncProvider provider,
            RecordingNotificationServiceProxy notificationRecorder,
            ClientCaseCourtSyncSchedulerService scheduler)
        {
            _connection = connection;
            _services = services;
            Provider = provider;
            NotificationRecorder = notificationRecorder;
            Scheduler = scheduler;
        }

        public RecordingCourtSyncProvider Provider { get; }
        public RecordingNotificationServiceProxy NotificationRecorder { get; }
        public ClientCaseCourtSyncSchedulerService Scheduler { get; }

        public static async Task<SchedulerFixture> CreateAsync(int batchSize = 25)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LegalIntegrations:CourtSyncScheduler:BatchSize"] = batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })
                .Build();
            var provider = new RecordingCourtSyncProvider();
            var notifications = DispatchProxy.Create<INotificationService, RecordingNotificationServiceProxy>();
            var notificationRecorder = (RecordingNotificationServiceProxy)(object)notifications;
            var services = new ServiceCollection();
            services.AddDbContext<PaymentContext>(options => options.UseSqlite(connection));
            services.AddOptions();
            services.Configure<AppFeatureFlagsOptions>(options => options.Legal.ExternalCourtSync = true);
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<ICaseDocumentStorage, LocalCaseDocumentStorage>();
            services.AddSingleton<ISudrfCourtSyncService, NoopSudrfCourtSyncService>();
            services.AddSingleton<ICourtSyncProviderRegistry>(provider);
            services.AddSingleton(notifications);
            var serviceProvider = services.BuildServiceProvider();

            await using (var scope = serviceProvider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<PaymentContext>().Database.EnsureCreatedAsync();
            }

            var scheduler = new ClientCaseCourtSyncSchedulerService(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<IOptionsMonitor<AppFeatureFlagsOptions>>(),
                configuration,
                NullLogger<ClientCaseCourtSyncSchedulerService>.Instance);
            return new SchedulerFixture(connection, serviceProvider, provider, notificationRecorder, scheduler);
        }

        public async Task<int> SeedUserAsync(string roleName, string email, bool canViewCases = true)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var user = new User
            {
                Email = email,
                FullName = email,
                PasswordHash = "test",
                IsActive = true,
                IsApproved = true,
                Role = new Role
                {
                    Name = roleName,
                    Description = roleName,
                    Permissions = canViewCases
                        ? [new RolePermission
                        {
                            Section = RolePermissionCatalog.Cases,
                            CanView = true,
                            ViewScope = DataVisibilityScope.All,
                        }]
                        : [],
                },
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user.Id;
        }

        public async Task<int> SeedCaseAsync(string courtUrl, int? responsibleUserId = null)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var client = new Client
            {
                Name = courtUrl,
                IsActive = true,
            };
            var clientCase = new ClientCase
            {
                Client = client,
                Title = courtUrl,
                CaseNumber = courtUrl,
                CourtUrl = courtUrl,
                CourtCaseNumber = courtUrl.Split('/').Last(),
                Status = ClientCaseStatus.Open,
                ResponsibleUserId = responsibleUserId,
            };
            db.ClientCases.Add(clientCase);
            await db.SaveChangesAsync();
            return clientCase.Id;
        }

        public async Task SetMonitoringAsync(int caseId, bool enabled)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var clientCase = await db.ClientCases.SingleAsync(item => item.Id == caseId);
            clientCase.CourtSyncMonitoringEnabled = enabled;
            await db.SaveChangesAsync();
        }

        public async Task<string?> ReadSettingAsync(string key)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PaymentContext>()
                .SystemSettings
                .AsNoTracking()
                .Where(item => item.Key == key)
                .Select(item => item.Value)
                .SingleOrDefaultAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingCourtSyncProvider : ICourtSyncProviderRegistry, ICourtSyncCaseReader
    {
        public List<int> ReadCaseIds { get; } = [];
        public string? FailureMessage { get; set; }
        public string Key => "test";
        public string Label => "Тестовый судебный источник";
        public string SourceType => "test";
        public int SchemaVersion => 1;
        public CourtSyncProviderCapability Capabilities => CourtSyncProviderCapability.ReadCase | CourtSyncProviderCapability.BackgroundSync;
        public IReadOnlyList<string> SupportedHosts { get; } = ["court.test"];

        public IReadOnlyList<CourtSyncProviderDescriptor> GetProviders() => [];

        public ICourtSyncProvider? Resolve(string? sourceUrl)
            => CanHandle(sourceUrl) ? this : null;

        public bool CanHandle(string? sourceUrl)
            => sourceUrl?.Contains("court.test", StringComparison.OrdinalIgnoreCase) == true;

        public Task<SudrfCourtCaseData> ReadCaseAsync(CourtSyncCaseBinding binding, CancellationToken ct)
        {
            var caseId = int.Parse(binding.SourceUrl.Split('-').Last(), System.Globalization.CultureInfo.InvariantCulture);
            ReadCaseIds.Add(caseId);
            if (FailureMessage is not null) throw new SudrfCourtSyncException(FailureMessage);
            return Task.FromResult(new SudrfCourtCaseData
            {
                SourceUrl = binding.SourceUrl,
                CourtName = "Тестовый суд",
                CaseNumber = binding.CourtCaseNumber ?? string.Empty,
            });
        }
    }

    public class RecordingNotificationServiceProxy : DispatchProxy
    {
        public List<(int CaseId, string Summary, IReadOnlyCollection<int> RecipientUserIds)> Failures { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(INotificationService.NotifyCourtSyncFailedAsync) && args is not null)
            {
                Failures.Add((((ClientCase)args[0]!).Id, (string)args[2]!, (IReadOnlyCollection<int>)args[5]!));
                return Task.CompletedTask;
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class NoopSudrfCourtSyncService : ISudrfCourtSyncService
    {
        public bool CanHandle(string? sourceUrl) => false;

        public Task<SudrfCourtCaseData> FetchCaseAsync(string sourceUrl, CancellationToken ct, string? courtCaseNumber = null)
            => throw new NotSupportedException();
    }
}
