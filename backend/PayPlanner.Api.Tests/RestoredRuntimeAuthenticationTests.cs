using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using PayPlanner.Api.Services;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RestoredRuntimeAuthenticationTests
{
    private const string WorkingSecret = "working-fixture-0123456789abcdef0123456789abcdef";
    private const string InstanceId = "cefe9b7e3bb64c51b3c80a9e96bb57ea";

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void OrdinaryAuthenticationIsUnchanged(string? enabled)
    {
        using var configuration = Configuration(enabled);
        RestoredRuntimeAuthentication.Configure(configuration);
        Assert.Equal(WorkingSecret, JwtConfiguration.GetRequiredSecret(configuration));
        Assert.Equal("WorkingIssuer", configuration["Jwt:Issuer"]);
        Assert.Equal("WorkingAudience", configuration["Jwt:Audience"]);
    }

    [Fact]
    public void EachRestoredProcessGetsAnIndependentInMemorySigningKey()
    {
        using var first = Configuration("true");
        using var second = Configuration("true");
        RestoredRuntimeAuthentication.Configure(first);
        RestoredRuntimeAuthentication.Configure(second);
        Assert.Equal($"PayPlanner.Restore.{InstanceId}", first["Jwt:Issuer"]);
        Assert.Equal(first["Jwt:Issuer"], first["Jwt:Audience"]);
        Assert.NotEqual(WorkingSecret, JwtConfiguration.GetRequiredSecret(first));
        Assert.NotEqual(JwtConfiguration.GetRequiredSecret(first), JwtConfiguration.GetRequiredSecret(second));
        Assert.Equal(64, Convert.FromBase64String(JwtConfiguration.GetRequiredSecret(first)).Length);
    }

    [Fact]
    public void RestoredValidationRejectsWorkingAndEarlierProbeTokensButAcceptsCurrentToken()
    {
        using var working = Configuration("false");
        using var earlier = Configuration("true");
        using var current = Configuration("true");
        RestoredRuntimeAuthentication.Configure(earlier);
        RestoredRuntimeAuthentication.Configure(current);
        var handler = new JwtSecurityTokenHandler();
        var validation = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtConfiguration.GetRequiredSecret(current))),
            ValidateIssuer = true,
            ValidIssuer = current["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = current["Jwt:Audience"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(Token(working), validation, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(Token(earlier), validation, out _));
        Assert.True(handler.ValidateToken(Token(current), validation, out _).Identity!.IsAuthenticated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public void MissingInstanceIdFailsBeforeChangingAuthentication(string? instanceId)
    {
        using var configuration = Configuration("true");
        configuration[RestoredRuntimeDatabaseIdentity.ConfigurationKey] = instanceId;
        Assert.Throws<InvalidOperationException>(() => RestoredRuntimeAuthentication.Configure(configuration));
        Assert.Equal(WorkingSecret, JwtConfiguration.GetRequiredSecret(configuration));
    }

    private static string Token(IConfiguration configuration)
    {
        // Exercise the application's producer, without login/database/email side effects.
        var service = new AuthService(null!, configuration, null!, null!, null!);
        return (string)typeof(AuthService).GetMethod("GenerateJwtToken", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [new User { Id = 1, Email = "fixture@fixture.invalid", FullName = "Fixture" }])!;
    }

    private static ConfigurationManager Configuration(string? enabled)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RestoredRuntimeIsolation.EnabledKey] = enabled,
            [RestoredRuntimeDatabaseIdentity.ConfigurationKey] = InstanceId,
            ["Jwt:Secret"] = WorkingSecret,
            ["Jwt:Issuer"] = "WorkingIssuer",
            ["Jwt:Audience"] = "WorkingAudience",
        });
        return configuration;
    }
}
