using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RussianSmsProviderTests
{
    [Fact]
    public async Task CredentialBackfill_ProtectsLegacyPlaintextTokenAndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new PaymentContext(options, useObjectAuditModel: false);
        await context.Database.EnsureCreatedAsync();
        context.SystemSettings.Add(new SystemSetting
        {
            Key = SystemSmsNotificationService.ApiTokenSettingKey,
            Value = "legacy-secret",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        var keyDirectory = Directory.CreateTempSubdirectory("pp-sms-keys-");

        try
        {
            var dataProtectionProvider = DataProtectionProvider.Create(keyDirectory);
            await SmsCredentialProtectionBackfillService.EnsureProtectedAsync(
                context,
                dataProtectionProvider);
            var protectedValue = await context.SystemSettings
                .Where(item => item.Key == SystemSmsNotificationService.ApiTokenSettingKey)
                .Select(item => item.Value)
                .SingleAsync();

            Assert.StartsWith("dp:v1:", protectedValue, StringComparison.Ordinal);
            Assert.DoesNotContain("legacy-secret", protectedValue, StringComparison.Ordinal);

            await SmsCredentialProtectionBackfillService.EnsureProtectedAsync(
                context,
                dataProtectionProvider);
            Assert.Equal(
                protectedValue,
                await context.SystemSettings
                    .Where(item => item.Key == SystemSmsNotificationService.ApiTokenSettingKey)
                    .Select(item => item.Value)
                    .SingleAsync());
        }
        finally
        {
            keyDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SmsRu_SendUsesFormAndParsesProviderMessageId()
    {
        var handler = new RecordingHandler(_ => Json(
            """{"status":"OK","sms":{"79991112233":{"status_code":100,"sms_id":"sms-42"}}}"""));
        var provider = new SmsRuProvider(
            CreateClient(handler, "https://sms.ru/"),
            new FixedCredentialStore());

        var result = await provider.SendAsync(CreateRequest());

        Assert.True(result.Accepted);
        Assert.Equal("sms-42", result.ProviderMessageId);
        Assert.Equal(HttpMethod.Post, handler.RequestMethod);
        Assert.Equal("/sms/send", handler.RequestUri?.AbsolutePath);
        Assert.Contains("api_id=test-token", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("to=79991112233", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", handler.RequestUri?.Query ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmscRu_SendUsesApiKeyAndParsesNumericId()
    {
        var handler = new RecordingHandler(_ => Json("""{"id":715,"cnt":1}"""));
        var provider = new SmscRuProvider(
            CreateClient(handler, "https://smsc.ru/"),
            new FixedCredentialStore());

        var result = await provider.SendAsync(CreateRequest());

        Assert.True(result.Accepted);
        Assert.Equal("715", result.ProviderMessageId);
        Assert.Equal("/sys/send.php", handler.RequestUri?.AbsolutePath);
        Assert.Contains("apikey=test-token", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("fmt=3", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmsAero_SendUsesBasicAuthenticationAndParsesId()
    {
        var handler = new RecordingHandler(_ => Json("""{"success":true,"data":{"id":812}}"""));
        var provider = new SmsAeroProvider(
            CreateClient(handler, "https://gate.smsaero.ru/"),
            new FixedCredentialStore());

        var result = await provider.SendAsync(CreateRequest(account: "owner@example.com"));

        Assert.True(result.Accepted);
        Assert.Equal("812", result.ProviderMessageId);
        Assert.Equal("/v2/sms/send", handler.RequestUri?.AbsolutePath);
        Assert.Equal("Basic", handler.Authorization?.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("owner@example.com:test-token")),
            handler.Authorization?.Parameter);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("+1 555 000-00-00")]
    [InlineData("")]
    public async Task Providers_RejectInvalidRussianPhoneBeforeNetwork(string phone)
    {
        var handler = new RecordingHandler(_ => Json("""{"status":"OK"}"""));
        var provider = new SmsRuProvider(
            CreateClient(handler, "https://sms.ru/"),
            new FixedCredentialStore());

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SendAsync(CreateRequest(phone)));

        Assert.Equal(0, handler.CallCount);
    }

    private static SmsProviderSendRequest CreateRequest(
        string phone = "+7 (999) 111-22-33",
        string account = "")
        => new(
            phone,
            "Тестовое сообщение",
            "PayPlanner",
            "pp-sms-v1-test",
            account,
            "test-token");

    private static HttpClient CreateClient(HttpMessageHandler handler, string baseAddress)
        => new(handler)
        {
            BaseAddress = new Uri(baseAddress),
        };

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class FixedCredentialStore : ISmsProviderCredentialStore
    {
        public Task<SmsProviderCredentials> LoadAsync(
            string providerKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SmsProviderCredentials(
                providerKey,
                "owner@example.com",
                "PayPlanner",
                "test-token"));
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpMethod? RequestMethod { get; private set; }
        public Uri? RequestUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
