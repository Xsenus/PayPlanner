using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TelegramNotificationServiceHttpErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "telegram 401")]
    [InlineData(HttpStatusCode.Forbidden, "telegram 403")]
    [InlineData(HttpStatusCode.NotFound, "telegram 404")]
    [InlineData(HttpStatusCode.TooManyRequests, "telegram 429")]
    [InlineData(HttpStatusCode.InternalServerError, "telegram 500")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "telegram 503")]
    public async Task SendMessageWithTokenAsync_ReturnsFailedForTelegramHttpErrors(
        HttpStatusCode statusCode,
        string expectedError)
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db, new StaticHttpClientFactory(new HttpClient(new TelegramErrorHandler(statusCode, expectedError))));

        var result = await service.SendMessageWithTokenAsync(100500, "Тест", "bot-token", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(NotificationDeliveryStatus.Failed, result.Status);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public async Task SendMessageWithTokenAsync_ReturnsFailedForExternalTimeout()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db, new StaticHttpClientFactory(new HttpClient(new ThrowingTelegramHandler(_ =>
            new TaskCanceledException("Telegram timeout.")))));

        var result = await service.SendMessageWithTokenAsync(100500, "Тест", "bot-token", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(NotificationDeliveryStatus.Failed, result.Status);
        Assert.Contains("timeout", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendMessageWithTokenAsync_PreservesCallerCancellation()
    {
        await using var db = await CreateContextAsync();
        var service = CreateService(db, new StaticHttpClientFactory(new HttpClient(new ThrowingTelegramHandler(_ =>
            new TaskCanceledException("Caller cancelled.")))));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            service.SendMessageWithTokenAsync(100500, "Тест", "bot-token", cts.Token));
    }

    private static TelegramNotificationService CreateService(PaymentContext db, IHttpClientFactory httpClientFactory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new TelegramNotificationService(
            db,
            configuration,
            httpClientFactory,
            NullLogger<TelegramNotificationService>.Instance);
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options, useObjectAuditModel: false);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TelegramErrorHandler(HttpStatusCode statusCode, string description) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    $$"""{"ok":false,"description":"{{description}}"}""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class ThrowingTelegramHandler(Func<HttpRequestMessage, Exception> resolveException) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(resolveException(request));
    }
}
