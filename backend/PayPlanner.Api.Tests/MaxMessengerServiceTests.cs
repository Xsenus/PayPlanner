using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class MaxMessengerServiceTests
{
    [Fact]
    public async Task ApiClient_SendsTokenOnlyInAuthorizationHeaderAndBuildsSafeLinkButton()
    {
        var handler = new RecordingHandler(_ => Json(
            """{"message":{"body":{"mid":"mid.42"}}}"""));
        var client = new MaxApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://platform-api2.max.ru/"),
        });

        var result = await client.SendTextAsync(
            123456,
            "Проверка MAX",
            "secret.bot.token",
            "Открыть CRM",
            new Uri("https://crm.example.test/open/opaque"));

        Assert.True(result.Success);
        Assert.Equal("mid.42", result.ExternalMessageId);
        Assert.Equal("secret.bot.token", handler.Authorization);
        Assert.DoesNotContain("secret.bot.token", handler.RequestUri?.AbsoluteUri ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"link\"", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("https://crm.example.test/open/opaque", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiClient_RejectsNonHttpsActionBeforeNetwork()
    {
        var handler = new RecordingHandler(_ => Json("{}"));
        var client = new MaxApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://platform-api2.max.ru/"),
        });

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendTextAsync(
            123456,
            "Проверка MAX",
            "secret.bot.token",
            "Открыть",
            new Uri("http://crm.example.test/open")));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Settings_StayDisabledAndProtectBothSecrets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new PaymentContext(options, useObjectAuditModel: false);
        await context.Database.EnsureCreatedAsync();
        var keyDirectory = Directory.CreateTempSubdirectory("pp-max-keys-");

        try
        {
            var service = new SystemMaxNotificationService(
                context,
                DataProtectionProvider.Create(keyDirectory));
            var settings = await service.UpdateSettingsAsync(new UpdateMaxNotificationSettingsRequest
            {
                Enabled = false,
                BotToken = "max-bot-token",
                KeepExistingBotToken = false,
                WebhookSecret = "max-webhook-secret",
                KeepExistingWebhookSecret = false,
                NotificationsEnabled = true,
                DocumentsEnabled = true,
                CorrespondenceEnabled = true,
                ConfirmationsEnabled = true,
                TechnicalPayloadRetentionDays = 120,
            });

            Assert.False(settings.Enabled);
            Assert.False(settings.Ready);
            Assert.True(settings.ApiConfirmed);
            Assert.True(settings.BotTokenSet);
            Assert.True(settings.WebhookSecretSet);
            Assert.Equal(90, settings.TechnicalPayloadRetentionDays);
            Assert.Equal(4, settings.Scenarios.Count);
            var storedSecrets = await context.SystemSettings
                .Where(item => item.Key == SystemMaxNotificationService.BotTokenKey
                    || item.Key == SystemMaxNotificationService.WebhookSecretKey)
                .Select(item => item.Value)
                .ToArrayAsync();
            Assert.All(storedSecrets, value => Assert.StartsWith("dp:v1:", value, StringComparison.Ordinal));
            Assert.DoesNotContain(storedSecrets, value => value.Contains("max-bot-token", StringComparison.Ordinal));
            Assert.DoesNotContain(storedSecrets, value => value.Contains("max-webhook-secret", StringComparison.Ordinal));
        }
        finally
        {
            keyDirectory.Delete(recursive: true);
        }
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.TryGetValues("Authorization", out var values)
                ? values.Single()
                : null;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
