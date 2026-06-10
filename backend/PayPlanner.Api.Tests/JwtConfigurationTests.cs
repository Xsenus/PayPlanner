using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class JwtConfigurationTests
{
    [Fact]
    public void GetRequiredSecret_Throws_WhenSecretIsMissing()
    {
        var configuration = new ConfigurationBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() => JwtConfiguration.GetRequiredSecret(configuration));
        Assert.Contains("Jwt:Secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRequiredSecret_Throws_WhenSecretIsTooShort()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "too-short"
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => JwtConfiguration.GetRequiredSecret(configuration));
        Assert.Contains("at least", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRequiredSecret_ReturnsConfiguredSecret()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = secret
            })
            .Build();

        Assert.Equal(secret, JwtConfiguration.GetRequiredSecret(configuration));
    }
}
