using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ExternalWorkOrderApprovalServiceTests
{
    [Fact]
    public async Task Create_open_and_approve_uses_immutable_public_snapshot()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var status = await fixture.Service.CreateAsync(
            fixture.CaseId,
            new CreateExternalWorkOrderApprovalRequest
            {
                RecipientName = "Иван Иванов",
                RecipientEmail = "client@example.test",
                RecipientType = "client",
            },
            7,
            "Менеджер",
            CancellationToken.None);

        var entity = await fixture.Db.ExternalWorkOrderApprovalRequests.SingleAsync();
        Assert.DoesNotContain("Внутренняя заметка", entity.SnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Секретный исполнитель", entity.SnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Email.Token, entity.AccessTokenHash, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Email.Otp, entity.OtpHash, StringComparison.Ordinal);

        var opened = await fixture.Service.OpenAsync(status.PublicId, fixture.Email.Token, CancellationToken.None);
        Assert.Equal("opened", opened.Status);
        Assert.Equal("Подготовка заявления", opened.Snapshot.Items.Single().ServiceName);
        Assert.Equal(15_000m, opened.Snapshot.TotalAmount);

        var result = await fixture.Service.DecideAsync(
            status.PublicId,
            new PublicWorkOrderApprovalDecisionRequest
            {
                Token = fixture.Email.Token,
                Otp = fixture.Email.Otp,
                Decision = "approved",
                Comment = "Согласовано.",
            },
            "127.0.0.1",
            "test-browser",
            CancellationToken.None);

        Assert.Equal("approved", result.Status);
        var workOrder = await fixture.Db.ClientCaseWorkOrders.SingleAsync();
        Assert.Equal("approved", workOrder.Status);
        Assert.Equal("Иван Иванов", workOrder.ApprovalDecisionByUserName);
        Assert.Equal(1, fixture.Notifier.DecisionCount);

        var repeated = await fixture.Service.DecideAsync(
            status.PublicId,
            new PublicWorkOrderApprovalDecisionRequest
            {
                Token = fixture.Email.Token,
                Otp = fixture.Email.Otp,
                Decision = "approved",
            },
            "127.0.0.1",
            "test-browser",
            CancellationToken.None);
        Assert.Equal(result.DecisionAt, repeated.DecisionAt);
        Assert.Equal(1, fixture.Notifier.DecisionCount);
    }

    [Fact]
    public async Task New_version_revokes_previous_link()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var first = await fixture.SendAsync();
        var firstToken = fixture.Email.Token;
        var second = await fixture.SendAsync();

        Assert.Equal(first.Version + 1, second.Version);
        var requests = await fixture.Db.ExternalWorkOrderApprovalRequests
            .OrderBy(item => item.Version)
            .ToListAsync();
        Assert.Equal("revoked", requests[0].Status);
        Assert.Equal("pending", requests[1].Status);
        await Assert.ThrowsAsync<ExternalWorkOrderApprovalException>(
            () => fixture.Service.OpenAsync(first.PublicId, firstToken, CancellationToken.None));
    }

    [Fact]
    public async Task Five_invalid_codes_lock_request()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var status = await fixture.SendAsync();
        var invalidOtp = fixture.Email.Otp == "000000" ? "999999" : "000000";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<ExternalWorkOrderApprovalException>(() =>
                fixture.Service.DecideAsync(
                    status.PublicId,
                    new PublicWorkOrderApprovalDecisionRequest
                    {
                        Token = fixture.Email.Token,
                        Otp = invalidOtp,
                        Decision = "approved",
                    },
                    null,
                    null,
                    CancellationToken.None));
        }

        Assert.Equal("locked", (await fixture.Db.ExternalWorkOrderApprovalRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Feature_is_safely_disabled_by_default()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        var error = await Assert.ThrowsAsync<ExternalWorkOrderApprovalException>(() => fixture.SendAsync());
        Assert.Equal("feature_disabled", error.Code);
        Assert.Empty(await fixture.Db.ExternalWorkOrderApprovalRequests.ToListAsync());
        Assert.Empty(fixture.Email.Messages);
    }

    [Fact]
    public async Task Expired_link_is_persisted_as_expired()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var status = await fixture.SendAsync();
        var entity = await fixture.Db.ExternalWorkOrderApprovalRequests.SingleAsync();
        entity.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ExternalWorkOrderApprovalException>(
            () => fixture.Service.OpenAsync(status.PublicId, fixture.Email.Token, CancellationToken.None));
        Assert.Equal("link_expired", error.Code);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("expired", (await fixture.Db.ExternalWorkOrderApprovalRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Expiration_worker_processes_due_requests_once()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        await fixture.SendAsync();
        var entity = await fixture.Db.ExternalWorkOrderApprovalRequests.SingleAsync();
        entity.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(fixture.Db);
        services.AddSingleton<IExternalWorkOrderApprovalNotifier>(fixture.Notifier);
        using var provider = services.BuildServiceProvider();
        var worker = new ExternalWorkOrderApprovalExpirationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedOptionsMonitor(new AppFeatureFlagsOptions
            {
                Legal = new LegalFeatureFlagsOptions { ExternalWorkOrderApproval = true },
            }),
            NullLogger<ExternalWorkOrderApprovalExpirationService>.Instance);

        Assert.Equal(1, await worker.RunOnceAsync());
        Assert.Equal(1, fixture.Notifier.ExpiredCount);
        fixture.Db.ChangeTracker.Clear();
        var processed = await fixture.Db.ExternalWorkOrderApprovalRequests.SingleAsync();
        Assert.Equal("expired", processed.Status);
        Assert.NotNull(processed.ExpirationNotificationProcessedAt);

        Assert.Equal(0, await worker.RunOnceAsync());
        Assert.Equal(1, fixture.Notifier.ExpiredCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(
            SqliteConnection connection,
            PaymentContext db,
            RecordingEmailService email,
            ExternalWorkOrderApprovalService service,
            RecordingNotifier notifier,
            int caseId)
        {
            _connection = connection;
            Db = db;
            Email = email;
            Service = service;
            Notifier = notifier;
            CaseId = caseId;
        }

        public PaymentContext Db { get; }
        public RecordingEmailService Email { get; }
        public ExternalWorkOrderApprovalService Service { get; }
        public RecordingNotifier Notifier { get; }
        public int CaseId { get; }

        public static async Task<Fixture> CreateAsync(bool enabled)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection)
                .Options;
            var db = new PaymentContext(options);
            await db.Database.EnsureCreatedAsync();

            var client = new Client { Name = "Тестовый клиент", Email = "client@example.test" };
            var clientCase = new ClientCase
            {
                Client = client,
                Title = "Тестовое дело",
                CaseNumber = "A-1",
            };
            var workOrder = new ClientCaseWorkOrder
            {
                ClientCase = clientCase,
                Title = "Заказ-наряд",
                Notes = "Внутренняя заметка",
                TotalAmount = 15_000m,
                Items =
                [
                    new ClientCaseWorkOrderItem
                    {
                        ServiceName = "Подготовка заявления",
                        Description = "Публичное описание",
                        ExecutorName = "Секретный исполнитель",
                        Quantity = 1,
                        UnitPrice = 15_000m,
                        TotalAmount = 15_000m,
                    },
                ],
            };
            db.ClientCaseWorkOrders.Add(workOrder);
            await db.SaveChangesAsync();

            var email = new RecordingEmailService();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["App:FrontendUrl"] = "https://crm.example.test",
                    ["App:OrganizationName"] = "Тестовая компания",
                })
                .Build();
            var flags = new FixedOptionsMonitor(new AppFeatureFlagsOptions
            {
                Legal = new LegalFeatureFlagsOptions { ExternalWorkOrderApproval = enabled },
            });
            var notifier = new RecordingNotifier();
            var service = new ExternalWorkOrderApprovalService(db, email, configuration, flags, notifier);
            return new Fixture(connection, db, email, service, notifier, clientCase.Id);
        }

        public Task<ExternalWorkOrderApprovalStatusDto> SendAsync()
            => Service.CreateAsync(
                CaseId,
                new CreateExternalWorkOrderApprovalRequest
                {
                    RecipientName = "Иван Иванов",
                    RecipientEmail = "client@example.test",
                    RecipientType = "client",
                },
                7,
                "Менеджер",
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingNotifier : IExternalWorkOrderApprovalNotifier
    {
        public int DecisionCount { get; private set; }
        public int ExpiredCount { get; private set; }

        public Task NotifyDecisionAsync(
            ExternalWorkOrderApprovalRequest request,
            ClientCaseWorkOrder workOrder,
            CancellationToken ct = default)
        {
            DecisionCount++;
            return Task.CompletedTask;
        }

        public Task NotifyExpiredAsync(
            ExternalWorkOrderApprovalRequest request,
            ClientCaseWorkOrder workOrder,
            CancellationToken ct = default)
        {
            ExpiredCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedOptionsMonitor : IOptionsMonitor<AppFeatureFlagsOptions>
    {
        public FixedOptionsMonitor(AppFeatureFlagsOptions value) => CurrentValue = value;
        public AppFeatureFlagsOptions CurrentValue { get; }
        public AppFeatureFlagsOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppFeatureFlagsOptions, string?> listener) => null;
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public List<string> Messages { get; } = [];
        public string Token { get; private set; } = string.Empty;
        public string Otp { get; private set; } = string.Empty;

        public Task SendEmailAsync(
            string toEmail,
            string subject,
            string body,
            bool isHtml = false,
            CancellationToken cancellationToken = default,
            EmailSendSettings? settings = null)
        {
            Messages.Add(body);
            var link = Regex.Match(body, @"#token=([^""&<]+)", RegexOptions.CultureInvariant);
            Token = Uri.UnescapeDataString(link.Groups[1].Value);
            Otp = Regex.Match(body, @">(\d{6})</strong>", RegexOptions.CultureInvariant).Groups[1].Value;
            return Task.CompletedTask;
        }

        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }
}
