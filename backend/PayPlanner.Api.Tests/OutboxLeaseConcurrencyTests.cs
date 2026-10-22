using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class OutboxLeaseConcurrencyTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    private const string PostgreSqlDatabasePrefix = "payplanner_outbox_lease_";

    [Fact]
    public async Task ParallelWorkersSendEachMaxAndDocumentOutboxItemOnlyOnce()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(PostgreSqlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                $"{PostgreSqlEnvironmentVariable} must be configured in CI.");
            return;
        }

        var databaseName = PostgreSqlDatabasePrefix + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        };
        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        try
        {
            await using (var create = adminConnection.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                Pooling = false,
            };
            await using (var migrationDb = new PostgreSqlPaymentContext(CreateOptions(testBuilder.ConnectionString)))
            {
                await migrationDb.Database.MigrateAsync();
            }

            var protection = new EphemeralDataProtectionProvider();
            await SeedOutboxesAsync(testBuilder.ConnectionString, protection);
            await VerifyMaxLeaseAsync(testBuilder.ConnectionString, protection);
            await VerifyDocumentLeaseAsync(testBuilder.ConnectionString, protection);
        }
        finally
        {
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(adminConnection, databaseName);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static async Task SeedOutboxesAsync(string connectionString, IDataProtectionProvider protection)
    {
        await using var db = new PostgreSqlPaymentContext(CreateOptions(connectionString));
        var maxProtector = protection.CreateProtector("PayPlanner.Notifications.Max.Outbox.v1");
        var documentProtector = protection.CreateProtector("PayPlanner.MessengerDocumentDelivery.Address.v1");
        var now = DateTime.UtcNow;
        var role = new Role { Name = "outbox-test", Description = "Outbox test role" };
        var user = new User
        {
            Email = "outbox-test@example.test",
            FullName = "Outbox Test",
            PasswordHash = "hash",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };
        var maxLink = new MaxRecipientLink
        {
            MaxUserId = 7001,
            MaxChatId = 7001,
            Status = MaxRecipientLinkStatuses.Verified,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
            LinkTokenExpiresAt = now.AddDays(1),
            VerifiedAt = now,
        };
        var documentBinding = new MessengerRecipientBinding
        {
            Channel = MessengerDocumentChannels.Telegram,
            Provider = "telegram-bot",
            DisplayName = "Тестовый получатель",
            ProtectedAddress = documentProtector.Protect("7001"),
            AddressHash = new string('a', 64),
            MaskedAddress = "***7001",
            VerificationStatus = "verified",
            VerifiedAt = now,
            IsActive = true,
        };
        db.AddRange(role, user, maxLink, documentBinding);
        await db.SaveChangesAsync();

        db.MaxOutboxMessages.Add(new MaxOutboxMessage
        {
            EventKey = "parallel-max",
            Scenario = MaxMessengerScenarios.Notifications,
            RecipientLinkId = maxLink.Id,
            ProtectedText = maxProtector.Protect("Безопасное уведомление"),
            BodyPreview = "Содержимое скрыто: сообщение MAX.",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = MaxOutboxStatuses.Queued,
            NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.MessengerDocumentDeliveries.Add(new MessengerDocumentDelivery
        {
            RecipientBindingId = documentBinding.Id,
            DocumentType = "invoice",
            DocumentId = 1,
            OriginalFileName = "invoice.pdf",
            SnapshotPath = "snapshot.pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            ChecksumSha256 = new string('B', 64),
            Channel = MessengerDocumentChannels.Telegram,
            Provider = "telegram-bot",
            Status = MessengerDocumentDeliveryStatuses.Queued,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ProtectedLinkToken = documentProtector.Protect("one-time-token"),
            LinkExpiresAt = now.AddHours(1),
            NextAttemptAt = now.AddMinutes(-1),
            CreatedByUserId = user.Id,
            CreatedAt = now,
            QueuedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task VerifyMaxLeaseAsync(string connectionString, IDataProtectionProvider protection)
    {
        var barrier = new OutboxSelectBarrier("MaxOutboxMessages");
        await using var firstDb = new PostgreSqlPaymentContext(CreateOptions(connectionString, barrier));
        await using var secondDb = new PostgreSqlPaymentContext(CreateOptions(connectionString, barrier));
        var api = new RecordingMaxApiClient();
        var settings = new ReadyMaxSettings();
        var time = TimeProvider.System;
        var first = new MaxOutboxProcessor(firstDb, settings, api, protection, time, NullLogger<MaxOutboxProcessor>.Instance);
        var second = new MaxOutboxProcessor(secondDb, settings, api, protection, time, NullLogger<MaxOutboxProcessor>.Instance);

        var processed = await Task.WhenAll(
            first.ProcessBatchAsync(CancellationToken.None),
            second.ProcessBatchAsync(CancellationToken.None));

        Assert.Equal(1, processed.Sum());
        Assert.Equal(1, api.SendCount);
        await using var verifyDb = new PostgreSqlPaymentContext(CreateOptions(connectionString));
        Assert.Equal(MaxOutboxStatuses.Sent, (await verifyDb.MaxOutboxMessages.AsNoTracking().SingleAsync()).Status);
    }

    private static async Task VerifyDocumentLeaseAsync(string connectionString, IDataProtectionProvider protection)
    {
        var barrier = new OutboxSelectBarrier("MessengerDocumentDeliveries");
        await using var firstDb = new PostgreSqlPaymentContext(CreateOptions(connectionString, barrier));
        await using var secondDb = new PostgreSqlPaymentContext(CreateOptions(connectionString, barrier));
        var provider = new RecordingDocumentProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MessengerDocumentDelivery:Enabled"] = "true",
            ["MessengerDocumentDelivery:PublicBaseUrl"] = "https://crm.example.test",
        }).Build();
        var first = new MessengerDocumentOutboxProcessor(
            firstDb, configuration, [provider], protection, NullLogger<MessengerDocumentOutboxProcessor>.Instance);
        var second = new MessengerDocumentOutboxProcessor(
            secondDb, configuration, [provider], protection, NullLogger<MessengerDocumentOutboxProcessor>.Instance);

        var processed = await Task.WhenAll(
            first.ProcessBatchAsync(CancellationToken.None),
            second.ProcessBatchAsync(CancellationToken.None));

        Assert.Equal(1, processed.Sum());
        Assert.Equal(1, provider.SendCount);
        await using var verifyDb = new PostgreSqlPaymentContext(CreateOptions(connectionString));
        Assert.Equal(
            MessengerDocumentDeliveryStatuses.Sent,
            (await verifyDb.MessengerDocumentDeliveries.AsNoTracking().SingleAsync()).Status);
    }

    private static DbContextOptions<PostgreSqlPaymentContext> CreateOptions(
        string connectionString,
        IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }
        return builder.Options;
    }

    private sealed class OutboxSelectBarrier(string tableName) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(tableName, StringComparison.Ordinal))
            {
                var arrival = Interlocked.Increment(ref _arrivals);
                if (arrival == 2)
                {
                    _release.TrySetResult();
                }
                if (arrival <= 2)
                {
                    await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                }
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ReadyMaxSettings : IMaxNotificationService
    {
        public Task<MaxRuntimeSettings> GetRuntimeSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MaxRuntimeSettings(
                true, false, "bot-token", "webhook-secret", true, true, true, true,
                false, new TimeOnly(22, 0), new TimeOnly(8, 0), "UTC", 20, 30));

        public Task<MaxNotificationDiagnosticsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MaxNotificationDiagnosticsDto> UpdateSettingsAsync(
            UpdateMaxNotificationSettingsRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingMaxApiClient : IMaxApiClient
    {
        private int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);

        public Task<MaxApiResult> CheckHealthAsync(string botToken, CancellationToken cancellationToken = default)
            => Task.FromResult(new MaxApiResult(true));

        public Task<MaxApiResult> SendTextAsync(
            long userId,
            string text,
            string botToken,
            string? actionText = null,
            Uri? actionUrl = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sendCount);
            return Task.FromResult(new MaxApiResult(true, "max-message"));
        }

        public Task<MaxUploadSlotResult> CreateUploadSlotAsync(
            string mediaType,
            string botToken,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MaxUploadSlotResult(false));
    }

    private sealed class RecordingDocumentProvider : IMessengerDocumentProvider
    {
        private int _sendCount;
        public string Channel => MessengerDocumentChannels.Telegram;
        public int SendCount => Volatile.Read(ref _sendCount);

        public Task<MessengerDocumentProviderResult> SendAsync(MessengerDocumentProviderRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _sendCount);
            return Task.FromResult(new MessengerDocumentProviderResult(
                true,
                MessengerDocumentDeliveryStatuses.Sent,
                "document-message"));
        }
    }
}
