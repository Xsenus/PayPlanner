using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class MaxWebhookServiceTests
{
    [Fact]
    public async Task BotStartedVerifiesOneTimeLinkAndReplayIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var link = await fixture.Service.CreateEmployeeLinkAsync(fixture.UserId, CancellationToken.None);
        var update = fixture.Update("bot_started", link.Payload);

        var first = await fixture.Service.ProcessAsync(update, "webhook-secret", CancellationToken.None);
        var replay = await fixture.Service.ProcessAsync(update, "webhook-secret", CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.Equal("link-verified", first.Status);
        Assert.True(replay.Duplicate);
        var stored = await fixture.Db.MaxRecipientLinks.SingleAsync(item => item.PublicId == link.LinkId);
        Assert.Equal(MaxRecipientLinkStatuses.Verified, stored.Status);
        Assert.Equal(9001, stored.MaxUserId);
        Assert.Single(await fixture.Db.MaxInboundEvents.ToListAsync());
    }

    [Fact]
    public async Task WrongSecretAndExpiredTimestampAreRejectedWithoutStorage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var link = await fixture.Service.CreateEmployeeLinkAsync(fixture.UserId, CancellationToken.None);

        var wrongSecret = await fixture.Service.ProcessAsync(
            fixture.Update("bot_started", link.Payload), "wrong-secret", CancellationToken.None);
        var expired = await fixture.Service.ProcessAsync(
            fixture.Update("bot_started", link.Payload, fixture.Now.AddMinutes(-11)), "webhook-secret", CancellationToken.None);

        Assert.False(wrongSecret.Accepted);
        Assert.Equal("invalid-secret", wrongSecret.ErrorCode);
        Assert.False(expired.Accepted);
        Assert.Equal("timestamp-out-of-window", expired.ErrorCode);
        Assert.Empty(await fixture.Db.MaxInboundEvents.ToListAsync());
    }

    [Fact]
    public async Task CallbackNeverApprovesBusinessObjectAndOptOutStopsLink()
    {
        await using var fixture = await Fixture.CreateAsync();
        var link = await fixture.Service.CreateEmployeeLinkAsync(fixture.UserId, CancellationToken.None);
        await fixture.Service.ProcessAsync(fixture.Update("bot_started", link.Payload), "webhook-secret", CancellationToken.None);
        var callback = JsonSerializer.Serialize(new
        {
            update_type = "message_callback",
            timestamp = new DateTimeOffset(fixture.Now).ToUnixTimeMilliseconds(),
            user = new { user_id = 9001 },
            chat_id = 8001,
            callback = new { callback_id = "approve-contract-5" }
        });

        var result = await fixture.Service.ProcessAsync(callback, "webhook-secret", CancellationToken.None);
        var optedOut = await fixture.Service.OptOutEmployeeAsync(fixture.UserId, CancellationToken.None);

        Assert.Equal("callback-recorded-no-decision", result.Status);
        Assert.True(optedOut);
        Assert.Equal(MaxRecipientLinkStatuses.OptedOut,
            (await fixture.Db.MaxRecipientLinks.SingleAsync(item => item.PublicId == link.LinkId)).Status);
    }

    [Fact]
    public async Task DisabledChannelRejectsWebhookBeforeSecretValidation()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        var result = await fixture.Service.ProcessAsync(fixture.Update("bot_started", "token"), null, CancellationToken.None);
        Assert.False(result.Accepted);
        Assert.Equal("channel-disabled", result.ErrorCode);
        Assert.Empty(await fixture.Db.MaxInboundEvents.ToListAsync());
    }

    [Fact]
    public async Task MaxAccountCannotBeLinkedToAnotherEmployee()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.CreateEmployeeLinkAsync(fixture.UserId, CancellationToken.None);
        await fixture.Service.ProcessAsync(fixture.Update("bot_started", first.Payload), "webhook-secret", CancellationToken.None);
        var secondUser = new User
        {
            Email = "second@example.test", FullName = "Другой сотрудник", PasswordHash = "hash",
            RoleId = (await fixture.Db.Users.Where(item => item.Id == fixture.UserId).Select(item => item.RoleId).SingleAsync()),
            IsActive = true, IsApproved = true
        };
        fixture.Db.Users.Add(secondUser);
        await fixture.Db.SaveChangesAsync();
        var second = await fixture.Service.CreateEmployeeLinkAsync(secondUser.Id, CancellationToken.None);

        var result = await fixture.Service.ProcessAsync(
            fixture.Update("bot_started", second.Payload), "webhook-secret", CancellationToken.None);

        Assert.Equal("link-conflict", result.Status);
        Assert.Equal(MaxRecipientLinkStatuses.Pending,
            (await fixture.Db.MaxRecipientLinks.SingleAsync(item => item.PublicId == second.LinkId)).Status);
    }

    [Fact]
    public async Task PortalAccountCanLinkAndOptOutWithoutEmployeeIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = new Client { Name = "Клиент для портала" };
        var account = new ClientPortalAccount
        {
            Client = client,
            Email = "client@example.test",
            DisplayName = "Клиент",
            AccountType = "client",
            IsActive = true
        };
        fixture.Db.ClientPortalAccounts.Add(account);
        await fixture.Db.SaveChangesAsync();

        var link = await fixture.Service.CreatePortalLinkAsync(account.Id, CancellationToken.None);
        var result = await fixture.Service.ProcessAsync(
            fixture.Update("bot_started", link.Payload), "webhook-secret", CancellationToken.None);
        var optedOut = await fixture.Service.OptOutPortalAsync(account.Id, CancellationToken.None);
        var stored = await fixture.Db.MaxRecipientLinks.SingleAsync(item => item.PublicId == link.LinkId);

        Assert.True(result.Accepted);
        Assert.Equal("link-verified", result.Status);
        Assert.Null(stored.UserId);
        Assert.Equal(account.Id, stored.ClientPortalAccountId);
        Assert.True(optedOut);
        Assert.Equal(MaxRecipientLinkStatuses.OptedOut, stored.Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DirectoryInfo _keys;
        public PaymentContext Db { get; }
        public MaxWebhookService Service { get; }
        public int UserId { get; }
        public DateTime Now { get; } = new(2026, 7, 31, 15, 0, 0, DateTimeKind.Utc);

        private Fixture(SqliteConnection connection, DirectoryInfo keys, PaymentContext db, MaxWebhookService service, int userId)
        {
            _connection = connection;
            _keys = keys;
            Db = db;
            Service = service;
            UserId = userId;
        }

        public string Update(string type, string payload, DateTime? timestamp = null) => JsonSerializer.Serialize(new
        {
            update_type = type,
            timestamp = new DateTimeOffset(timestamp ?? Now).ToUnixTimeMilliseconds(),
            chat_id = 8001,
            user = new { user_id = 9001, name = "Тест" },
            payload
        });

        public static async Task<Fixture> CreateAsync(bool enabled = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options, useObjectAuditModel: false);
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = "employee", Description = "Сотрудник" };
            var user = new User
            {
                Email = "employee@example.test", FullName = "Сотрудник", PasswordHash = "hash",
                Role = role, IsActive = true, IsApproved = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var keys = Directory.CreateTempSubdirectory("pp-max-webhook-");
            var settings = new SystemMaxNotificationService(db, DataProtectionProvider.Create(keys));
            await settings.UpdateSettingsAsync(new UpdateMaxNotificationSettingsRequest
            {
                Enabled = enabled,
                BotToken = "bot-token",
                KeepExistingBotToken = false,
                WebhookSecret = "webhook-secret",
                KeepExistingWebhookSecret = false,
                NotificationsEnabled = true,
                CorrespondenceEnabled = true
            });
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MaxMessenger:BotUsername"] = "TestBot"
            }).Build();
            var time = new FixedTimeProvider(new DateTimeOffset(2026, 7, 31, 15, 0, 0, TimeSpan.Zero));
            return new Fixture(connection, keys, db, new MaxWebhookService(db, settings, configuration, time), user.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            _keys.Delete(true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
