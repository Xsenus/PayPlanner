using System.Net;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RestoredRuntimeIsolationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("true")]
    public async Task NamedAndTypedHttpClientsRespectIsolationWithoutReachingTransport(string? enabled)
    {
        var configuration = Configuration(enabled);
        var transport = new CountingHandler();
        var logs = new CapturedLogs();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddHttpClient("fixture").ConfigurePrimaryHttpMessageHandler(() => transport);
        services.AddHttpClient<TypedClient>().ConfigurePrimaryHttpMessageHandler(() => new CountingHandler());
        services.AddRestoredRuntimeIsolation();
        services.AddRestoredRuntimeIsolation(); // No duplicate filter registration.
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("fixture");
        var typed = provider.GetRequiredService<TypedClient>();
        if (enabled == "true")
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://fixture.invalid/private?token=fixture-token"));
            Assert.Equal(RestoredRuntimeIsolation.BlockedMessage, error.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => typed.Client.GetAsync("http://127.0.0.1/private"));
            Assert.Equal(0, transport.Requests);
            Assert.DoesNotContain(logs.Messages, message => message.Contains("fixture-token", StringComparison.Ordinal));
        }
        else
        {
            using var response = await client.GetAsync("https://fixture.invalid/");
            using var typedResponse = await typed.Client.GetAsync("https://fixture.invalid/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, typedResponse.StatusCode);
            Assert.Equal(1, transport.Requests);
        }
    }

    [Fact]
    public async Task InvalidIsolationConfigurationFailsClosedWithoutReachingTransport()
    {
        var services = new ServiceCollection();
        var transport = new CountingHandler();
        services.AddSingleton<IConfiguration>(Configuration("invalid"));
        services.AddHttpClient("fixture").ConfigurePrimaryHttpMessageHandler(() => transport);
        services.AddRestoredRuntimeIsolation();
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("fixture");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://fixture.invalid/"));
        Assert.Equal(0, transport.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("invalid")]
    public void SynchronousHttpRequestsCannotBypassIsolation(string? enabled)
    {
        var transport = new CountingHandler();
        var logs = new CapturedLogs();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<IConfiguration>(Configuration(enabled));
        services.AddHttpClient("fixture").ConfigurePrimaryHttpMessageHandler(() => transport);
        services.AddRestoredRuntimeIsolation();
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("fixture");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://fixture.invalid/private?token=fixture-token");
        if (enabled is "true" or "invalid")
        {
            Assert.Throws<InvalidOperationException>(() => client.Send(request));
            Assert.Equal(0, transport.Requests);
            Assert.DoesNotContain(logs.Messages, message => message.Contains("fixture-token", StringComparison.Ordinal));
        }
        else
        {
            using var response = client.Send(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, transport.Requests);
        }
    }

    [Fact]
    public async Task CancelledRequestNeverReachesTransport()
    {
        var services = new ServiceCollection();
        var transport = new CountingHandler();
        services.AddSingleton<IConfiguration>(Configuration("true"));
        services.AddHttpClient("fixture").ConfigurePrimaryHttpMessageHandler(() => transport);
        services.AddRestoredRuntimeIsolation();
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("fixture");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://fixture.invalid/", cancellation.Token));
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public async Task ExistingHttpClientCannotBypassLaterIsolationFlag()
    {
        var configuration = Configuration("false");
        var transport = new CountingHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddHttpClient("fixture").ConfigurePrimaryHttpMessageHandler(() => transport);
        services.AddRestoredRuntimeIsolation();
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("fixture");
        using var response = await client.GetAsync("https://fixture.invalid/");
        configuration[RestoredRuntimeIsolation.EnabledKey] = "true";
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://fixture.invalid/"));
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task EmailOverrideAndAttachmentCannotBypassIsolation()
    {
        var email = new EmailService(Configuration("true"), null!, NullLogger<EmailService>.Instance);
        var settings = new EmailSendSettings(true, "fixture.invalid", 587, "fixture-user", "fixture-password",
            "fixture@fixture.invalid", "Fixture", true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => email.SendEmailAsync(
            "recipient@fixture.invalid", "Fixture", "Fixture", settings: settings));
        Assert.Equal(RestoredRuntimeIsolation.BlockedMessage, error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => email.SendEmailWithAttachmentAsync(
            "recipient@fixture.invalid", "Fixture", "Fixture", "fixture.txt", [1], "text/plain"));
    }

    [Fact]
    public void S3FactoryUsesInjectedIsolationConfigurationBeforeConstructingSdkClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration("true"));
        services.AddSingleton<IS3StorageClientFactory, S3StorageClientFactory>();
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IS3StorageClientFactory>().Create(null!));
        Assert.Equal(RestoredRuntimeIsolation.BlockedMessage, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TelegramDirectAndProxyFactoriesCannotCreateAnUnrestrictedClient(bool proxy)
    {
        var factory = new CountingFactory();
        var connection = proxy ? new TelegramBotConnectionSettings("http://fixture.invalid:8080", "fixture-user", "fixture-password") : null;
        var type = typeof(EmailService).Assembly.GetType("PayPlanner.Api.Services.Notifications.TelegramBotApiHttpClientFactory")!;
        var error = Assert.Throws<TargetInvocationException>(() => type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [factory, connection, Configuration("true")]));
        Assert.Equal(RestoredRuntimeIsolation.BlockedMessage, Assert.IsType<InvalidOperationException>(error.InnerException).Message);
        Assert.Equal(0, factory.Creates);
    }

    [Fact]
    public async Task WebPushCannotConstructItsDirectTransportInIsolation()
    {
        var service = new ExposedPush(Configuration("true"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync());
        Assert.Equal(RestoredRuntimeIsolation.BlockedMessage, error.Message);
    }

    private static IConfigurationRoot Configuration(string? enabled)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RestoredRuntimeIsolation.EnabledKey] = enabled,
        }).Build();

    public sealed class TypedClient(HttpClient client) { public HttpClient Client { get; } = client; }
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
    private sealed class CountingFactory : IHttpClientFactory
    {
        public int Creates { get; private set; }
        public HttpClient CreateClient(string name) { Creates++; return new HttpClient(new CountingHandler()); }
    }
    private sealed class ExposedPush(IConfiguration configuration)
        : WebPushNotificationService(null!, configuration, NullLogger<WebPushNotificationService>.Instance)
    {
        public Task SendAsync() => base.SendSubscriptionAsync(null!, "fixture", "fixture", "fixture", "fixture", CancellationToken.None);
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);
        public void Dispose() { }
        private sealed class CapturedLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
        }
    }
}
