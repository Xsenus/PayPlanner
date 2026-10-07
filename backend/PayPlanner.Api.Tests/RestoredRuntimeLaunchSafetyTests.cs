using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RestoredRuntimeLaunchSafetyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://[::1]:5080")]
    [InlineData("https://127.0.0.1:5080; http://[::1]:5081")]
    public void IsolatedModeAcceptsOnlyExplicitLocalConfiguration(string urls)
        => RestoredRuntimeLaunchSafety.Validate(Configuration(), urls);

    [Theory]
    [InlineData("")]
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("http://[::]:5080")]
    [InlineData("http://localhost:5080")]
    [InlineData("http://fixture.invalid:5080")]
    [InlineData("http://+:5080")]
    [InlineData("http://127.0.0.1:5080;http://0.0.0.0:5081")]
    [InlineData("http://127.0.0.1:5080;")]
    [InlineData("http://user:fixture-secret@127.0.0.1:5080")]
    [InlineData("http://127.0.0.1:5080/path")]
    [InlineData("http://127.0.0.1:5080/?key=fixture-secret")]
    [InlineData("http://127.0.0.1:5080/#fixture-secret")]
    [InlineData("ftp://127.0.0.1:5080")]
    public void IsolatedModeRejectsUnqualifiedListenAddressesWithoutEchoingSecrets(string urls)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RestoredRuntimeLaunchSafety.Validate(Configuration(), urls));
        Assert.DoesNotContain("fixture-secret", error.Message);
    }

    [Theory]
    [InlineData("BackgroundServices:Enabled", null)]
    [InlineData("BackgroundServices:Enabled", "true")]
    [InlineData("BackgroundServices:Enabled", "invalid")]
    [InlineData("Email:Enabled", null)]
    [InlineData("Email:Enabled", "true")]
    [InlineData("Email:Enabled", "invalid")]
    public void IsolatedModeFailsWhenSafetyFlagIsMissingEnabledOrMalformed(string key, string? value)
    {
        var configuration = Configuration();
        configuration[key] = value;
        Assert.Throws<InvalidOperationException>(() => RestoredRuntimeLaunchSafety.Validate(configuration, "http://127.0.0.1:5080"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void OrdinaryModeDoesNotChangeProductionBindingOrBackgroundFlags(string? enabled)
    {
        var configuration = Configuration();
        configuration[RestoredRuntimeIsolation.EnabledKey] = enabled;
        configuration["BackgroundServices:Enabled"] = "true";
        configuration["Email:Enabled"] = "true";
        RestoredRuntimeLaunchSafety.Validate(configuration, "http://0.0.0.0:5080");
        Assert.Equal("true", configuration["BackgroundServices:Enabled"]);
        Assert.Equal("true", configuration["Email:Enabled"]);
    }

    private static IConfigurationRoot Configuration()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RestoredRuntimeIsolation.EnabledKey] = "true",
            [RestoredRuntimeDatabaseIdentity.ConfigurationKey] = "cefe9b7e3bb64c51b3c80a9e96bb57ea",
            ["BackgroundServices:Enabled"] = "false",
            ["Email:Enabled"] = "false",
        }).Build();
}
