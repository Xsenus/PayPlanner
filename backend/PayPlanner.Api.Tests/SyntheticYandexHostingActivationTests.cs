using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SyntheticYandexHostingActivationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task OrdinaryTestHostStartsWithoutSyntheticDatabaseOrProviders(string? activation)
    {
        var settings = new Dictionary<string, string>();
        if (activation is not null) settings[SyntheticYandexHostingStartup.EnabledKey] = activation;
        using var host = await Host(settings).StartAsync();
        Assert.Equal("ordinary-host", await host.GetTestClient().GetStringAsync("/"));
        Assert.Null(host.Services.GetService(typeof(PayPlanner.Api.Services.IYandexDiskService)));
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("invalid")]
    public async Task InvalidActivationDoesNotSilentlyStartAnOrdinaryHost(string activation)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Host(new()
        { [SyntheticYandexHostingStartup.EnabledKey] = activation }).StartAsync());
        Assert.Contains("Synthetic Yandex activation must be true or false.", error.ToString());
    }

    [Theory]
    [InlineData(null, SyntheticYandexHostingStartup.EndpointKey)]
    [InlineData(null, SyntheticYandexHostingStartup.WorkerKey)]
    [InlineData(null, SyntheticYandexHostingStartup.CourtKey)]
    [InlineData(null, SyntheticYandexHostingStartup.LocalFaultKey)]
    [InlineData("false", SyntheticYandexHostingStartup.EndpointKey)]
    [InlineData("false", SyntheticYandexHostingStartup.WorkerKey)]
    [InlineData("false", SyntheticYandexHostingStartup.CourtKey)]
    [InlineData("false", SyntheticYandexHostingStartup.LocalFaultKey)]
    public async Task SyntheticSettingsWithoutActivationFailBeforeRegisteringTransports(string? activation, string key)
    {
        var settings = new Dictionary<string, string> { [key] = "invalid-owned-setting" };
        if (activation is not null) settings[SyntheticYandexHostingStartup.EnabledKey] = activation;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Host(settings).StartAsync());
        Assert.Contains("Synthetic Yandex configuration requires explicit activation.", error.ToString());
    }

    [Fact]
    public async Task ExplicitActivationStillRejectsMissingDatabase()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Host(new()
        { [SyntheticYandexHostingStartup.EnabledKey] = "true" }).StartAsync());
        Assert.Contains("Missing synthetic database configuration.", error.ToString());
    }

    [Theory]
    [InlineData("root")]
    [InlineData("email")]
    [InlineData("background")]
    [InlineData("endpoint")]
    public async Task ExplicitActivationRetainsOwnedRootMailAndLoopbackGuards(string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), (boundary == "root" ? "pp_unowned_" : "pp_task_upload_browser_") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new Dictionary<string, string>
            {
                [SyntheticYandexHostingStartup.EnabledKey] = "true",
                ["ConnectionStrings:Default"] = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "synthetic.db") }.ToString(),
                [SyntheticYandexHostingStartup.EndpointKey] = "http://127.0.0.1:12345/",
                [SyntheticYandexHostingStartup.WorkerKey] = new string('a', 64)
            };
            if (boundary == "email") settings["Email:Enabled"] = "true";
            if (boundary == "background") settings["BackgroundServices:Enabled"] = "true";
            if (boundary == "endpoint") settings[SyntheticYandexHostingStartup.EndpointKey] = "https://127.0.0.1:12345/";
            var error = await Assert.ThrowsAnyAsync<Exception>(() => Host(settings, root).StartAsync());
            Assert.Contains("Synthetic Yandex startup requires an owned isolated database and endpoint.", error.ToString());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root); }
    }

    private static IHostBuilder Host(Dictionary<string, string> settings, string? root = null)
        => new HostBuilder().ConfigureWebHost(webHost =>
        {
            if (root is not null) webHost.UseContentRoot(root);
            webHost.UseTestServer();
            webHost.UseSetting(WebHostDefaults.ApplicationKey, typeof(SyntheticYandexHostingStartup).Assembly.GetName().Name!);
            webHost.UseSetting("Email:Enabled", "false");
            webHost.UseSetting("BackgroundServices:Enabled", "false");
            foreach (var setting in settings) webHost.UseSetting(setting.Key, setting.Value);
            webHost.Configure(app => app.Run(context => context.Response.WriteAsync("ordinary-host")));
        });
}